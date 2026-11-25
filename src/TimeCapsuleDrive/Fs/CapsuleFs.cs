using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Fsp;
using TimeCapsuleDrive.Afp;
using FileInfo = Fsp.Interop.FileInfo;
using VolumeInfo = Fsp.Interop.VolumeInfo;

namespace TimeCapsuleDrive.Fs;

/// <summary>
/// Presents an AFP volume to Windows as a drive (through WinFsp).
///
/// Every open file has one <see cref="FileState"/> shared by all handles to it, and at most one fork on the
/// server. A file opened for writing holds a write fork that makes the server refuse other writers, so two
/// computers cannot write the same file at once.
///
/// File contents are fetched in fixed-size blocks; when a file is read front to back the next blocks are
/// requested ahead of time over the same connection, which is where most of the speed comes from.
/// </summary>
public sealed class CapsuleFs : FileSystemBase
{
    const int BlockSize = 256 * 1024;
    const int ReadAheadBlocks = 4;
    const int KeepBehindBlocks = 4;
    const int WriteWindow = 4;
    /// <summary>Writes smaller than this are gathered into bigger pieces before being sent.</summary>
    const int GatherBelow = 128 * 1024;
    const long MetaTtlMs = 5000;
    /// <summary>The server allows a session only a couple of hundred open files; stay well below that.</summary>
    const int MaxOpenForks = 120;

    const int StatusEndOfFile = unchecked((int)0xC0000011), StatusAccessDenied = unchecked((int)0xC0000022),
        StatusNotFound = unchecked((int)0xC0000034), StatusCollision = unchecked((int)0xC0000035),
        StatusSharingViolation = unchecked((int)0xC0000043), StatusDiskFull = unchecked((int)0xC000007F),
        StatusWriteProtected = unchecked((int)0xC00000A2), StatusNetworkError = unchecked((int)0xC00000C4),
        StatusDirNotEmpty = unchecked((int)0xC0000101), StatusIoError = unchecked((int)0xC0000185);

    const uint AttrHidden = 0x2, AttrSystem = 0x4, AttrDirectory = 0x10, AttrArchive = 0x20;
    const uint FileDirectoryFile = 0x1;
    const uint AccessWriteData = 0x2, AccessAppendData = 0x4;
    static readonly DateTime AfpEpoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    readonly AfpClient _afp;
    readonly string _label;
    readonly Action<string> _log;
    readonly byte[] _security;

    readonly object _metaLock = new();
    readonly Dictionary<string, (Entry? entry, long at)> _meta = new();
    readonly Dictionary<string, (List<Entry> items, long at)> _lists = new();
    readonly object _statesLock = new();
    readonly Dictionary<string, FileState> _states = new();
    (ulong free, ulong total, long at) _space;
    int _openForks;

    sealed class Block
    {
        public AfpClient.PendingRead Pending;
        public byte[] Buf = null!;
        public int Len = -1; // -1 while the request is still on the wire
    }

    /// <summary>A piece of a file that has been sent to the server but not yet acknowledged.</summary>
    sealed class WriteChunk
    {
        public AfpClient.PendingWrite Pending;
        public byte[] Buf = null!;
        public int Len;
        public long Offset;
    }

    /// <summary>Everything known about one open file. Locked (as a monitor) around all I/O on that file.</summary>
    sealed class FileState
    {
        public string Path;
        public int Refs;
        /// <summary>Handles an application still holds. Windows may keep a file "open" for a while after that.</summary>
        public int Active;
        public AfpFork? Fork;
        public readonly Dictionary<long, Block> Blocks = new();
        public long LastBlock = -2;
        public int Sequential;
        /// <summary>The span of the last read that bypassed the block cache, and how many such reads adjoined in a row.</summary>
        public long DirectStart = -1, DirectEnd = -1;
        public int DirectRun;
        public readonly Queue<WriteChunk> Writes = new();
        /// <summary>Consecutive small writes gathered into one piece that has not been sent yet.</summary>
        public byte[]? Gather;
        public long GatherOffset, GatherSince;
        public int GatherLen;
        /// <summary>A failure to save data written earlier; reported on the next operation on the file.</summary>
        public Exception? WriteError;
        public bool Written, Deleted;
        /// <summary>
        /// A size the application asked for by growing the file, not yet real on the server. Windows sets the
        /// final size before copying a file; passing that on makes the Time Capsule zero-fill the whole file
        /// first, so every byte would be written twice. Instead the file grows as data arrives and the
        /// promised size is only enforced when the file is closed.
        /// </summary>
        public ulong? Promised;
        /// <summary>
        /// The file's size as this drive knows it while the file is being changed here. It overrides what the
        /// server reports: an answer to a question asked before our last write may arrive after it and would
        /// otherwise bring an outdated size back. (Nobody else can change the file: we hold the write fork.)
        /// </summary>
        public ulong? KnownSize;
        public int? WantCreate, WantMod;
        public FileState(string path) { Path = path; }
    }

    sealed class Handle
    {
        public readonly FileState? State; // null for folders
        public bool CleanedUp;
        string _dirPath;
        public bool IsDir => State == null;
        public string Path { get => State?.Path ?? _dirPath; set { if (State == null) _dirPath = value; } }
        public Handle(FileState? state, string path) { State = state; _dirPath = path; }
    }

    sealed class DirIter
    {
        public List<(string name, Entry entry)> Items = new();
        public int Next;
    }

