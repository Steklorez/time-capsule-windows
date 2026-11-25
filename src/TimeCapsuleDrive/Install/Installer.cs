using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TimeCapsuleDrive.Afp;

namespace TimeCapsuleDrive.Install;

/// <summary>
/// Installs the program on this computer (and removes it again). Needs administrator rights.
///
/// What an installation consists of:
///   - WinFsp, the file-system driver the drive is built on (installed silently if missing);
///   - the program itself in %ProgramFiles%\TimeCapsuleDrive;
///   - settings in %ProgramData%\TimeCapsuleDrive (password encrypted, administrators only);
///   - a scheduled task that mounts the drive for the whole computer when Windows starts;
///   - optionally a network share of that drive plus a firewall rule for it;
///   - an entry in "Apps and features" for uninstalling.
/// </summary>
static class Installer
{
    public const string TaskName = "TimeCapsuleDrive";
    public const string FirewallRule = "TimeCapsuleDrive file sharing (SMB-In)";
    public const string StopEventName = @"Global\TimeCapsuleDrive.Stop";
    public const string FileSystemName = "AFP";
    const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\TimeCapsuleDrive";
    public const string Version = "1.3.1";

    // The official WinFsp release this program was built and tested with.
    const string WinFspUrl = "https://github.com/winfsp/winfsp/releases/download/v2.2/winfsp-2.2.26323.msi";
    const string WinFspSha256 = "36ce01f9eedbbe6842b7fb4bedc79d7d4f71983e8aceb6877b46a07b331c7e19";

    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TimeCapsuleDrive");
    public static string InstalledExe => Path.Combine(InstallDir, "TimeCapsuleDrive.exe");

    /// <summary>True when the running program is the installed copy (as opposed to the setup file or a build).</summary>
    public static bool RunningInstalled =>
        string.Equals(Path.GetFullPath(Environment.ProcessPath ?? ""), InstalledExe, StringComparison.OrdinalIgnoreCase);

    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static bool IsInstalled => File.Exists(InstalledExe) && File.Exists(Config.InstalledPath);

