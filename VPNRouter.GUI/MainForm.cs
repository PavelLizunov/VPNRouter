using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Service;

namespace VPNRouter.GUI;

/// <summary>
/// Main settings window. Header with logo, tabs: Servers | Apps, bottom status + Start/Stop + Apply.
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
    private Button _applyBtn = null!;
    private CheckBox _autostartCheck = null!;
    private Button _restartServiceBtn = null!;
    private Label _statusLabel = null!;
    private Panel _statusPanel = null!;
    private Label _statusDot = null!;

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
        Text = AppBranding.WindowTitle;
        Size = new Size(540, 680);
        MinimumSize = new Size(480, 560);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Background;
        Font = Theme.BodyFont;
        Icon = AppBranding.GetIcon(32);

        // ── Header panel (logo + brand) ──
        var header = BuildHeaderPanel();

        // ── Tabs ──
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.Font = Theme.BodyFont;

        var serversPage = new TabPage("Servers") { BackColor = Theme.Background, Padding = new Padding(10) };
        BuildServersTab(serversPage);
        tabs.TabPages.Add(serversPage);

        var appsPage = new TabPage("Applications") { BackColor = Theme.Background, Padding = new Padding(10) };
        BuildAppsTab(appsPage);
        tabs.TabPages.Add(appsPage);

        // ── Status bar ──
        _statusPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 32,
            BackColor = Theme.Background,
            Padding = new Padding(14, 0, 14, 0)
        };

        _statusDot = new Label
        {
            Text = "\u25cf",
            Font = new Font("Segoe UI", 10f),
            ForeColor = Theme.TextMuted,
            AutoSize = true,
            Location = new Point(14, 6)
        };

        _statusLabel = new Label
        {
            Text = "Not connected",
            Font = Theme.BodyFont,
            ForeColor = Theme.TextMuted,
            AutoSize = true,
            Location = new Point(32, 8)
        };

        _statusPanel.Paint += (s, e) =>
        {
            using var pen = new Pen(Theme.SurfaceBorder);
            e.Graphics.DrawLine(pen, 0, 0, _statusPanel.Width, 0);
        };

        _statusPanel.Controls.Add(_statusDot);
        _statusPanel.Controls.Add(_statusLabel);

        // ── Action panel (Start/Stop + Apply + autostart) ──
        var actionPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 80,
            BackColor = Theme.Surface,
            Padding = new Padding(14, 8, 14, 8)
        };

        _startStopBtn = new Button
        {
            Text = "\u25b6  Start VPN",
            Size = new Size(330, 36),
            Location = new Point(14, 4),
            Font = Theme.StartStopFont,
            Cursor = Cursors.Hand,
            FlatStyle = FlatStyle.Flat
        };
        _startStopBtn.FlatAppearance.BorderSize = 0;
        _startStopBtn.Click += OnStartStop;
        ApplyStartStyle();

        _applyBtn = new Button
        {
            Text = "\u21bb  Apply",
            Size = new Size(155, 36),
            Location = new Point(350, 4),
            Font = Theme.StartStopFont,
            Cursor = Cursors.Hand,
            FlatStyle = FlatStyle.Flat,
            Visible = false
        };
        Theme.ApplySecondary(_applyBtn);
        _applyBtn.FlatAppearance.BorderSize = 0;
        _applyBtn.BackColor = Color.FromArgb(245, 158, 11); // amber/orange
        _applyBtn.ForeColor = Color.White;
        _applyBtn.Click += OnApplyChanges;

        _autostartCheck = new CheckBox
        {
            Text = "Autostart with Windows",
            Font = Theme.SmallFont,
            ForeColor = Theme.TextSecondary,
            Checked = ServiceInstaller.IsInstalled(),
            AutoSize = true,
            Location = new Point(14, 50),
            BackColor = Theme.Surface
        };
        _autostartCheck.CheckedChanged += OnAutostartChanged;

        _restartServiceBtn = new Button
        {
            Text = "\u21bb  Restart Service",
            Size = new Size(130, 24),
            Location = new Point(200, 50),
            Font = Theme.SmallFont,
            Cursor = Cursors.Hand,
            FlatStyle = FlatStyle.Flat,
            Visible = false
        };
        Theme.ApplySecondary(_restartServiceBtn);
        _restartServiceBtn.FlatAppearance.BorderSize = 0;
        _restartServiceBtn.Click += OnRestartService;

        actionPanel.Controls.Add(_startStopBtn);
        actionPanel.Controls.Add(_applyBtn);
        actionPanel.Controls.Add(_autostartCheck);
        actionPanel.Controls.Add(_restartServiceBtn);

        // ── Dock order: last added docks first ──
        // Fill = tabs, Bottom = status then action, Top = header
        Controls.Add(tabs);
        Controls.Add(_statusPanel);
        Controls.Add(actionPanel);
        Controls.Add(header);

        // Set initial UI state
        bool running = _engine.IsRunning || _tray.RunningAsService;
        if (running) UpdateUI(true);
    }

    private Panel BuildHeaderPanel()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 72,
            BackColor = Theme.Surface
        };

        var logo = new PictureBox
        {
            Image = AppBranding.GetLogo(),
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(50, 50),
            Location = new Point(16, 11),
            BackColor = Color.Transparent
        };

        var title = new Label
        {
            Text = AppBranding.AppName,
            Font = Theme.HeaderFont,
            ForeColor = Theme.Primary,
            AutoSize = true,
            Location = new Point(74, 12),
            BackColor = Color.Transparent
        };

        var subtitle = new Label
        {
            Text = $"by {AppBranding.Publisher}  \u00b7  v{AppBranding.Version}",
            Font = Theme.SubHeaderFont,
            ForeColor = Theme.TextMuted,
            AutoSize = true,
            Location = new Point(76, 42),
            BackColor = Color.Transparent
        };

        panel.Controls.Add(logo);
        panel.Controls.Add(title);
        panel.Controls.Add(subtitle);

        // Bottom border
        panel.Paint += (s, e) =>
        {
            using var pen = new Pen(Theme.SurfaceBorder);
            e.Graphics.DrawLine(pen, 0, panel.Height - 1, panel.Width, panel.Height - 1);
        };

        return panel;
    }

    private void BuildServersTab(TabPage page)
    {
        var inputLabel = new Label
        {
            Text = "Paste VLESS URI(s) \u2014 first server = Primary, others = Fallback:",
            Dock = DockStyle.Top,
            Height = 22,
            ForeColor = Theme.TextSecondary,
            Font = Theme.BodyFont
        };

        _uriInput = new TextBox
        {
            Dock = DockStyle.Top,
            Height = 60,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            PlaceholderText = "vless://uuid@server:443?security=reality&sni=...#name",
            BackColor = Theme.InputBackground,
            Font = Theme.BodyFont
        };

        _addBtn = new Button
        {
            Text = "Add Server(s)",
            Dock = DockStyle.Top,
            Height = 32
        };
        Theme.ApplyPrimary(_addBtn);
        _addBtn.Click += OnAddServer;

        _serverList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            BackColor = Theme.Surface,
            ForeColor = Theme.TextPrimary,
            Font = Theme.BodyFont
        };
        _serverList.Columns.Add("Role", 70);
        _serverList.Columns.Add("Name", 110);
        _serverList.Columns.Add("Server", 160);
        _serverList.Columns.Add("Port", 50);
        _serverList.Columns.Add("Security", 70);

        var btnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 36,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = Theme.Background
        };

        _clearBtn = new Button { Text = "Clear All", Width = 72, Height = 28 };
        Theme.ApplySecondary(_clearBtn);
        _clearBtn.Click += (_, _) => { _servers.Clear(); RefreshServerList(); SaveSettings(); };

        _removeBtn = new Button { Text = "Remove", Width = 72, Height = 28 };
        Theme.ApplySecondary(_removeBtn);
        _removeBtn.Click += OnRemoveServer;

        _downBtn = new Button { Text = "\u25bc Down", Width = 68, Height = 28 };
        Theme.ApplySecondary(_downBtn);
        _downBtn.Click += OnMoveDown;

        _upBtn = new Button { Text = "\u25b2 Up", Width = 58, Height = 28 };
        Theme.ApplySecondary(_upBtn);
        _upBtn.Click += OnMoveUp;

        btnPanel.Controls.Add(_clearBtn);
        btnPanel.Controls.Add(_removeBtn);
        btnPanel.Controls.Add(_downBtn);
        btnPanel.Controls.Add(_upBtn);

        // Dock order: last added = top
        page.Controls.Add(_serverList);
        page.Controls.Add(btnPanel);
        page.Controls.Add(_addBtn);
        page.Controls.Add(_uriInput);
        page.Controls.Add(inputLabel);
    }

    private void BuildAppsTab(TabPage page)
    {
        var label = new Label
        {
            Text = "Check groups to route through VPN (expand to see apps inside):",
            Dock = DockStyle.Top,
            Height = 25,
            ForeColor = Theme.TextSecondary,
            Font = Theme.BodyFont
        };

        _profileTree = new TreeView
        {
            Dock = DockStyle.Fill,
            CheckBoxes = true,
            Font = new Font("Segoe UI", 10),
            ShowLines = true,
            ShowPlusMinus = true,
            ShowRootLines = true,
            BackColor = Theme.Surface,
            ForeColor = Theme.TextPrimary
        };
        _profileTree.AfterCheck += OnProfileTreeCheck;

        // Load built-in profiles with their processes
        var builtIn = BuiltInProfiles.Get();
        foreach (var profile in builtIn.Profiles)
        {
            var node = new TreeNode($"{profile.Name}  \u2014  {profile.Description}") { Tag = profile.Name };
            foreach (var proc in profile.Processes)
            {
                var childText = proc.IncludeChildren
                    ? $"{proc.Name} (+ child processes)"
                    : proc.Name;
                node.Nodes.Add(new TreeNode(childText) { ForeColor = Theme.TextMuted });
            }
            _profileTree.Nodes.Add(node);
        }

        // Custom apps section at bottom
        var customPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 65,
            BackColor = Theme.Background
        };

        var customLabel = new Label
        {
            Text = "Add custom app (exe name, e.g. spotify.exe):",
            Dock = DockStyle.Top,
            Height = 18,
            ForeColor = Theme.TextSecondary,
            Font = Theme.SmallFont
        };

        var inputRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 32,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Theme.Background
        };

        _customAppInput = new TextBox
        {
            Width = 250,
            PlaceholderText = "app.exe",
            BackColor = Theme.InputBackground,
            Font = Theme.BodyFont
        };
        _customAppInput.KeyDown += (_, ke) => { if (ke.KeyCode == Keys.Enter) { OnAddCustomApp(null, EventArgs.Empty); ke.SuppressKeyPress = true; } };

        _addCustomBtn = new Button { Text = "Add", Width = 55, Height = 26 };
        Theme.ApplyPrimary(_addCustomBtn);
        _addCustomBtn.Click += OnAddCustomApp;

        _removeCustomBtn = new Button { Text = "Remove checked", Width = 120, Height = 26 };
        Theme.ApplySecondary(_removeCustomBtn);
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
        if (e.Action == TreeViewAction.Unknown) return;

        var node = e.Node!;
        if (node.Parent == null)
        {
            foreach (TreeNode child in node.Nodes)
                child.Checked = node.Checked;
        }

        // Show Apply button if VPN is running and profiles changed
        ShowApplyIfNeeded();
    }

    private void OnAddCustomApp(object? sender, EventArgs e)
    {
        var name = _customAppInput.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;

        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name += ".exe";

        if (_customApps.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            _customAppInput.Clear();
            return;
        }

        _customApps.Add(name);

        var customRoot = GetOrCreateCustomNode();
        customRoot.Nodes.Add(new TreeNode(name) { Checked = true });
        customRoot.Checked = true;
        customRoot.Expand();

        _customAppInput.Clear();
        SaveSettings();
        ShowApplyIfNeeded();
    }

    private void OnRemoveCustomApp(object? sender, EventArgs e)
    {
        var customRoot = FindCustomNode();
        if (customRoot == null) return;

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
        ShowApplyIfNeeded();
    }

    private TreeNode GetOrCreateCustomNode()
    {
        var existing = FindCustomNode();
        if (existing != null) return existing;

        var node = new TreeNode("Custom Apps  \u2014  Your custom applications")
        {
            Tag = "_custom",
            ForeColor = Theme.Primary,
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

    // ─── Apply button logic ──────────────────────────────────────────────────

    /// <summary>
    /// Show the Apply button when VPN is running and the user changes profiles/apps.
    /// </summary>
    private void ShowApplyIfNeeded()
    {
        bool vpnRunning = _engine.IsRunning || _tray.RunningAsService;
        if (!vpnRunning)
        {
            _applyBtn.Visible = false;
            return;
        }

        _applyBtn.Visible = true;
    }

    private async void OnApplyChanges(object? sender, EventArgs e)
    {
        SaveSettings();
        _applyBtn.Enabled = false;
        _applyBtn.Text = "Applying...";
        _startStopBtn.Enabled = false;

        try
        {
            if (_tray.RunningAsService)
            {
                // Service mode: stop → start service
                _statusLabel.Text = "Restarting service...";
                await Task.Run(() =>
                {
                    ServiceInstaller.Stop();
                    ServiceInstaller.Start();
                });
            }
            else
            {
                // In-process mode: stop engine → start engine
                _statusLabel.Text = "Applying changes...";
                _engine.Stop();

                var settings = SettingsLoader.Load();
                await _engine.StartAsync(settings);
            }

            _applyBtn.Visible = false;
            UpdateUI(true);
        }
        catch (Exception ex)
        {
            UpdateUI(false);
            MessageBox.Show($"Failed to apply changes:\n{ex.Message}", AppBranding.AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _applyBtn.Enabled = true;
            _applyBtn.Text = "\u21bb  Apply";
            _startStopBtn.Enabled = true;
        }
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

        // Load custom apps from config
        if (_settings.ActiveProfile?.Contains("_custom", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Custom apps stored in config.yaml under custom_apps key
        }

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
            var role = i == 0 ? "\u2605 Primary" : $"Fallback {i}";
            var item = new ListViewItem(role);
            item.SubItems.Add(string.IsNullOrEmpty(s.Name) ? "(no name)" : s.Name);
            item.SubItems.Add(s.Server);
            item.SubItems.Add(s.Port.ToString());
            item.SubItems.Add(s.Security);

            // Highlight primary
            if (i == 0)
            {
                item.ForeColor = Theme.Primary;
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
                MessageBox.Show("No valid VLESS URIs found.", AppBranding.AppName,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Filter out duplicate servers (same host:port+uuid)
            var added = 0;
            var skipped = 0;
            foreach (var entry in entries)
            {
                bool isDuplicate = _servers.Any(s =>
                    s.Server.Equals(entry.Server, StringComparison.OrdinalIgnoreCase) &&
                    s.Port == entry.Port &&
                    s.Uuid.Equals(entry.Uuid, StringComparison.OrdinalIgnoreCase));

                if (isDuplicate)
                {
                    skipped++;
                }
                else
                {
                    _servers.Add(entry);
                    added++;
                }
            }

            RefreshServerList();
            _uriInput.Clear();
            SaveSettings();

            if (skipped > 0)
            {
                MessageBox.Show($"Added {added} server(s), skipped {skipped} duplicate(s).",
                    AppBranding.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to parse VLESS URI:\n{ex.Message}", AppBranding.AppName,
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

    private async void OnAutostartChanged(object? sender, EventArgs e)
    {
        SaveSettings();
        _autostartCheck.Enabled = false;
        _startStopBtn.Enabled = false;

        try
        {
            if (_autostartCheck.Checked)
            {
                var serviceExe = Path.Combine(AppContext.BaseDirectory, "service", "VPNRouter.Service.exe");
                if (!File.Exists(serviceExe))
                    serviceExe = Path.Combine(AppContext.BaseDirectory, "VPNRouter.Service.exe");

                if (!File.Exists(serviceExe))
                {
                    MessageBox.Show("VPNRouter.Service.exe not found.\nAutostart requires the service binary.",
                        AppBranding.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    _autostartCheck.Checked = false;
                    return;
                }

                _statusLabel.Text = "Installing service...";

                if (_engine.IsRunning)
                {
                    _engine.Stop();
                }

                // Run blocking service operations on a background thread to avoid UI freeze
                var result = await Task.Run(() =>
                {
                    var installResult = ServiceInstaller.Install(serviceExe);
                    if (!installResult.Success) return installResult;
                    return ServiceInstaller.Start();
                });

                if (!result.Success)
                {
                    MessageBox.Show($"Failed to setup service:\n{result.Message}",
                        AppBranding.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    _autostartCheck.Checked = false;
                    UpdateUI(false);
                    return;
                }

                _tray.RunningAsService = true;
                _tray.SyncTrayState(null);
                UpdateUI(true);
            }
            else
            {
                _statusLabel.Text = "Removing service...";

                // Run blocking service operations on a background thread
                await Task.Run(() =>
                {
                    if (ServiceInstaller.IsRunning())
                        ServiceInstaller.Stop();
                    if (ServiceInstaller.IsInstalled())
                        ServiceInstaller.Uninstall();
                });

                _tray.RunningAsService = false;
                _tray.SyncTrayState(null);
                UpdateUI(false);
            }
        }
        finally
        {
            _autostartCheck.Enabled = true;
            _startStopBtn.Enabled = true;
        }
    }

    private async void OnRestartService(object? sender, EventArgs e)
    {
        if (!_tray.RunningAsService) return;

        _restartServiceBtn.Enabled = false;
        _restartServiceBtn.Text = "Restarting...";
        _startStopBtn.Enabled = false;
        _statusLabel.Text = "Restarting service...";

        try
        {
            var result = await Task.Run(() =>
            {
                var stopResult = ServiceInstaller.Stop();
                if (!stopResult.Success) return stopResult;
                return ServiceInstaller.Start();
            });

            if (!result.Success)
            {
                _tray.RunningAsService = false;
                _tray.SyncTrayState(null);
                UpdateUI(false);
                MessageBox.Show($"Failed to restart service:\n{result.Message}",
                    AppBranding.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _tray.SyncTrayState(null);
            UpdateUI(true);
        }
        finally
        {
            _restartServiceBtn.Enabled = true;
            _restartServiceBtn.Text = "\u21bb  Restart Service";
            _startStopBtn.Enabled = true;
        }
    }

    private async void OnStartStop(object? sender, EventArgs e)
    {
        if (_tray.RunningAsService)
        {
            _startStopBtn.Enabled = false;
            _startStopBtn.Text = "Stopping...";
            await Task.Run(() => ServiceInstaller.Stop());
            _tray.RunningAsService = false;
            _tray.SyncTrayState(null);
            UpdateUI(false);
            return;
        }

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
            MessageBox.Show("Add at least one VLESS server first.", AppBranding.AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var checkedCount = _profileTree.Nodes.Cast<TreeNode>().Count(n => n.Checked);
        if (checkedCount == 0)
        {
            MessageBox.Show("Select at least one application group.", AppBranding.AppName,
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
            MessageBox.Show($"Failed to start VPN:\n{ex.Message}", AppBranding.AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateUI(bool running)
    {
        _startStopBtn.Enabled = true;

        if (running)
        {
            _startStopBtn.Text = "\u2b1b  Stop VPN";
            _startStopBtn.BackColor = Theme.Danger;
            _startStopBtn.ForeColor = Theme.TextOnPrimary;
            // Resize Start/Stop when Apply is visible
            _startStopBtn.Size = new Size(330, 36);
        }
        else
        {
            ApplyStartStyle();
            _applyBtn.Visible = false;
            // Full width when Apply is hidden
            _startStopBtn.Size = new Size(498, 36);
        }

        _restartServiceBtn.Visible = _tray.RunningAsService;

        if (_tray.RunningAsService)
        {
            _statusLabel.Text = "Connected \u2014 Windows Service (autostart)";
            _statusLabel.ForeColor = Theme.Success;
            _statusDot.ForeColor = Theme.Success;
            _statusPanel.BackColor = Theme.SuccessLight;
        }
        else if (running)
        {
            _statusLabel.Text = $"Connected \u2014 {_engine.ActiveProfileName} \u2014 PID {_engine.SingBoxPid}";
            _statusLabel.ForeColor = Theme.Success;
            _statusDot.ForeColor = Theme.Success;
            _statusPanel.BackColor = Theme.SuccessLight;
        }
        else
        {
            _statusLabel.Text = "Not connected";
            _statusLabel.ForeColor = Theme.TextMuted;
            _statusDot.ForeColor = Theme.TextMuted;
            _statusPanel.BackColor = Theme.Background;
        }
    }

    private void ApplyStartStyle()
    {
        _startStopBtn.Text = "\u25b6  Start VPN";
        _startStopBtn.BackColor = Theme.Primary;
        _startStopBtn.ForeColor = Theme.TextOnPrimary;
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
