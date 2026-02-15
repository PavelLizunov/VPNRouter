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
    private TreeView _profileTree = null!;
    private TextBox _customAppInput = null!;
    private Button _addCustomBtn = null!;
    private Button _removeCustomBtn = null!;
    private readonly List<string> _customApps = new();

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
            Text = "Check groups to route through VPN (expand to see apps inside):",
            Dock = DockStyle.Top,
            Height = 25
        };

        _profileTree = new TreeView
        {
            Dock = DockStyle.Fill,
            CheckBoxes = true,
            Font = new Font(Font.FontFamily, 10),
            ShowLines = true,
            ShowPlusMinus = true,
            ShowRootLines = true
        };
        _profileTree.AfterCheck += OnProfileTreeCheck;

        // Load built-in profiles with their processes
        var builtIn = BuiltInProfiles.Get();
        foreach (var profile in builtIn.Profiles)
        {
            var node = new TreeNode($"{profile.Name}  —  {profile.Description}") { Tag = profile.Name };
            foreach (var proc in profile.Processes)
            {
                var childText = proc.IncludeChildren
                    ? $"{proc.Name} (+ child processes)"
                    : proc.Name;
                node.Nodes.Add(new TreeNode(childText) { ForeColor = Color.Gray });
            }
            _profileTree.Nodes.Add(node);
        }

        // Custom apps section at bottom
        var customPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 65
        };

        var customLabel = new Label
        {
            Text = "Add custom app (exe name, e.g. spotify.exe):",
            Dock = DockStyle.Top,
            Height = 18,
            ForeColor = Color.DimGray
        };

        var inputRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 30,
            FlowDirection = FlowDirection.LeftToRight
        };

        _customAppInput = new TextBox
        {
            Width = 250,
            PlaceholderText = "app.exe"
        };
        _customAppInput.KeyDown += (_, ke) => { if (ke.KeyCode == Keys.Enter) { OnAddCustomApp(null, EventArgs.Empty); ke.SuppressKeyPress = true; } };

        _addCustomBtn = new Button { Text = "Add", Width = 55 };
        _addCustomBtn.Click += OnAddCustomApp;

        _removeCustomBtn = new Button { Text = "Remove checked custom", Width = 140 };
        _removeCustomBtn.Click += OnRemoveCustomApp;

        inputRow.Controls.Add(_customAppInput);
        inputRow.Controls.Add(_addCustomBtn);
        inputRow.Controls.Add(_removeCustomBtn);

        customPanel.Controls.Add(inputRow);
        customPanel.Controls.Add(customLabel);

        page.Controls.Add(_profileTree);
        page.Controls.Add(customPanel);
        page.Controls.Add(label);
    }

    private void OnProfileTreeCheck(object? sender, TreeViewEventArgs e)
    {
        // Only handle user actions, not programmatic checks
        if (e.Action == TreeViewAction.Unknown) return;

        var node = e.Node!;

        // If parent node checked/unchecked — propagate to children
        if (node.Parent == null)
        {
            foreach (TreeNode child in node.Nodes)
                child.Checked = node.Checked;
        }
    }

    private void OnAddCustomApp(object? sender, EventArgs e)
    {
        var name = _customAppInput.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;

        // Ensure .exe extension
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name += ".exe";

        // Avoid duplicates
        if (_customApps.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            _customAppInput.Clear();
            return;
        }

        _customApps.Add(name);

        // Add to tree under "Custom Apps" node
        var customRoot = GetOrCreateCustomNode();
        customRoot.Nodes.Add(new TreeNode(name) { Checked = true });
        customRoot.Checked = true;
        customRoot.Expand();

        _customAppInput.Clear();
        SaveSettings();
    }

    private void OnRemoveCustomApp(object? sender, EventArgs e)
    {
        var customRoot = FindCustomNode();
        if (customRoot == null) return;

        // Collect checked children to remove
        var toRemove = new List<TreeNode>();
        foreach (TreeNode child in customRoot.Nodes)
        {
            if (child.Checked)
                toRemove.Add(child);
        }

        foreach (var node in toRemove)
        {
            _customApps.Remove(node.Text);
            customRoot.Nodes.Remove(node);
        }

        if (customRoot.Nodes.Count == 0)
        {
            _profileTree.Nodes.Remove(customRoot);
        }

        SaveSettings();
    }

    private TreeNode GetOrCreateCustomNode()
    {
        var existing = FindCustomNode();
        if (existing != null) return existing;

        var node = new TreeNode("Custom Apps  —  Your custom applications")
        {
            Tag = "_custom",
            ForeColor = Color.DarkBlue,
            NodeFont = new Font(_profileTree.Font, FontStyle.Bold)
        };
        _profileTree.Nodes.Add(node);
        return node;
    }

    private TreeNode? FindCustomNode()
    {
        foreach (TreeNode node in _profileTree.Nodes)
        {
            if (node.Tag?.ToString() == "_custom") return node;
        }
        return null;
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

        // Check active profile checkboxes in tree
        if (!string.IsNullOrEmpty(_settings.ActiveProfile))
        {
            var activeNames = _settings.ActiveProfile
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (TreeNode node in _profileTree.Nodes)
            {
                var profileName = node.Tag?.ToString();
                if (profileName != null && activeNames.Contains(profileName))
                {
                    node.Checked = true;
                    foreach (TreeNode child in node.Nodes)
                        child.Checked = true;
                }
            }
        }

        // Load custom apps from config (stored as _custom profile processes)
        if (_settings.ActiveProfile?.Contains("_custom", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Custom apps are stored in config.yaml under a special key
            // For now, they're persisted as custom_apps list in AppSettings
        }

        // Load custom apps from settings if any
        if (_settings.CustomApps != null)
        {
            foreach (var app in _settings.CustomApps)
            {
                _customApps.Add(app);
            }

            if (_customApps.Count > 0)
            {
                var customRoot = GetOrCreateCustomNode();
                foreach (var app in _customApps)
                {
                    customRoot.Nodes.Add(new TreeNode(app) { Checked = true });
                }
                customRoot.Checked = true;
                customRoot.Expand();
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

        // Update active profile from tree
        var checkedNames = new List<string>();
        foreach (TreeNode node in _profileTree.Nodes)
        {
            var profileName = node.Tag?.ToString();
            if (profileName != null && profileName != "_custom" && node.Checked)
                checkedNames.Add(profileName);
        }
        _settings.ActiveProfile = string.Join(",", checkedNames);

        // Save custom apps
        _settings.CustomApps = new List<string>(_customApps);

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

        var checkedCount = _profileTree.Nodes.Cast<TreeNode>()
            .Count(n => n.Checked);

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
