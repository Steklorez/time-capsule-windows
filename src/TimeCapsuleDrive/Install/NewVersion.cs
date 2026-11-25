using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TimeCapsuleDrive.Install;

/// <summary>
/// Tells the user when a newer version has been published. It only tells: nothing is downloaded or installed
/// by itself. The one network request asks github.com where "the latest release" points to and sends nothing
/// about this computer.
///
/// The background drive runs without a screen, so the notice comes from a separate scheduled task that runs
/// in the signed-in user's session (at sign-in and once a day). The user's answers are remembered per user.
/// </summary>
static class NewVersion
{
    public const string TaskName = "TimeCapsuleDrive update check";
    public const string ReleasePage = "https://github.com/Steklorez/time-capsule-windows/releases/latest";
    const string UserKey = @"Software\TimeCapsuleDrive";

    /// <summary>The newest published version, or null when it cannot be found out (no internet, GitHub changed, …).</summary>
    public static Version? Latest()
    {
        try
        {
            // "releases/latest" answers with a redirect to "releases/tag/v1.2.3"; the address is all that is needed.
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("TimeCapsuleDrive/" + Installer.Version);
            using var reply = http.Send(new HttpRequestMessage(HttpMethod.Head, ReleasePage));
            return Parse(reply.Headers.Location?.ToString());
        }
        catch (Exception) { return null; }
    }

    public static Version? Parse(string? address)
    {
        var m = Regex.Match(address ?? "", @"/releases/tag/v(\d+\.\d+(?:\.\d+){0,2})$");
        return m.Success && Version.TryParse(m.Groups[1].Value, out var v) ? v : null;
    }

    public static bool IsNewer(Version latest, string current) => Version.TryParse(current, out var mine) && latest > mine;

    /// <summary>Opens the download page in the user's browser (through Explorer, so that a setup window running
    /// as administrator does not start the browser as administrator too).</summary>
    public static void OpenReleasePage()
    {
        try { Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), ReleasePage) { UseShellExecute = false }); }
        catch (Exception) { }
    }

    /// <summary>
    /// The scheduled check. Shows a notice when there is a newer version the user has not declined.
    /// </summary>
    /// <param name="force">Testing aid: ignore "checked recently", "remind me later", "skip" and "never".</param>
    /// <param name="asVersion">Testing aid: behave as if this version were installed.</param>
    public static int Check(bool force, string? asVersion)
    {
        string current = asVersion ?? Installer.Version;
        using var key = Registry.CurrentUser.CreateSubKey(UserKey);
        DateTime now = DateTime.UtcNow;
        if (!force)
        {
            if (key.GetValue("NoUpdateCheck") is int off && off != 0) return 0;
            // Sign-in and the daily run may come close together; one question a day is enough.
            if (Time(key, "LastCheck") is DateTime last && now - last < TimeSpan.FromHours(20) && now >= last) return 0;
        }
        var latest = Latest();
        if (latest == null) return 2;
        key.SetValue("LastCheck", now.ToString("o"));
        if (!IsNewer(latest, current)) return 0;
        if (!force)
        {
            if (key.GetValue("SkipVersion") as string == latest.ToString()) return 0;
            if (Time(key, "RemindAfter") is DateTime later && now < later) return 0;
        }

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var form = new UpdateForm(latest.ToString(), current);
        Application.Run(form);

        if (form.Never) key.SetValue("NoUpdateCheck", 1, RegistryValueKind.DWord);
        switch (form.Answer)
        {
            case UpdateForm.Choice.Download:
                OpenReleasePage();
                key.SetValue("RemindAfter", now.AddDays(1).ToString("o")); // in case the download was not installed
                break;
            case UpdateForm.Choice.Skip:
                key.SetValue("SkipVersion", latest.ToString());
                break;
            default:
                key.SetValue("RemindAfter", now.AddDays(3).ToString("o"));
                break;
        }
        return 0;

        static DateTime? Time(RegistryKey k, string name) =>
            DateTime.TryParse(k.GetValue(name) as string, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t.ToUniversalTime() : null;
    }

    /// <summary>The task that runs the check for whoever is signed in. Registered by the installer.</summary>
    public static string TaskXml(string exe, string dir)
    {
        static string X(string s) => System.Security.SecurityElement.Escape(s);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Tells you when a newer version of Time Capsule Drive is available. It only shows a notice; nothing is downloaded or installed.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled><Delay>PT3M</Delay></LogonTrigger>
                <CalendarTrigger>
                  <StartBoundary>2026-01-01T13:00:00</StartBoundary>
                  <Enabled>true</Enabled>
                  <RandomDelay>PT2H</RandomDelay>
                  <ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay>
                </CalendarTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author"><GroupId>S-1-5-32-545</GroupId><RunLevel>LeastPrivilege</RunLevel></Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>true</RunOnlyIfNetworkAvailable>
                <ExecutionTimeLimit>PT12H</ExecutionTimeLimit>
                <Priority>7</Priority>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{X(exe)}</Command>
                  <Arguments>update-check</Arguments>
                  <WorkingDirectory>{X(dir)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }
}

/// <summary>The notice about a newer version.</summary>
sealed class UpdateForm : Form
{
    public enum Choice { Later, Download, Skip }
    public Choice Answer = Choice.Later;
    public bool Never => _never.Checked;
    readonly CheckBox _never = new() { AutoSize = true };

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern uint GetDpiForSystem();

    public UpdateForm(string latest, string current)
    {
        // Written for 96 dpi and scaled by hand, like the setup window.
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.None;
        Text = "Time Capsule Drive";
        Font = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        const int w = 500, h = 196, left = 18;
        var head = new Label { Text = string.Format(T.UpdateHeadFmt, latest), Font = new Font("Segoe UI", 11f, FontStyle.Bold) };
        head.SetBounds(left, 16, w - 2 * left, 26);
        var body = new Label { Text = string.Format(T.UpdateBodyFmt, current) };
        body.SetBounds(left, 48, w - 2 * left, 56);
        _never.Text = T.UpdateNever;
        _never.Location = new Point(left, 110);
        var download = new Button { Text = T.UpdateDownload };
        var later = new Button { Text = T.UpdateLater };
        var skip = new Button { Text = T.UpdateSkip };
        download.SetBounds(left, h - 46, 130, 30);
        later.SetBounds(left + 138, h - 46, 150, 30);
        skip.SetBounds(w - left - 168, h - 46, 168, 30);
        download.Click += (_, _) => { Answer = Choice.Download; Close(); };
        later.Click += (_, _) => { Answer = Choice.Later; Close(); };
        skip.Click += (_, _) => { Answer = Choice.Skip; Close(); };
        Controls.AddRange(new Control[] { head, body, _never, download, later, skip });
        AcceptButton = download;
        CancelButton = later;
        float k = GetDpiForSystem() / 96f;
        foreach (Control c in Controls)
            c.SetBounds((int)(c.Left * k), (int)(c.Top * k), (int)(c.Width * k), (int)(c.Height * k));
        ClientSize = new Size((int)(w * k), (int)(h * k));
        ResumeLayout(false);
        PerformLayout();
    }
}
