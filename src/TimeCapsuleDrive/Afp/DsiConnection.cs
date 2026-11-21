using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;

namespace TimeCapsuleDrive.Afp;

/// <summary>The TCP session to the server is gone (or never came up). The caller may reconnect and retry.</summary>
public sealed class AfpConnectionException : IOException
{
    public AfpConnectionException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>The server answered with an AFP error code.</summary>
public sealed class AfpException : Exception
{
    public int Code { get; }
    public AfpException(int code, string what) : base($"AFP error {code} ({AfpError.Name(code)}) in {what}") => Code = code;
}

public static class AfpError
{
    public const int AccessDenied = -5000, AuthContinue = -5001, BadUam = -5002, DenyConflict = -5006,
        DirNotEmpty = -5007, DiskFull = -5008, Eof = -5009, FileBusy = -5010, ItemNotFound = -5012,
        ObjectExists = -5017, ObjectNotFound = -5018, ParamErr = -5019, UserNotAuth = -5023,
        CallNotSupported = -5024, ObjectTypeErr = -5025, TooManyFilesOpen = -5026, VolLocked = -5031,
        ObjectLocked = -5032, CantRename = -5033;

    public static string Name(int code) => code switch
    {
        AccessDenied => "access denied", BadUam => "login method not supported", DenyConflict => "file is in use by someone else",
        DirNotEmpty => "directory not empty", DiskFull => "disk full", Eof => "end of file", FileBusy => "file busy",
        ItemNotFound => "item not found", ObjectExists => "already exists", ObjectNotFound => "not found",
        ParamErr => "bad parameter", UserNotAuth => "wrong user name or password", CallNotSupported => "not supported by the server",
        ObjectTypeErr => "wrong object type", TooManyFilesOpen => "too many open files", VolLocked => "volume is read-only",
        ObjectLocked => "object locked", CantRename => "cannot rename", _ => "?",
    };
}

/// <summary>One request that has been sent and is waiting for its reply.</summary>
public sealed class DsiPending
{
    internal readonly ManualResetEventSlim Done = new(false);
    internal byte[]? Dest;
    internal Exception? Failure;
    public int Error;
    public byte[] Data = Array.Empty<byte>();
    public int Length;
    public ReadOnlySpan<byte> Span => Data.AsSpan(0, Length);
}

/// <summary>
/// Source of DSI request ids. It outlives a connection on purpose: the server keeps a replay cache keyed by
/// request id, so after a session is carried over to a new connection the numbering must simply continue,
/// otherwise a new request could be answered with the cached reply of an old one.
/// </summary>
public sealed class DsiRequestIds
{
    ushort _last;
    public ushort Next() => ++_last;
}

/// <summary>
/// DSI (AFP over TCP) transport. Thread-safe; many requests may be in flight at once and are matched to
/// replies by request id, which is what makes read-ahead pipelining possible.
/// </summary>
public sealed class DsiConnection : IDisposable
{
    const byte CmdCloseSession = 1, CmdCommand = 2, CmdGetStatus = 3, CmdOpenSession = 4, CmdTickle = 5, CmdWrite = 6, CmdAttention = 8;

    /// <summary>The protocol's keep-alive rules: a tickle every 30 s; no data at all for this long means the peer is gone.</summary>
    const int TickleMs = 30_000, IdleDeadMs = 120_000, BusyDeadMs = 60_000;
    /// <summary>Backstop only; the watchdog normally notices a dead server first.</summary>
    const int ReplyTimeoutMs = 300_000;
    /// <summary>The server remembers its last 32 replies; never have more than that outstanding.</summary>
    const int MaxInFlight = 24;

    readonly Socket _sock;
    readonly object _sendLock = new();
    readonly ConcurrentDictionary<ushort, DsiPending> _pending = new();
    readonly SemaphoreSlim _slots = new(MaxInFlight, MaxInFlight);
    readonly DsiRequestIds _ids;
    readonly Thread _reader;
    Timer? _tickle;
    long _lastReceived = Environment.TickCount64;
    volatile bool _dead;
    Exception? _deadReason;

    /// <summary>Largest request the server accepts (bounds the size of one write).</summary>
    public int ServerQuantum { get; private set; } = 0x20000;
    public bool Dead => _dead;
    /// <summary>Identifies the server-side session; a connection that took over an old session inherits its value.</summary>
    public object Session = new();
    /// <summary>Called (on the reader thread) with the 16-bit code of a server attention message.</summary>
    public Action<ushort>? Attention;

    public DsiConnection(string host, int port, DsiRequestIds ids)
    {
        _ids = ids;
        _sock = Connect(host, port);
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "dsi-reader" };
        _reader.Start();