    public CapsuleFs(AfpClient afp, string label, Action<string> log)
    {
        _afp = afp; _label = label; _log = log;
        var sd = new RawSecurityDescriptor("O:BAG:BAD:P(A;;FA;;;WD)");
        _security = new byte[sd.BinaryLength];
        sd.GetBinaryForm(_security, 0);
        // The server tells us when anything on the volume changes, including changes by other computers.
        _afp.VolumeChanged = InvalidateAll;
        _idleSender = new Timer(_ => SendIdleData(), null, 1000, 1000);
    }

    readonly Timer _idleSender;

    /// <summary>Free and total space of the volume; the server is asked at most every ten seconds.</summary>
    (ulong free, ulong total) Space()
    {
        long now = Environment.TickCount64;
        (ulong free, ulong total, long at) s;
        lock (_metaLock) s = _space;
        if (s.total == 0 || now - s.at > 10_000)
        {
            var (free, total) = _afp.VolumeSpace();
            s = (free, total, now);
            lock (_metaLock) _space = s;
        }
        return (s.free, s.total);
    }

    // ---------------------------------------------------------------- errors

    public override int ExceptionHandler(Exception ex)
    {
        switch (ex)
        {
            case AfpException a:
                int st = a.Code switch
                {
                    AfpError.AccessDenied or AfpError.ObjectLocked => StatusAccessDenied,
                    AfpError.DenyConflict or AfpError.FileBusy => StatusSharingViolation,
                    AfpError.DirNotEmpty => StatusDirNotEmpty,
                    AfpError.DiskFull => StatusDiskFull,
                    AfpError.ObjectExists => StatusCollision,
                    AfpError.ObjectNotFound or AfpError.ItemNotFound => StatusNotFound,
                    AfpError.VolLocked => StatusWriteProtected,
                    _ => StatusIoError,
                };
                if (st == StatusIoError) _log("error: " + a.Message);
                return st;
            case AfpConnectionException c:
                _log("network error: " + c.Message);
                return StatusNetworkError;
            default:
                _log("unexpected error: " + ex);
                return StatusIoError;
        }
    }

    // ---------------------------------------------------------------- metadata cache

    static string Key(string path)
    {
        var p = path.TrimEnd('\\');
        return p.Length == 0 ? "\\" : p.ToLowerInvariant();
    }

    static string ParentOf(string key)
    {
        int i = key.LastIndexOf('\\');
        return i <= 0 ? "\\" : key.Substring(0, i);
    }

    static string Child(string dir, string name) => dir.EndsWith('\\') ? dir + name : dir + "\\" + name;

    Entry? GetEntry(string path)
    {
        var e = GetServerEntry(path);
        if (e is { IsDir: false })
        {
            FileState? st;
            lock (_statesLock) _states.TryGetValue(Key(path), out st);
            if (st?.KnownSize is ulong known && known != e.Size) return e with { Size = known };
        }
        return e;
    }

    Entry? GetServerEntry(string path)
    {
        string key = Key(path);
        long now = Environment.TickCount64;
        lock (_metaLock)
        {
            if (_meta.TryGetValue(key, out var m) && now - m.at < MetaTtlMs) return m.entry;
            // A fresh listing of the parent that did not contain this name means it does not exist.
            if (key != "\\" && _lists.TryGetValue(ParentOf(key), out var l) && now - l.at < MetaTtlMs) return null;
        }
        var e = _afp.Stat(path);
        lock (_metaLock)
        {
            // Both caches go together: a listing that outlives its entries would make files look missing.
            if (_meta.Count > 100_000) { _meta.Clear(); _lists.Clear(); }
            _meta[key] = (e, Environment.TickCount64);
        }
        return e;
    }

    List<Entry> ListDir(string path, bool fresh = false)
    {
        string key = Key(path);
        long now = Environment.TickCount64;
        if (!fresh)
            lock (_metaLock)
                if (_lists.TryGetValue(key, out var l) && now - l.at < MetaTtlMs) return l.items;
        var items = _afp.List(path);
        items.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        lock (_metaLock)
        {
            now = Environment.TickCount64;
            if (_lists.Count > 2000) _lists.Clear();
            _lists[key] = (items, now);
            foreach (var e in items) _meta[Key(Child(key, e.Name))] = (e, now);
        }
        return items;
    }

    /// <summary>Forget everything cached; used after create, delete and rename, and when the server reports a change.</summary>
    void InvalidateAll()
    {
        lock (_metaLock) { _meta.Clear(); _lists.Clear(); }
    }

    /// <summary>Drops what is remembered about a file, so that the next question about it goes to the server.</summary>
    void Forget(string path)
    {
        string key = Key(path);
        // The parent's listing goes too: a listing without this entry would say the file does not exist.
        lock (_metaLock) { _meta.Remove(key); _lists.Remove(ParentOf(key)); }
    }

    void PutEntry(string path, Entry e)
    {
        string key = Key(path);
        lock (_metaLock)
        {
            _meta[key] = (e, Environment.TickCount64);
            _lists.Remove(ParentOf(key));
        }
    }

    // ---------------------------------------------------------------- open files

    FileState Acquire(string path)
    {
        lock (_statesLock)
        {
            string key = Key(path);
            if (!_states.TryGetValue(key, out var st)) _states[key] = st = new FileState(path);
            st.Refs++;
            st.Active++;
            return st;
        }
    }

    void Release(FileState st)
    {
        lock (_statesLock)
            if (--st.Refs > 0) return;
        lock (st)
        {
            lock (_statesLock)
                if (st.Refs > 0) return; // opened again in the meantime: keep everything
            try { FinalClose(st); }
            catch (Exception e) { _log($"close of {st.Path}: {e.Message}"); }
            lock (_statesLock)
                if (st.Refs == 0 && _states.TryGetValue(Key(st.Path), out var cur) && ReferenceEquals(cur, st))
                    _states.Remove(Key(st.Path));
        }
    }

