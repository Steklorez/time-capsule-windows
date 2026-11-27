using System.Numerics;

namespace TimeCapsuleDrive
{
    /// <summary>Unsigned big-endian numbers, as the login exchange sends them.</summary>
    static class Big
    {
        public static BigInteger Read(ReadOnlySpan<byte> bigEndian)
        {
#if NETFRAMEWORK
            // The constructor wants little-endian bytes; the extra zero at the top keeps the number positive.
            var le = new byte[bigEndian.Length + 1];
            for (int i = 0; i < bigEndian.Length; i++) le[i] = bigEndian[bigEndian.Length - 1 - i];
            return new BigInteger(le);
#else
            return new BigInteger(bigEndian, true, true);
#endif
        }

        public static byte[] Bytes(BigInteger v)
        {
#if NETFRAMEWORK
            var le = v.ToByteArray();
            int n = le.Length;
            while (n > 1 && le[n - 1] == 0) n--;
            var be = new byte[n];
            for (int i = 0; i < n; i++) be[i] = le[n - 1 - i];
            return be;
#else
            return v.ToByteArray(true, true);
#endif
        }
    }

    static class CommandLine
    {
        /// <summary>Joins arguments into one command line the way Windows programs split it back.</summary>
        public static string Join(IEnumerable<string> args)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var a in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) { sb.Append(a); continue; }
                sb.Append('"');
                int slashes = 0;
                foreach (char c in a)
                {
                    if (c == '\\') { slashes++; continue; }
                    if (c == '"') { sb.Append('\\', slashes * 2 + 1); sb.Append('"'); }
                    else { sb.Append('\\', slashes); sb.Append(c); }
                    slashes = 0;
                }
                sb.Append('\\', slashes * 2);
                sb.Append('"');
            }
            return sb.ToString();
        }
    }
}

#if NETFRAMEWORK
// Everything below exists only in the build for .NET Framework 4.8 (the one that also runs on Windows 7 and 8).
// It gives the older runtime the handful of newer library calls the program uses, so the rest of the source
// is the same for both builds.

namespace System.Windows.Forms
{
    enum HighDpiMode { SystemAware }
}

namespace System.Threading
{
    static class EventWaitHandleAcl
    {
        public static EventWaitHandle Create(bool initialState, EventResetMode mode, string name, out bool createdNew,
            System.Security.AccessControl.EventWaitHandleSecurity security) =>
            new EventWaitHandle(initialState, mode, name, out createdNew, security);
    }
}

namespace TimeCapsuleDrive
{
    using System.Diagnostics;
    using System.Net;
    using System.Net.Http;
    using System.Net.Sockets;
    using System.Runtime.InteropServices;
    using System.Security.AccessControl;
    using System.Security.Cryptography;
    using System.Text;

    /// <summary>One Unicode character (a surrogate pair counts as one).</summary>
    readonly struct CodePoint
    {
        public readonly int Value;
        public CodePoint(int value) { Value = value; }
        public override string ToString() => char.ConvertFromUtf32(Value);
    }

    /// <summary>
    /// The libraries the program needs travel inside the program file and are loaded straight from memory,
    /// so nothing is unpacked to a folder another user could write to.
    /// </summary>
    static class Libraries
    {
        static readonly Dictionary<string, System.Reflection.Assembly> Loaded = new(StringComparer.OrdinalIgnoreCase);
        static bool _hooked;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int MessageBoxW(IntPtr owner, string text, string caption, int type);

        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            RequireFramework48();
            AppDomain.CurrentDomain.AssemblyResolve += (_, e) => Resolve(new System.Reflection.AssemblyName(e.Name).Name ?? "");
            // Windows 7 and 8 do not offer TLS 1.2 by default, and GitHub accepts nothing older.
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch (Exception) { }
        }

        static System.Reflection.Assembly? Resolve(string name)
        {
            lock (Loaded)
            {
                if (Loaded.TryGetValue(name, out var known)) return known;
                System.Reflection.Assembly? a = null;
                if (name == "winfsp-msil") a = System.Reflection.Assembly.LoadFrom(Install.WinFspLibrary.Locate());
                else
                {
                    using var s = typeof(Libraries).Assembly.GetManifestResourceStream("lib/" + name + ".dll");
                    if (s != null)
                    {
                        var data = new byte[s.Length];
                        int got = 0;
                        while (got < data.Length) { int n = s.Read(data, got, data.Length - got); if (n <= 0) break; got += n; }
                        a = System.Reflection.Assembly.Load(data);
                    }
                }
                if (a != null) Loaded[name] = a;
                return a;
            }
        }