        // The keep-alive watchdog does not exist yet, so this first exchange gets its own short timeout.
        var r = End(Send(CmdOpenSession, default, default, 0, null), "DSIOpenSession", 15_000);
        var o = r.Span;
        while (o.Length >= 2)
        {
            int type = o[0], len = o[1];
            if (o.Length < 2 + len) break;
            if (type == 0 && len == 4) ServerQuantum = (int)BinaryPrimitives.ReadUInt32BigEndian(o.Slice(2));
            o = o.Slice(2 + len);
        }
        _tickle = new Timer(_ => OnTimer(), null, TickleMs, TickleMs);
    }

    static Socket Connect(string host, int port)
    {
        try
        {
            var s = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            s.ReceiveBufferSize = 1 << 20;
            s.SendBufferSize = 1 << 20;
            s.SendTimeout = 60_000; // a send that cannot make progress for a minute means the peer is gone
            var ar = s.BeginConnect(host, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(10_000)) { s.Close(); throw new TimeoutException("connect timed out"); }
            s.EndConnect(ar);
            return s;
        }
        catch (Exception e)
        {
            throw new AfpConnectionException($"cannot connect to {host}:{port}: {e.Message}", e);
        }
    }

    /// <summary>
    /// Asks a server to describe itself (name, model, capabilities) without logging in.
    /// Uses its own short connection, as the protocol prescribes. Returns the raw server information block.
    /// </summary>
    public static byte[] GetStatus(string host, int port, int timeoutMs = 5000)
    {
        using var s = Connect(host, port);
        s.ReceiveTimeout = timeoutMs;
        s.SendTimeout = timeoutMs;
        try
        {
            var head = new byte[16];
            head[1] = CmdGetStatus;
            head[3] = 1;
            s.Send(head);
            ReadExact(s, head, 16);
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(8));
            if (len < 0 || len > 1 << 20) throw new IOException("bad status length");
            var data = new byte[len];
            ReadExact(s, data, len);
            return data;
        }
        catch (Exception e) when (e is not AfpConnectionException)
        {
            throw new AfpConnectionException("no status reply: " + e.Message, e);
        }
    }

    /// <summary>Sends an AFP command and returns immediately; pair with <see cref="End"/>.</summary>
    public DsiPending BeginCommand(ReadOnlySpan<byte> afp, byte[]? dest = null) => Send(CmdCommand, afp, default, 0, dest);

    /// <summary>Sends an AFP write command: <paramref name="afp"/> is the command header, <paramref name="data"/> the file bytes.</summary>
    public DsiPending BeginWrite(ReadOnlySpan<byte> afp, ReadOnlySpan<byte> data) => Send(CmdWrite, afp, data, (uint)afp.Length, null);

    public DsiPending Command(ReadOnlySpan<byte> afp, string what) => End(BeginCommand(afp), what);

    /// <summary>
    /// Sends a command whose answer is not essential and waits only briefly for it.
    /// Returns true when the server answered without an error; never throws.
    /// </summary>
    public bool TryCommand(ReadOnlySpan<byte> afp, int waitMs)
    {
        try
        {
            var p = BeginCommand(afp);
            return p.Done.Wait(waitMs) && p.Failure == null && p.Error == 0;
        }
        catch (AfpConnectionException) { return false; }
    }

    /// <summary>Waits for the reply. Throws only for transport failures; the AFP error code is left in the result.</summary>
    public DsiPending End(DsiPending p, string what, int timeoutMs = ReplyTimeoutMs)
    {
        if (!p.Done.Wait(timeoutMs))
        {
            Fail(new TimeoutException($"no reply to {what} in {timeoutMs / 1000}s"));
            p.Done.Wait(5000);
        }
        if (p.Failure != null) throw new AfpConnectionException($"connection lost during {what}: {p.Failure.Message}", p.Failure);
        // Never hand back a request that did not actually complete: it would look like an empty success.
        if (!p.Done.IsSet) throw new AfpConnectionException($"no reply to {what}");
        return p;
    }

    DsiPending Send(byte cmd, ReadOnlySpan<byte> afp, ReadOnlySpan<byte> data, uint dataOffset, byte[]? dest)
    {
        var p = new DsiPending { Dest = dest };
        var head = new byte[16 + afp.Length];
        head[1] = cmd;
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(4), dataOffset);
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(8), (uint)(afp.Length + data.Length));
        afp.CopyTo(head.AsSpan(16));

        while (!_slots.Wait(1000))
            if (_dead) throw new AfpConnectionException("connection is closed", _deadReason);
        bool queued = false;
        try
        {
            lock (_sendLock)
            {
                ushort id;
                do { id = _ids.Next(); } while (!_pending.TryAdd(id, p));
                queued = true;
                if (_dead)
                {
                    if (_pending.TryRemove(id, out _)) queued = false;
                    throw new AfpConnectionException("connection is closed", _deadReason);
                }
                BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(2), id);
                try
                {
                    SendAll(head);
                    if (data.Length > 0) SendAll(data);
                }
                catch (Exception e)
                {
                    Fail(e);
                    throw new AfpConnectionException("send failed: " + e.Message, e);
                }
            }
        }
        finally
        {
            // Once queued, the slot is returned by whoever completes the request (reader thread or Fail).
            if (!queued) _slots.Release();
        }
        return p;
    }

    void Complete(DsiPending p)
    {
        p.Done.Set();
        _slots.Release();
    }

    void OnTimer()
    {
        if (_dead) return;
        long silent = Environment.TickCount64 - Interlocked.Read(ref _lastReceived);
        if (silent > IdleDeadMs || (silent > BusyDeadMs && !_pending.IsEmpty))
        {
            Fail(new TimeoutException($"server silent for {silent / 1000}s"));
            return;
        }
        var head = new byte[16];
        head[1] = CmdTickle;
        try
        {
            lock (_sendLock)
            {
                BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(2), _ids.Next());
                SendAll(head);
            }
        }
        catch (Exception e) { Fail(e); }
    }

    void SendAll(ReadOnlySpan<byte> b)
    {
        while (b.Length > 0)
        {
            int n = _sock.Send(b);
            if (n <= 0) throw new IOException("socket closed");
            b = b.Slice(n);
        }
    }

    static void ReadExact(Socket s, byte[] buf, int count)
    {
        int got = 0;
        while (got < count)
        {
            int n = s.Receive(buf, got, count - got, SocketFlags.None);
            if (n <= 0) throw new IOException("server closed the connection");
            got += n;
        }
    }

    void ReadPayload(byte[] buf, int count)
    {
        int got = 0;
        while (got < count)
        {
            int n = _sock.Receive(buf, got, count - got, SocketFlags.None);
            if (n <= 0) throw new IOException("server closed the connection");
            got += n;
            Interlocked.Exchange(ref _lastReceived, Environment.TickCount64);
        }
    }

    void ReadLoop()
    {
        var head = new byte[16];
        try
        {
            while (!_dead)
            {
                ReadPayload(head, 16);
                byte flags = head[0], cmd = head[1];
                ushort id = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(2));
                int err = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(4));
                int len = (int)BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(8));
                if (len < 0 || len > 64 << 20) throw new IOException("bad DSI length " + len);

                // Replies are told apart from the server's own requests by the flag byte first: the server
                // numbers its requests independently, so its ids can coincide with ours.
                if (flags == 1 && _pending.TryRemove(id, out var p))
                {
                    var buf = p.Dest != null && p.Dest.Length >= len ? p.Dest : (len == 0 ? Array.Empty<byte>() : new byte[len]);
                    // The request is no longer in the table, so Fail() cannot reach it: fail it here if the
                    // connection dies in the middle of its reply.
                    try { ReadPayload(buf, len); }
                    catch (Exception e) { p.Failure = e; Complete(p); throw; }
                    p.Data = buf; p.Length = len; p.Error = err;
                    Complete(p);
                    continue;
                }
                var extra = len > 0 ? new byte[len] : Array.Empty<byte>();
                if (len > 0) ReadPayload(extra, len);
                if (flags != 0) continue; // a reply nobody waits for any more
                if (cmd == CmdCloseSession) throw new IOException("server closed the session");
                if (cmd == CmdAttention && len >= 2)
                {
                    ushort code = BinaryPrimitives.ReadUInt16BigEndian(extra);
                    try { Attention?.Invoke(code); } catch { }
                }
            }
        }
        catch (Exception e) { Fail(e); }
    }

    /// <summary>Declares the connection dead: closes the socket and fails every waiting request.</summary>
    public void Fail(Exception e)
    {
        lock (_pending)
        {
            if (_dead) return;
            _deadReason = e;
            _dead = true;
        }
        try { _sock.Close(); } catch { }
        try { _tickle?.Dispose(); } catch { }
        foreach (var kv in _pending)
            if (_pending.TryRemove(kv.Key, out var p)) { p.Failure = e; Complete(p); }
    }

    /// <summary>Orderly end of the DSI session (the AFP logout, if any, must already have been sent).</summary>
    public void Dispose()
    {
        if (!_dead)
        {
            try
            {
                var head = new byte[16];
                head[1] = CmdCloseSession;
                lock (_sendLock)
                {
                    BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(2), _ids.Next());
                    SendAll(head);
                }
            }
            catch { }
        }
        Fail(new ObjectDisposedException(nameof(DsiConnection)));
    }
}