    /// <summary>The last handle to a file is gone: finish pending work and give the file back to the server.</summary>
    void FinalClose(FileState st)
    {
        try
        {
            // Whatever happens while finishing the file, its fork must be given back: an orphaned write fork
            // would keep the file locked against everyone, including this drive.
            try { if (!st.Deleted) SettlePromisedSize(st); }
            finally { CloseFork(st); }
            // Closing a written file stamps it with "now"; put back the times the application asked for.
            if (st.Written && !st.Deleted && (st.WantCreate.HasValue || st.WantMod.HasValue))
            {
                try { _afp.SetTimes(st.Path, st.WantCreate, st.WantMod); }
                catch (AfpException e) when (e.Code == AfpError.ObjectNotFound) { } // deleted meanwhile
                Forget(st.Path);
            }
        }
        finally
        {
            if (st.WriteError != null && !st.Deleted)
                _log($"DATA NOT SAVED: part of what was written to {st.Path} did not reach the server: {st.WriteError.Message}");
            st.WriteError = null;
            if (st.Gather != null) { ArrayPool<byte>.Shared.Return(st.Gather); st.Gather = null; }
            st.GatherLen = 0;
            st.Written = st.Deleted = false;
            st.Promised = null;
            st.KnownSize = null;
            st.WantCreate = st.WantMod = null;
        }
    }

    /// <summary>Makes a promised size real if the application never wrote that far.</summary>
    void SettlePromisedSize(FileState st)
    {
        if (st.Promised is not ulong promised) return;
        st.Promised = null;
        DrainWrites(st);
        var real = _afp.Stat(st.Path);
        if (real != null && !real.IsDir && real.Size < promised)
        {
            // The server fills the new space with zeros, which takes time; grow in steps so that no single
            // request runs into the reply timeout.
            const ulong step = 256UL << 20;
            var fork = EnsureFork(st, true);
            for (ulong size = real.Size; size < promised;)
            {
                size = Math.Min(promised, size + step);
                _afp.SetSize(fork, (long)size);
            }
        }
        Forget(st.Path);
    }

    /// <summary>
    /// The file's fork on the server, opened on first need. Reading uses a plain read fork; the first write
    /// replaces it with a write fork, which the server grants only if nobody else is writing the file.
    /// </summary>
    AfpFork EnsureFork(FileState st, bool needWrite)
    {
        if (st.Fork != null && (!needWrite || st.Fork.Writable)) return st.Fork;
        if (st.Fork != null) CloseFork(st);
        if (Volatile.Read(ref _openForks) >= MaxOpenForks) TrimForks(st);
        st.Fork = _afp.OpenFork(st.Path, needWrite);
        Interlocked.Increment(ref _openForks);
        return st.Fork;
    }

    void CloseFork(FileState st)
    {
        DropBlocks(st, true);
        if (st.Fork == null) return;
        try { DrainWrites(st); }
        finally
        {
            var f = st.Fork;
            st.Fork = null;
            Interlocked.Decrement(ref _openForks);
            _afp.CloseFork(f);
        }
    }

    /// <summary>
    /// Too many files are open on the server: give back those no application is using any more.
    /// (Windows keeps closed files around for a few seconds; their forks can be reopened if needed.)
    /// </summary>
    void TrimForks(FileState except)
    {
        foreach (var st in OpenStates())
        {
            if (ReferenceEquals(st, except) || st.Fork == null || Volatile.Read(ref st.Active) > 0) continue;
            if (!Monitor.TryEnter(st)) continue;
            try { if (st.Active == 0) CloseFork(st); }
            catch (Exception e) { _log($"close of {st.Path}: {e.Message}"); }
            finally { Monitor.Exit(st); }
        }
    }

    List<FileState> OpenStates()
    {
        lock (_statesLock) return _states.Values.ToList();
    }

    /// <summary>Sends everything still buffered to the server's disk (used before the computer sleeps).</summary>
    public void FlushAll()
    {
        foreach (var st in OpenStates())
        {
            try
            {
                lock (st)
                {
                    DrainWrites(st);
                    if (st.Fork is { Writable: true } f && st.Written) _afp.Sync(f);
                }
            }
            catch (Exception e) { _log($"flush of {st.Path}: {e.Message}"); }
        }
    }

    /// <summary>Closes every open file on the server (used when the drive is being unmounted).</summary>
    public void CloseAll()
    {
        _idleSender.Dispose();
        foreach (var st in OpenStates())
        {
            try { lock (st) FinalClose(st); }
            catch (Exception e) { _log($"close of {st.Path}: {e.Message}"); }
        }
    }

    // ---------------------------------------------------------------- conversions

    static uint Attributes(Entry e)
    {
        uint a = e.IsDir ? AttrDirectory : AttrArchive;
        if ((e.Attr & 1) != 0 || e.Name.StartsWith('.')) a |= AttrHidden;
        // Folders Windows itself creates on every drive: keep them out of sight, as on any other disk.
        if (e.Name is "$RECYCLE.BIN" or "System Volume Information") a |= AttrHidden | AttrSystem;
        return a;
    }

    static ulong ToFileTime(int afp)
    {
        if (afp == int.MinValue) afp = 0;
        return (ulong)AfpEpoch.AddSeconds(afp).ToFileTimeUtc();
    }

