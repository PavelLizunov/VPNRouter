using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Service;

namespace VPNRouter.GUI;

/// <summary>
/// Main settings window. Tabs: Servers (VLESS URIs) | Apps (profiles) | bottom Start/Stop bar.
/// </summary>
public class MainForm : Form
{
    private readonly VpnEngine _engine;
    private readonly TrayApplicationContext _tray;

    // ── Servers tab ──
    private TextBox _uriInput = null!;
    private Button _addBtn = null!;
    private ListView _serverList = null!;
    private Button _removeBtn = null!;
    private Button _clearBtn = null!;
    private Button _upBtn = null!;
    private Button _downBtn = null!;

    // ── Apps tab ──
    private CheckedListBox _profileList = null!;

    // ── Bottom panel ──
    private Button _startStopBtn = null!;
    private CheckBox _autostartCheck = null!;
    private Label _statusLabel = null!;

    // ── State ──
    private AppSettings _settings = null!;
    private readonly List<VlessServerEntry> _servers = new();

    public MainForm(VpnEngine engine, TrayApplicationContext tray)
    {
        _engine = engine;
        _tray = tray;
        InitializeComponent();
        LoadSettings();

        _engine.StatusChanged += OnEngineStatus;
    }

    /// <summary>
    /// Called by TrayApplicationContext to sync MainForm UI with current engine state.
    /// </summary>
    public void RefreshStatus()
    {
        if (InvokeRequired)
        {
            BeginInvoke(RefreshStatus);
            return;
        }

        bool running = _engine.IsRunning || _tray.RunningAsService;
        UpdateUI(running);
    }

    // ─── UI Construction ─────────────────────────────────────────────────────

    private void InitializeComponent()
    {
        Text = "VPNRouter Settings";
        Size = new Size(520, 600);
        MinimumSize = new Size(460, 520);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var tabs = new TabControl { Dock = DockStyle.Fill };

        // ── Servers tab ──
        var serversPage = new TabPage("Servers");
        BuildServersTab(serversPage);
        tabs.TabPages.Add(serversPage);

        // ── Apps tab ──
        var appsPage = new TabPage("Applications");
        BuildAppsTab(appsPage);
        tabs.TabPages.Add(appsPage);

        // ── Bottom panel ──
        var bottomPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 85,
            Padding = new Padding(10, 5, 10, 5)
        };

