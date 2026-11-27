using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace TimeCapsuleDrive.Afp;

/// <summary>A file or folder as the server describes it. Dates are AFP time (seconds since 2000-01-01 UTC).</summary>
public sealed record Entry(string Name, bool IsDir, ulong Size, ushort Attr, int CreateDate, int ModDate, uint NodeId);

/// <summary>What a server says about itself before login.</summary>
public sealed record ServerInfo(string Name, string Model, ushort Flags, string[] AfpVersions, string[] LoginMethods)
{
    public bool SupportsExtendedSleep => (Flags & 0x0800) != 0;
    public bool SupportsReconnect => (Flags & 0x0080) != 0;
    public bool SupportsNotifications => (Flags & 0x0040) != 0;
    public bool AllowsGuest => LoginMethods.Contains("No User Authent");
}

/// <summary>An open data fork. Survives reconnects: it is transparently reopened on the new session.</summary>
public sealed class AfpFork
{
    public const ushort Read = 0x01, Write = 0x02, DenyRead = 0x10, DenyWrite = 0x20;

    public string Path;
    public readonly ushort Access;
    public bool Writable => (Access & Write) != 0;
    internal object? Session;
    internal ushort Ref;
    internal AfpFork(string path, ushort access) { Path = path; Access = access; }
}

/// <summary>
/// AFP 3.x client for one volume. Paths are Windows-style ("\Folder\file.txt", "\" is the volume root).
/// Reconnects and logs in again by itself when the session drops.
/// </summary>
public sealed class AfpClient : IDisposable
{
    const ushort FileBitmap = 0x290D; // attributes, create date, mod date, node id, ext data fork length, UTF-8 name
    const ushort DirBitmap = 0x210D;  // attributes, create date, mod date, node id, UTF-8 name
    const uint RootDirId = 2;