    public static bool WinFspInstalled()
    {
        try
        {
            using var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(@"SOFTWARE\WinFsp");
            return k?.GetValue("InstallDir") is string dir && File.Exists(Path.Combine(dir, "bin", "winfsp-x64.dll"));
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Drive letters taken in the session of the person who started setup (their mapped network drives, above
    /// all). Setup itself runs with administrator rights and cannot see those: Windows keeps network drives
    /// per sign-in. A drive mounted under such a letter would exist but stay hidden behind the network drive,
    /// so the ordinary, non-elevated start of the program collects the letters and hands them over.
    /// </summary>
    public static HashSet<char> UserLetters = new();

    /// <summary>The letters in use in the current session, except a drive of ours; in the form passed to --taken.</summary>
    public static string LettersTakenInThisSession()
    {
        var letters = new List<char>();
        foreach (var d in DriveInfo.GetDrives())
        {
            char c = char.ToUpperInvariant(d.Name[0]);
            if (!IsOurDrive(d.Name)) letters.Add(c);
        }
        return letters.Count > 0 ? new string(letters.ToArray()) : "-";
    }

    /// <summary>Drive letters nothing is using right now (the currently configured one is offered too).</summary>
    public static List<string> FreeDriveLetters(string? alsoOffer = null)
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var res = new List<string>();
        for (char c = 'Z'; c >= 'D'; c--)
        {
            if (UserLetters.Contains(c)) continue;
            if (!used.Contains(c) || (alsoOffer != null && alsoOffer.Length > 0 && char.ToUpperInvariant(alsoOffer[0]) == c))
                res.Add(c + ":");
        }
        return res;
    }

    /// <summary>True when the drive letter currently shows our file system (and not some other disk).</summary>
    public static bool IsOurDrive(string drive)
    {
        try
        {
            var d = new DriveInfo(drive.Substring(0, 1));
            return d.IsReady && d.DriveFormat == FileSystemName;
        }
        catch (Exception) { return false; }
    }

    // ---------------------------------------------------------------- install

    public static void Install(Config cfg, Action<string> say)
    {
        if (!IsElevated()) throw new InvalidOperationException(T.NeedAdmin);

        // Everything that can be checked is checked first, before the working installation is touched.
        Config? previous = null;
        try { if (File.Exists(Config.InstalledPath)) previous = Config.Load(Config.InstalledPath); } catch (Exception) { }
        byte[]? previousConfigFile = null;
        try { if (File.Exists(Config.InstalledPath)) previousConfigFile = File.ReadAllBytes(Config.InstalledPath); } catch (Exception) { }
        string previousShare = previous?.Share ?? "";

        cfg.Drive = cfg.Drive.TrimEnd('\\').ToUpperInvariant();
        if (!Regex.IsMatch(cfg.Drive, "^[D-Z]:$")) throw new InvalidOperationException(string.Format(T.BadDrive, cfg.Drive));
        if (UserLetters.Contains(cfg.Drive[0])) throw new InvalidOperationException(string.Format(T.DriveInUse, cfg.Drive));
        bool letterIsOurs = previous != null && string.Equals(previous.Drive, cfg.Drive, StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(cfg.Drive + "\\") && !letterIsOurs && !IsOurDrive(cfg.Drive))
            throw new InvalidOperationException(string.Format(T.DriveInUse, cfg.Drive));
        if (cfg.Share.Length > 0)
        {
            if (!Regex.IsMatch(cfg.Share, @"^[A-Za-z0-9_\-\.\$ ]{1,80}$") || cfg.Share != cfg.Share.Trim())
                throw new InvalidOperationException(T.BadShareName);
            // A share of that name that is not ours belongs to something else on this computer: leave it alone.
            if (!string.Equals(cfg.Share, previousShare, StringComparison.OrdinalIgnoreCase) && ShareExists(cfg.Share))
                throw new InvalidOperationException(string.Format(T.ShareNameTaken, cfg.Share));
        }

        say(T.StepLogin);
        CheckLogin(cfg);

        if (!WinFspInstalled())
        {
            say(T.StepWinFsp);
            InstallWinFsp(say);
            if (!WinFspInstalled()) throw new InvalidOperationException(T.WinFspFailed);
        }

        say(T.StepStop);
        StopService();
        RemoveShare(previousShare);
        try
        {
            say(T.StepFiles);
            Directory.CreateDirectory(InstallDir);
            string self = Environment.ProcessPath!;
            if (!RunningInstalled) CopyWithRetry(self, InstalledExe);
            File.WriteAllBytes(Path.Combine(InstallDir, WinFspLibrary.FileName), WinFspLibrary.Bytes());
            RegisterUninstaller(); // from here on the program can be removed through "Apps and features"
            cfg.SaveInstalled();

            say(T.StepTask);
            RegisterTask();
            Run("schtasks.exe", $"/Run /TN \"{TaskName}\"");

            say(T.StepMount);
            if (!WaitFor(() => IsOurDrive(cfg.Drive), 90))
                throw new InvalidOperationException(string.Format(T.MountFailed, Path.Combine(Config.DataDir, "timecapsule.log")));
            // Open Explorer windows do not notice the new drive by themselves.
            if (previous != null && !string.Equals(previous.Drive, cfg.Drive, StringComparison.OrdinalIgnoreCase))
                ShellNotify.DriveRemoved(previous.Drive);
            ShellNotify.DriveAdded(cfg.Drive);

            if (cfg.Share.Length > 0)
            {
                // The running drive creates the share itself (it has to do that again after every restart).
                say(string.Format(T.StepShare, cfg.Share));
                if (!WaitFor(() => ShareExists(cfg.Share), 30))
                    throw new InvalidOperationException(string.Format(T.ShareFailed, Path.Combine(Config.DataDir, "timecapsule.log")));
                Run("netsh.exe", $"advfirewall firewall delete rule name=\"{FirewallRule}\"");
                Run("netsh.exe", $"advfirewall firewall add rule name=\"{FirewallRule}\" dir=in action=allow protocol=TCP localport=445 profile=private,domain remoteip=localsubnet");
            }
            else Run("netsh.exe", $"advfirewall firewall delete rule name=\"{FirewallRule}\"");
        }
        catch (Exception)
        {
            // Put the previous, working settings back and start the drive with them again.
            if (previousConfigFile != null)
            {
                try
                {
                    StopService();
                    previous!.SaveInstalled();
                    Run("schtasks.exe", $"/Run /TN \"{TaskName}\"");
                    say(T.RolledBack);
                }
                catch (Exception) { }
            }
            throw;
        }
        say(T.Done);
    }

    /// <summary>Signs in with the given settings and makes sure the chosen disk exists, reporting problems in plain words.</summary>
    static void CheckLogin(Config cfg)
    {
        List<string> volumes;
        try { volumes = AfpClient.ListVolumes(cfg.Host, cfg.Port, cfg.Login, cfg.Password); }
        catch (AfpException e) when (e.Code is AfpError.UserNotAuth or AfpError.ParamErr or AfpError.AccessDenied)
        {
            throw new InvalidOperationException(T.WrongPassword);
        }
        catch (Exception e) when (e is AfpException or AfpConnectionException)
        {
            throw new InvalidOperationException(string.Format(T.CannotReachFmt, cfg.Host, e.Message));
        }
        if (!volumes.Contains(cfg.Volume))
            throw new InvalidOperationException(string.Format(T.NoSuchVolume, cfg.Volume, string.Join(", ", volumes)));
        // Remember what the device calls itself, so the drive can find it again if its address changes.
        try
        {
            var info = AfpClient.Probe(cfg.Host, cfg.Port);
            cfg.ServerName = info.Name;
            cfg.Model = info.Model;
        }
        catch (Exception) { }
    }

    static bool WaitFor(Func<bool> condition, int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            if (condition()) return true;
            Thread.Sleep(1000);
        }
        return condition();
    }

    /// <summary>The WinFsp installer embedded at build time, or null if this build has none.</summary>
    public static byte[]? BundledWinFsp()
    {
        using var s = typeof(Installer).Assembly.GetManifestResourceStream("winfsp.msi");
        if (s == null) return null;
        var data = new byte[s.Length];
        s.ReadExactly(data);
        return data;
    }

    /// <summary>True when the embedded WinFsp installer is present and is exactly the expected release.</summary>
    public static bool BundledWinFspIsValid() =>
        BundledWinFsp() is { } d && Convert.ToHexString(SHA256.HashData(d)).Equals(WinFspSha256, StringComparison.OrdinalIgnoreCase);

    static void InstallWinFsp(Action<string> say)
    {
        // Normally the WinFsp installer travels inside this program. A build made without it downloads the
        // same file instead; either way only the exact, known release is accepted.
        string? temp = null;
        try
        {
            byte[]? data = BundledWinFsp();
            if (data == null)
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("TimeCapsuleDrive-Setup");
                try { data = http.GetByteArrayAsync(WinFspUrl).GetAwaiter().GetResult(); }
                catch (Exception e) { throw new InvalidOperationException(string.Format(T.WinFspDownloadFailed, WinFspUrl, e.Message)); }
            }
            if (!Convert.ToHexString(SHA256.HashData(data)).Equals(WinFspSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(T.WinFspBadHash);
            // Written to a folder only administrators can change, so the file that runs is the file checked above.
            Directory.CreateDirectory(InstallDir);
            string msi = temp = Path.Combine(InstallDir, "winfsp-setup-" + Guid.NewGuid().ToString("N") + ".msi");
            File.WriteAllBytes(msi, data);
            var (code, _) = Run("msiexec.exe", $"/i \"{msi}\" /qn /norestart");
            if (code != 0 && code != 3010) throw new InvalidOperationException(T.WinFspFailed + " (msiexec " + code + ")");
        }
        finally
        {
            if (temp != null) try { File.Delete(temp); } catch (Exception) { }
        }
    }

    /// <summary>Stops the running drive: politely first (so files are closed on the server), then by force.</summary>
    static void StopService()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(StopEventName, out var ev))
                using (ev) ev.Set();
        }
        catch (Exception) { }
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && Service().Count > 0) Thread.Sleep(300);
        Run("schtasks.exe", $"/End /TN \"{TaskName}\"");
        foreach (var p in Service())
            try { p.Kill(); p.WaitForExit(5000); } catch (Exception) { }
        Thread.Sleep(500);

        // The background drive is the instance the task scheduler started: it runs in session 0. Setup windows
        // and command-line tools run in a user's session and are never touched.
        static List<Process> Service()
        {
            int me = Environment.ProcessId;
            return Process.GetProcessesByName("TimeCapsuleDrive").Where(p => p.Id != me && p.SessionId == 0).ToList();
        }
    }

    static void CopyWithRetry(string from, string to)
    {
        for (int i = 0; ; i++)
        {
            try { File.Copy(from, to, true); return; }
            catch (IOException) when (i < 10) { Thread.Sleep(500); }
        }
    }

    static void RegisterTask()
    {
        static string X(string s) => System.Security.SecurityElement.Escape(s);
        // The settings file is named explicitly: the background drive must never pick up settings from anywhere else.
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Mounts an Apple Time Capsule (AFP) as a drive for the whole computer.</Description>
              </RegistrationInfo>
              <Triggers>
                <BootTrigger><Enabled>true</Enabled></BootTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
                <Enabled>true</Enabled>
                <RestartOnFailure><Interval>PT1M</Interval><Count>999</Count></RestartOnFailure>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{X(InstalledExe)}</Command>
                  <Arguments>mount --system --config "{X(Config.InstalledPath)}"</Arguments>
                  <WorkingDirectory>{X(InstallDir)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
        string file = Path.Combine(InstallDir, "task-" + Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(file, xml, Encoding.Unicode);
        try
        {
            var (code, output) = Run("schtasks.exe", $"/Create /TN \"{TaskName}\" /XML \"{file}\" /F");
            if (code != 0) throw new InvalidOperationException("schtasks: " + output.Trim());
        }
        finally { try { File.Delete(file); } catch (Exception) { } }
    }

    // ---------------------------------------------------------------- network share

    public static bool ShareExists(string name) => name.Length > 0 && Run("net.exe", $"share \"{name}\"").code == 0;

    /// <summary>
    /// (Re)creates the network share of the drive. Called by the running drive after every mount: Windows does
    /// not bring a share back by itself when its drive appears only after the sharing service has started.
    /// </summary>
    public static void EnsureShare(string name, string root)
    {
        RemoveShare(name);
        // "Everyone" has a different name on every language of Windows; ask for the local one.
        string everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null).Translate(typeof(NTAccount)).Value;
        var (code, output) = Run("net.exe", $"share \"{name}\"={root} \"/GRANT:{everyone},FULL\" /REMARK:\"Apple Time Capsule (AFP)\"");
        if (code != 0) throw new InvalidOperationException("net share: " + output.Trim());
    }

    static void RemoveShare(string name)
    {
        if (name.Length > 0) Run("net.exe", $"share \"{name}\" /delete /y");
    }

    static void RegisterUninstaller()
    {
        using var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).CreateSubKey(UninstallKey);
        k.SetValue("DisplayName", "Time Capsule Drive");
        k.SetValue("DisplayVersion", Version);
        k.SetValue("Publisher", "TimeCapsuleDrive");
        k.SetValue("InstallLocation", InstallDir);
        k.SetValue("DisplayIcon", InstalledExe);
        k.SetValue("UninstallString", $"\"{InstalledExe}\" uninstall");
        k.SetValue("ModifyPath", $"\"{InstalledExe}\" setup");
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        try { k.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord); } catch (Exception) { }
    }

    // ---------------------------------------------------------------- uninstall

    /// <summary>Removes everything Install created. WinFsp is left alone: other programs may be using it.</summary>
    public static void Uninstall(Action<string> say)
    {
        if (!IsElevated()) throw new InvalidOperationException(T.NeedAdmin);
        say(T.StepStop);
        string share = "", drive = "";
        try
        {
            if (File.Exists(Config.InstalledPath))
            {
                var old = Config.Load(Config.InstalledPath);
                share = old.Share;
                drive = old.Drive;
            }
        }
        catch (Exception) { }
        StopService();
        if (drive.Length > 0) ShellNotify.DriveRemoved(drive);
        Run("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F");
        RemoveShare(share);
        Run("netsh.exe", $"advfirewall firewall delete rule name=\"{FirewallRule}\"");
        try { RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).DeleteSubKeyTree(UninstallKey, false); } catch (Exception) { }
        try { if (Directory.Exists(Config.DataDir)) Directory.Delete(Config.DataDir, true); } catch (Exception) { }

        // The program cannot delete its own running file. A detached command prompt keeps trying for a minute,
        // which covers the time this process (and a message box it may still show) needs to go away.
        if (Directory.Exists(InstallDir))
            Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c for /l %i in (1,1,30) do (ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{InstallDir}\" 2>nul & if not exist \"{InstallDir}\" exit)")
            {
                CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Environment.SystemDirectory,
            });
        say(T.Removed);
    }

    // ---------------------------------------------------------------- helpers

    static (int code, string output) Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, exe), args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output + err.GetAwaiter().GetResult());
    }
}

/// <summary>
/// The WinFsp .NET library. It has to exist as a real file on disk (it inspects its own file), so it is carried
/// inside the program and written out: next to the installed program (a folder only administrators can change),
/// or into the user's own profile when the program runs without being installed.
/// </summary>
static class WinFspLibrary
{
    public const string FileName = "winfsp-msil.dll";

    public static byte[] Bytes()
    {
        using var s = typeof(WinFspLibrary).Assembly.GetManifestResourceStream(FileName)
            ?? throw new InvalidOperationException("this build does not contain " + FileName);
        var data = new byte[s.Length];
        s.ReadExactly(data);
        return data;
    }

    /// <summary>Path of the library to load, creating the per-user copy if necessary.</summary>
    public static string Locate()
    {
        string beside = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, FileName);
        if (File.Exists(beside)) return beside;
        // The installed (system-wide) program must only ever load the copy the installer placed next to it.
        if (Installer.RunningInstalled) throw new FileNotFoundException("the installation is incomplete: missing " + beside);
        var data = Bytes();
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TimeCapsuleDrive");
        string path = Path.Combine(dir, FileName);
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(data))
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, data);
        }
        return path;
    }
}
