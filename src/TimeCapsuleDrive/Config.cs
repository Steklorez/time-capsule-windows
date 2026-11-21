using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using TimeCapsuleDrive.Install;

namespace TimeCapsuleDrive;

/// <summary>
/// Settings: which server and volume to mount, as whom, under which drive letter, and whether to share it.
/// Stored as key=value lines ('#' starts a comment line).
///
/// An installed copy keeps them in %ProgramData%\TimeCapsuleDrive\config.txt with the password encrypted
/// (Windows DPAPI, machine scope) and the file readable by administrators only. A development copy may use
/// a plain credentials.txt found next to the program or in a parent folder.
/// </summary>
sealed class Config
{
    public string File = "", Host = "", Volume = "", Login = "", Password = "", Drive = "T:", Share = "";
    /// <summary>What the server called itself when it was set up; used to find it again if its address changes.</summary>
    public string ServerName = "", Model = "";
    public int Port = 548;

    static readonly byte[] Entropy = Encoding.ASCII.GetBytes("TimeCapsuleDrive.v1");

    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TimeCapsuleDrive");
    public static string InstalledPath => Path.Combine(DataDir, "config.txt");

    public bool IsGuest => Login.Length == 0 && Password.Length == 0;

    /// <summary>Stable name of this installation for the server (see AfpClient): machine + volume.</summary>
    public string ClientId
    {
        get
        {
            string machine = "";
            try
            {
                using var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                machine = k?.GetValue("MachineGuid") as string ?? "";
            }
            catch (Exception) { }
            if (machine.Length == 0) machine = Environment.MachineName;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(machine + "|" + Host + "|" + Volume));
            return "tcd-" + Convert.ToHexString(hash, 0, 16);
        }
    }

    public static Config Load(string? path)
    {
        path ??= Find() ?? throw new FileNotFoundException(
            "Settings not found. Run the program without arguments to set it up, or pass --config <file>.");
        var c = new Config { File = Path.GetFullPath(path) };
        foreach (var raw in System.IO.File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int i = line.IndexOf('=');
            if (i < 0) continue;
            string k = line.Substring(0, i).Trim().ToLowerInvariant(), v = line.Substring(i + 1).Trim();
            switch (k)
            {
                case "host": c.Host = v; break;
                case "port": c.Port = int.Parse(v); break;
                case "volume": c.Volume = v; break;
                case "login": c.Login = v; break;
                case "password": c.Password = v; break;
                case "password_protected":
                    c.Password = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(v), Entropy, DataProtectionScope.LocalMachine));
                    break;
                case "drive": c.Drive = v.TrimEnd('\\'); break;
                case "share": c.Share = v; break;
                case "server_name": c.ServerName = v; break;
                case "model": c.Model = v; break;
            }
        }
        if (c.Host.Length == 0 || c.Volume.Length == 0) throw new InvalidDataException($"{path}: 'host' and 'volume' are required");
        return c;
    }

    /// <summary>
    /// Writes the installed settings file. The folder is made writable by administrators only (the background
    /// drive runs with full system rights and reads its settings and writes its log there, so nobody else may
    /// be able to plant or replace files in it), and the settings file itself is readable by administrators only.
    /// </summary>
    public void SaveInstalled()
    {
        SecureDataDir();
        var sb = new StringBuilder();
        sb.AppendLine("# TimeCapsuleDrive settings. Change them by running the setup program again.");
        sb.AppendLine("host=" + Host);
        sb.AppendLine("port=" + Port);
        sb.AppendLine("volume=" + Volume);
        sb.AppendLine("login=" + Login);
        if (Password.Length > 0)
            sb.AppendLine("password_protected=" + Convert.ToBase64String(
                ProtectedData.Protect(Encoding.UTF8.GetBytes(Password), Entropy, DataProtectionScope.LocalMachine)));
        sb.AppendLine("drive=" + Drive);
        sb.AppendLine("share=" + Share);
        sb.AppendLine("server_name=" + ServerName);
        sb.AppendLine("model=" + Model);

        var sec = new FileSecurity();
        sec.SetAccessRuleProtection(true, false);
        sec.AddAccessRule(new FileSystemAccessRule(Sid(WellKnownSidType.LocalSystemSid), FileSystemRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(Sid(WellKnownSidType.BuiltinAdministratorsSid), FileSystemRights.FullControl, AccessControlType.Allow));

        // Created under a fresh name with its final permissions from the first moment, then moved into place.
        string tmp = Path.Combine(DataDir, "config-" + Guid.NewGuid().ToString("N") + ".tmp");
        var bytes = new UTF8Encoding(false).GetBytes(sb.ToString());
        using (var fs = new System.IO.FileInfo(tmp).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, sec))
            fs.Write(bytes);
        System.IO.File.Move(tmp, InstalledPath, true);
        File = InstalledPath;
    }

    static SecurityIdentifier Sid(WellKnownSidType type) => new(type, null);

    /// <summary>Creates the data folder, or takes an existing one back under administrators' control.</summary>
    static void SecureDataDir()
    {
        var dir = new DirectoryInfo(DataDir);
        if (dir.Exists)
        {
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(DataDir + " is a link to another location; remove it and run setup again.");
            // A folder somebody else created may contain planted files: start clean.
            var owner = dir.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            bool trusted = owner != null && (owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || owner.IsWellKnown(WellKnownSidType.LocalSystemSid));
            if (!trusted)
                foreach (var entry in dir.GetFileSystemInfos())
                {
                    if (entry is DirectoryInfo sub && (sub.Attributes & FileAttributes.ReparsePoint) == 0) sub.Delete(true);
                    else entry.Delete();
                }
        }
        else dir.Create();

        var sec = new DirectorySecurity();
        sec.SetOwner(Sid(WellKnownSidType.BuiltinAdministratorsSid));
        sec.SetAccessRuleProtection(true, false);
        const InheritanceFlags all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        sec.AddAccessRule(new FileSystemAccessRule(Sid(WellKnownSidType.LocalSystemSid), FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(Sid(WellKnownSidType.BuiltinAdministratorsSid), FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        // Everybody may read the log; nobody but administrators may add or change anything.
        sec.AddAccessRule(new FileSystemAccessRule(Sid(WellKnownSidType.BuiltinUsersSid), FileSystemRights.ReadAndExecute, all, PropagationFlags.None, AccessControlType.Allow));
        dir.SetAccessControl(sec);
    }

    static string? Find()
    {
        // An installed copy only ever uses its own settings file.
        if (!Installer.RunningInstalled)
        {
            string exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            foreach (var start in new[] { Environment.CurrentDirectory, exeDir })
                for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
                {
                    var p = Path.Combine(d.FullName, "credentials.txt");
                    if (System.IO.File.Exists(p)) return p;
                }
        }
        return System.IO.File.Exists(InstalledPath) ? InstalledPath : null;
    }
}