        /// <summary>An older .NET Framework 4.x would start the program and then fail in odd places; say it plainly instead.</summary>
        static void RequireFramework48()
        {
            int release = 0;
            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full");
                release = k?.GetValue("Release") is int r ? r : 0;
            }
            catch (Exception) { return; }
            if (release >= 528040) return;
            bool ru = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";
            MessageBoxW(IntPtr.Zero, ru
                ? "Р”Р»СЏ Time Capsule Drive РЅСѓР¶РµРЅ Microsoft .NET Framework 4.8.\n\nРЈСЃС‚Р°РЅРѕРІРёС‚Рµ РµРіРѕ СЃ СЃР°Р№С‚Р° Microsoft Рё Р·Р°РїСѓСЃС‚РёС‚Рµ РїСЂРѕРіСЂР°РјРјСѓ СЃРЅРѕРІР°:\nhttps://dotnet.microsoft.com/download/dotnet-framework/net48"
                : "Time Capsule Drive needs Microsoft .NET Framework 4.8.\n\nInstall it from Microsoft and start the program again:\nhttps://dotnet.microsoft.com/download/dotnet-framework/net48",
                "Time Capsule Drive", 0x30);
            Environment.Exit(3);
        }
    }

    static class Compat
    {
        [DllImport("kernel32.dll")] static extern ulong GetTickCount64();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool MoveFileEx(string from, string to, int flags);
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        static readonly string Exe = Process.GetCurrentProcess().MainModule!.FileName;
        static readonly int Pid = Process.GetCurrentProcess().Id;

        extension(Environment)
        {
            public static long TickCount64 => (long)GetTickCount64();
            public static string ProcessPath => Exe;
            public static int ProcessId => Pid;
        }

        extension(Math)
        {
            public static long Clamp(long value, long min, long max) => value < min ? min : value > max ? max : value;
            public static double Clamp(double value, double min, double max) => value < min ? min : value > max ? max : value;
        }

        extension(Convert)
        {
            public static string ToHexString(byte[] data) => ToHexString(data, 0, data.Length);

            public static string ToHexString(byte[] data, int offset, int length)
            {
                var sb = new StringBuilder(length * 2);
                for (int i = 0; i < length; i++) sb.Append(data[offset + i].ToString("X2"));
                return sb.ToString();
            }
        }

        extension(SHA256)
        {
            public static byte[] HashData(byte[] data) { using var h = SHA256.Create(); return h.ComputeHash(data); }
        }

        extension(RandomNumberGenerator)
        {
            public static byte[] GetBytes(int count)
            {
                var b = new byte[count];
                using var r = RandomNumberGenerator.Create();
                r.GetBytes(b);
                return b;
            }
        }

        extension(System.IO.File)
        {
            public static void Move(string from, string to, bool overwrite)
            {
                if (!overwrite) { System.IO.File.Move(from, to); return; }
                // 1 = replace existing, 2 = allow a copy when the folders are on different disks
                if (!MoveFileEx(Path.GetFullPath(from), Path.GetFullPath(to), 1 | 2))
                    throw new IOException("cannot move " + from + " to " + to, new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            }
        }

        extension(System.Windows.Forms.Application)
        {
            public static void SetHighDpiMode(System.Windows.Forms.HighDpiMode mode) { try { SetProcessDPIAware(); } catch (Exception) { } }
        }

        public static unsafe string GetString(this Encoding e, ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length == 0) return "";
            fixed (byte* p = bytes) return e.GetString(p, bytes.Length);
        }

        public static IEnumerable<CodePoint> EnumerateRunes(this string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                    yield return new CodePoint(char.ConvertToUtf32(c, s[++i]));
                else
                    yield return new CodePoint(char.IsSurrogate(c) ? 0xFFFD : c);
            }
        }

        public static string[] Split(this string s, char separator, StringSplitOptions options) => s.Split(new[] { separator }, options);
        public static bool EndsWith(this string s, char c) => s.Length > 0 && s[s.Length - 1] == c;
        public static bool StartsWith(this string s, char c) => s.Length > 0 && s[0] == c;

        public static void Write(this Stream s, byte[] data) => s.Write(data, 0, data.Length);

        public static void ReadExactly(this Stream s, byte[] data)
        {
            int got = 0;
            while (got < data.Length)
            {
                int n = s.Read(data, got, data.Length - got);
                if (n <= 0) throw new EndOfStreamException();
                got += n;
            }
        }

        public static V? GetValueOrDefault<K, V>(this Dictionary<K, V> d, K key) where K : notnull => d.TryGetValue(key, out var v) ? v : default;
        public static V GetValueOrDefault<K, V>(this Dictionary<K, V> d, K key, V fallback) where K : notnull => d.TryGetValue(key, out var v) ? v : fallback;

        public static int Send(this Socket s, ReadOnlySpan<byte> data)
        {
            var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(data.Length);
            try
            {
                data.CopyTo(buf);
                return s.Send(buf, 0, data.Length, SocketFlags.None);
            }
            finally { System.Buffers.ArrayPool<byte>.Shared.Return(buf); }
        }

        public static async Task ConnectAsync(this Socket s, IPAddress address, int port, CancellationToken cancel)
        {
            // Closing the socket is the only way to abandon a connection attempt here.
            using (cancel.Register(() => { try { s.Close(); } catch (Exception) { } }))
            {
                try { await Task.Factory.FromAsync(s.BeginConnect, s.EndConnect, address, port, null).ConfigureAwait(false); }
                catch (Exception) when (cancel.IsCancellationRequested) { throw new OperationCanceledException(cancel); }
            }
        }

        public static FileStream Create(this FileInfo f, FileMode mode, FileSystemRights rights, FileShare share, int bufferSize,
            FileOptions options, FileSecurity security) =>
            new FileStream(f.FullName, mode, rights, share, bufferSize, options, security);

        public static HttpResponseMessage Send(this HttpClient http, HttpRequestMessage request) =>
            Task.Run(() => http.SendAsync(request)).GetAwaiter().GetResult();
    }

    static class CompatMd5
    {
        extension(System.Security.Cryptography.MD5)
        {
            public static byte[] HashData(byte[] data) { using var h = System.Security.Cryptography.MD5.Create(); return h.ComputeHash(data); }
        }
    }
}
#endif
