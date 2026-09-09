using System.Diagnostics;
using System.Reflection;
using NAudio.CoreAudioApi;
using Jerboa.Audio;
using Jerboa.Ui;

namespace Jerboa;

internal sealed class MainForm : Form
{
    private static readonly Color Ink = Color.FromArgb(32, 32, 30);
    private static readonly Color Muted = Color.FromArgb(112, 112, 110);
    private static readonly Color RecordRed = Color.FromArgb(163, 45, 45);
    private static readonly Color PauseAmber = Color.FromArgb(133, 79, 11);
    private static readonly Color NoticeBack = Color.FromArgb(225, 245, 238);
    private static readonly Color NoticeInk = Color.FromArgb(15, 110, 86);

    private const int FieldWidth = 214;

    private readonly Settings _settings = Settings.Load();
    private readonly RecordingSession _session = new();
    private readonly TrayIcons _trayIcons = new();
    private readonly NotifyIcon _tray = new();
    private readonly ContextMenuStrip _trayMenu = new();
    private readonly Hotkey _hotkey = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 80 };

    private readonly TableLayoutPanel _root = Column();
    private readonly Label _time = new();
    private readonly Label _status = new();
    private readonly LevelMeter _systemMeter = new();
    private readonly LevelMeter _micMeter = new();
    private readonly Panel _notice = new();
    private readonly TableLayoutPanel _buttons = new();
    private readonly Button _primary = new();
    private readonly Button _secondary = new();
    private readonly Button _gear = new();

    private readonly TableLayoutPanel _settingsPanel = new();
    private readonly TextBox _folder = new();
    private readonly ComboBox _playback = new();
    private readonly ComboBox _microphone = new();
    private readonly ComboBox _autoStop = new();
    private readonly CheckBox _shortcutOn = new();
    private readonly ShortcutBox _shortcut = new();
    private readonly Label _shortcutNote = new();
    private readonly CheckBox _onTop = new();
    private readonly CheckBox _autostart = new();

    private readonly ToolTip _tips = new();
    private bool _loading = true;
    private bool _startHidden;
    private int _pulseFrame = -1;
    private bool _placeAutomatically = true;
    private int _settingsNaturalWidth;

    private const int WindowMargin = 12;
    private const double SettingsWidthFactor = 1.3;
    private bool _settingsOpen;
    private bool _reallyExit;
    private string? _lastFolder;

    public MainForm(bool startHidden = false)
    {
        _startHidden = startHidden;
        BuildWindow();
        BuildTray();

        LoadSettingsIntoUi();
        _loading = false;
        ApplyShortcut();
        RefreshState();

        _session.Warning += message => BeginInvoke(() => Notify("Device problem", message));
        _session.DeviceLost += OnDeviceLost;
        _hotkey.Pressed += () => BeginInvoke(ToggleRecording);
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
    }

    // ---------------------------------------------------------------- scaffolding

    private static TableLayoutPanel Column() => new()
    {
        ColumnCount = 1,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Dock = DockStyle.Fill,
        Margin = Padding.Empty
    };

    private static Label Caption(string text) => new()
    {
        Text = text,
        ForeColor = Muted,
        AutoSize = true,
        Margin = new Padding(0, 7, 10, 0),
        Anchor = AnchorStyles.Left
    };

    // ---------------------------------------------------------------- window

    private void BuildWindow()
    {
        Text = "Jerboa";
        Icon = _trayIcons.App;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.White;
        ForeColor = Ink;
        Font = new Font("Segoe UI", 9f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        _root.Padding = new Padding(14);
        Controls.Add(_root);

        _root.Controls.Add(BuildHeader());
        _root.Controls.Add(BuildMeters());
        _root.Controls.Add(BuildNotice());
        _root.Controls.Add(BuildButtons());
        _root.Controls.Add(BuildSettings());
    }

    private Control BuildHeader()
    {
        var header = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 8)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _time.Text = "00:00:00";
        _time.Font = new Font("Segoe UI", 24f);
        _time.AutoSize = true;
        _time.Margin = Padding.Empty;

        _status.AutoSize = false;
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.BottomRight;
        _status.Margin = new Padding(0, 0, 2, 8);

        header.Controls.Add(_time, 0, 0);
        header.Controls.Add(_status, 1, 0);
        return header;
    }

    private Control BuildMeters()
    {
        var meters = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 12)
        };
        meters.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        meters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        foreach (var meter in new[] { _systemMeter, _micMeter })
        {
            meter.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            meter.Margin = new Padding(0, 6, 0, 6);
        }

        meters.Controls.Add(Caption("System"), 0, 0);
        meters.Controls.Add(_systemMeter, 1, 0);
        meters.Controls.Add(Caption("Microphone"), 0, 1);
        meters.Controls.Add(_micMeter, 1, 1);
        return meters;
    }

    private Control BuildNotice()
    {
        _notice.BackColor = NoticeBack;
        _notice.AutoSize = true;
        _notice.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _notice.Dock = DockStyle.Fill;
        _notice.Padding = new Padding(10, 7, 10, 7);
        _notice.Margin = new Padding(0, 0, 0, 12);
        _notice.Visible = false;

        var line = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Margin = Padding.Empty,
            WrapContents = false
        };

        var text = new Label
        {
            Text = "Recording stored.",
            ForeColor = NoticeInk,
            AutoSize = true,
            Margin = new Padding(0, 0, 6, 0)
        };

        var link = new LinkLabel
        {
            Text = "Open here",
            LinkColor = NoticeInk,
            ActiveLinkColor = NoticeInk,
            VisitedLinkColor = NoticeInk,
            AutoSize = true,
            Margin = Padding.Empty
        };
        link.Click += (_, _) => OpenFolder(_lastFolder ?? _folder.Text);

        line.Controls.Add(text);
        line.Controls.Add(link);
        _notice.Controls.Add(line);
        return _notice;
    }

    private Control BuildButtons()
    {
        _buttons.ColumnCount = 3;
        _buttons.RowCount = 1;
        _buttons.AutoSize = true;
        _buttons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _buttons.Dock = DockStyle.Fill;
        _buttons.Margin = Padding.Empty;
        _buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0));
        _buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        foreach (var button in new[] { _primary, _secondary })
        {
            button.FlatStyle = FlatStyle.System;
            button.Height = 36;
            button.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            button.Margin = new Padding(0, 0, 6, 0);
        }

        _gear.FlatStyle = FlatStyle.System;
        _gear.Text = "⚙";
        _gear.Font = new Font("Segoe UI Symbol", 11f);
        _gear.Height = 36;
        _gear.Width = 38;
        _gear.Margin = Padding.Empty;

        _primary.Click += (_, _) => OnPrimary();
        _secondary.Click += (_, _) => StopRecording(false);
        _gear.Click += (_, _) => ToggleSettings();

        _buttons.Controls.Add(_primary, 0, 0);
        _buttons.Controls.Add(_secondary, 1, 0);
        _buttons.Controls.Add(_gear, 2, 0);
        return _buttons;
    }

    private Control BuildSettings()
    {
        _settingsPanel.ColumnCount = 2;
        _settingsPanel.AutoSize = true;
        _settingsPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _settingsPanel.Dock = DockStyle.Fill;
        _settingsPanel.Margin = new Padding(0, 16, 0, 0);
        _settingsPanel.Visible = false;
        _settingsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _settingsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var folderRow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0, 3, 0, 3)
        };
        _folder.ReadOnly = true;
        _folder.Width = FieldWidth - 42;
        _folder.Margin = new Padding(0, 0, 6, 0);
        var browse = new Button { Text = "…", Width = 36, Height = 24, FlatStyle = FlatStyle.System, Margin = Padding.Empty };
        browse.Click += (_, _) => ChooseFolder();
        folderRow.Controls.Add(_folder);
        folderRow.Controls.Add(browse);

        foreach (var combo in new[] { _playback, _microphone, _autoStop })
        {
            combo.DropDownStyle = ComboBoxStyle.DropDownList;
            combo.Width = FieldWidth;
            combo.Margin = new Padding(0, 3, 0, 3);
            combo.SelectedIndexChanged += (_, _) => Persist();
        }

        var shortcutRow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0, 3, 0, 0)
        };
        _shortcutOn.AutoSize = true;
        _shortcutOn.Margin = new Padding(0, 3, 6, 0);
        _shortcutOn.CheckedChanged += (_, _) => { Persist(); ApplyShortcut(); };
        _shortcut.Width = FieldWidth - 26;
        _shortcut.Margin = Padding.Empty;
        _shortcut.Captured += (control, shift, alt, key) =>
        {
            _settings.ShortcutControl = control;
            _settings.ShortcutShift = shift;
            _settings.ShortcutAlt = alt;
            _settings.ShortcutKey = key;
            _shortcut.Text = _settings.ShortcutText;
            Persist();
            ApplyShortcut();
        };
        shortcutRow.Controls.Add(_shortcutOn);
        shortcutRow.Controls.Add(_shortcut);

        _shortcutNote.AutoSize = true;
        _shortcutNote.ForeColor = Muted;
        _shortcutNote.Margin = new Padding(26, 0, 0, 6);

        _onTop.Text = "Keep window on top";
        _onTop.AutoSize = true;
        _onTop.Margin = new Padding(0, 8, 0, 0);
        _onTop.CheckedChanged += (_, _) =>
        {
            _settings.AlwaysOnTop = _onTop.Checked;
            TopMost = _onTop.Checked;
            Persist();
        };

        _autostart.Text = "Start with Windows";
        _autostart.AutoSize = true;
        _autostart.Margin = new Padding(0, 4, 0, 0);
        _autostart.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            if (Autostart.Set(_autostart.Checked)) return;
            MessageBox.Show(this, "Windows would not accept the autostart entry.",
                "Jerboa", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _loading = true;
            _autostart.Checked = Autostart.IsEnabled;
            _loading = false;
        };

        _settingsPanel.Controls.Add(Caption("Folder"), 0, 0);
        _settingsPanel.Controls.Add(folderRow, 1, 0);
        _settingsPanel.Controls.Add(Caption("System audio"), 0, 1);
        _settingsPanel.Controls.Add(_playback, 1, 1);
        _settingsPanel.Controls.Add(Caption("Microphone"), 0, 2);
        _settingsPanel.Controls.Add(_microphone, 1, 2);
        _settingsPanel.Controls.Add(Caption("End recording"), 0, 3);
        _settingsPanel.Controls.Add(_autoStop, 1, 3);
        _settingsPanel.Controls.Add(Caption("Shortcut"), 0, 4);
        _settingsPanel.Controls.Add(shortcutRow, 1, 4);
        _settingsPanel.Controls.Add(_shortcutNote, 1, 5);

        _settingsPanel.Controls.Add(_onTop, 0, 6);
        _settingsPanel.SetColumnSpan(_onTop, 2);
        _settingsPanel.Controls.Add(_autostart, 0, 7);
        _settingsPanel.SetColumnSpan(_autostart, 2);
        var footer = BuildFooter();
        _settingsPanel.Controls.Add(footer, 0, 8);
        _settingsPanel.SetColumnSpan(footer, 2);

        return _settingsPanel;
    }

    private Control BuildFooter()
    {
        var footer = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 12, 0, 0)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var hint = new Label
        {
            Text = "One stereo MP3 per recording —\nmicrophone left, system audio right.",
            ForeColor = Muted,
            AutoSize = true,
            Margin = Padding.Empty
        };

        var version = new Label
        {
            Text = $"jerboa · v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0"}",
            ForeColor = Muted,
            Font = new Font("Consolas", 8f),
            AutoSize = true,
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
            Margin = new Padding(12, 0, 0, 2)
        };

        footer.Controls.Add(hint, 0, 0);
        footer.Controls.Add(version, 1, 0);
        return footer;
    }

    private void BuildTray()
    {
        _tray.Icon = _trayIcons.Idle;
        _tray.Visible = true;
        _tray.ContextMenuStrip = _trayMenu;
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleWindow(); };
        _trayMenu.Opening += (_, _) => BuildTrayMenu();
    }

    private void BuildTrayMenu()
    {
        _trayMenu.Items.Clear();

        switch (_session.State)
        {
            case SessionState.Idle:
                _trayMenu.Items.Add("Start recording", null, (_, _) => StartRecording());
                break;
            case SessionState.Recording:
                _trayMenu.Items.Add("Pause recording", null, (_, _) => { _session.Pause(); RefreshState(); });
                _trayMenu.Items.Add("Stop recording", null, (_, _) => StopRecording(false));
                break;
            case SessionState.Paused:
                _trayMenu.Items.Add("Resume recording", null, (_, _) => { _session.Resume(); RefreshState(); });
                _trayMenu.Items.Add("Stop recording", null, (_, _) => StopRecording(false));
                break;
        }

        _trayMenu.Items.Add("Open folder", null, (_, _) => OpenFolder(_lastFolder ?? _folder.Text));
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("Settings", null, (_, _) => { ShowWindow(); if (!_settingsOpen) ToggleSettings(); });
        _trayMenu.Items.Add("Exit", null, (_, _) => RequestExit());
    }

    private void ToggleSettings()
    {
        _settingsOpen = !_settingsOpen;
        _settingsPanel.Visible = _settingsOpen;
        ApplySettingsLayout();
    }

    /// <summary>
    /// Unfolded settings get a third more width than they would take by themselves, and
    /// the extra goes to the fields — a folder path cut off after "C:\Users\Nutzer\Mus"
    /// tells you nothing. Folding them away returns the window to its compact size.
    /// </summary>
    private void ApplySettingsLayout()
    {
        if (!_settingsOpen)
        {
            MinimumSize = Size.Empty;
            SetFieldWidths(0);
            return;
        }

        SetFieldWidths(0);
        PerformLayout();
        if (_settingsNaturalWidth == 0) _settingsNaturalWidth = Width;

        int target = (int)Math.Round(_settingsNaturalWidth * SettingsWidthFactor);
        SetFieldWidths(target - _settingsNaturalWidth);

        // Labels and padding grow along with the fields, so the first guess overshoots.
        // Measuring once and taking the difference back off lands on the intended width.
        PerformLayout();
        int overshoot = Width - target;
        if (overshoot > 0) SetFieldWidths(target - _settingsNaturalWidth - overshoot);

        MinimumSize = new Size(target, 0);
    }

    private void SetFieldWidths(int extra)
    {
        _folder.Width = FieldWidth - 42 + extra;
        foreach (var combo in new[] { _playback, _microphone, _autoStop }) combo.Width = FieldWidth + extra;
        _shortcut.Width = FieldWidth - 26 + extra;
    }

    /// <summary>Hooks used by the --screenshot diagnostic to capture each layout state.</summary>
    internal void PreviewSettings() { _settingsOpen = true; _notice.Visible = false; _settingsPanel.Visible = true; ApplySettingsLayout(); }

    internal void PreviewNotice() { _settingsOpen = false; _settingsPanel.Visible = false; ApplySettingsLayout(); _notice.Visible = true; }

    internal void ForceClose() { _reallyExit = true; Close(); }

    // ---------------------------------------------------------------- settings

    private void LoadSettingsIntoUi()
    {
        _folder.Text = _settings.Folder;
        _tips.SetToolTip(_folder, _settings.Folder);

        _playback.Items.AddRange(Devices.Playback().ToArray());
        _microphone.Items.AddRange(Devices.Microphones().ToArray());
        Select(_playback, _settings.PlaybackDeviceId);
        Select(_microphone, _settings.MicrophoneDeviceId);

        _autoStop.Items.Add("Never");
        for (int minutes = 15; minutes <= 720; minutes += 15) _autoStop.Items.Add(DescribeMinutes(minutes));
        _autoStop.SelectedIndex = _settings.AutoStopMinutes is >= 15 and <= 720 ? _settings.AutoStopMinutes / 15 : 0;

        _shortcutOn.Checked = _settings.ShortcutEnabled;
        _shortcut.Text = _settings.ShortcutText;
        _onTop.Checked = _settings.AlwaysOnTop;
        TopMost = _settings.AlwaysOnTop;
        _autostart.Checked = Autostart.IsEnabled;
    }

    private static string DescribeMinutes(int minutes)
    {
        if (minutes < 60) return $"After {minutes} min";
        int hours = minutes / 60;
        int rest = minutes % 60;
        return rest == 0 ? $"After {hours} h" : $"After {hours} h {rest} min";
    }

    private static void Select(ComboBox combo, string? id)
    {
        for (int i = 0; i < combo.Items.Count; i++)
            if (combo.Items[i] is AudioDevice device && device.Id == id) { combo.SelectedIndex = i; return; }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private void Persist()
    {
        if (_loading) return;   // populating the controls must not write defaults back over the file
        _settings.Folder = _folder.Text;
        _settings.PlaybackDeviceId = (_playback.SelectedItem as AudioDevice)?.Id;
        _settings.MicrophoneDeviceId = (_microphone.SelectedItem as AudioDevice)?.Id;
        _settings.AutoStopMinutes = Math.Max(0, _autoStop.SelectedIndex) * 15;
        _settings.ShortcutEnabled = _shortcutOn.Checked;
        _settings.AlwaysOnTop = _onTop.Checked;
        _settings.Save();
    }

    private void ApplyShortcut()
    {
        if (!_settings.ShortcutEnabled)
        {
            _hotkey.Unregister();
            _shortcut.Enabled = false;
            _shortcutNote.Text = "";
            return;
        }

        _shortcut.Enabled = true;
        bool registered = _hotkey.Register(
            _settings.ShortcutControl, _settings.ShortcutShift, _settings.ShortcutAlt, _settings.ShortcutKey);
        _shortcutNote.Text = registered ? "starts and stops recording" : "already used by another application";
        _shortcutNote.ForeColor = registered ? Muted : RecordRed;
    }

    private void ChooseFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            SelectedPath = Directory.Exists(_folder.Text) ? _folder.Text : Settings.DefaultFolder,
            UseDescriptionForTitle = true,
            Description = "Where recordings are saved"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _folder.Text = dialog.SelectedPath;
        _tips.SetToolTip(_folder, dialog.SelectedPath);
        Persist();
    }

    // ---------------------------------------------------------------- recording

    private void OnPrimary()
    {
        switch (_session.State)
        {
            case SessionState.Idle: StartRecording(); break;
            case SessionState.Recording: _session.Pause(); RefreshState(); break;
            case SessionState.Paused: _session.Resume(); RefreshState(); break;
        }
    }

    private void ToggleRecording()
    {
        if (_session.State == SessionState.Idle) StartRecording();
        else StopRecording(false);
    }

    private void StartRecording()
    {
        if (_session.State != SessionState.Idle) return;
        Persist();

        try
        {
            _session.Start(
                Devices.Resolve(_settings.PlaybackDeviceId, DataFlow.Render),
                Devices.Resolve(_settings.MicrophoneDeviceId, DataFlow.Capture),
                _folder.Text,
                DateTime.Now);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Jerboa", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _lastFolder = _folder.Text;
        _notice.Visible = false;
        if (_settingsOpen) ToggleSettings();
        RefreshState();
    }

    private void StopRecording(bool automatic)
    {
        if (_session.State == SessionState.Idle) return;

        SessionResult result;
        try { result = _session.Stop(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Jerboa", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            RefreshState();
            return;
        }

        _lastFolder = Path.GetDirectoryName(result.FilePath) ?? _folder.Text;
        _notice.Visible = true;
        _systemMeter.Reset();
        _micMeter.Reset();
        RefreshState();

        if (automatic || !Visible) Notify("Recording finished", Path.GetFileName(result.FilePath));
    }

    private void OnDeviceLost(string message)
    {
        BeginInvoke(() =>
        {
            if (_session.State == SessionState.Idle) return;
            int minutes = Math.Max(1, (int)Math.Round(_session.Duration.TotalMinutes));
            StopRecording(false);
            Notify("Recording stopped", $"{message} {minutes} min were saved.");
        });
    }

    private void OnTick()
    {
        if (_session.State != SessionState.Idle)
        {
            var elapsed = _session.Duration;
            _time.Text = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
            _tray.Text = Truncate($"Jerboa — {(_session.State == SessionState.Paused ? "paused" : "recording")} {_time.Text}");

            int limit = _settings.AutoStopMinutes;
            if (limit > 0 && elapsed.TotalMinutes >= limit) { StopRecording(true); return; }

            if (_session.State == SessionState.Recording)
            {
                int frame = TrayIcons.FrameIndex(elapsed);
                if (frame != _pulseFrame)
                {
                    _pulseFrame = frame;
                    _tray.Icon = _trayIcons.RecordingAt(elapsed);
                }
            }
        }

        _systemMeter.Value = _session.SystemPeak;
        _micMeter.Value = _session.MicrophonePeak;
    }

    private static string Truncate(string text) => text.Length <= 63 ? text : text[..63];

    /// <summary>
    /// Starting a recording is the one thing this window is for, so that button carries
    /// the colour. Once a recording is running the button becomes Pause, which is an
    /// ordinary action and gets an ordinary button.
    /// </summary>
    private static void StyleAsRecordButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = RecordRed;
        button.ForeColor = Color.White;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = ControlPaint.Light(RecordRed, 0.15f);
        button.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(RecordRed, 0.08f);
    }

    private static void StyleAsPlainButton(Button button)
    {
        button.FlatStyle = FlatStyle.System;
        button.UseVisualStyleBackColor = true;
        button.ForeColor = Ink;
    }

    private void RefreshState()
    {
        switch (_session.State)
        {
            case SessionState.Idle:
                _time.Text = "00:00:00";
                _time.ForeColor = Muted;
                _status.Text = "Ready";
                _status.ForeColor = Muted;
                _primary.Text = "Start recording";
                StyleAsRecordButton(_primary);
                _tray.Text = "Jerboa — ready";
                _buttons.ColumnStyles[0] = new ColumnStyle(SizeType.Percent, 100);
                _buttons.ColumnStyles[1] = new ColumnStyle(SizeType.Absolute, 0);
                _buttons.ColumnStyles[2] = new ColumnStyle(SizeType.AutoSize);
                _secondary.Visible = false;
                _gear.Visible = true;
                break;

            case SessionState.Recording:
            case SessionState.Paused:
                bool paused = _session.State == SessionState.Paused;
                _time.ForeColor = Ink;
                _status.Text = paused ? "Paused" : "Recording";
                _status.ForeColor = paused ? PauseAmber : RecordRed;
                _primary.Text = paused ? "Resume" : "Pause";
                StyleAsPlainButton(_primary);
                _secondary.Text = "Stop";
                _buttons.ColumnStyles[0] = new ColumnStyle(SizeType.Percent, 50);
                _buttons.ColumnStyles[1] = new ColumnStyle(SizeType.Percent, 50);
                _buttons.ColumnStyles[2] = new ColumnStyle(SizeType.Absolute, 0);
                _secondary.Visible = true;
                _gear.Visible = false;
                break;
        }

        _pulseFrame = -1;
        _tray.Icon = _trayIcons.For(_session.State, _session.Duration);
    }

    // ---------------------------------------------------------------- shell

    private void Notify(string title, string message)
    {
        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText = message;
        _tray.ShowBalloonTip(6000);
    }

    private static void OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { /* nothing useful to say if the shell refuses */ }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (_placeAutomatically) MoveToBottomLeft();
    }

    /// <summary>
    /// Opens in the bottom-left corner of the main screen, clear of the taskbar: out of
    /// the way of the meeting window, and in the same place every time.
    /// </summary>
    private void MoveToBottomLeft()
    {
        PerformLayout();
        var area = (Screen.PrimaryScreen ?? Screen.FromControl(this)).WorkingArea;
        Location = new Point(area.Left + WindowMargin, area.Bottom - Height - WindowMargin);
    }

    /// <summary>
    /// Unfolding the settings makes the window taller. Sitting at the bottom of the
    /// screen, that would push it behind the taskbar, so it climbs instead.
    /// </summary>
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (!_placeAutomatically || !IsHandleCreated) return;

        var area = Screen.FromControl(this).WorkingArea;
        int y = Math.Min(Top, area.Bottom - Height - WindowMargin);
        int x = Math.Min(Left, area.Right - Width - WindowMargin);
        Location = new Point(Math.Max(area.Left, x), Math.Max(area.Top, y));
    }

    /// <summary>Parks the window off-screen so the screenshot mode can draw it unseen.</summary>
    internal void PlaceOffScreen()
    {
        _placeAutomatically = false;
        Location = new Point(-4000, -4000);
    }

    /// <summary>A second launch broadcasts a message rather than opening another recorder.</summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == SingleInstance.ShowWindowMessage) ShowWindow();
        base.WndProc(ref m);
    }

    /// <summary>Lets the first Show() be suppressed when Windows started us at logon.</summary>
    protected override void SetVisibleCore(bool value)
    {
        if (_startHidden)
        {
            _startHidden = false;
            if (!IsHandleCreated) CreateHandle();
            value = false;
        }
        base.SetVisibleCore(value);
    }

    private void ToggleWindow()
    {
        if (Visible && WindowState != FormWindowState.Minimized) Hide();
        else ShowWindow();
    }

    private void ShowWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void RequestExit()
    {
        if (_session.State != SessionState.Idle)
        {
            ShowWindow();
            var answer = MessageBox.Show(this,
                "A recording is still running. Stop it and quit?",
                "Jerboa", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;
            StopRecording(false);
        }

        _reallyExit = true;
        Close();
    }

    /// <summary>Closing the window only puts it away — the recording and the tray icon stay.</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _timer.Stop();
        Persist();
        _hotkey.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _session.Dispose();
        _trayIcons.Dispose();
        base.OnFormClosing(e);
    }
}
