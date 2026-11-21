using System.Drawing;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows.Forms;
using TimeCapsuleDrive.Afp;

namespace TimeCapsuleDrive.Install;

/// <summary>The one window of the setup program: pick the Time Capsule, sign in, choose a drive letter, install.</summary>
sealed class SetupForm : Form
{
    sealed record ServerItem(string Host, string Text)
    {
        public override string ToString() => Text;
    }

    readonly ComboBox _server = new() { DropDownStyle = ComboBoxStyle.DropDown };
    readonly TextBox _login = new(), _password = new() { UseSystemPasswordChar = true }, _shareName = new() { Text = "TimeCapsule" };
    readonly ComboBox _volume = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox _drive = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly CheckBox _share = new() { AutoSize = true };
    readonly Label _shareHint = new(), _status = new(), _state = new();
    readonly System.Windows.Forms.Timer _stateTimer = new() { Interval = 3000 };
    readonly Button _connect = new(), _install = new(), _remove = new(), _close = new();
    readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    readonly Config? _existing;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern uint GetDpiForSystem();

    public SetupForm()
    {
        try { _existing = Installer.IsInstalled ? Config.Load(Config.InstalledPath) : null; } catch (Exception) { }

        // The layout below is written for 96 dpi and scaled to the screen by hand at the end.
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.None;
        Text = T.Title;
        Font = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        const int formH = 754;
        ClientSize = new Size(560, formH);

        const int left = 16, labelW = 170, fieldX = left + labelW, fieldW = 560 - fieldX - 16;
        int y = 14;
        Add(new Label { Text = T.Intro, Location = new Point(left, y), Size = new Size(528, 38) });
        y += 48;

        // What the installed drive is doing right now (hidden until there is an installation to report on).
        _state.SetBounds(left, y - 6, 528, 54);
        Add(_state);
        y += 58;

        Row(T.Server, _server, ref y);
        Add(new Label { Text = T.ServerHint, Location = new Point(fieldX, y - 4), Size = new Size(fieldW, 54), ForeColor = SystemColors.GrayText });
        y += 58;
        Row(T.Login, _login, ref y);
        Row(T.Password, _password, ref y);
        Add(new Label { Text = T.LoginHint, Location = new Point(fieldX, y - 4), Size = new Size(fieldW, 52), ForeColor = SystemColors.GrayText });
        y += 54;
        _connect.Text = T.Connect;
        _connect.SetBounds(fieldX, y, 140, 28);
        Add(_connect);
        y += 34;
        _status.SetBounds(fieldX, y, fieldW, 34);
        Add(_status);
        y += 38;

        Row(T.Volume, _volume, ref y);
        Row(T.Drive, _drive, ref y, 80);

        _share.Text = T.ShareCheck;
        _share.Location = new Point(left, y + 4);
        Add(_share);
        y += 28;
        _shareName.SetBounds(left + 20, y, 200, 24);
        Add(_shareName);
        y += 28;
        _shareHint.SetBounds(left + 20, y, 520, 52);
        _shareHint.ForeColor = SystemColors.GrayText;
        Add(_shareHint);
        y += 58;

        _log.SetBounds(left, y, 528, formH - y - 56);
        Add(_log);

        _install.Text = _existing != null ? T.UpdateButton : T.InstallButton;
        _install.SetBounds(560 - 16 - 130 - 100, formH - 44, 124, 30);
        _close.Text = T.CloseButton;
        _close.SetBounds(560 - 16 - 96, formH - 44, 96, 30);
        _remove.Text = T.RemoveButton;
        _remove.SetBounds(left, formH - 44, 150, 30);
        _remove.Visible = _existing != null;
        Add(_install); Add(_close); Add(_remove);
        AcceptButton = _install;

        foreach (var d in Installer.FreeDriveLetters(_existing?.Drive)) _drive.Items.Add(d);
        string wanted = _existing?.Drive ?? "T:";
        _drive.SelectedItem = _drive.Items.Contains(wanted) ? wanted : (_drive.Items.Count > 0 ? _drive.Items[0] : null);

        if (_existing != null)
        {
            _server.Text = _existing.Host;
            _login.Text = _existing.Login;
            _password.Text = _existing.Password;
            _volume.Items.Add(_existing.Volume);
            _volume.SelectedIndex = 0;
            _share.Checked = _existing.Share.Length > 0;
            if (_existing.Share.Length > 0) _shareName.Text = _existing.Share;
        }

        _share.CheckedChanged += (_, _) => UpdateShareHint();
        _shareName.TextChanged += (_, _) => UpdateShareHint();
        UpdateShareHint();
        _connect.Click += async (_, _) => await ConnectAsync();
        _install.Click += async (_, _) => await InstallAsync();
        _remove.Click += async (_, _) => await RemoveAsync();
        _close.Click += (_, _) => Close();
        Shown += async (_, _) => await DiscoverAsync();
        _stateTimer.Tick += (_, _) => ShowState();
        _stateTimer.Start();
        ShowState();
        float k = GetDpiForSystem() / 96f;
        foreach (Control c in Controls)
            c.SetBounds((int)(c.Left * k), (int)(c.Top * k), (int)(c.Width * k), (int)(c.Height * k));
        ClientSize = new Size((int)(560 * k), (int)(formH * k));
        ResumeLayout(false);
        PerformLayout();

        void Add(Control c) => Controls.Add(c);
        void Row(string label, Control field, ref int top, int width = 0)
        {
            Add(new Label { Text = label, Location = new Point(left, top + 4), Size = new Size(labelW - 6, 20) });
            field.SetBounds(fieldX, top, width > 0 ? width : fieldW, 24);
            Add(field);
            top += 32;
        }
    }