    string _host;
    readonly string _volume, _user, _password;
    readonly int _port;
    readonly string? _clientId;
    int _stamp = (int)(DateTime.UtcNow - new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
    readonly Action<string> _log;
    readonly object _connLock = new();
    readonly DsiRequestIds _ids = new();
    DsiConnection? _conn;
    ushort _volId;
    ServerInfo? _server;
    bool _registered;
    long _noReconnectUntil;
    bool _extendedSleep;
    AfpConnectionException? _connectFailure;
    long _connectFailedAt;

    public int MaxWrite { get; private set; } = 0x10000;
    /// <summary>The server reported that something on the volume changed (possibly by another client).</summary>
    public Action? VolumeChanged;
    /// <summary>Called when a connection attempt succeeds or fails: (connected, AFP error code or 0, message).</summary>
    public Action<bool, int, string>? ConnectionChanged;
    /// <summary>
    /// Asked when the server cannot be reached at its address: may return the address it has moved to
    /// (home routers hand out new addresses now and then), or null.
    /// </summary>
    public Func<string?>? Relocate;
    long _relocateAskedAt = -1_000_000;
    /// <summary>The address currently in use.</summary>
    public string Host => _host;

    /// <param name="clientId">
    /// Stable name of this installation, or null. With it the server is told who we are after every login, which
    /// lets it throw away the files a previous, crashed run of the same installation left open. Two programs
    /// running at the same time must not share an id, so one-off tools pass null.
    /// </param>
    public AfpClient(string host, int port, string volume, string user, string password, string? clientId, Action<string> log)
    {
        _host = host; _port = port; _volume = volume; _user = user; _password = password; _clientId = clientId; _log = log;
    }

    // ---------------------------------------------------------------- server discovery

    public static ServerInfo Probe(string host, int port = 548, int timeoutMs = 5000)
    {
        var d = DsiConnection.GetStatus(host, port, timeoutMs);
        string PStr(int o) => Encoding.UTF8.GetString(d, o + 1, d[o]);
        string[] PList(int o)
        {
            int n = d[o++];
            var res = new string[n];
            for (int i = 0; i < n; i++) { res[i] = Encoding.ASCII.GetString(d, o + 1, d[o]); o += 1 + d[o]; }
            return res;
        }
        int machine = BinaryPrimitives.ReadUInt16BigEndian(d), versions = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(2)),
            uams = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(4));
        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(8));
        return new ServerInfo(PStr(10), PStr(machine), flags, PList(versions), PList(uams));
    }

    /// <summary>Logs in and returns the names of the volumes the server shares.</summary>
    public static List<string> ListVolumes(string host, int port, string user, string password)
    {
        using var c = new DsiConnection(host, port, new DsiRequestIds());
        new AfpClient(host, port, "", user, password, null, _ => { }).Login(c);
        var r = c.Command(new Msg(16).U8(0).Span, "FPGetSrvrParms");
        Check(r, "listing volumes");
        var d = r.Span;
        var res = new List<string>();
        int n = d[4], o = 5;
        for (int i = 0; i < n; i++)
        {
            int len = d[o + 1];
            res.Add(Encoding.UTF8.GetString(d.Slice(o + 2, len)));
            o += 2 + len;
        }
        try { c.Command(new Msg(20).U8(0).Span, "FPLogout"); } catch (AfpConnectionException) { }
        return res;
    }

    // ---------------------------------------------------------------- session

    DsiConnection Connect()
    {
        lock (_connLock)
        {
            if (_conn is { Dead: false }) return _conn;
            _conn?.Dispose();
            _conn = null;
            if (Environment.TickCount64 < _noReconnectUntil)
                throw new AfpConnectionException("the server disconnected this user and asked not to reconnect yet");
            // After a failed attempt, fail fast for a few seconds: otherwise every waiting file operation would
            // spend its own long connect timeout, one after another, and the whole drive would freeze.
            if (_connectFailure != null && Environment.TickCount64 - _connectFailedAt < 5000)
                throw new AfpConnectionException(_connectFailure.Message, _connectFailure);
            try
            {
                DsiConnection c;
                try { c = ConnectNow(); }
                catch (AfpConnectionException) when (Relocate != null && Environment.TickCount64 - _relocateAskedAt > 60_000)
                {
                    // Not reachable at the known address: maybe it has a new one. Asked at most once a minute.
                    _relocateAskedAt = Environment.TickCount64;
                    string? moved = null;
                    try { moved = Relocate(); } catch (Exception) { }
                    if (moved == null || moved == _host) throw;
                    _log($"the server is no longer at {_host}; found it at {moved}");
                    _host = moved;
                    _server = null;
                    c = ConnectNow();
                }
                ConnectionChanged?.Invoke(true, 0, "");
                return c;
            }
            catch (AfpConnectionException e)
            {
                _connectFailure = e;
                _connectFailedAt = Environment.TickCount64;
                ConnectionChanged?.Invoke(false, 0, e.Message);
                throw;
            }
            catch (AfpException e)
            {
                ConnectionChanged?.Invoke(false, e.Code, e.Message);
                throw;
            }
        }
    }

    /// <summary>Opens a new connection, logs in and takes over or replaces the previous session. Caller holds the lock.</summary>
    DsiConnection ConnectNow()
    {
        {
            if (_server == null)
            {
                try { _server = Probe(_host, _port); }
                catch (Exception e) when (e is AfpConnectionException or ArgumentException or IndexOutOfRangeException)
                {
                    if (e is AfpConnectionException) throw;
                }
            }
            var c = new DsiConnection(_host, _port, _ids);
            try
            {
                Login(c);
                // Reconnecting: our previous session may still exist on the server and would keep our own files
                // locked against us. Announcing a new start time makes the server discard it together with
                // everything it held; open files are then simply reopened on this session.
                //
                // (The protocol can also carry the old session over to the new connection. That was tried and
                // dropped: after a connection cut in the middle of a write, the Time Capsule stopped answering
                // on the carried-over session for about two minutes.)
                if (_registered) _stamp++;
                RegisterSession(c);
                var r = c.Command(new Msg(24).U8(0).U16(0x0020).PStr(_volume).Span, "FPOpenVol");
                Check(r, "opening volume '" + _volume + "'");
                _volId = BinaryPrimitives.ReadUInt16BigEndian(r.Span.Slice(2));
                _log($"connected to {_host}, volume '{_volume}', user '{(IsGuest ? "guest" : _user)}'");
            }
            catch { c.Fail(new IOException("login failed")); throw; }
            c.Attention = code => OnAttention(c, code);
            MaxWrite = Math.Max(0x8000, c.ServerQuantum - 4096);
            _conn = c;
            _connectFailure = null;
            return c;
        }
    }

    bool IsGuest => _user.Length == 0 && _password.Length == 0;

    void Login(DsiConnection c)
    {
        if (IsGuest)
        {
            Check(c.Command(new Msg(18).PStr("AFP3.3").PStr("No User Authent").Span, "FPLogin"), "guest login");
            return;
        }
        string user = _user.Length == 0 ? "admin" : _user;

        // DHX2: Diffie-Hellman key exchange, then the password travels CAST5-encrypted.
        var r = c.Command(new Msg(18).PStr("AFP3.3").PStr("DHX2").PStr(user).Span, "FPLogin");
        if (r.Error != AfpError.AuthContinue) throw new AfpException(r.Error, "login (DHX2 step 1)");
        var d = r.Span;
        ushort id = BinaryPrimitives.ReadUInt16BigEndian(d);
        var g = Big.Read(d.Slice(2, 4));
        int len = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(6));
        var p = Big.Read(d.Slice(8, len));
        var mb = Big.Read(d.Slice(8 + len, len));

        var ra = Big.Read(RandomNumberGenerator.GetBytes(Math.Max(16, len - 1)));
        var ma = BigInteger.ModPow(g, ra, p);
        byte[] key = MD5.HashData(Pad(BigInteger.ModPow(mb, ra, p), len));
        byte[] c2sIv = Encoding.ASCII.GetBytes("LWallace"), s2cIv = Encoding.ASCII.GetBytes("CJalbert");
        byte[] nonce = RandomNumberGenerator.GetBytes(16);

        r = c.Command(new Msg(19).U8(0).U16(id).Bytes(Pad(ma, len)).Bytes(Cast5(true, key, c2sIv, nonce)).Span, "FPLoginCont");
        if (r.Error != AfpError.AuthContinue) throw new AfpException(r.Error, "login (DHX2 step 2)");
        d = r.Span;
        ushort id2 = BinaryPrimitives.ReadUInt16BigEndian(d);
        byte[] plain = Cast5(false, key, s2cIv, d.Slice(2, 32).ToArray());
        if (!plain.AsSpan(0, 16).SequenceEqual(PlusOne(nonce)))
            throw new AfpException(AfpError.UserNotAuth, "login (key exchange check failed)");

        var last = new byte[16 + 256];
        PlusOne(plain.AsSpan(16, 16).ToArray()).CopyTo(last, 0);
        var pw = Encoding.UTF8.GetBytes(_password);
        pw.AsSpan(0, Math.Min(pw.Length, 255)).CopyTo(last.AsSpan(16));
        r = c.Command(new Msg(19).U8(0).U16(id2).Bytes(Cast5(true, key, c2sIv, last)).Span, "FPLoginCont");
        Check(r, "login as '" + user + "'");
    }

    /// <summary>
    /// Tells the server who we are (installation id + start time of this run). A known id with a different
    /// start time means "that client restarted": the server then closes whatever its previous run left open.
    /// </summary>
    void RegisterSession(DsiConnection c)
    {
        if (_clientId == null) return;
        var id = Encoding.ASCII.GetBytes(_clientId);
        var w = new Msg(64).U8(0).U16(3).U32((uint)id.Length).U32((uint)_stamp).Bytes(id);
        var r = c.Command(w.Span, "FPGetSessionToken");
        _registered = r.Error == 0;
        if (!_registered && !_registrationWarned)
        {
            _registrationWarned = true;
            _log($"the server did not accept the session registration (code {r.Error}); after a lost connection files may stay locked for a few minutes");
        }
    }
    bool _registrationWarned;

    void OnAttention(DsiConnection c, ushort code)
    {
        if ((code & 0xF000) == 0x3000)
        {
            // Server notification: bit 0 = the modification date of a mounted volume changed.
            if ((code & 1) != 0) VolumeChanged?.Invoke();
            return;
        }
        int minutes = code & 0x0FFF;
        if ((code & 0x2000) != 0) ThreadPool.QueueUserWorkItem(_ => LogServerMessage(c));
        if ((code & 0x4000) != 0)
        {
            _log("the server reports it is going down right now");
            c.Fail(new IOException("server crash notification"));
            return;
        }
        if ((code & 0x8000) != 0)
        {
            if (minutes == 0x0FFF) { _log("the server cancelled its shutdown"); return; }
            bool dontReconnect = (code & 0x1000) != 0;
            _log(dontReconnect ? $"the server is disconnecting this user in {minutes} min" : $"the server is shutting down in {minutes} min");
            if (dontReconnect && minutes == 0) _noReconnectUntil = Environment.TickCount64 + 5 * 60_000;
        }
    }

    void LogServerMessage(DsiConnection c)
    {
        try
        {
            var r = c.Command(new Msg(38).U8(0).U16(1).U16(3).Span, "FPGetSrvrMsg");
            if (r.Error != 0 || r.Length < 6) return;
            var d = r.Span;
            bool utf8 = (BinaryPrimitives.ReadUInt16BigEndian(d.Slice(2)) & 2) != 0;
            string msg = utf8
                ? Encoding.UTF8.GetString(d.Slice(6, Math.Min(BinaryPrimitives.ReadUInt16BigEndian(d.Slice(4)), d.Length - 6)))
                : Encoding.UTF8.GetString(d.Slice(5, Math.Min(d[4], d.Length - 5)));
            if (msg.Length > 0) _log("message from the server: " + msg);
        }
        catch (Exception) { }
    }

    /// <summary>The computer is about to sleep: ask the server to keep the session although it will hear nothing from us.</summary>
    public void Sleep()
    {
        lock (_connLock)
        {
            if (_conn is not { Dead: false } c) return;
            _extendedSleep = _server?.SupportsExtendedSleep == true;
            c.TryCommand(new Msg(122).U8(0).U32(_extendedSleep ? 1u : 0u).Span, 2000);
        }
    }

    /// <summary>The computer woke up.</summary>
    public void Wake()
    {
        lock (_connLock)
        {
            if (_conn is not { Dead: false } c) return;
            // Extended sleep ends only on an explicit wake-up; if the session did not survive, the next
            // file operation reconnects by itself.
            if (_extendedSleep && !c.TryCommand(new Msg(122).U8(0).U32(2).Span, 3000))
                c.Fail(new IOException("no answer after wake-up"));
            _extendedSleep = false;
        }
    }

    static byte[] Pad(BigInteger v, int len)
    {
        var raw = Big.Bytes(v);
        if (raw.Length >= len) return raw.AsSpan(raw.Length - len).ToArray();
        var res = new byte[len];
        raw.CopyTo(res, len - raw.Length);
        return res;
    }

    static byte[] PlusOne(byte[] v)
    {
        var r = (byte[])v.Clone();
        for (int i = r.Length - 1; i >= 0; i--) if (++r[i] != 0) break;
        return r;
    }

    static byte[] Cast5(bool encrypt, byte[] key, byte[] iv, byte[] data)
    {
        var c = new BufferedBlockCipher(new CbcBlockCipher(new Cast5Engine()));
        c.Init(encrypt, new ParametersWithIV(new KeyParameter(key), iv));
        return c.DoFinal(data);
    }

    void Drop(DsiConnection? c)
    {
        lock (_connLock)
        {
            if (c != null && ReferenceEquals(_conn, c)) { _conn = null; }
        }
        try { c?.Fail(new IOException("dropped")); } catch { }
    }

    /// <summary>Test hook: cuts the connection the way a network failure would (no goodbye to the server).</summary>
    public void DebugCutConnection()
    {
        lock (_connLock) _conn?.Fail(new IOException("cut for testing"));
    }

    /// <summary>Runs an operation, reconnecting and retrying when the session turns out to be dead.</summary>
    T Run<T>(Func<DsiConnection, T> op) => Run((c, _) => op(c));

    /// <summary>
    /// As above; the operation is told when it is being repeated. A repeated create, delete or rename may find
    /// its own first attempt already done (only the answer was lost) and must not report that as an error.
    /// </summary>
    T Run<T>(Func<DsiConnection, bool, T> op)
    {
        for (int attempt = 0; ; attempt++)
        {
            DsiConnection? c = null;
            try
            {
                c = Connect();
                return op(c, attempt > 0);
            }
            catch (AfpConnectionException e) when (attempt < 2)
            {
                _log("connection problem, retrying: " + e.Message);
                Drop(c);
                Thread.Sleep(300 + 1500 * attempt);
            }
        }
    }

    static void Check(DsiPending r, string what)
    {
        if (r.Error != 0) throw new AfpException(r.Error, what);
    }

    // ---------------------------------------------------------------- names and paths

    const string Forbidden = "\\/:*?\"<>|";

    /// <summary>
    /// Server name to Windows name: composed Unicode; characters Windows cannot have in a name (and a trailing
    /// dot or space) are moved to a private range so the name stays usable and maps back exactly.
    /// </summary>
    static string ToWindowsName(ReadOnlySpan<byte> utf8)
    {
        var s = Encoding.UTF8.GetString(utf8).Normalize(NormalizationForm.FormC);
        StringBuilder? sb = null;
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            bool trailing = i == s.Length - 1 && (ch == '.' || ch == ' ');
            if (ch < 0x20 || Forbidden.Contains(ch) || trailing)
            {
                sb ??= new StringBuilder(s);
                sb[i] = (char)(0xF000 + ch);
            }
        }
        return sb?.ToString() ?? s;
    }

    /// <summary>
    /// Decomposes a name the way the server's file system stores it: canonical decomposition, except for the
    /// ranges HFS+ leaves alone (U+2000-2FFF, U+F900-FAFF, U+2F800-2FAFF).
    /// </summary>
    static string Decompose(string s)
    {
        static bool Keep(int cp) => cp is >= 0x2000 and <= 0x2FFF or >= 0xF900 and <= 0xFAFF or >= 0x2F800 and <= 0x2FAFF;
        bool any = false;
        foreach (var r in s.EnumerateRunes()) if (Keep(r.Value)) { any = true; break; }
        if (!any) return s.Normalize(NormalizationForm.FormD);
        var res = new StringBuilder();
        var run = new StringBuilder();
        foreach (var r in s.EnumerateRunes())
        {
            if (Keep(r.Value))
            {
                if (run.Length > 0) { res.Append(run.ToString().Normalize(NormalizationForm.FormD)); run.Clear(); }
                res.Append(r.ToString());
            }
            else run.Append(r.ToString());
        }
        if (run.Length > 0) res.Append(run.ToString().Normalize(NormalizationForm.FormD));
        return res.ToString();
    }

    static byte[] ToServerPath(string winPath)
    {
        var parts = winPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var ms = new MemoryStream();
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) ms.WriteByte(0);
            var sb = new StringBuilder(parts[i]);
            for (int k = 0; k < sb.Length; k++)
                if (sb[k] >= 0xF000 && sb[k] < 0xF080) sb[k] = (char)(sb[k] - 0xF000);
            ms.Write(Encoding.UTF8.GetBytes(Decompose(sb.ToString())));
        }
        return ms.ToArray();
    }

    static (string dir, string name) SplitPath(string winPath)
    {
        var p = winPath.TrimEnd('\\');
        int i = p.LastIndexOf('\\');
        return i <= 0 ? ("\\", p.Substring(i + 1)) : (p.Substring(0, i), p.Substring(i + 1));
    }

    /// <summary>
    /// Directory id of a folder. The Time Capsule refuses to create objects through a multi-component path,
    /// so everything that changes the tree addresses its target as (parent directory id, name).
    /// </summary>
    uint DirId(DsiConnection c, string dirPath)
    {
        var sp = ToServerPath(dirPath);
        if (sp.Length == 0) return RootDirId;
        var r = c.Command(new Msg(34).U8(0).U16(_volId).U32(RootDirId).U16(0).U16(0x0100).Path(sp).Span, "FPGetFileDirParms");
        Check(r, "locating folder " + dirPath);
        if ((r.Span[4] & 0x80) == 0) throw new AfpException(AfpError.ObjectNotFound, "locating folder " + dirPath);
        return BinaryPrimitives.ReadUInt32BigEndian(r.Span.Slice(6));
    }

    /// <summary>Command header addressing <paramref name="path"/> as (parent directory id, name).</summary>
    Msg PathCmd(DsiConnection c, byte cmd, byte flag, string path)
    {
        var (dir, name) = SplitPath(path);
        return new Msg(cmd).U8(flag).U16(_volId).U32(DirId(c, dir)).Path(ToServerPath(name));
    }

    static Entry ParseEntry(ReadOnlySpan<byte> p, bool isDir)
    {
        ushort attr = BinaryPrimitives.ReadUInt16BigEndian(p);
        int cdate = BinaryPrimitives.ReadInt32BigEndian(p.Slice(2));
        int mdate = BinaryPrimitives.ReadInt32BigEndian(p.Slice(6));
        uint node = BinaryPrimitives.ReadUInt32BigEndian(p.Slice(10));
        int o = 14;
        ulong size = 0;
        if (!isDir) { size = BinaryPrimitives.ReadUInt64BigEndian(p.Slice(o)); o += 8; }
        int nameOff = BinaryPrimitives.ReadUInt16BigEndian(p.Slice(o));
        int nameLen = BinaryPrimitives.ReadUInt16BigEndian(p.Slice(nameOff + 4));
        return new Entry(ToWindowsName(p.Slice(nameOff + 6, nameLen)), isDir, size, attr, cdate, mdate, node);
    }

    // ---------------------------------------------------------------- metadata

    /// <summary>Returns null when the path does not exist.</summary>
    public Entry? Stat(string path) => Run(c =>
    {
        var w = new Msg(34).U8(0).U16(_volId).U32(RootDirId).U16(FileBitmap).U16(DirBitmap).Path(ToServerPath(path));
        var r = c.Command(w.Span, "FPGetFileDirParms");
        if (r.Error is AfpError.ObjectNotFound or AfpError.ItemNotFound or AfpError.ParamErr or AfpError.ObjectTypeErr) return null;
        Check(r, "reading info of " + path);
        var d = r.Span;
        return ParseEntry(d.Slice(6), (d[4] & 0x80) != 0);
    });

    public List<Entry> List(string path) => Run(c =>
    {
        var res = new List<Entry>();
        var sp = ToServerPath(path);
        uint index = 1;
        while (true)
        {
            var w = new Msg(68).U8(0).U16(_volId).U32(RootDirId).U16(FileBitmap).U16(DirBitmap)
                .U16(1000).U32(index).U32(0x20000).Path(sp);
            var r = c.Command(w.Span, "FPEnumerateExt2");
            if (r.Error == AfpError.ObjectNotFound) break;
            Check(r, "listing " + path);
            var d = r.Span;
            int count = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(4));
            if (count == 0) break;
            int o = 6;
            for (int i = 0; i < count; i++)
            {
                int len = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(o));
                bool isDir = (d[o + 2] & 0x80) != 0;
                res.Add(ParseEntry(d.Slice(o + 4, len - 4), isDir));
                o += len;
            }
            index += (uint)count;
        }
        return res;
    });

    public (ulong free, ulong total) VolumeSpace() => Run(c =>
    {
        var r = c.Command(new Msg(17).U8(0).U16(_volId).U16(0x0600).Span, "FPGetVolParms");
        Check(r, "reading volume size");
        var d = r.Span;
        return (BinaryPrimitives.ReadUInt64BigEndian(d.Slice(2)), BinaryPrimitives.ReadUInt64BigEndian(d.Slice(10)));
    });

    bool Exists(DsiConnection c, string path)
    {
        var w = new Msg(34).U8(0).U16(_volId).U32(RootDirId).U16(0).U16(0).Path(ToServerPath(path));
        return c.Command(w.Span, "FPGetFileDirParms").Error == 0;
    }

    public void CreateFile(string path) => Run((c, retry) =>
    {
        var r = c.Command(PathCmd(c, 7, 0, path).Span, "FPCreateFile");
        if (!(retry && r.Error == AfpError.ObjectExists)) Check(r, "creating " + path);
        return 0;
    });

    public void CreateDir(string path) => Run((c, retry) =>
    {
        var r = c.Command(PathCmd(c, 6, 0, path).Span, "FPCreateDir");
        if (!(retry && r.Error == AfpError.ObjectExists)) Check(r, "creating folder " + path);
        return 0;
    });

    public void Delete(string path) => Run((c, retry) =>
    {
        var r = c.Command(PathCmd(c, 8, 0, path).Span, "FPDelete");
        if (!(retry && r.Error == AfpError.ObjectNotFound)) Check(r, "deleting " + path);
        return 0;
    });

    public void Rename(string from, string to) => Run((c, retry) =>
    {
        if (retry && !Exists(c, from) && Exists(c, to)) return 0; // the first attempt went through
        var (toDir, toName) = SplitPath(to);
        var (fromDir, fromName) = SplitPath(from);
        var w = new Msg(23).U8(0).U16(_volId).U32(DirId(c, fromDir)).U32(DirId(c, toDir))
            .Path(ToServerPath(fromName)).Path(Array.Empty<byte>()).Path(ToServerPath(toName));
        Check(c.Command(w.Span, "FPMoveAndRename"), $"renaming {from} to {to}");
        return 0;
    });

    /// <summary>Sets creation and/or modification time (AFP seconds); null leaves a value alone.</summary>
    public void SetTimes(string path, int? create, int? mod) => Run(c =>
    {
        ushort bitmap = (ushort)((create.HasValue ? 0x0004 : 0) | (mod.HasValue ? 0x0008 : 0));
        if (bitmap == 0) return 0;
        var (dir, name) = SplitPath(path);
        var w = new Msg(35).U8(0).U16(_volId).U32(DirId(c, dir)).U16(bitmap).Path(ToServerPath(name)).PadEven();
        if (create.HasValue) w.U32((uint)create.Value);
        if (mod.HasValue) w.U32((uint)mod.Value);
        Check(c.Command(w.Span, "FPSetFileDirParms"), "setting times of " + path);
        return 0;
    });

    // ---------------------------------------------------------------- forks (file contents)

    /// <summary>
    /// Opens a file's contents. A writer also asks the server to refuse other writers (deny-write), so two
    /// computers can never write the same file at once; the second one gets "file is in use".
    /// </summary>
    public AfpFork OpenFork(string path, bool writable)
    {
        var f = new AfpFork(path, writable ? (ushort)(AfpFork.Read | AfpFork.Write | AfpFork.DenyWrite) : AfpFork.Read);
        Run(c => ForkRef(c, f));
        return f;
    }

    ushort ForkRef(DsiConnection c, AfpFork f)
    {
        lock (f)
        {
            if (ReferenceEquals(f.Session, c.Session)) return f.Ref;
            var w = new Msg(26).U8(0).U16(_volId).U32(RootDirId).U16(0).U16(f.Access).Path(ToServerPath(f.Path));
            var r = c.Command(w.Span, "FPOpenFork");
            Check(r, "opening " + f.Path);
            f.Ref = BinaryPrimitives.ReadUInt16BigEndian(r.Span.Slice(2));
            f.Session = c.Session;
            return f.Ref;
        }
    }

    public void CloseFork(AfpFork f)
    {
        ushort fref;
        object? session;
        lock (f) { session = f.Session; fref = f.Ref; f.Session = null; }
        if (session == null) return;
        DsiConnection? c;
        lock (_connLock) c = _conn;
        // The fork only exists on the session it was opened in (or one that took that session over).
        if (c is { Dead: false })
        {
            if (!ReferenceEquals(c.Session, session)) return; // that session is gone, and the fork with it
            try
            {
                c.Command(new Msg(4).U8(0).U16(fref).Span, "FPCloseFork");
                return;
            }
            catch (AfpConnectionException) { }
        }
        // Not connected right now: nothing to do. The session this fork belongs to is discarded by the server
        // at the next reconnect, and the fork with it.
    }

    /// <summary>A read that is on the wire. Several may be outstanding at once.</summary>
    public readonly record struct PendingRead(DsiConnection Conn, DsiPending Reply);

    /// <summary>Starts a read into <paramref name="dest"/> without waiting for the answer.</summary>
    public PendingRead BeginRead(AfpFork f, long offset, int count, byte[] dest)
    {
        var c = Connect();
        ushort fref = ForkRef(c, f);
        var w = new Msg(60).U8(0).U16(fref).U64((ulong)offset).U64((ulong)count);
        return new PendingRead(c, c.BeginCommand(w.Span, dest));
    }

    /// <summary>Completes a read; returns the number of bytes placed in the buffer (short at end of file).</summary>
    public int EndRead(PendingRead p)
    {
        try
        {
            var r = p.Conn.End(p.Reply, "FPReadExt");
            if (r.Error != 0 && r.Error != AfpError.Eof) throw new AfpException(r.Error, "reading");
            return r.Length;
        }
        catch (AfpConnectionException) { Drop(p.Conn); throw; }
    }

    /// <summary>A write that is on the wire.</summary>
    public readonly record struct PendingWrite(DsiConnection Conn, DsiPending Reply);

    /// <summary>Starts a write of at most <see cref="MaxWrite"/> bytes without waiting for the answer.</summary>
    public PendingWrite BeginWrite(AfpFork f, long offset, ReadOnlySpan<byte> data)
    {
        var c = Connect();
        ushort fref = ForkRef(c, f);
        var w = new Msg(61).U8(0).U16(fref).U64((ulong)offset).U64((ulong)data.Length);
        return new PendingWrite(c, c.BeginWrite(w.Span, data));
    }

    public void EndWrite(PendingWrite p)
    {
        try
        {
            var r = p.Conn.End(p.Reply, "FPWriteExt");
            if (r.Error != 0) throw new AfpException(r.Error, "writing");
        }
        catch (AfpConnectionException) { Drop(p.Conn); throw; }
    }

    /// <summary>Blocking write with reconnect; splits the data into requests the server accepts.</summary>
    public void Write(AfpFork f, long offset, byte[] data, int length)
    {
        int done = 0;
        while (done < length)
        {
            int n = Math.Min(MaxWrite, length - done);
            // Data the application already handed over is worth waiting for: keep trying for about a minute
            // (a router or the Time Capsule itself restarting) before giving up.
            for (int attempt = 0; ; attempt++)
            {
                try { EndWrite(BeginWrite(f, offset + done, data.AsSpan(done, n))); break; }
                catch (AfpConnectionException) when (attempt < 10) { Thread.Sleep(attempt == 0 ? 300 : 6000); }
            }
            done += n;
        }
    }

    public void SetSize(AfpFork f, long size) => Run(c =>
    {
        ushort fref = ForkRef(c, f);
        Check(c.Command(new Msg(31).U8(0).U16(fref).U16(0x0800).U64((ulong)size).Span, "FPSetForkParms"), "resizing " + f.Path);
        return 0;
    });

    /// <summary>
    /// Asks the server to put everything written to the file onto the disk itself ("stable storage").
    /// Falls back to the weaker flush on servers without that command (and then returns false).
    /// </summary>
    public bool Sync(AfpFork f) => Run(c =>
    {
        ushort fref = ForkRef(c, f);
        var r = c.Command(new Msg(79).U8(0).U16(fref).Span, "FPSyncFork");
        if (r.Error == 0) return true;
        Check(c.Command(new Msg(11).U8(0).U16(fref).Span, "FPFlushFork"), "flushing " + f.Path);
        return false;
    });

    /// <summary>Orderly goodbye: close the volume, log out, end the session. Open forks must be closed first.</summary>
    public void Dispose()
    {
        lock (_connLock)
        {
            if (_conn is { Dead: false } c)
            {
                c.TryCommand(new Msg(2).U8(0).U16(_volId).Span, 3000);
                c.TryCommand(new Msg(20).U8(0).Span, 3000);
            }
            _conn?.Dispose();
            _conn = null;
        }
    }

    // ---------------------------------------------------------------- message builder

    sealed class Msg
    {
        byte[] _b = new byte[64];
        int _n;
        public Msg(byte command) { _b[0] = command; _n = 1; }
        public ReadOnlySpan<byte> Span => _b.AsSpan(0, _n);
        Span<byte> Take(int n)
        {
            if (_n + n > _b.Length) Array.Resize(ref _b, Math.Max(_b.Length * 2, _n + n));
            var s = _b.AsSpan(_n, n);
            _n += n;
            return s;
        }
        public Msg U8(byte v) { Take(1)[0] = v; return this; }
        public Msg U16(ushort v) { BinaryPrimitives.WriteUInt16BigEndian(Take(2), v); return this; }
        public Msg U32(uint v) { BinaryPrimitives.WriteUInt32BigEndian(Take(4), v); return this; }
        public Msg U64(ulong v) { BinaryPrimitives.WriteUInt64BigEndian(Take(8), v); return this; }
        public Msg Bytes(ReadOnlySpan<byte> v) { v.CopyTo(Take(v.Length)); return this; }
        public Msg PStr(string s) { var b = Encoding.UTF8.GetBytes(s); U8((byte)b.Length); return Bytes(b); }
        /// <summary>AFP pathname, type 3 (UTF-8): type, text encoding hint, length, bytes.</summary>
        public Msg Path(byte[] p) { U8(3); U32(0x08000103); U16((ushort)p.Length); return Bytes(p); }
        public Msg PadEven() { if ((_n & 1) != 0) U8(0); return this; }
    }
}
