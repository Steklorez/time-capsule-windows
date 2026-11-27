using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Fsp;
using TimeCapsuleDrive.Afp;
using TimeCapsuleDrive.Fs;
using TimeCapsuleDrive.Install;

namespace TimeCapsuleDrive;

static class Program
{
    static readonly object LogLock = new();
    static string? _logFile;

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processId);

    static void Log(string msg)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}";
        lock (LogLock)
        {
            try { Console.WriteLine(line); } catch (Exception) { }
            if (_logFile == null) return;
            try
            {
                var fi = new FileInfo(_logFile);
                if (fi.Exists && fi.Length > 2 << 20) File.Move(_logFile, _logFile + ".old", true);
                File.AppendAllText(_logFile, line + Environment.NewLine);
            }
            catch (Exception) { }
        }
    }

    [STAThread]
    static int Main(string[] args)
    {
        var rest = new List<string>();
        string? configPath = null, share = null, logPath = null;
        bool network = false, system = false, silent = false;
        int chaos = 0;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--config" && i + 1 < args.Length) configPath = args[++i];
            else if (args[i] == "--share" && i + 1 < args.Length) share = args[++i];
            else if (args[i] == "--log" && i + 1 < args.Length) logPath = args[++i];
            else if (args[i] == "--chaos" && i + 1 < args.Length) chaos = int.Parse(args[++i]);
            else if (args[i] == "--taken" && i + 1 < args.Length) Installer.UserLetters = args[++i].ToUpperInvariant().Where(char.IsLetter).ToHashSet();
            else if (args[i] == "--net") network = true;
            else if (args[i] == "--system") system = true;
            else if (args[i] == "--silent") silent = true;
            else rest.Add(args[i]);
        }
        string cmd = rest.Count > 0 ? rest[0].ToLowerInvariant() : "setup";

        // The WinFsp .NET library is not part of the program file itself; see WinFspLibrary.
#if NETFRAMEWORK
        Libraries.Hook();
#else
        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (context, name) =>
            name.Name == "winfsp-msil" ? context.LoadFromAssemblyPath(WinFspLibrary.Locate()) : null;