    /// <summary>Shows in plain words whether the installed drive is connected, and if not, why.</summary>
    void ShowState()
    {
        var s = Status.Read();
        if (s == null) { _state.Text = ""; return; }
        if (s.Drive.Length > 0 && Installer.UserLetters.Contains(char.ToUpperInvariant(s.Drive[0])))
        {
            _state.Text = string.Format(T.StateShadowed, s.Drive);
            _state.ForeColor = Color.FromArgb(180, 30, 30);
            return;
        }
        bool up = s.State == Status.Connected && Installer.IsOurDrive(s.Drive);
        // A status file that says "connected" while the drive is gone means the background program is not running.
        _state.Text = up ? string.Format(T.StateConnected, s.Drive)
            : s.State switch
            {
                Status.Connected => T.StateStopped,
                Status.Connecting => string.Format(T.StateConnecting, s.Host),
                Status.Unreachable => string.Format(T.StateUnreachable, s.Host),
                Status.WrongPassword => T.StateWrongPassword,
                _ => string.Format(T.StateError, s.Detail),
            };
        _state.ForeColor = up ? Color.FromArgb(0, 110, 40) : s.State == Status.Connecting ? SystemColors.ControlText : Color.FromArgb(180, 30, 30);
    }

    void UpdateShareHint()
    {
        _shareName.Enabled = _share.Checked;
        _shareHint.Text = _share.Checked ? string.Format(T.ShareHint, Environment.MachineName, _shareName.Text.Trim()) : "";
    }

    string Host => (_server.SelectedItem as ServerItem)?.Host ?? _server.Text.Trim();

    void Say(string line)
    {
        if (IsDisposed) return;
        BeginInvoke(() => _log.AppendText(line + Environment.NewLine));
    }

    void Busy(bool busy)
    {
        foreach (Control c in Controls)
            if (c is not Label && !ReferenceEquals(c, _log)) c.Enabled = !busy;
        if (!busy) UpdateShareHint();
        UseWaitCursor = busy;
    }

    // ---------------------------------------------------------------- finding servers on the local network

    async Task DiscoverAsync()
    {
        _status.Text = T.Searching;
        var found = await Task.Run(() => Discovery.FindServers()
            .Select(f => new ServerItem(f.Host, f.Info != null ? $"{f.Info.Name} ({f.Host})" : f.Host)).ToList());
        if (IsDisposed) return;
        string typed = _server.Text;
        foreach (var s in found) _server.Items.Add(s);
        if (typed.Length == 0 && found.Count > 0) _server.SelectedIndex = 0;
        if (_status.Text == T.Searching)
            _status.Text = found.Count == 0 ? T.NotFound : string.Format(T.FoundFmt, string.Join(", ", found.Select(f => f.Text)));
    }

    // ---------------------------------------------------------------- actions

    async Task ConnectAsync()
    {
        string host = Host, user = _login.Text.Trim(), pass = _password.Text;
        if (host.Length == 0) { _status.Text = T.NeedServer; return; }
        Busy(true);
        _status.Text = T.Connecting;
        try
        {
            var vols = await Task.Run(() => AfpClient.ListVolumes(host, 548, user, pass));
            string? keep = _volume.SelectedItem as string;
            _volume.Items.Clear();
            foreach (var v in vols) _volume.Items.Add(v);
            if (vols.Count > 0) _volume.SelectedItem = keep != null && vols.Contains(keep) ? keep : vols[0];
            _status.Text = vols.Count > 0 ? string.Format(T.ConnectedFmt, vols.Count) : T.NoVolumes;
        }
        catch (AfpException e) when (e.Code is AfpError.UserNotAuth or AfpError.ParamErr or AfpError.AccessDenied)
        {
            _status.Text = T.WrongPassword;
        }
        catch (Exception e)
        {
            _status.Text = string.Format(T.CannotReachFmt, host, e.Message);
        }
        finally { Busy(false); }
    }

    async Task InstallAsync()
    {
        if (Host.Length == 0) { _status.Text = T.NeedServer; return; }
        // Pressing Install straight away is fine: sign in first and take the disk if there is only one choice.
        if (_volume.SelectedItem is not string) await ConnectAsync();
        if (_volume.SelectedItem is not string volume)
        {
            if (_volume.Items.Count > 0 || _status.Text.Length == 0) _status.Text = T.NeedVolume;
            return;
        }
        if (_share.Checked && _shareName.Text.Trim().Length == 0) { _status.Text = T.NeedShareName; return; }
        var cfg = new Config
        {
            Host = Host, Volume = volume, Login = _login.Text.Trim(), Password = _password.Text,
            Drive = _drive.SelectedItem as string ?? "T:", Share = _share.Checked ? _shareName.Text.Trim() : "",
        };
        Busy(true);
        _status.Text = "";
        try
        {
            await Task.Run(() => Installer.Install(cfg, Say));
            _status.Text = string.Format(T.InstalledFmt, cfg.Drive);
            MessageBox.Show(this, _status.Text, T.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            _install.Text = T.UpdateButton;
            _remove.Visible = true;
        }
        catch (Exception e)
        {
            Say(string.Format(T.FailedFmt, e.Message));
            MessageBox.Show(this, string.Format(T.FailedFmt, e.Message), T.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { Busy(false); }
    }

    async Task RemoveAsync()
    {
        if (MessageBox.Show(this, T.ConfirmRemove, T.Title, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        Busy(true);
        try
        {
            await Task.Run(() => Installer.Uninstall(Say));
            MessageBox.Show(this, T.Removed, T.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
        catch (Exception e)
        {
            MessageBox.Show(this, string.Format(T.FailedFmt, e.Message), T.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Busy(false);
        }
    }
}