    static int ToAfpTime(ulong fileTime)
    {
        double s = (DateTime.FromFileTimeUtc((long)fileTime) - AfpEpoch).TotalSeconds;
        return (int)Math.Clamp(s, int.MinValue + 1, int.MaxValue);
    }

    static int NowAfp() => (int)(DateTime.UtcNow - AfpEpoch).TotalSeconds;

    static void Fill(Entry e, out FileInfo fi)
    {
        fi = default;
        fi.FileAttributes = Attributes(e);
        fi.FileSize = e.Size;
        fi.AllocationSize = (e.Size + 4095) / 4096 * 4096;
        fi.CreationTime = ToFileTime(e.CreateDate);
        fi.LastWriteTime = fi.ChangeTime = fi.LastAccessTime = ToFileTime(e.ModDate);
        fi.IndexNumber = e.NodeId;
    }

    // ---------------------------------------------------------------- volume

    public override int GetVolumeInfo(out VolumeInfo VolumeInfo)
    {
        VolumeInfo = default;
        long now = Environment.TickCount64;
        (ulong free, ulong total, long at) s;
        lock (_metaLock) s = _space;
        if (s.total == 0 || now - s.at > 10_000)
        {
            var (free, total) = _afp.VolumeSpace();
            s = (free, total, now);
            lock (_metaLock) _space = s;
        }
        VolumeInfo.TotalSize = s.total;
        VolumeInfo.FreeSize = s.free;
        VolumeInfo.SetVolumeLabel(_label);
        return STATUS_SUCCESS;
    }

    public override int GetSecurityByName(string FileName, out uint FileAttributes, ref byte[] SecurityDescriptor)
    {
        FileAttributes = 0;
        var e = GetEntry(FileName);
        if (e == null) return StatusNotFound;
        FileAttributes = Attributes(e);
        if (SecurityDescriptor != null) SecurityDescriptor = _security;
        return STATUS_SUCCESS;
    }

    public override int GetSecurity(object FileNode, object FileDesc, ref byte[] SecurityDescriptor)
    {
        SecurityDescriptor = _security;
        return STATUS_SUCCESS;
    }

    public override int SetSecurity(object FileNode, object FileDesc, AccessControlSections Sections, byte[] SecurityDescriptor) => STATUS_SUCCESS;

    // ---------------------------------------------------------------- open / create / close

    /// <summary>
    /// Binds a handle to its file. A handle that may write takes the write fork right away, so that
    /// "someone else is writing this file" is reported when the file is opened, not in the middle of saving.
    /// </summary>
    Handle OpenFile(string path, uint grantedAccess)
    {
        var st = Acquire(path);
        try
        {
            if ((grantedAccess & (AccessWriteData | AccessAppendData)) != 0)
                lock (st) EnsureFork(st, true);
        }
        catch
        {
            Release(st);
            throw;
        }
        return new Handle(st, path);
    }

    public override int Open(string FileName, uint CreateOptions, uint GrantedAccess, out object FileNode, out object FileDesc,
        out FileInfo FileInfo, out string NormalizedName)
    {
        FileNode = null!; FileDesc = null!; NormalizedName = null!; FileInfo = default;
        var e = GetEntry(FileName);
        if (e == null) return StatusNotFound;
        FileDesc = e.IsDir ? new Handle(null, FileName) : OpenFile(FileName, GrantedAccess);
        Fill(e, out FileInfo);
        return STATUS_SUCCESS;
    }

    public override int Create(string FileName, uint CreateOptions, uint GrantedAccess, uint FileAttributes, byte[] SecurityDescriptor,
        ulong AllocationSize, out object FileNode, out object FileDesc, out FileInfo FileInfo, out string NormalizedName)
    {
        FileNode = null!; FileDesc = null!; NormalizedName = null!; FileInfo = default;
        bool dir = (CreateOptions & FileDirectoryFile) != 0;
        try
        {
            if (dir) _afp.CreateDir(FileName); else _afp.CreateFile(FileName);
        }
        finally { InvalidateAll(); }
        var e = GetEntry(FileName);
        if (e == null) return StatusIoError;
        FileDesc = dir ? new Handle(null, FileName) : OpenFile(FileName, GrantedAccess);
        Fill(e, out FileInfo);
        return STATUS_SUCCESS;
    }

    public override int Overwrite(object FileNode, object FileDesc, uint FileAttributes, bool ReplaceFileAttributes, ulong AllocationSize,
        out FileInfo FileInfo)
    {
        var st = ((Handle)FileDesc).State!;
        lock (st)
        {
            DrainWrites(st);
            st.Promised = null;
            _afp.SetSize(EnsureFork(st, true), 0);
            st.Written = true;
            Changed(st, _ => 0);
        }
        return GetFileInfo(FileNode, FileDesc, out FileInfo);
    }

