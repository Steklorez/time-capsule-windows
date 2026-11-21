using System.Text;

namespace TimeCapsuleDrive;

/// <summary>
/// What the background drive is doing right now, written to a small file that the setup window reads.
/// The background drive has no screen of its own; without this a wrong password or an unreachable server
/// would show up only as "the drive is not there".
/// </summary>
static class Status
{
    public const string Connected = "connected", Connecting = "connecting", Unreachable = "unreachable",
        WrongPassword = "auth", Error = "error";

    public sealed record Snapshot(string State, string Detail, string Host, string Drive, DateTime Time);

    public static string FilePath => Path.Combine(Config.DataDir, "status.txt");

    static readonly object Lock = new();
    static bool _enabled;
    static string _host = "", _drive = "", _last = "";

    /// <summary>Turns reporting on; only the installed background drive does this.</summary>
    public static void Enable(string host, string drive)
    {
        lock (Lock) { _enabled = true; _host = host; _drive = drive; }
    }

    public static void SetHost(string host)
    {
        lock (Lock) _host = host;
    }

    public static void Report(string state, string detail = "")
    {
        lock (Lock)
        {
            if (!_enabled) return;
            string key = state + "|" + detail + "|" + _host;
            if (key == _last) return; // nothing new: do not touch the disk
            _last = key;
            var sb = new StringBuilder();
            sb.AppendLine("state=" + state);
            sb.AppendLine("detail=" + detail.Replace('\r', ' ').Replace('\n', ' '));
            sb.AppendLine("host=" + _host);
            sb.AppendLine("drive=" + _drive);
            sb.AppendLine("time=" + DateTime.UtcNow.ToString("o"));
            try
            {
                string tmp = FilePath + ".new";
                File.WriteAllText(tmp, sb.ToString());
                File.Move(tmp, FilePath, true);
            }
            catch (Exception) { }
        }
    }

    public static Snapshot? Read()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var d = new Dictionary<string, string>();
            foreach (var line in File.ReadAllLines(FilePath))
            {
                int i = line.IndexOf('=');
                if (i > 0) d[line.Substring(0, i)] = line.Substring(i + 1);
            }
            DateTime.TryParse(d.GetValueOrDefault("time", ""), null, System.Globalization.DateTimeStyles.RoundtripKind, out var time);
            return new Snapshot(d.GetValueOrDefault("state", ""), d.GetValueOrDefault("detail", ""), d.GetValueOrDefault("host", ""),
                d.GetValueOrDefault("drive", ""), time);
        }
        catch (Exception) { return null; }
    }
}