#endif

        // Whatever goes wrong unexpectedly must end up in the log: the background drive has no screen.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("fatal: " + e.ExceptionObject);

        // The program is a windowed application (so that the setup window opens without a console);
        // for command-line use it borrows the console it was started from.
        if (cmd != "setup" && cmd != "update-check") AttachConsole(-1);
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (Exception) { }

        try
        {
            switch (cmd)
            {
                case "help" or "-h" or "--help" or "/?": Usage(); return 0;
                case "setup": return Setup(preview: rest.Contains("--preview"));
                case "install": return InstallSilently(configPath, share, logPath);
                case "uninstall": return Uninstall(silent, logPath);
                case "stop": return Stop();
                case "update-check":
                {
                    int at = rest.IndexOf("--as-version");
                    return NewVersion.Check(rest.Contains("--force"), at >= 0 && at + 1 < rest.Count ? rest[at + 1] : null);
                }
                case "winfsp-check":
                    Console.WriteLine(Installer.BundledWinFsp() == null ? "WinFsp installer: not included in this build (will be downloaded)"
                        : Installer.BundledWinFspIsValid() ? "WinFsp installer: included and verified" : "WinFsp installer: included but NOT the expected file");
                    Console.WriteLine("WinFsp on this computer: " + (Installer.WinFspInstalled() ? "installed" : "not installed"));
                    Console.WriteLine("WinFsp .NET library: " + WinFspLibrary.Bytes().Length + " bytes inside, loads from " + WinFspLibrary.Locate());
                    return 0;
            }

            var cfg = Config.Load(configPath);
            if (cmd == "mount")
            {
                _logFile = Path.Combine(Path.GetDirectoryName(cfg.File)!, "timecapsule.log");
                return Mount(cfg, rest.Count > 1 ? rest[1] : cfg.Drive, network, system, chaos);
            }
            using var afp = new AfpClient(cfg.Host, cfg.Port, cfg.Volume, cfg.Login, cfg.Password, null, Log);
            switch (cmd)
            {
                case "ls": return Ls(afp, rest.Count > 1 ? rest[1] : "\\");
                case "selftest": return SelfTest.Run(cfg, Log, rest.Contains("--zombie"));
                case "probe":
                    Console.WriteLine(AfpClient.Probe(cfg.Host, cfg.Port));
                    Console.WriteLine("volumes: " + string.Join(", ", AfpClient.ListVolumes(cfg.Host, cfg.Port, cfg.Login, cfg.Password)));
                    return 0;
                case "idletest": return IdleTest(afp, rest[1], int.Parse(rest[2]));
                case "mkdir": afp.CreateDir(rest[1]); return 0;
                case "touch": afp.CreateFile(rest[1]); return 0;
                case "rm": afp.Delete(rest[1]); return 0;
                case "bench": return Bench(afp, rest[1], rest.Count > 2 ? int.Parse(rest[2]) : 400, rest.Count > 3 ? int.Parse(rest[3]) : 4);
                case "benchw": return BenchWrite(afp, rest[1], rest.Count > 2 ? int.Parse(rest[2]) : 200, rest.Count > 3 ? int.Parse(rest[3]) : 1, rest.Count > 4 ? int.Parse(rest[4]) : 0);
                default: Usage(); return 2;
            }
        }
        catch (Exception e) when (e is AfpException or AfpConnectionException or FileNotFoundException or InvalidDataException or InvalidOperationException)
        {
            Log("failed: " + e.Message);
            return 1;
        }
    }

    static void Usage() => Console.WriteLine(
        """
        TimeCapsuleDrive - mounts an Apple Time Capsule (AFP) as a Windows drive.

          TimeCapsuleDrive                      open the setup window (install, change settings, uninstall)
          TimeCapsuleDrive install --config F   install without a window, settings taken from file F
                                   [--share N]  ...and share the drive on the network under the name N
          TimeCapsuleDrive uninstall [--silent] remove the program from this computer
          TimeCapsuleDrive stop                 stop the running drive (it starts again with Windows)

        Tools (use the installed settings, or --config <file>):
          mount [T:] [--system] [--net]         mount in the foreground; --system = visible to all users
          ls [\folder]                          list a folder
          probe                                 show what the server says about itself and its disks
          selftest                              check write protection, durable flush and reconnect on the server
          bench \file [MB] [N]                  read speed, N requests in flight
          benchw \file [MB] [N] [KB]            write speed (creates and deletes the file)
        """);

    // ---------------------------------------------------------------- setup / install / uninstall

    /// <summary>Starts this program again with administrator rights and waits for it.</summary>
    static int Elevate(string[] args)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        psi.Arguments = CommandLine.Join(args);
        try
        {
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception) { return 1223; } // the user declined
    }

    /// <param name="preview">Show the window without asking for administrator rights (installing will then be refused).</param>
    static int Setup(bool preview)
    {
        if (!preview && !Installer.IsElevated()) return Elevate(new[] { "setup", "--taken", Installer.LettersTakenInThisSession() });
        if (preview) Installer.UserLetters = Installer.LettersTakenInThisSession().Where(char.IsLetter).ToHashSet();
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.SystemAware);
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        System.Windows.Forms.Application.Run(new SetupForm());
        return 0;
    }

    static int InstallSilently(string? configPath, string? share, string? logPath)
    {
        // Resolve the settings file before elevating: the elevated copy may run in another folder.
        var cfg = Config.Load(configPath);
        if (!Installer.IsElevated())
        {
            var a = new List<string> { "install", "--config", cfg.File, "--log", logPath ?? Path.Combine(Path.GetTempPath(), "TimeCapsuleDrive-install.log"),
                "--taken", Installer.LettersTakenInThisSession() };
            if (share != null) { a.Add("--share"); a.Add(share); }
            return Elevate(a.ToArray());
        }
        _logFile = logPath;
        if (_logFile != null) try { File.Delete(_logFile); } catch (Exception) { }
        if (share != null) cfg.Share = share;
        try
        {
            Installer.Install(cfg, Log);
            return 0;
        }
        catch (Exception e)
        {
            Log("install failed: " + e.Message);
            return 1;
        }
    }

    static int Uninstall(bool silent, string? logPath)
    {
        if (!silent && System.Windows.Forms.MessageBox.Show(T.ConfirmRemove, T.Title, System.Windows.Forms.MessageBoxButtons.OKCancel,
                System.Windows.Forms.MessageBoxIcon.Question) != System.Windows.Forms.DialogResult.OK) return 1;
        if (!Installer.IsElevated())
            return Elevate(new[] { "uninstall", "--silent", "--log", logPath ?? Path.Combine(Path.GetTempPath(), "TimeCapsuleDrive-uninstall.log") });
        _logFile = logPath;
        try
        {
            Installer.Uninstall(Log);
            return 0;
        }
        catch (Exception e)
        {
            Log("uninstall failed: " + e.Message);
            return 1;
        }
    }

    static int Stop()
    {
        if (!EventWaitHandle.TryOpenExisting(Installer.StopEventName, out var ev))
        {
            Console.WriteLine("the drive is not running (or this needs administrator rights)");
            return 1;
        }
        using (ev) ev.Set();
        Console.WriteLine("stop requested");
        return 0;
    }

    // ---------------------------------------------------------------- the drive

    /// <param name="chaos">Testing aid: cut the connection every this many seconds (0 = never).</param>
    static int Mount(Config cfg, string drive, bool network, bool system, int chaos = 0)
    {
        // The system-wide drive announces itself under the installation's name, so that a restart cleans up after a
        // crash. Any other instance gets a name of its own: two programs sharing one name would keep throwing each
        // other's session out.
        var afp = new AfpClient(cfg.Host, cfg.Port, cfg.Volume, cfg.Login, cfg.Password, system ? cfg.ClientId : cfg.ClientId + "-" + Environment.ProcessId, Log);
        bool installed = string.Equals(cfg.File, Config.InstalledPath, StringComparison.OrdinalIgnoreCase);
        if (system && installed) Status.Enable(cfg.Host, drive.TrimEnd('\\'));
        Status.Report(Status.Connecting);
        afp.ConnectionChanged = (up, code, message) =>
        {
            if (up && afp.Host != cfg.Host)
            {
                // The device was found at a new address: keep using it after a restart too.
                cfg.Host = afp.Host;
                Status.SetHost(cfg.Host);
                if (installed)
                    try { cfg.SaveInstalled(); } catch (Exception e) { Log("could not save the new address: " + e.Message); }
            }
            if (up) Status.Report(Status.Connected);
            else if (code is AfpError.UserNotAuth or AfpError.AccessDenied or AfpError.ParamErr) Status.Report(Status.WrongPassword, message);
            else if (code != 0) Status.Report(Status.Error, message);
            else Status.Report(Status.Unreachable, message);
        };
        if (cfg.ServerName.Length > 0)
            afp.Relocate = () => Discovery.FindByName(cfg.ServerName, cfg.Model);

        // Connect and log in now, so that a wrong password is reported before the drive appears.
        // At boot the network may not be up yet, so connection failures are retried.
        for (int attempt = 1; ; attempt++)
        {
            try { afp.Stat("\\"); break; }
            catch (AfpConnectionException e)
            {
                if (attempt == 1 || attempt % 30 == 0) Log("server not reachable yet, will keep trying: " + e.Message);
                Thread.Sleep(10_000);
            }
        }

        var fs = new CapsuleFs(afp, cfg.Volume, Log);
        var host = new FileSystemHost(fs)
        {
            SectorSize = 4096,
            SectorsPerAllocationUnit = 1,
            MaxComponentLength = 255,
            FileInfoTimeout = 1000,
            CaseSensitiveSearch = false,
            CasePreservedNames = true,
            UnicodeOnDisk = true,
            PersistentAcls = false,
            PostCleanupWhenModifiedOnly = false, // the file system finishes a file as soon as its last handle is closed
            VolumeCreationTime = (ulong)DateTime.UtcNow.ToFileTimeUtc(),
            VolumeSerialNumber = 0x54434150,
            FileSystemName = Installer.FileSystemName,
        };
        if (network) host.Prefix = $"\\{cfg.Host}\\{cfg.Volume}";
        // "\\.\T:" asks WinFsp to mount through the Mount Manager: the drive then exists for every user and
        // for Windows services (file sharing), not only for the account that started this process.
        string mountPoint = system ? "\\\\.\\" + drive.TrimEnd('\\') : drive;
        int status = host.Mount(mountPoint, null, false, 0);
        if (status < 0)
        {
            Log($"cannot mount {mountPoint}: status 0x{status:X8}");
            Status.Report(Status.Error, $"cannot mount {mountPoint} (0x{status:X8})");
            afp.Dispose();
            return 1;
        }
        Log($"mounted {cfg.Host} '{cfg.Volume}' as {host.MountPoint()}");

        Power.Watch(onSuspend: () => { fs.FlushAll(); afp.Sleep(); }, onResume: afp.Wake, Log);

        if (system && cfg.Share.Length > 0)
        {
            // Sharing is redone after every mount: at boot the Windows sharing service starts before this drive
            // exists and silently drops a share whose drive is missing.
            try
            {
                Installer.EnsureShare(cfg.Share, drive.TrimEnd('\\') + "\\");
                Log($"shared as \\\\{Environment.MachineName}\\{cfg.Share}");
            }
            catch (Exception e) { Log("could not share the drive: " + e.Message); }
        }
        using var chaosTimer = chaos > 0 ? new Timer(_ => afp.DebugCutConnection(), null, chaos * 1000, chaos * 1000) : null;

        var ctrlC = new ManualResetEvent(false);
        Console.CancelKeyPress += (_, a) => { a.Cancel = true; ctrlC.Set(); };
        using var stop = CreateStopEvent();
        WaitHandle.WaitAny(stop != null ? new WaitHandle[] { ctrlC, stop } : new WaitHandle[] { ctrlC });

        // Orderly shutdown: take the drive away, close every file on the server, log out.
        host.Unmount();
        fs.CloseAll();
        afp.Dispose();
        Log("unmounted");
        return 0;
    }

    /// <summary>A named event that lets an administrator (the setup program) ask the running drive to stop cleanly.</summary>
    static EventWaitHandle? CreateStopEvent()
    {
        try
        {
            var sec = new EventWaitHandleSecurity();
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                sec.AddAccessRule(new EventWaitHandleAccessRule(new SecurityIdentifier(sid, null), EventWaitHandleRights.FullControl, AccessControlType.Allow));
            return EventWaitHandleAcl.Create(false, EventResetMode.ManualReset, Installer.StopEventName, out _, sec);
        }
        catch (Exception) { return null; } // not allowed to create machine-wide objects: Ctrl+C still works
    }

    // ---------------------------------------------------------------- tools

    static int Ls(AfpClient afp, string path)
    {
        foreach (var e in afp.List(path).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"{(e.IsDir ? "<DIR>" : e.Size.ToString()),14}  {e.Name}");
        var (free, total) = afp.VolumeSpace();
        Console.WriteLine($"free {free / 1e9:F1} GB of {total / 1e9:F1} GB");
        return 0;
    }

    /// <summary>
    /// Stays connected but silent for a while, then times a read from the disk.
    /// A long first answer means the server's disk had gone to sleep although a client was connected.
    /// </summary>
    static int IdleTest(AfpClient afp, string file, int minutes)
    {
        var buf = new byte[65536];
        long size = (long)afp.Stat(file)!.Size;
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} connected, staying idle for {minutes} min");
        Thread.Sleep(minutes * 60_000);
        var sw = Stopwatch.StartNew();
        var fork = afp.OpenFork(file, false);
        afp.EndRead(afp.BeginRead(fork, Math.Max(0, size - (500 << 20)), buf.Length, buf));
        sw.Stop();
        afp.CloseFork(fork);
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} first read after {minutes} min idle took {sw.Elapsed.TotalSeconds:F1} s");
        return 0;
    }

    static int Bench(AfpClient afp, string path, int megabytes, int window)
    {
        var e = afp.Stat(path);
        if (e == null || e.IsDir) { Console.Error.WriteLine("no such file: " + path); return 1; }
        const int chunk = 256 * 1024;
        long limit = Math.Min((long)e.Size, (long)megabytes << 20), next = 0, got = 0;
        var fork = afp.OpenFork(path, false);
        var q = new Queue<AfpClient.PendingRead>();
        var sw = Stopwatch.StartNew();
        while (got < limit)
        {
            while (q.Count < window && next < limit)
            {
                q.Enqueue(afp.BeginRead(fork, next, chunk, new byte[chunk]));
                next += chunk;
            }
            int n = afp.EndRead(q.Dequeue());
            if (n == 0) break;
            got += n;
        }
        sw.Stop();
        afp.CloseFork(fork);
        Console.WriteLine($"read {got / 1048576.0:F0} MB in {sw.Elapsed.TotalSeconds:F1}s = {got / 1048576.0 / sw.Elapsed.TotalSeconds:F1} MB/s (in flight: {window})");
        return 0;
    }

    static int BenchWrite(AfpClient afp, string path, int megabytes, int window, int chunkKb)
    {
        afp.Stat("\\");
        int chunk = chunkKb > 0 ? chunkKb * 1024 : afp.MaxWrite;
        var data = new byte[chunk];
        new Random(1).NextBytes(data);
        afp.CreateFile(path);
        var fork = afp.OpenFork(path, true);
        long limit = (long)megabytes << 20, next = 0;
        var q = new Queue<AfpClient.PendingWrite>();
        var sw = Stopwatch.StartNew();
        while (next < limit || q.Count > 0)
        {
            while (q.Count < window && next < limit)
            {
                q.Enqueue(afp.BeginWrite(fork, next, data));
                next += chunk;
            }
            afp.EndWrite(q.Dequeue());
        }
        afp.Sync(fork);
        sw.Stop();
        afp.CloseFork(fork);
        afp.Delete(path);
        Console.WriteLine($"wrote {next / 1048576.0:F0} MB in {sw.Elapsed.TotalSeconds:F1}s = {next / 1048576.0 / sw.Elapsed.TotalSeconds:F1} MB/s (chunk {chunk / 1024} KB, in flight: {window})");
        return 0;
    }
}