        _statusLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 20,
            Text = "Not running",
            ForeColor = Color.Gray,
            TextAlign = ContentAlignment.MiddleCenter
        };

        _autostartCheck = new CheckBox
        {
            Dock = DockStyle.Top,
            Height = 22,
            Text = "Autostart with Windows (runs as background service, no GUI needed)",
            Checked = ServiceInstaller.IsInstalled(),
            Padding = new Padding(0, 2, 0, 0)
        };
        _autostartCheck.CheckedChanged += OnAutostartChanged;

        _startStopBtn = new Button
        {
            Dock = DockStyle.Bottom,
            Height = 35,
            Text = "▶ Start VPN",
            BackColor = Color.MediumSeaGreen,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font(Font.FontFamily, 11, FontStyle.Bold)
        };
        _startStopBtn.Click += OnStartStop;

        bottomPanel.Controls.Add(_startStopBtn);
        bottomPanel.Controls.Add(_autostartCheck);
        bottomPanel.Controls.Add(_statusLabel);

        Controls.Add(tabs);
        Controls.Add(bottomPanel);

        // Set initial UI state
        bool running = _engine.IsRunning || _tray.RunningAsService;
        if (running) UpdateUI(true);
    }

    private void BuildServersTab(TabPage page)
    {
        page.Padding = new Padding(10);

        var inputLabel = new Label
        {
            Text = "Paste VLESS URI(s) — first server = Primary, others = Fallback:",
            Dock = DockStyle.Top,
            Height = 20
        };

        _uriInput = new TextBox
        {
            Dock = DockStyle.Top,
            Height = 60,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            PlaceholderText = "vless://uuid@server:443?security=reality&sni=...#name"
        };

        _addBtn = new Button
        {
            Text = "Add Server(s)",
            Dock = DockStyle.Top,
            Height = 30
        };
        _addBtn.Click += OnAddServer;

        _serverList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true
        };
        _serverList.Columns.Add("Role", 70);
        _serverList.Columns.Add("Name", 110);
        _serverList.Columns.Add("Server", 160);
        _serverList.Columns.Add("Port", 50);
        _serverList.Columns.Add("Security", 70);

        var btnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 35,
            FlowDirection = FlowDirection.RightToLeft
        };

        _clearBtn = new Button { Text = "Clear All", Width = 70 };
        _clearBtn.Click += (_, _) => { _servers.Clear(); RefreshServerList(); SaveSettings(); };

        _removeBtn = new Button { Text = "Remove", Width = 70 };
        _removeBtn.Click += OnRemoveServer;

        _downBtn = new Button { Text = "▼ Down", Width = 65 };
        _downBtn.Click += OnMoveDown;

        _upBtn = new Button { Text = "▲ Up", Width = 55 };
        _upBtn.Click += OnMoveUp;

        btnPanel.Controls.Add(_clearBtn);
        btnPanel.Controls.Add(_removeBtn);
        btnPanel.Controls.Add(_downBtn);
        btnPanel.Controls.Add(_upBtn);

        // Order matters: last added = top of dock
        page.Controls.Add(_serverList);
        page.Controls.Add(btnPanel);
        page.Controls.Add(_addBtn);
        page.Controls.Add(_uriInput);
        page.Controls.Add(inputLabel);
    }

    private void BuildAppsTab(TabPage page)
    {
        page.Padding = new Padding(10);

        var label = new Label
        {
            Text = "Select application groups to route through VPN:",
            Dock = DockStyle.Top,
            Height = 25
        };

        _profileList = new CheckedListBox
        {
            Dock = DockStyle.Fill,
            CheckOnClick = true,
            Font = new Font(Font.FontFamily, 10)
        };

        // Load built-in profile names
        var builtIn = BuiltInProfiles.Get();
        foreach (var profile in builtIn.Profiles)
        {
            _profileList.Items.Add(profile.Name, isChecked: false);
        }

        page.Controls.Add(_profileList);
        page.Controls.Add(label);
    }

    // ─── Data loading ────────────────────────────────────────────────────────

    private void LoadSettings()
    {
        try
        {
            _settings = SettingsLoader.Load();
        }
        catch
        {
            _settings = new AppSettings();
        }

        // Load servers from config
        _servers.Clear();
        _servers.AddRange(_settings.Vless.GetEffectiveServers());
        RefreshServerList();

        // Check active profile checkboxes
        if (!string.IsNullOrEmpty(_settings.ActiveProfile))
        {
            var activeNames = _settings.ActiveProfile
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < _profileList.Items.Count; i++)
            {
                var name = _profileList.Items[i].ToString()!;
                if (activeNames.Contains(name))
                    _profileList.SetItemChecked(i, true);
            }
        }
    }

    private void RefreshServerList()
    {
        var selectedIdx = _serverList.SelectedIndices.Count > 0
            ? _serverList.SelectedIndices[0]
            : -1;

        _serverList.Items.Clear();
        for (int i = 0; i < _servers.Count; i++)
        {
            var s = _servers[i];
            var role = i == 0 ? "★ Primary" : $"Fallback {i}";
            var item = new ListViewItem(role);
            item.SubItems.Add(string.IsNullOrEmpty(s.Name) ? "(no name)" : s.Name);
            item.SubItems.Add(s.Server);
            item.SubItems.Add(s.Port.ToString());
            item.SubItems.Add(s.Security);

            // Highlight primary
            if (i == 0)
            {
                item.ForeColor = Color.DarkGreen;
                item.Font = new Font(_serverList.Font, FontStyle.Bold);
            }

            _serverList.Items.Add(item);
        }

        // Restore selection
        if (selectedIdx >= 0 && selectedIdx < _serverList.Items.Count)
        {
            _serverList.Items[selectedIdx].Selected = true;
            _serverList.Items[selectedIdx].Focused = true;
        }
    }

    // ─── Save config ─────────────────────────────────────────────────────────

    private void SaveSettings()
    {
        // Update servers
        _settings.Vless.Servers = new List<VlessServerEntry>(_servers);
        // Clear legacy fields if using multi-server
        if (_servers.Count > 0)
            _settings.Vless.Server = string.Empty;

        // Update active profile
        var checkedNames = new List<string>();
        for (int i = 0; i < _profileList.Items.Count; i++)
        {
            if (_profileList.GetItemChecked(i))
                checkedNames.Add(_profileList.Items[i].ToString()!);
        }
        _settings.ActiveProfile = string.Join(",", checkedNames);

        SettingsLoader.Save(_settings);
    }

    // ─── Event handlers ──────────────────────────────────────────────────────

    private void OnAddServer(object? sender, EventArgs e)
    {
        var text = _uriInput.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        try
        {
            var entries = VlessUriParser.ParseMultiple(text);
            if (entries.Count == 0)
            {
                MessageBox.Show("No valid VLESS URIs found.", "VPNRouter",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _servers.AddRange(entries);
            RefreshServerList();
            _uriInput.Clear();

            SaveSettings();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to parse VLESS URI:\n{ex.Message}", "VPNRouter",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnRemoveServer(object? sender, EventArgs e)
    {
        if (_serverList.SelectedIndices.Count == 0) return;

        for (int i = _serverList.SelectedIndices.Count - 1; i >= 0; i--)
        {
            var idx = _serverList.SelectedIndices[i];
            _servers.RemoveAt(idx);
        }

        RefreshServerList();
        SaveSettings();
    }

    private void OnMoveUp(object? sender, EventArgs e)
    {
        if (_serverList.SelectedIndices.Count == 0) return;
        var idx = _serverList.SelectedIndices[0];
        if (idx <= 0) return;

        (_servers[idx], _servers[idx - 1]) = (_servers[idx - 1], _servers[idx]);
        _serverList.SelectedIndices.Clear();
        RefreshServerList();
        _serverList.Items[idx - 1].Selected = true;
        _serverList.Items[idx - 1].Focused = true;
        SaveSettings();
    }

    private void OnMoveDown(object? sender, EventArgs e)
    {
        if (_serverList.SelectedIndices.Count == 0) return;
        var idx = _serverList.SelectedIndices[0];
        if (idx >= _servers.Count - 1) return;

        (_servers[idx], _servers[idx + 1]) = (_servers[idx + 1], _servers[idx]);
        _serverList.SelectedIndices.Clear();
        RefreshServerList();
        _serverList.Items[idx + 1].Selected = true;
        _serverList.Items[idx + 1].Focused = true;
        SaveSettings();
    }

    private void OnAutostartChanged(object? sender, EventArgs e)
    {
        SaveSettings(); // save config before installing service

        if (_autostartCheck.Checked)
        {
            // Install and start service
            var serviceExe = Path.Combine(AppContext.BaseDirectory, "service", "VPNRouter.Service.exe");
            if (!File.Exists(serviceExe))
                serviceExe = Path.Combine(AppContext.BaseDirectory, "VPNRouter.Service.exe");

            if (!File.Exists(serviceExe))
            {
                MessageBox.Show("VPNRouter.Service.exe not found.\nAutostart requires the service binary.",
                    "VPNRouter", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _autostartCheck.Checked = false;
                return;
            }

            // Stop in-process VPN first if running
            if (_engine.IsRunning)
            {
                _engine.Stop();
            }

            var result = ServiceInstaller.Install(serviceExe);
            if (!result.Success)
            {
                MessageBox.Show($"Failed to install service:\n{result.Message}",
                    "VPNRouter", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _autostartCheck.Checked = false;
                return;
            }

            ServiceInstaller.Start();
            _tray.RunningAsService = true;
            _tray.SyncTrayState(null);

            _statusLabel.Text = "Running as Windows Service (autostart enabled)";
            _statusLabel.ForeColor = Color.Green;
            _startStopBtn.Text = "⬛ Stop VPN";
            _startStopBtn.BackColor = Color.IndianRed;
        }
        else
        {
            // Stop and uninstall service
            if (ServiceInstaller.IsRunning())
            {
                ServiceInstaller.Stop();
            }

            if (ServiceInstaller.IsInstalled())
            {
                ServiceInstaller.Uninstall();
            }

            _tray.RunningAsService = false;
            _tray.SyncTrayState(null);
            UpdateUI(false);
        }
    }

    private async void OnStartStop(object? sender, EventArgs e)
    {
        // If running as service, stop service
        if (_tray.RunningAsService)
        {
            ServiceInstaller.Stop();
            _tray.RunningAsService = false;
            _tray.SyncTrayState(null);
            UpdateUI(false);
            return;
        }

        // If running in-process, stop
        if (_engine.IsRunning)
        {
            _engine.Stop();
            UpdateUI(false);
            return;
        }

        // Start VPN
        SaveSettings();

        if (_servers.Count == 0)
        {
            MessageBox.Show("Add at least one VLESS server first.", "VPNRouter",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var checkedCount = Enumerable.Range(0, _profileList.Items.Count)
            .Count(i => _profileList.GetItemChecked(i));

        if (checkedCount == 0)
        {
            MessageBox.Show("Select at least one application group.", "VPNRouter",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            _startStopBtn.Enabled = false;
            _startStopBtn.Text = "Starting...";

            var settings = SettingsLoader.Load();
            await _engine.StartAsync(settings);

            UpdateUI(true);
        }
        catch (Exception ex)
        {
            UpdateUI(false);
            MessageBox.Show($"Failed to start VPN:\n{ex.Message}", "VPNRouter",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateUI(bool running)
    {
        _startStopBtn.Enabled = true;
        _startStopBtn.Text = running ? "⬛ Stop VPN" : "▶ Start VPN";
        _startStopBtn.BackColor = running ? Color.IndianRed : Color.MediumSeaGreen;

        if (_tray.RunningAsService)
        {
            _statusLabel.Text = "Running as Windows Service (autostart enabled)";
            _statusLabel.ForeColor = Color.Green;
        }
        else if (running)
        {
            _statusLabel.Text = $"Running — {_engine.ActiveProfileName} — PID {_engine.SingBoxPid}";
            _statusLabel.ForeColor = Color.Green;
        }
        else
        {
            _statusLabel.Text = "Not running";
            _statusLabel.ForeColor = Color.Gray;
        }
    }

    private void OnEngineStatus(string msg)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnEngineStatus(msg));
            return;
        }

        _statusLabel.Text = msg;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Don't exit app — just hide to tray
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _engine.StatusChanged -= OnEngineStatus;
        base.OnFormClosing(e);
    }
}