    /// <summary>
    /// An application closed its handle. Windows may still keep the file open internally for a while, but once
    /// no application holds it the file is finished on the server right away: pending data is written, the
    /// write protection is released and the server-side fork is closed. Otherwise hundreds of files handled
    /// in a row would pile up as open files on the server.
    /// </summary>
    public override void Cleanup(object FileNode, object FileDesc, string FileName, uint Flags)
    {
        var h = (Handle)FileDesc;
        if (h.State is { } state && Deactivate(h) == 0 && (Flags & CleanupDelete) == 0)
        {
            lock (state)
            {
                bool stillIdle;
                lock (_statesLock) stillIdle = state.Active == 0;
                if (stillIdle)
                    try { FinalClose(state); }
                    catch (Exception e) { _log($"close of {state.Path}: {e.Message}"); }
            }
        }
        if ((Flags & CleanupDelete) == 0) return;
        try
        {
            if (h.State is { } st)
            {
                lock (st)
                {
                    st.Deleted = true;
                    st.Promised = null;
                    st.KnownSize = null;
                    try { CloseFork(st); }
                    catch (Exception e) { _log($"close of {st.Path} before delete: {e.Message}"); }
                }
                // A file created later under the same name is a different file and gets its own state.
                lock (_statesLock)
                    if (_states.TryGetValue(Key(st.Path), out var cur) && ReferenceEquals(cur, st)) _states.Remove(Key(st.Path));
            }
            _afp.Delete(h.Path);
        }
        catch (Exception e) { _log($"delete of {h.Path} failed: {e.Message}"); }
        InvalidateAll();
    }

    public override void Close(object FileNode, object FileDesc)
    {
        var h = (Handle)FileDesc;
        if (h.State is not { } st) return;
        Deactivate(h);
        Release(st);
    }

    /// <summary>Counts a handle as no longer used by an application (once); returns how many still are.</summary>
    int Deactivate(Handle h)
    {
        lock (_statesLock)
        {
            if (!h.CleanedUp) { h.CleanedUp = true; h.State!.Active--; }
            return h.State!.Active;
        }
    }

    // ---------------------------------------------------------------- read

