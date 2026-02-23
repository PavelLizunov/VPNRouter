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
    private RadioButton _splitRadio = null!;
    private RadioButton _fullRadio = null!;
    private readonly List<string> _customApps = new();

    // ── Bottom panel ──
    private Button _startStopBtn = null!;
    private Button _applyBtn = null!;
    private CheckBox _autostartCheck = null!;
    private Button _restartServiceBtn = null!;
    private Button _reinstallServiceBtn = null!;
    private Label _statusLabel = null!;
    private Panel _statusPanel = null!;
    private Label _statusDot = null!;

    // ── Update notification ──
    private Panel _updatePanel = null!;
    private Label _updateLabel = null!;
    private Button _updateBtn = null!;
    private ProgressBar _updateProgress = null!;
    private UpdateChecker? _updateChecker;
    private UpdateInfo? _pendingUpdate;

    // ── Theme-aware controls (promoted from locals for ApplyTheme) ──
    private Panel _headerPanel = null!;
    private Label _titleLabel = null!;
    private Label _subtitleLabel = null!;
    private LinkLabel _themeToggle = null!;
    private LinkLabel _checkUpdateLink = null!;
    private TabControl _tabs = null!;
    private TabPage _serversPage = null!;
    private TabPage _appsPage = null!;
    private Panel _actionPanel = null!;
    private Panel _routingPanel = null!;
    private Label _serversInputLabel = null!;
    private FlowLayoutPanel _serversBtnPanel = null!;
    private Label _appsLabel = null!;
    private Panel _customPanel = null!;
    private Label _customLabel = null!;
    private FlowLayoutPanel _customInputRow = null!;

    // ── State ──
    private AppSettings _settings = null!;
    private readonly List<VlessServerEntry> _servers = new();

    public MainForm(VpnEngine engine, TrayApplicationContext tray)
    {
        _engine = engine;
        _tray = tray;

        // Load settings FIRST so theme is set before building UI
        LoadSettings();

        var isDark = (_settings.App.Theme ?? "light")
            .Equals("dark", StringComparison.OrdinalIgnoreCase);
        Theme.SetTheme(isDark);

        InitializeComponent();
        LoadSettingsIntoUI();

        _engine.StatusChanged += OnEngineStatus;

        // Check for updates in background (fire-and-forget, silent fail)
        _ = CheckForUpdateAsync();
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
        var t = Theme.Current;

        Text = AppBranding.WindowTitle;
        Size = new Size(540, 680);
        MinimumSize = new Size(480, 560);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = t.Background;
        Font = t.BodyFont;
        Icon = AppBranding.GetIcon(32);

        // ── Header panel (logo + brand) ──
        BuildHeaderPanel();

        // ── Tabs ──
        _tabs = new TabControl { Dock = DockStyle.Fill };
        _tabs.Font = t.BodyFont;

        _serversPage = new TabPage("Servers") { BackColor = t.Background, Padding = new Padding(10) };
        BuildServersTab(_serversPage);
        _tabs.TabPages.Add(_serversPage);

        _appsPage = new TabPage("Applications") { BackColor = t.Background, Padding = new Padding(10) };
        BuildAppsTab(_appsPage);
        _tabs.TabPages.Add(_appsPage);

        // ── Status bar ──
        _statusPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 32,
            BackColor = t.Background,
            Padding = new Padding(14, 0, 14, 0)
        };

        _statusDot = new Label
        {
            Text = "\u25cf",
            Font = t.BodyFont,
            ForeColor = t.TextMuted,
            AutoSize = true,
            Location = new Point(14, 6)
        };

        _statusLabel = new Label
        {
            Text = "Not connected",
            Font = t.BodyFont,
            ForeColor = t.TextMuted,
            AutoSize = true,
            Location = new Point(32, 8)
        };

        _statusPanel.Paint += (s, e) =>
        {
            var borderColor = Theme.Current.SurfaceBorder;
            e.Graphics.DrawLine(new Pen(borderColor), 0, 0, _statusPanel.Width, 0);
        };

        _statusPanel.Controls.Add(_statusDot);
        _statusPanel.Controls.Add(_statusLabel);

        // ── Action panel (Start/Stop + Apply + autostart) ──
        _actionPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 80,
            BackColor = t.Surface,
            Padding = new Padding(14, 8, 14, 8)
        };

        _startStopBtn = new Button
        {
            Text = "\u25b6  Start VPN",
            Size = new Size(330, 36),
            Location = new Point(14, 4),
            Font = t.StartStopFont,
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
            Font = t.StartStopFont,
            Cursor = Cursors.Hand,
            FlatStyle = FlatStyle.Flat,
            Visible = false
        };
        Theme.ApplySecondary(_applyBtn);
        _applyBtn.FlatAppearance.BorderSize = 0;
        _applyBtn.BackColor = t.AmberButton;
        _applyBtn.ForeColor = Color.White;
        _applyBtn.Click += OnApplyChanges;

        _autostartCheck = new CheckBox
        {
            Text = "Autostart with Windows",
            Font = t.SmallFont,
            ForeColor = t.TextSecondary,
            Checked = ServiceInstaller.IsInstalled(),
            AutoSize = true,
            Location = new Point(14, 50),
            BackColor = t.Surface
        };
        _autostartCheck.CheckedChanged += OnAutostartChanged;

        _restartServiceBtn = new Button
        {
            Text = "\u21bb  Restart Service",
            Size = new Size(130, 24),
            Location = new Point(200, 50),
            Font = t.SmallFont,
            Cursor = Cursors.Hand,
            FlatStyle = FlatStyle.Flat,
            Visible = false
        };
        Theme.ApplySecondary(_restartServiceBtn);
        _restartServiceBtn.FlatAppearance.BorderSize = 0;
        _restartServiceBtn.Click += OnRestartService;

        _reinstallServiceBtn = new Button
        {
            Text = "\u21bb  Reinstall Service",
            Size = new Size(140, 24),
            Location = new Point(340, 50),
            Font = t.SmallFont,
            Cursor = Cursors.Hand,
            FlatStyle = FlatStyle.Flat,
            Visible = false
        };
        Theme.ApplySecondary(_reinstallServiceBtn);
        _reinstallServiceBtn.FlatAppearance.BorderSize = 0;
        _reinstallServiceBtn.Click += OnReinstallService;

        _actionPanel.Controls.Add(_startStopBtn);
        _actionPanel.Controls.Add(_applyBtn);
        _actionPanel.Controls.Add(_autostartCheck);
        _actionPanel.Controls.Add(_restartServiceBtn);
        _actionPanel.Controls.Add(_reinstallServiceBtn);

        // ── Update notification panel ──
        _updatePanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 40,
            BackColor = t.UpdatePanelBg,
            Visible = false,
            Padding = new Padding(14, 0, 14, 0)
        };

        _updateLabel = new Label
        {
            Text = "",
            Font = t.BodyFont,
            ForeColor = t.UpdatePanelText,
            AutoSize = true,
            Location = new Point(14, 11)
        };

        _updateBtn = new Button
        {
            Text = "Update",
            Size = new Size(80, 28),
            Location = new Point(430, 6),
            Font = t.ButtonFont,
            Cursor = Cursors.Hand,
            FlatStyle = FlatStyle.Flat,
            BackColor = t.AmberButton,
            ForeColor = Color.White
        };
        _updateBtn.FlatAppearance.BorderSize = 0;
        _updateBtn.Click += OnUpdateClick;

        _updateProgress = new ProgressBar
        {
            Size = new Size(80, 28),
            Location = new Point(430, 6),
            Style = ProgressBarStyle.Continuous,
            Visible = false
        };

        _updatePanel.Controls.Add(_updateLabel);
        _updatePanel.Controls.Add(_updateBtn);
        _updatePanel.Controls.Add(_updateProgress);

        _updatePanel.Paint += (s, e) =>
        {
            var borderColor = Theme.Current.UpdatePanelBorder;
            e.Graphics.DrawLine(new Pen(borderColor), 0, _updatePanel.Height - 1, _updatePanel.Width, _updatePanel.Height - 1);
        };

        // ── Dock order: last added docks first ──
        // Fill = tabs, Bottom = status then action, Top = header then update panel
        Controls.Add(_tabs);
        Controls.Add(_statusPanel);
        Controls.Add(_actionPanel);
        Controls.Add(_updatePanel);
        Controls.Add(_headerPanel);

        // Set initial UI state
        bool running = _engine.IsRunning || _tray.RunningAsService;
        if (running) UpdateUI(true);
    }

    private void BuildHeaderPanel()
    {
        var t = Theme.Current;

        _headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 72,
            BackColor = t.Surface
        };

        var logo = new PictureBox
        {
            Image = AppBranding.GetLogo(),
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(50, 50),
            Location = new Point(16, 11),
            BackColor = Color.Transparent
        };

        _titleLabel = new Label
        {
            Text = AppBranding.AppName,
            Font = t.HeaderFont,
            ForeColor = t.Primary,
            AutoSize = true,
            Location = new Point(74, 12),
            BackColor = Color.Transparent
        };

        _subtitleLabel = new Label
        {
            Text = $"by {AppBranding.Publisher}  \u00b7  v{AppBranding.Version}",
            Font = t.SubHeaderFont,
            ForeColor = t.TextMuted,
            AutoSize = true,
            Location = new Point(76, 42),
            BackColor = Color.Transparent
        };

        _checkUpdateLink = new LinkLabel
        {
            Text = "Check for updates",
            Font = t.SmallFont,
            AutoSize = true,
            Location = new Point(340, 46),
            LinkColor = t.TextMuted,
            ActiveLinkColor = t.Primary,
            VisitedLinkColor = t.TextMuted,
            BackColor = Color.Transparent
        };

        _themeToggle = new LinkLabel
        {
            Text = Theme.IsDark ? "\u25cb Light" : "\u25cf Dark",
            Font = t.SmallFont,
            AutoSize = true,
            Location = new Point(468, 46),
            LinkColor = t.TextMuted,
            ActiveLinkColor = t.Primary,
            VisitedLinkColor = t.TextMuted,
            BackColor = Color.Transparent
        };
        _themeToggle.Click += OnThemeToggle;
        _checkUpdateLink.Click += async (_, __) =>
        {
            _checkUpdateLink.Text = "Checking...";
            _checkUpdateLink.Enabled = false;
            try
            {
                await CheckForUpdateAsync();
                if (_pendingUpdate == null)
                    _checkUpdateLink.Text = "You're up to date \u2713";
            }
            catch
            {
                _checkUpdateLink.Text = "Check failed";
            }
            finally
            {
                _checkUpdateLink.Enabled = true;
                _ = Task.Delay(3000).ContinueWith(_ =>
                {
                    try
                    {
                        if (!IsDisposed && IsHandleCreated)
                            BeginInvoke(() => _checkUpdateLink.Text = "Check for updates");
                    }
                    catch (ObjectDisposedException) { }
                });
            }
        };

        _headerPanel.Controls.Add(logo);
        _headerPanel.Controls.Add(_titleLabel);
        _headerPanel.Controls.Add(_subtitleLabel);
        _headerPanel.Controls.Add(_themeToggle);
        _headerPanel.Controls.Add(_checkUpdateLink);

        // Bottom border
        _headerPanel.Paint += (s, e) =>
        {
            var borderColor = Theme.Current.SurfaceBorder;
            e.Graphics.DrawLine(new Pen(borderColor), 0, _headerPanel.Height - 1, _headerPanel.Width, _headerPanel.Height - 1);
        };
    }

    private void BuildServersTab(TabPage page)
    {
        var t = Theme.Current;

        _serversInputLabel = new Label
        {
            Text = "Paste VLESS URI(s) \u2014 first server = Primary, others = Fallback:",
            Dock = DockStyle.Top,
            Height = 22,
            ForeColor = t.TextSecondary,
            Font = t.BodyFont
        };

        _uriInput = new TextBox
        {
            Dock = DockStyle.Top,
            Height = 60,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            PlaceholderText = "vless://uuid@server:443?security=reality&sni=...#name",
            BackColor = t.InputBackground,
            ForeColor = t.TextPrimary,
            Font = t.BodyFont
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
            BackColor = t.Surface,
            ForeColor = t.TextPrimary,
            Font = t.BodyFont
        };
        _serverList.Columns.Add("Role", 70);
        _serverList.Columns.Add("Name", 110);
        _serverList.Columns.Add("Server", 160);
        _serverList.Columns.Add("Port", 50);
        _serverList.Columns.Add("Security", 70);

        _serversBtnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 36,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = t.Background
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

        _serversBtnPanel.Controls.Add(_clearBtn);
        _serversBtnPanel.Controls.Add(_removeBtn);
        _serversBtnPanel.Controls.Add(_downBtn);
        _serversBtnPanel.Controls.Add(_upBtn);

        // Dock order: last added = top
        page.Controls.Add(_serverList);
        page.Controls.Add(_serversBtnPanel);
        page.Controls.Add(_addBtn);
        page.Controls.Add(_uriInput);
        page.Controls.Add(_serversInputLabel);
    }

    private void BuildAppsTab(TabPage page)
    {
        var t = Theme.Current;

        // ── Routing mode selector ──
        _routingPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 36,
            BackColor = t.Background,
            Padding = new Padding(0, 4, 0, 4)
        };

        _splitRadio = new RadioButton
        {
            Text = "Split Tunnel (selected apps)",
            Font = t.BodyFont,
            ForeColor = t.TextPrimary,
            Checked = true,
            AutoSize = true,
            Location = new Point(0, 6)
        };

        _fullRadio = new RadioButton
        {
            Text = "Full Tunnel (all traffic)",
            Font = t.BodyFont,
            ForeColor = t.TextPrimary,
            AutoSize = true,
            Location = new Point(260, 6)
        };

        _splitRadio.CheckedChanged += (_, __) =>
        {
            _profileTree.Enabled = _splitRadio.Checked;
            _customAppInput.Enabled = _splitRadio.Checked;
            _addCustomBtn.Enabled = _splitRadio.Checked;
            _removeCustomBtn.Enabled = _splitRadio.Checked;
        };

        _routingPanel.Controls.Add(_splitRadio);
        _routingPanel.Controls.Add(_fullRadio);

        _appsLabel = new Label
        {
            Text = "Check groups to route through VPN (expand to see apps inside):",
            Dock = DockStyle.Top,
            Height = 25,
            ForeColor = t.TextSecondary,
            Font = t.BodyFont
        };

        _profileTree = new TreeView
        {
            Dock = DockStyle.Fill,
            CheckBoxes = true,
            Font = t.BodyFont,
            ShowLines = true,
            ShowPlusMinus = true,
            ShowRootLines = true,
            BackColor = t.Surface,
            ForeColor = t.TextPrimary
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
                node.Nodes.Add(new TreeNode(childText) { ForeColor = t.TextMuted });
            }
            _profileTree.Nodes.Add(node);
        }

        // Custom apps section at bottom
        _customPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 65,
            BackColor = t.Background
        };

        _customLabel = new Label
        {
            Text = "Add custom app (exe name, e.g. spotify.exe):",
            Dock = DockStyle.Top,
            Height = 18,
            ForeColor = t.TextSecondary,
            Font = t.SmallFont
        };

        _customInputRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 32,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = t.Background
        };

        _customAppInput = new TextBox
        {
            Width = 250,
            PlaceholderText = "app.exe",
            BackColor = t.InputBackground,
            ForeColor = t.TextPrimary,
            Font = t.BodyFont
        };
        _customAppInput.KeyDown += (_, ke) => { if (ke.KeyCode == Keys.Enter) { OnAddCustomApp(null, EventArgs.Empty); ke.SuppressKeyPress = true; } };

        _addCustomBtn = new Button { Text = "Add", Width = 55, Height = 26 };
        Theme.ApplyPrimary(_addCustomBtn);
        _addCustomBtn.Click += OnAddCustomApp;

        _removeCustomBtn = new Button { Text = "Remove checked", Width = 120, Height = 26 };
        Theme.ApplySecondary(_removeCustomBtn);
        _removeCustomBtn.Click += OnRemoveCustomApp;

        _customInputRow.Controls.Add(_customAppInput);
        _customInputRow.Controls.Add(_addCustomBtn);
        _customInputRow.Controls.Add(_removeCustomBtn);

        _customPanel.Controls.Add(_customInputRow);
        _customPanel.Controls.Add(_customLabel);

        page.Controls.Add(_profileTree);
        page.Controls.Add(_customPanel);
        page.Controls.Add(_appsLabel);
        page.Controls.Add(_routingPanel);
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
            ForeColor = Theme.Current.Primary,
            NodeFont = Theme.Current.BoldBodyFont
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

    // ─── Theme toggle ─────────────────────────────────────────────────────────

    private void OnThemeToggle(object? sender, EventArgs e)
    {
        Theme.SetTheme(!Theme.IsDark);
        _themeToggle.Text = Theme.IsDark ? "\u25cb Light" : "\u25cf Dark";
        _settings.App.Theme = Theme.IsDark ? "dark" : "light";
        SaveSettings();
        ApplyTheme();
    }

    /// <summary>
    /// Re-applies Theme.Current colors/fonts to all controls at runtime.
    /// </summary>
    private void ApplyTheme()
    {
        var t = Theme.Current;

        SuspendLayout();

        // ── Form ──
        BackColor = t.Background;
        Font = t.BodyFont;

        // ── Header ──
        _headerPanel.BackColor = t.Surface;
        _titleLabel.Font = t.HeaderFont;
        _titleLabel.ForeColor = t.Primary;
        _subtitleLabel.Font = t.SubHeaderFont;
        _subtitleLabel.ForeColor = t.TextMuted;
        _themeToggle.LinkColor = t.TextMuted;
        _themeToggle.ActiveLinkColor = t.Primary;
        _themeToggle.VisitedLinkColor = t.TextMuted;
        _checkUpdateLink.LinkColor = t.TextMuted;
        _checkUpdateLink.ActiveLinkColor = t.Primary;
        _checkUpdateLink.VisitedLinkColor = t.TextMuted;

        // ── Tabs ──
        _tabs.Font = t.BodyFont;
        _serversPage.BackColor = t.Background;
        _appsPage.BackColor = t.Background;

        // ── Servers tab ──
        _serversInputLabel.ForeColor = t.TextSecondary;
        _serversInputLabel.Font = t.BodyFont;
        _uriInput.BackColor = t.InputBackground;
        _uriInput.ForeColor = t.TextPrimary;
        _uriInput.Font = t.BodyFont;
        Theme.ApplyPrimary(_addBtn);
        _serverList.BackColor = t.Surface;
        _serverList.ForeColor = t.TextPrimary;
        _serverList.Font = t.BodyFont;
        _serversBtnPanel.BackColor = t.Background;
        Theme.ApplySecondary(_clearBtn);
        Theme.ApplySecondary(_removeBtn);
        Theme.ApplySecondary(_downBtn);
        Theme.ApplySecondary(_upBtn);

        // ── Apps tab ──
        _routingPanel.BackColor = t.Background;
        _splitRadio.Font = t.BodyFont;
        _splitRadio.ForeColor = t.TextPrimary;
        _fullRadio.Font = t.BodyFont;
        _fullRadio.ForeColor = t.TextPrimary;
        _appsLabel.ForeColor = t.TextSecondary;
        _appsLabel.Font = t.BodyFont;
        _profileTree.BackColor = t.Surface;
        _profileTree.ForeColor = t.TextPrimary;
        _profileTree.Font = t.BodyFont;
        // Update child node colors in tree
        foreach (TreeNode rootNode in _profileTree.Nodes)
        {
            if (rootNode.Tag?.ToString() == "_custom")
            {
                rootNode.ForeColor = t.Primary;
                rootNode.NodeFont = t.BoldBodyFont;
            }
            foreach (TreeNode child in rootNode.Nodes)
            {
                if (rootNode.Tag?.ToString() != "_custom")
                    child.ForeColor = t.TextMuted;
            }
        }
        _customPanel.BackColor = t.Background;
        _customLabel.ForeColor = t.TextSecondary;
        _customLabel.Font = t.SmallFont;
        _customInputRow.BackColor = t.Background;
        _customAppInput.BackColor = t.InputBackground;
        _customAppInput.ForeColor = t.TextPrimary;
        _customAppInput.Font = t.BodyFont;
        Theme.ApplyPrimary(_addCustomBtn);
        Theme.ApplySecondary(_removeCustomBtn);

        // ── Action panel ──
        _actionPanel.BackColor = t.Surface;
        _startStopBtn.Font = t.StartStopFont;
        _applyBtn.Font = t.StartStopFont;
        _applyBtn.BackColor = t.AmberButton;
        _applyBtn.ForeColor = Color.White;
        _autostartCheck.Font = t.SmallFont;
        _autostartCheck.ForeColor = t.TextSecondary;
        _autostartCheck.BackColor = t.Surface;
        Theme.ApplySecondary(_restartServiceBtn);
        _restartServiceBtn.FlatAppearance.BorderSize = 0;
        Theme.ApplySecondary(_reinstallServiceBtn);
        _reinstallServiceBtn.FlatAppearance.BorderSize = 0;

        // ── Status panel ──
        // UpdateUI will set correct colors based on running state
        bool running = _engine.IsRunning || _tray.RunningAsService;
        UpdateUI(running);

        // ── Update panel ──
        _updatePanel.BackColor = t.UpdatePanelBg;
        _updateLabel.ForeColor = t.UpdatePanelText;
        _updateLabel.Font = t.BodyFont;
        _updateBtn.BackColor = t.AmberButton;
        _updateBtn.Font = t.ButtonFont;

        // Refresh server list to update primary row styling
        RefreshServerList();

        ResumeLayout(true);

        // Force repaint of border lines
        _headerPanel.Invalidate();
        _statusPanel.Invalidate();
        _updatePanel.Invalidate();
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

    /// <summary>Load settings from disk (no UI interaction).</summary>
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
    }

    /// <summary>Populate UI controls from loaded settings.</summary>
    private void LoadSettingsIntoUI()
    {
        // Load routing mode
        var isFullTunnel = (_settings.App.RoutingMode ?? "split")
            .Equals("full", StringComparison.OrdinalIgnoreCase);
        _splitRadio.Checked = !isFullTunnel;
        _fullRadio.Checked = isFullTunnel;

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

    // ─── Auto-update ──────────────────────────────────────────────────────────

    private async Task CheckForUpdateAsync()
    {
        try
        {
            var updateSettings = _settings.Update ?? new UpdateSettings();
            if (!updateSettings.AutoCheck || string.IsNullOrWhiteSpace(updateSettings.GitHubRepo))
                return;

            _updateChecker = new UpdateChecker(updateSettings, AppBranding.Version);
            _updateChecker.CleanupStagingDir();
            var info = await _updateChecker.CheckForUpdateAsync();

            if (info is { IsNewer: true })
            {
                if (InvokeRequired) { BeginInvoke(() => ShowUpdateNotification(info)); return; }
                ShowUpdateNotification(info);
            }
        }
        catch
        {
            // Silent fail — update check is non-critical
        }
    }

    private void ShowUpdateNotification(UpdateInfo info)
    {
        _pendingUpdate = info;
        var sizeMb = info.SizeBytes > 0 ? $"  ({info.SizeBytes / 1024 / 1024} MB)" : "";
        _updateLabel.Text = $"Update available: v{info.LatestVersion}{sizeMb}";
        _updatePanel.Visible = true;
    }

    private async void OnUpdateClick(object? sender, EventArgs e)
    {
        if (_pendingUpdate == null || _updateChecker == null) return;

        var msg = $"Update to v{_pendingUpdate.LatestVersion}?\n";

        if (!string.IsNullOrWhiteSpace(_pendingUpdate.ReleaseNotes))
            msg += $"\n--- Changelog ---\n{_pendingUpdate.ReleaseNotes}\n-----------------\n";

        bool vpnRunning = _engine.IsRunning || _tray.RunningAsService;
        bool serviceInstalled = ServiceInstaller.IsInstalled();

        if (vpnRunning || serviceInstalled)
            msg += "\nVPN will be stopped before applying the update.";
        msg += "\nThe application will restart automatically.";

        if (MessageBox.Show(msg, AppBranding.AppName,
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        _updateBtn.Visible = false;
        _updateProgress.Visible = true;
        _updateProgress.Value = 0;
        _startStopBtn.Enabled = false;
        _applyBtn.Enabled = false;

        _updateChecker.DownloadProgress += p =>
        {
            if (InvokeRequired) { BeginInvoke(() => _updateProgress.Value = p); return; }
            _updateProgress.Value = p;
        };
        _updateChecker.StatusChanged += s =>
        {
            if (InvokeRequired) { BeginInvoke(() => _updateLabel.Text = s); return; }
            _updateLabel.Text = s;
        };

        try
        {
            // Download first (VPN stays on — GitHub may be blocked without it)
            var extractedDir = await _updateChecker.DownloadAndStageAsync(_pendingUpdate);

            // Stop VPN/Service only before applying (replacing files)
            if (_engine.IsRunning)
            {
                _updateLabel.Text = "Stopping VPN...";
                _engine.Stop();
            }

            if (_tray.RunningAsService)
            {
                _updateLabel.Text = "Stopping service...";
                await Task.Run(() => ServiceInstaller.Stop());
            }

            // Apply in-process: copies files directly, renames locked exes
            _updateLabel.Text = "Applying update...";
            _updateChecker.ApplyUpdate(extractedDir);

            // ApplyUpdate already launched the new GUI — just exit
            Application.Exit();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Update failed:\n{ex.Message}", AppBranding.AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            _updateBtn.Visible = true;
            _updateBtn.Enabled = true;
            _updateProgress.Visible = false;
            _updateLabel.Text = $"Update available: v{_pendingUpdate.LatestVersion}";
            _startStopBtn.Enabled = true;
            _applyBtn.Enabled = true;
        }
    }

    private void RefreshServerList()
    {
        var t = Theme.Current;
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
                item.ForeColor = t.Primary;
                item.Font = t.BoldBodyFont;
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

        // Save routing mode
        _settings.App.RoutingMode = _fullRadio.Checked ? "full" : "split";

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

    private async void OnReinstallService(object? sender, EventArgs e)
    {
        var confirm = MessageBox.Show(
            "This will stop the service, uninstall it, and reinstall from the current binary.\n\n" +
            "Use this after updating VPNRouter to apply the new service executable.\n\nContinue?",
            AppBranding.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);

        if (confirm != DialogResult.Yes) return;

        _reinstallServiceBtn.Enabled = false;
        _reinstallServiceBtn.Text = "Reinstalling...";
        _restartServiceBtn.Enabled = false;
        _startStopBtn.Enabled = false;
        _autostartCheck.Enabled = false;
        _statusLabel.Text = "Reinstalling service...";

        try
        {
            var serviceExe = Path.Combine(AppContext.BaseDirectory, "service", "VPNRouter.Service.exe");
            if (!File.Exists(serviceExe))
                serviceExe = Path.Combine(AppContext.BaseDirectory, "VPNRouter.Service.exe");

            if (!File.Exists(serviceExe))
            {
                MessageBox.Show("VPNRouter.Service.exe not found.\nCannot reinstall service.",
                    AppBranding.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var result = await Task.Run(() =>
            {
                // 1. Stop if running
                if (ServiceInstaller.IsRunning())
                {
                    var stopResult = ServiceInstaller.Stop();
                    if (!stopResult.Success)
                        return InstallResult.Fail($"Failed to stop: {stopResult.Message}");
                }

                // 2. Uninstall
                if (ServiceInstaller.IsInstalled())
                {
                    var uninstResult = ServiceInstaller.Uninstall();
                    if (!uninstResult.Success)
                        return InstallResult.Fail($"Failed to uninstall: {uninstResult.Message}");

                    // Brief pause for SCM to fully release the service
                    Thread.Sleep(1000);
                }

                // 3. Install with current binary path
                var installResult = ServiceInstaller.Install(serviceExe);
                if (!installResult.Success)
                    return InstallResult.Fail($"Failed to install: {installResult.Message}");

                // 4. Start
                return ServiceInstaller.Start();
            });

            if (!result.Success)
            {
                _tray.RunningAsService = false;
                _tray.SyncTrayState(null);
                UpdateUI(false);
                _autostartCheck.Checked = ServiceInstaller.IsInstalled();
                MessageBox.Show($"Service reinstall failed:\n{result.Message}",
                    AppBranding.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _tray.RunningAsService = true;
            _tray.SyncTrayState(null);
            UpdateUI(true);
            _autostartCheck.Checked = true;
            MessageBox.Show("Service reinstalled and started successfully.",
                AppBranding.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        finally
        {
            _reinstallServiceBtn.Enabled = true;
            _reinstallServiceBtn.Text = "\u21bb  Reinstall Service";
            _restartServiceBtn.Enabled = true;
            _startStopBtn.Enabled = true;
            _autostartCheck.Enabled = true;
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

        if (_splitRadio.Checked)
        {
            var checkedCount = _profileTree.Nodes.Cast<TreeNode>().Count(n => n.Checked);
            if (checkedCount == 0)
            {
                MessageBox.Show("Select at least one application group.", AppBranding.AppName,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
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
        var t = Theme.Current;
        _startStopBtn.Enabled = true;

        if (running)
        {
            _startStopBtn.Text = "\u2b1b  Stop VPN";
            _startStopBtn.BackColor = t.Danger;
            _startStopBtn.ForeColor = t.TextOnPrimary;
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
        _reinstallServiceBtn.Visible = _tray.RunningAsService || ServiceInstaller.IsInstalled();

        if (_tray.RunningAsService)
        {
            _statusLabel.Text = "Connected \u2014 Windows Service (autostart)";
            _statusLabel.ForeColor = t.Success;
            _statusDot.ForeColor = t.Success;
            _statusPanel.BackColor = t.SuccessLight;
        }
        else if (running)
        {
            _statusLabel.Text = $"Connected \u2014 {_engine.ActiveProfileName} \u2014 PID {_engine.SingBoxPid}";
            _statusLabel.ForeColor = t.Success;
            _statusDot.ForeColor = t.Success;
            _statusPanel.BackColor = t.SuccessLight;
        }
        else
        {
            _statusLabel.Text = "Not connected";
            _statusLabel.ForeColor = t.TextMuted;
            _statusDot.ForeColor = t.TextMuted;
            _statusPanel.BackColor = t.Background;
        }
    }

    private void ApplyStartStyle()
    {
        var t = Theme.Current;
        _startStopBtn.Text = "\u25b6  Start VPN";
        _startStopBtn.BackColor = t.Primary;
        _startStopBtn.ForeColor = t.TextOnPrimary;
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