    public override int Read(object FileNode, object FileDesc, IntPtr Buffer, ulong Offset, uint Length, out uint BytesTransferred)
    {
        BytesTransferred = 0;
        var st = ((Handle)FileDesc).State!;
        var e = GetEntry(st.Path);
        if (e == null) return StatusNotFound;
        long size = (long)e.Size, off = (long)Offset;
        if (off >= size) return StatusEndOfFile;
        int len = (int)Math.Min(Length, size - off);
        lock (st)
        {
            // What was written must be on the server before it is read back from there.
            if (st.GatherLen > 0 || st.Writes.Count > 0)
            {
                try { DrainWrites(st); }
                catch (Exception ex) when (ex is AfpException or AfpConnectionException) { st.WriteError ??= ex; throw; }
            }
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    BytesTransferred = (uint)ReadBlocks(st, Buffer, off, len, size);
                    break;
                }
                catch (AfpConnectionException) when (attempt < 2)
                {
                    DropBlocks(st, false);
                    Thread.Sleep(300 + 1500 * attempt);
                }
                catch
                {
                    DropBlocks(st, false);
                    throw;
                }
            }
            if (BytesTransferred < len && st.Promised != null)
            {
                // The not-yet-written tail of a promised size reads as zeros.
                unsafe { new Span<byte>((void*)(Buffer + (int)BytesTransferred), len - (int)BytesTransferred).Clear(); }
                BytesTransferred = (uint)len;
            }
        }
        return BytesTransferred == 0 ? StatusEndOfFile : STATUS_SUCCESS;
    }

    int ReadBlocks(FileState st, IntPtr buffer, long off, int len, long size)
    {
        var fork = EnsureFork(st, false);

        long first = off / BlockSize, last = (off + len - 1) / BlockSize, lastInFile = (size - 1) / BlockSize;
        bool sequential = first == st.LastBlock || first == st.LastBlock + 1;

        // A read that jumps to another place in the file (a database, an archive index, a disk image) fetches
        // just the bytes asked for: pulling a whole block per 4 KB request made random reads several times
        // slower than they need to be. Reads that then carry on from there go back to blocks and read-ahead.
        // (A step back into the block just before the last one is a file being read backwards: blocks suit that.)
        if (!sequential && first != st.LastBlock - 1 && len <= BlockSize && !(st.LastBlock == -2 && off == 0)
            && !Cached(st, first, last))
        {
            st.DirectRun = off == st.DirectEnd || off + len == st.DirectStart ? st.DirectRun + 1 : 0;
            if (st.DirectRun < 2)
            {
                var buf = ArrayPool<byte>.Shared.Rent(len);
                // The buffer goes back to the pool only after the reply: a failed request may still write into it.
                int got = _afp.EndRead(_afp.BeginRead(fork, off, len, buf));
                got = Math.Min(got, len);
                if (got > 0) Marshal.Copy(buf, 0, buffer, got);
                ArrayPool<byte>.Shared.Return(buf);
                st.DirectStart = off;
                st.DirectEnd = off + got;
                return got;
            }
        }
        st.DirectStart = st.DirectEnd = -1;
        st.Sequential = sequential ? st.Sequential + 1 : 0;
        long upto = Math.Min(lastInFile, last + (st.Sequential >= 2 ? ReadAheadBlocks : 0));

        if (st.Blocks.Count > 0)
        {
            List<long>? evict = null;
            foreach (var k in st.Blocks.Keys)
                if (k < first - KeepBehindBlocks || k > upto) (evict ??= new()).Add(k);
            if (evict != null)
                foreach (var k in evict) { Release(st.Blocks[k], true); st.Blocks.Remove(k); }
        }

        for (long b = first; b <= upto; b++)
        {
            if (st.Blocks.ContainsKey(b)) continue;
            var blk = new Block { Buf = ArrayPool<byte>.Shared.Rent(BlockSize) };
            int want = (int)Math.Min(BlockSize, size - b * BlockSize);
            try { blk.Pending = _afp.BeginRead(fork, b * BlockSize, want, blk.Buf); }
            catch { ArrayPool<byte>.Shared.Return(blk.Buf); throw; }
            st.Blocks[b] = blk;
        }

        int done = 0;
        for (long b = first; b <= last; b++)
        {
            var blk = st.Blocks[b];
            if (blk.Len < 0) blk.Len = _afp.EndRead(blk.Pending);
            int inBlock = (int)(off + done - b * BlockSize);
            int take = Math.Min(blk.Len - inBlock, len - done);
            if (take <= 0) break;
            Marshal.Copy(blk.Buf, inBlock, buffer + done, take);
            done += take;
            if (blk.Len < BlockSize) break;
        }
        st.LastBlock = last;
        return done;
    }

    static bool Cached(FileState st, long first, long last)
    {
        for (long b = first; b <= last; b++)
            if (!st.Blocks.ContainsKey(b)) return false;
        return true;
    }

    /// <summary>Gives a block's buffer back. A buffer is only reused once its request can no longer write into it.</summary>
    void Release(Block blk, bool reuse)
    {
        if (!reuse) return;
        if (blk.Len < 0)
        {
            try { blk.Len = _afp.EndRead(blk.Pending); }
            catch { return; }
        }
        ArrayPool<byte>.Shared.Return(blk.Buf);
    }

    void DropBlocks(FileState st, bool reuse)
    {
        if (st.Blocks.Count == 0) return;
        foreach (var blk in st.Blocks.Values) Release(blk, reuse);
        st.Blocks.Clear();
        st.LastBlock = -2;
        st.Sequential = 0;
    }

    // ---------------------------------------------------------------- write

    /// <summary>Records that the file's contents changed: updates the cached size and drops stale read-ahead.</summary>
    void Changed(FileState st, Func<ulong, ulong> newSize)
    {
        DropBlocks(st, true);
        st.WantMod = null; // a modification time requested earlier no longer describes the file
        var e = GetEntry(st.Path);
        ulong size = newSize(st.KnownSize ?? e?.Size ?? 0);
        st.KnownSize = size;
        if (e != null) PutEntry(st.Path, e with { Size = size, ModDate = NowAfp() });
    }

    public override int Write(object FileNode, object FileDesc, IntPtr Buffer, ulong Offset, uint Length, bool WriteToEndOfFile,
        bool ConstrainedIo, out uint BytesTransferred, out FileInfo FileInfo)
    {
        BytesTransferred = 0;
        var st = ((Handle)FileDesc).State!;
        lock (st)
        {
            var e = GetEntry(st.Path);
            if (e == null) { FileInfo = default; return StatusNotFound; }
            ulong size = e.Size, off = Offset;
            uint len = Length;
            if (ConstrainedIo)
            {
                if (off >= size) { Fill(e, out FileInfo); return STATUS_SUCCESS; }
                if (off + len > size) len = (uint)(size - off);
            }
            else if (WriteToEndOfFile) off = size;

            if (st.WriteError != null) throw st.WriteError; // data written earlier was lost: do not pretend all is well
            if (off + len > size && SpaceIsShort(len)) { FileInfo = default; return StatusDiskFull; }
            st.Written = true;
            var fork = EnsureFork(st, true);
            try
            {
                if (len >= GatherBelow)
                {
                    // A large write: send it now and tell the application "written" only after the server has
                    // acknowledged every piece, so an error reaches the application that caused it.
                    SendGathered(st);
                    QueueWrite(st, fork, Buffer, (long)off, (int)len);
                    FinishQueued(st);
                }
                else GatherWrite(st, Buffer, (long)off, (int)len);
            }
            catch (Exception ex) when (ex is AfpException or AfpConnectionException)
            {
                st.WriteError ??= ex;
                throw;
            }
            BytesTransferred = len;
            ulong end = off + len;
            Changed(st, s => Math.Max(s, end));
        }
        return GetFileInfo(FileNode, FileDesc, out FileInfo);
    }

    /// <summary>True when the volume is (nearly) full; asks the server at most every ten seconds.</summary>
    bool SpaceIsShort(uint length)
    {
        try
        {
            var (free, _) = Space();
            return free < (ulong)length + (4UL << 20);
        }
        catch (Exception) { return false; } // cannot tell: let the write itself decide
    }

    /// <summary>
    /// Many programs write in small pieces (a few kilobytes each). Sending every piece as its own request and
    /// waiting for the answer makes copying several times slower, so consecutive small writes are gathered into
    /// one request-sized piece first. The gathered data is sent when the piece is full, when anything else
    /// touches the file (read, flush, resize, close) and at the latest about two seconds after it arrived.
    /// An error for data sent this way reaches the application on its next operation on the file.
    /// </summary>
    void GatherWrite(FileState st, IntPtr buffer, long offset, int length)
    {
        int capacity = _afp.MaxWrite;
        if (st.GatherLen > 0 && (offset != st.GatherOffset + st.GatherLen || st.GatherLen + length > capacity)) SendGathered(st);
        if (st.GatherLen == 0)
        {
            st.Gather ??= ArrayPool<byte>.Shared.Rent(capacity);
            st.GatherOffset = offset;
            st.GatherSince = Environment.TickCount64;
        }
        Marshal.Copy(buffer, st.Gather!, st.GatherLen, length);
        st.GatherLen += length;
        if (st.GatherLen >= capacity) SendGathered(st);
    }

    /// <summary>Sends the gathered piece on its way (without waiting for the answer).</summary>
    void SendGathered(FileState st)
    {
        if (st.GatherLen == 0) return;
        var c = new WriteChunk { Buf = st.Gather!, Len = st.GatherLen, Offset = st.GatherOffset };
        st.Gather = null;
        st.GatherLen = 0;
        bool queued = false;
        try
        {
            while (st.Writes.Count >= WriteWindow) FinishWrite(st);
            try
            {
                c.Pending = _afp.BeginWrite(st.Fork!, c.Offset, c.Buf.AsSpan(0, c.Len));
                st.Writes.Enqueue(c);
                queued = true;
            }
            catch (AfpConnectionException)
            {
                // Connection lost: re-send what is still unacknowledged first, in order, then this piece.
                FinishQueued(st);
                _afp.Write(st.Fork!, c.Offset, c.Buf, c.Len);
            }
        }
        finally
        {
            if (!queued) ArrayPool<byte>.Shared.Return(c.Buf);
        }
    }

    /// <summary>Called every second: sends gathered data that has been waiting for more to come.</summary>
    void SendIdleData()
    {
        long now = Environment.TickCount64;
        foreach (var st in OpenStates())
        {
            if (st.GatherLen == 0 || now - st.GatherSince < 1500 || !Monitor.TryEnter(st)) continue;
            try { DrainWrites(st); }
            catch (Exception e)
            {
                st.WriteError ??= e;
                _log($"data written to {st.Path} could not be saved: {e.Message}");
            }
            finally { Monitor.Exit(st); }
        }
    }

    /// <summary>
    /// Sends the data as several requests with up to <see cref="WriteWindow"/> of them in flight, so the
    /// server never idles waiting for the next piece. The caller waits for the rest with FinishQueued.
    /// </summary>
    void QueueWrite(FileState st, AfpFork fork, IntPtr buffer, long offset, int length)
    {
        int done = 0;
        while (done < length)
        {
            int n = Math.Min(_afp.MaxWrite, length - done);
            while (st.Writes.Count >= WriteWindow) FinishWrite(st);
            var c = new WriteChunk { Buf = ArrayPool<byte>.Shared.Rent(n), Len = n, Offset = offset + done };
            Marshal.Copy(buffer + done, c.Buf, 0, n);
            try
            {
                c.Pending = _afp.BeginWrite(fork, c.Offset, c.Buf.AsSpan(0, n));
                st.Writes.Enqueue(c);
            }
            catch (AfpConnectionException)
            {
                // Connection lost: re-send what is still unacknowledged first, in order, then this piece.
                try { FinishQueued(st); _afp.Write(fork, c.Offset, c.Buf, n); }
                finally { ArrayPool<byte>.Shared.Return(c.Buf); }
            }
            done += n;
        }
    }

    /// <summary>Waits for the oldest outstanding write; if the connection dropped, sends that piece again.</summary>
    void FinishWrite(FileState st)
    {
        var c = st.Writes.Dequeue();
        try
        {
            try { _afp.EndWrite(c.Pending); }
            catch (AfpConnectionException) { _afp.Write(st.Fork!, c.Offset, c.Buf, c.Len); }
        }
        finally { ArrayPool<byte>.Shared.Return(c.Buf); }
    }

    /// <summary>Waits until the server has acknowledged every piece already sent.</summary>
    void FinishQueued(FileState st)
    {
        Exception? first = null;
        while (st.Writes.Count > 0)
        {
            try { FinishWrite(st); }
            catch (Exception e) { first ??= e; }
        }
        if (first != null) throw first;
    }

    /// <summary>Gets everything the applications have written onto the server: gathered data included.</summary>
    void DrainWrites(FileState st)
    {
        Exception? first = null;
        try { SendGathered(st); }
        catch (Exception e) { first = e; }
        try { FinishQueued(st); }
        catch (Exception e) { first ??= e; }
        if (first != null) throw first;
    }

    /// <summary>An application asked for its data to be safely on disk: wait for it and tell the server to commit.</summary>
    public override int Flush(object FileNode, object FileDesc, out FileInfo FileInfo)
    {
        FileInfo = default;
        if (FileDesc is not Handle h)
        {
            FlushAll(); // whole-volume flush
            return STATUS_SUCCESS;
        }
        if (h.State is { } st)
            lock (st)
            {
                if (st.WriteError != null) throw st.WriteError;
                try { DrainWrites(st); }
                catch (Exception ex) when (ex is AfpException or AfpConnectionException) { st.WriteError ??= ex; throw; }
                if (st.Fork is { Writable: true } f && st.Written) _afp.Sync(f);
            }
        return GetFileInfo(FileNode, FileDesc, out FileInfo);
    }

    public override int SetFileSize(object FileNode, object FileDesc, ulong NewSize, bool SetAllocationSize, out FileInfo FileInfo)
    {
        var st = ((Handle)FileDesc).State!;
        lock (st)
        {
            var e = GetEntry(st.Path);
            if (e == null) { FileInfo = default; return StatusNotFound; }
            // Allocation size is only a hint here, except that shrinking it below the file size truncates.
            if (SetAllocationSize && NewSize >= e.Size) return GetFileInfo(FileNode, FileDesc, out FileInfo);
            if (NewSize > e.Size)
            {
                EnsureFork(st, true);
                st.Promised = NewSize;
                st.Written = true;
                Changed(st, _ => NewSize);
            }
            else if (NewSize < e.Size)
            {
                DrainWrites(st);
                st.Promised = null;
                var fork = EnsureFork(st, true);
                var real = _afp.Stat(st.Path);
                if (real != null && NewSize < real.Size) _afp.SetSize(fork, (long)NewSize);
                else if (real != null && NewSize > real.Size) st.Promised = NewSize;
                st.Written = true;
                Forget(st.Path);
                Changed(st, _ => NewSize);
            }
        }
        return GetFileInfo(FileNode, FileDesc, out FileInfo);
    }

    // ---------------------------------------------------------------- info / rename / delete

    public override int GetFileInfo(object FileNode, object FileDesc, out FileInfo FileInfo)
    {
        FileInfo = default;
        var e = GetEntry(((Handle)FileDesc).Path);
        if (e == null) return StatusNotFound;
        Fill(e, out FileInfo);
        return STATUS_SUCCESS;
    }

    public override int SetBasicInfo(object FileNode, object FileDesc, uint FileAttributes, ulong CreationTime, ulong LastAccessTime,
        ulong LastWriteTime, ulong ChangeTime, out FileInfo FileInfo)
    {
        var h = (Handle)FileDesc;
        // 0 and the all-ones value both mean "leave this time alone".
        static bool Given(ulong t) => t != 0 && t <= 2650467743999999999UL;
        int? create = Given(CreationTime) ? ToAfpTime(CreationTime) : null;
        int? mod = Given(LastWriteTime) ? ToAfpTime(LastWriteTime) : null;
        if (create.HasValue || mod.HasValue)
        {
            lock ((object?)h.State ?? h)
            {
                if (h.State is { } st)
                {
                    if (create.HasValue) st.WantCreate = create;
                    if (mod.HasValue) st.WantMod = mod;
                }
                try { _afp.SetTimes(h.Path, create, mod); }
                catch (AfpException e) { _log($"cannot set times of {h.Path}: {e.Message}"); }
                var cur = GetEntry(h.Path);
                if (cur != null)
                    PutEntry(h.Path, cur with { CreateDate = create ?? cur.CreateDate, ModDate = mod ?? cur.ModDate });
            }
        }
        return GetFileInfo(FileNode, FileDesc, out FileInfo);
    }

    public override int CanDelete(object FileNode, object FileDesc, string FileName)
    {
        var h = (Handle)FileDesc;
        if (h.IsDir && ListDir(h.Path, true).Count > 0) return StatusDirNotEmpty;
        return STATUS_SUCCESS;
    }

    public override int Rename(object FileNode, object FileDesc, string FileName, string NewFileName, bool ReplaceIfExists)
    {
        var h = (Handle)FileDesc;
        string oldKey = Key(FileName), newKey = Key(NewFileName);
        string? aside = null;
        try
        {
            if (oldKey != newKey)
            {
                var target = _afp.Stat(NewFileName);
                if (target != null)
                {
                    if (!ReplaceIfExists) return StatusCollision;
                    if (target.IsDir) return StatusAccessDenied;
                    // Move the file being replaced aside instead of deleting it first: if the rename then
                    // fails, it is put back and the user does not end up with neither file.
                    aside = NewFileName + ".tcd-replaced-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    _afp.Rename(NewFileName, aside);
                }
            }
            try { _afp.Rename(FileName, NewFileName); }
            catch when (aside != null)
            {
                try { _afp.Rename(aside, NewFileName); }
                catch (Exception e) { _log($"could not restore {NewFileName} (kept as {aside}): {e.Message}"); }
                throw;
            }
            if (aside != null)
                try { _afp.Delete(aside); }
                catch (Exception e) { _log($"could not remove replaced file {aside}: {e.Message}"); }

            // Open files that lived under the old name (the file itself, or everything inside a renamed folder)
            // now live under the new one.
            lock (_statesLock)
            {
                foreach (var kv in _states.ToList())
                {
                    bool self = kv.Key == oldKey, inside = kv.Key.StartsWith(oldKey + "\\", StringComparison.Ordinal);
                    if (!self && !inside) continue;
                    var st = kv.Value;
                    st.Path = self ? NewFileName : NewFileName.TrimEnd('\\') + st.Path.Substring(FileName.TrimEnd('\\').Length);
                    if (st.Fork != null) st.Fork.Path = st.Path;
                    _states.Remove(kv.Key);
                    _states[Key(st.Path)] = st;
                }
            }
            h.Path = NewFileName;
        }
        finally { InvalidateAll(); }
        return STATUS_SUCCESS;
    }

    // ---------------------------------------------------------------- directory listing

    public override bool ReadDirectoryEntry(object FileNode, object FileDesc, string Pattern, string Marker, ref object Context,
        out string FileName, out FileInfo FileInfo)
    {
        var h = (Handle)FileDesc;
        if (Context is not DirIter it)
        {
            it = new DirIter();
            var items = ListDir(h.Path);
            bool root = Key(h.Path) == "\\";
            int skipDots = Marker == "." ? 1 : Marker == ".." ? 2 : 0;
            if (!root && (Marker == null || skipDots > 0))
            {
                var self = GetEntry(h.Path);
                var parent = GetEntry(ParentOf(h.Path.TrimEnd('\\'))) ?? self;
                if (self != null)
                {
                    if (skipDots < 1) it.Items.Add((".", self));
                    if (skipDots < 2) it.Items.Add(("..", parent!));
                }
            }
            foreach (var e in items)
                if (Marker == null || skipDots > 0 || string.CompareOrdinal(e.Name, Marker) > 0)
                    it.Items.Add((e.Name, e));
            Context = it;
        }
        if (it.Next < it.Items.Count)
        {
            var (name, entry) = it.Items[it.Next++];
            FileName = name;
            Fill(entry, out FileInfo);
            return true;
        }
        FileName = null!;
        FileInfo = default;
        return false;
    }
}
