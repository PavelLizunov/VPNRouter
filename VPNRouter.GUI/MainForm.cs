using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.GUI.Localization;
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
    private Panel _vlessPanel = null!;
    private FlowLayoutPanel _vlessBtnPanel = null!;
    private Button _addServerBtn = null!;
    private ListView _serverList = null!;
    private Button _removeBtn = null!;
    // (removed: _clearBtn — Remove is sufficient)
    private Label _serverHintLabel = null!;

    // ── Custom config mode ──
    private FlowLayoutPanel _configModePanel = null!;
    private RadioButton _vlessRadio = null!;
    private RadioButton _customConfigRadio = null!;
    private Panel _customConfigPanel = null!;
    private ListView _customConfigList = null!;
    private Button _addCustomConfigBtn = null!;
    private Button _removeCustomConfigBtn = null!;
    private FlowLayoutPanel _customConfigBtnPanel = null!;

    // ── Apps tab ──
    private TreeView _profileTree = null!;
    private TextBox _customAppInput = null!;
    private Button _addCustomBtn = null!;
    private Button _removeCustomBtn = null!;
    private RadioButton _splitRadio = null!;
    private RadioButton _fullRadio = null!;
    private CheckBox _bypassRuCheck = null!;
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
    private LinkLabel _langToggle = null!;
    private LinkLabel _channelToggle = null!;
    private LinkLabel _checkUpdateLink = null!;
    private TabControl _tabs = null!;
    private TabPage _serversPage = null!;
    private TabPage _appsPage = null!;
    private Panel _actionPanel = null!;
    private FlowLayoutPanel _routingPanel = null!;
    // (removed: _serversInputLabel, _serversBtnPanel — unified with _vlessBtnPanel)
    private Label _appsLabel = null!;
    private Panel _customPanel = null!;
    private Label _customLabel = null!;
    private FlowLayoutPanel _customInputRow = null!;

    // ── Tooltips ──
    private readonly ToolTip _statusTooltip = new();

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
        Strings.Lang = _settings.App.Language ?? "en";

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

        AutoScaleMode = AutoScaleMode.Dpi;
        Text = AppBranding.WindowTitle;
        Size = new Size(540, 680);
        MinimumSize = new Size(480, 560);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = t.Background;
        Font = t.BodyFont;
        Icon = AppBranding.GetIcon(32);

        // ── Header panel (logo + brand) ──
        BuildHeaderPanel();

        // ── Tabs ──
        _tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Font = t.BodyFont,
            DrawMode = TabDrawMode.OwnerDrawFixed,
            Padding = new Point(12, 4)
        };
        _tabs.DrawItem += OnDrawTab;
        _tabs.Paint += OnTabPaint;

        _serversPage = new TabPage(Strings.TabServers) { BackColor = t.Background, Padding = new Padding(10) };
        BuildServersTab(_serversPage);
        _tabs.TabPages.Add(_serversPage);

        _appsPage = new TabPage(Strings.TabApps) { BackColor = t.Background, Padding = new Padding(10) };
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
            Text = Strings.NotConnected,
            Font = t.BodyFont,
            ForeColor = t.TextMuted,
            AutoSize = false,
            AutoEllipsis = true,
            Location = new Point(32, 8),
            Height = 16
        };

        _statusPanel.Paint += (s, e) =>
        {
            using var borderPen = new Pen(Theme.Current.SurfaceBorder);
            e.Graphics.DrawLine(borderPen, 0, 0, _statusPanel.Width, 0);
        };

        _statusPanel.Controls.Add(_statusDot);
        _statusPanel.Controls.Add(_statusLabel);
        // Anchor status label to fill available width
        _statusLabel.Width = Math.Max(100, _statusPanel.ClientSize.Width - 32 - 14);
        _statusPanel.Resize += (_, _) =>
        {
            _statusLabel.Width = Math.Max(100, _statusPanel.ClientSize.Width - 32 - 14);
        };

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
            Text = Strings.StartVPN,
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
            Text = Strings.Apply,
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
            Text = Strings.AutostartWindows,
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
            Text = Strings.RestartService,
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
            Text = Strings.ReinstallService,
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
        _actionPanel.Resize += (_, _) => LayoutActionButtons();

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
            AutoSize = false,
            AutoEllipsis = true,
            Location = new Point(14, 11),
            Height = 18
        };

        _updateBtn = new Button
        {
            Text = Strings.Update,
            Size = new Size(80, 28),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
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
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Style = ProgressBarStyle.Continuous,
            Visible = false
        };

        _updatePanel.Controls.Add(_updateLabel);
        _updatePanel.Controls.Add(_updateBtn);
        _updatePanel.Controls.Add(_updateProgress);

        // Layout update panel controls dynamically
        void LayoutUpdatePanel()
        {
            int btnX = _updatePanel.ClientSize.Width - 80 - 14;
            _updateBtn.Location = new Point(btnX, 6);
            _updateProgress.Location = new Point(btnX, 6);
            _updateLabel.Width = Math.Max(50, btnX - 28);
        }
        LayoutUpdatePanel();
        _updatePanel.Resize += (_, _) => LayoutUpdatePanel();

        _updatePanel.Paint += (s, e) =>
        {
            using var borderPen = new Pen(Theme.Current.UpdatePanelBorder);
            e.Graphics.DrawLine(borderPen, 0, _updatePanel.Height - 1, _updatePanel.Width, _updatePanel.Height - 1);
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
            Text = Strings.CheckForUpdates,
            Font = t.SmallFont,
            AutoSize = true,
            Location = new Point(300, 46),
            LinkColor = t.TextMuted,
            ActiveLinkColor = t.Primary,
            VisitedLinkColor = t.TextMuted,
            BackColor = Color.Transparent
        };

        _themeToggle = new LinkLabel
        {
            Text = Theme.IsDark ? Strings.ThemeLight : Strings.ThemeDark,
            Font = t.SmallFont,
            AutoSize = true,
            Location = new Point(428, 46),
            LinkColor = t.TextMuted,
            ActiveLinkColor = t.Primary,
            VisitedLinkColor = t.TextMuted,
            BackColor = Color.Transparent
        };
        _themeToggle.Click += OnThemeToggle;

        _langToggle = new LinkLabel
        {
            Text = Strings.Lang.Equals("ru", StringComparison.OrdinalIgnoreCase) ? "ENG" : "RUS",
            Font = t.SmallFont,
            AutoSize = true,
            Location = new Point(488, 46),
            LinkColor = t.TextMuted,
            ActiveLinkColor = t.Primary,
            VisitedLinkColor = t.TextMuted,
            BackColor = Color.Transparent
        };
        _langToggle.Click += OnLangToggle;

        var isExp = _settings.Update.IsExperimental;
        _channelToggle = new LinkLabel
        {
            Text = isExp ? Strings.ChannelExp : Strings.ChannelStable,
            Font = t.SmallFont,
            AutoSize = true,
            Location = new Point(300, 30),
            LinkColor = isExp ? t.AmberButton : t.TextMuted,
            ActiveLinkColor = t.Primary,
            VisitedLinkColor = isExp ? t.AmberButton : t.TextMuted,
            BackColor = Color.Transparent
        };
        _channelToggle.Click += OnChannelToggle;
        _checkUpdateLink.Click += async (_, __) =>
        {
            _checkUpdateLink.Text = Strings.Checking;
            _checkUpdateLink.Enabled = false;
            try
            {
                await CheckForUpdateAsync();
                if (_pendingUpdate == null)
                    _checkUpdateLink.Text = Strings.UpToDate;
            }
            catch
            {
                _checkUpdateLink.Text = Strings.CheckFailed;
            }
            finally
            {
                _checkUpdateLink.Enabled = true;
                _ = Task.Delay(3000).ContinueWith(_ =>
                {
                    try
                    {
                        if (!IsDisposed && IsHandleCreated)
                            BeginInvoke(() => _checkUpdateLink.Text = Strings.CheckForUpdates);
                    }
                    catch (ObjectDisposedException) { }
                });
            }
        };

        _headerPanel.Controls.Add(logo);
        _headerPanel.Controls.Add(_titleLabel);
        _headerPanel.Controls.Add(_subtitleLabel);
        _headerPanel.Controls.Add(_themeToggle);
        _headerPanel.Controls.Add(_langToggle);
        _headerPanel.Controls.Add(_channelToggle);
        _headerPanel.Controls.Add(_checkUpdateLink);

        RepositionHeaderLinks();
        _headerPanel.Resize += (_, _) => RepositionHeaderLinks();

        // Bottom border
        _headerPanel.Paint += (s, e) =>
        {
            using var borderPen = new Pen(Theme.Current.SurfaceBorder);
            e.Graphics.DrawLine(borderPen, 0, _headerPanel.Height - 1, _headerPanel.Width, _headerPanel.Height - 1);
        };
    }

    /// <summary>
    /// Positions header links right-to-left from the right edge using measured text widths.
    /// Called on init, resize, language change, and theme change.
    /// </summary>
    private void RepositionHeaderLinks()
    {
        const int rightMargin = 14;
        const int gap = 10;
        int rightEdge = _headerPanel.Width > 0 ? _headerPanel.Width - rightMargin : ClientSize.Width - rightMargin;

        // Measure actual text widths (not PreferredWidth which may not be ready)
        int langW = TextRenderer.MeasureText(_langToggle.Text, _langToggle.Font).Width + 4;
        int themeW = TextRenderer.MeasureText(_themeToggle.Text, _themeToggle.Font).Width + 4;
        int checkW = TextRenderer.MeasureText(_checkUpdateLink.Text, _checkUpdateLink.Font).Width + 4;
        int channelW = TextRenderer.MeasureText(_channelToggle.Text, _channelToggle.Font).Width + 4;

        // Row 3 (y=46): langToggle | themeToggle | checkUpdateLink — right to left
        var row3 = LayoutHelper.CalculateRightToLeftPositions(rightEdge, gap, new[] { langW, themeW, checkW });
        _langToggle.Location = new Point(row3[0], 46);
        _themeToggle.Location = new Point(row3[1], 46);
        _checkUpdateLink.Location = new Point(row3[2], 46);

        // Row 2 (y=30): channelToggle — right-aligned
        var row2 = LayoutHelper.CalculateRightToLeftPositions(rightEdge, gap, new[] { channelW });
        _channelToggle.Location = new Point(row2[0], 30);
    }

    private void BuildServersTab(TabPage page)
    {
        var t = Theme.Current;

        // ── Config mode selector (FlowLayout — auto-positions for any language) ──
        _configModePanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 36,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = t.Background,
            Padding = new Padding(0, 4, 0, 4)
        };

        _vlessRadio = new RadioButton
        {
            Text = Strings.VlessServers,
            Font = t.BodyFont,
            ForeColor = t.TextPrimary,
            Checked = true,
            AutoSize = true,
            Margin = new Padding(0, 2, 10, 0)
        };
        _vlessRadio.CheckedChanged += OnConfigModeChanged;

        _customConfigRadio = new RadioButton
        {
            Text = Strings.CustomConfigJson,
            Font = t.BodyFont,
            ForeColor = t.TextPrimary,
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 0)
        };

        _configModePanel.Controls.Add(_vlessRadio);
        _configModePanel.Controls.Add(_customConfigRadio);

        // ── Custom config panel (hidden by default) ──
        _customConfigPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Visible = false
        };

        _customConfigBtnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 36,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = t.Background
        };

        _addCustomConfigBtn = new Button { Text = Strings.AddConfig, Width = 140, Height = 28 };
        Theme.ApplyPrimary(_addCustomConfigBtn);
        _addCustomConfigBtn.Click += OnAddCustomConfig;

        _removeCustomConfigBtn = new Button { Text = Strings.Remove, Width = 72, Height = 28 };
        Theme.ApplySecondary(_removeCustomConfigBtn);
        _removeCustomConfigBtn.Click += OnRemoveCustomConfig;

        _customConfigBtnPanel.Controls.Add(_addCustomConfigBtn);
        _customConfigBtnPanel.Controls.Add(_removeCustomConfigBtn);

        _customConfigList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = false,
            BackColor = t.Surface,
            ForeColor = t.TextPrimary,
            Font = t.BodyFont,
            OwnerDraw = true
        };
        _customConfigList.DrawColumnHeader += OnDrawColumnHeader;
        _customConfigList.DrawItem += OnDrawListItem;
        _customConfigList.DrawSubItem += OnDrawListSubItem;
        _customConfigList.Columns.Add("", 24);         // ★ marker
        _customConfigList.Columns.Add(Strings.ColName, 110);
        _customConfigList.Columns.Add(Strings.ColProtocols, 80);
        _customConfigList.Columns.Add(Strings.ColServer, 140);
        _customConfigList.DoubleClick += OnCustomConfigDoubleClick;
        _customConfigList.Resize += (_, _) => LayoutHelper.AutoSizeColumns(_customConfigList, new[] { 1, 4, 3, 5 });

        var customHintLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 20,
            Text = Strings.CustomConfigHint,
            ForeColor = t.TextMuted,
            Font = t.SmallFont,
            Padding = new Padding(4, 0, 0, 0)
        };

        _customConfigPanel.Controls.Add(_customConfigList);
        _customConfigPanel.Controls.Add(customHintLabel);
        _customConfigPanel.Controls.Add(_customConfigBtnPanel);

        // ── VLESS panel (mirrors Custom Config layout: buttons top, list fill, hint bottom) ──
        _vlessPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Visible = true
        };

        _vlessBtnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 36,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = t.Background
        };

        _addServerBtn = new Button { Text = Strings.AddServers, Width = 140, Height = 28 };
        Theme.ApplyPrimary(_addServerBtn);
        _addServerBtn.Click += OnAddServer;

        _removeBtn = new Button { Text = Strings.Remove, Width = 72, Height = 28 };
        Theme.ApplySecondary(_removeBtn);
        _removeBtn.Click += OnRemoveServer;

        _vlessBtnPanel.Controls.Add(_addServerBtn);
        _vlessBtnPanel.Controls.Add(_removeBtn);

        _serverList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            BackColor = t.Surface,
            ForeColor = t.TextPrimary,
            Font = t.BodyFont,
            OwnerDraw = true
        };
        _serverList.DrawColumnHeader += OnDrawColumnHeader;
        _serverList.DrawItem += OnDrawListItem;
        _serverList.DrawSubItem += OnDrawListSubItem;
        _serverList.Columns.Add("", 24);                     // ★ marker
        _serverList.Columns.Add(Strings.ColName, 110);
        _serverList.Columns.Add(Strings.ColRole, 80);
        _serverList.Columns.Add(Strings.ColServer, 140);
        _serverList.Resize += (_, _) => LayoutHelper.AutoSizeColumns(_serverList, new[] { 1, 4, 3, 5 });
        _serverList.DoubleClick += OnServerDoubleClick;

        _serverHintLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 20,
            Text = Strings.VlessHint,
            ForeColor = t.TextMuted,
            Font = t.SmallFont,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(4, 0, 0, 0)
        };

        _vlessPanel.Controls.Add(_serverList);        // Fill
        _vlessPanel.Controls.Add(_serverHintLabel);    // Bottom (single hint, updated dynamically)
        _vlessPanel.Controls.Add(_vlessBtnPanel);      // Top

        // Dock order: last added = top
        page.Controls.Add(_customConfigPanel);  // Fill (hidden by default)
        page.Controls.Add(_vlessPanel);          // Fill (visible by default)
        page.Controls.Add(_configModePanel);     // Top — mode selector
    }

    private void BuildAppsTab(TabPage page)
    {
        var t = Theme.Current;

        // ── Routing mode selector (FlowLayout — auto-positions for any language) ──
        _routingPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 64,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            BackColor = t.Background,
            Padding = new Padding(0, 4, 0, 4)
        };

        _splitRadio = new RadioButton
        {
            Text = Strings.SplitTunnel,
            Font = t.BodyFont,
            ForeColor = t.TextPrimary,
            Checked = true,
            AutoSize = true,
            Margin = new Padding(0, 2, 10, 0)
        };

        _fullRadio = new RadioButton
        {
            Text = Strings.FullTunnel,
            Font = t.BodyFont,
            ForeColor = t.TextPrimary,
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 0)
        };

        _splitRadio.CheckedChanged += (_, __) =>
        {
            _profileTree.Enabled = _splitRadio.Checked;
            _customAppInput.Enabled = _splitRadio.Checked;
            _addCustomBtn.Enabled = _splitRadio.Checked;
            _removeCustomBtn.Enabled = _splitRadio.Checked;

            if (!_isLoadingUI)
            {
                _settings.App.RoutingMode = _fullRadio.Checked ? "full" : "split";
                SaveSettings();
                ShowApplyIfNeeded();
            }
        };

        _routingPanel.Controls.Add(_splitRadio);
        _routingPanel.Controls.Add(_fullRadio);
        _routingPanel.SetFlowBreak(_fullRadio, true);

        _bypassRuCheck = new CheckBox
        {
            Text = Strings.BypassRussianTraffic,
            Font = t.BodyFont,
            ForeColor = t.TextPrimary,
            AutoSize = true,
            Checked = true,
            Margin = new Padding(0, 6, 0, 0)
        };
        _bypassRuCheck.CheckedChanged += (_, __) =>
        {
            if (_isLoadingUI) return;
            _settings.App.BypassRussianTraffic = _bypassRuCheck.Checked;
            SaveSettings();
            ShowApplyIfNeeded();
        };
        _routingPanel.Controls.Add(_bypassRuCheck);

        _appsLabel = new Label
        {
            Text = Strings.AppsHint,
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
                    ? $"{proc.Name} {Strings.ChildProcesses}"
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
            Text = Strings.CustomAppLabel,
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

        _addCustomBtn = new Button { Text = Strings.BtnAdd, Width = 55, Height = 26 };
        Theme.ApplyPrimary(_addCustomBtn);
        _addCustomBtn.Click += OnAddCustomApp;

        _removeCustomBtn = new Button { Text = Strings.RemoveChecked, Width = 140, Height = 26 };
        Theme.ApplySecondary(_removeCustomBtn);
        _removeCustomBtn.Click += OnRemoveCustomApp;

        _customInputRow.Controls.Add(_customAppInput);
        _customInputRow.Controls.Add(_addCustomBtn);
        _customInputRow.Controls.Add(_removeCustomBtn);
        _customInputRow.Resize += (_, _) =>
        {
            int btnsWidth = _addCustomBtn.Width + _removeCustomBtn.Width + 20;
            _customAppInput.Width = Math.Max(80, _customInputRow.ClientSize.Width - btnsWidth);
        };

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

        var node = new TreeNode(Strings.CustomAppsNode)
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

    // ─── Owner-draw handlers ────────────────────────────────────────────────

    private void OnDrawTab(object? sender, DrawItemEventArgs e)
    {
        var t = Theme.Current;
        var page = _tabs.TabPages[e.Index];
        var bounds = _tabs.GetTabRect(e.Index);
        bool selected = _tabs.SelectedIndex == e.Index;

        using var bgBrush = new SolidBrush(selected ? t.Background : t.Surface);
        e.Graphics.FillRectangle(bgBrush, bounds);

        // Draw bottom border on selected tab to blend with page
        if (selected)
        {
            using var borderPen = new Pen(t.Primary, 2);
            e.Graphics.DrawLine(borderPen, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
        }

        var textColor = selected ? t.TextPrimary : t.TextSecondary;
        TextRenderer.DrawText(e.Graphics, page.Text, t.BodyFont, bounds, textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private void OnTabPaint(object? sender, PaintEventArgs e)
    {
        // Fill the background area around tabs that WinForms draws with system color
        using var bgBrush = new SolidBrush(Theme.Current.Surface);
        e.Graphics.FillRectangle(bgBrush, e.ClipRectangle);

        // Redraw each tab on top of the filled background
        for (int i = 0; i < _tabs.TabCount; i++)
        {
            var tabRect = _tabs.GetTabRect(i);
            bool selected = _tabs.SelectedIndex == i;

            using var tabBrush = new SolidBrush(selected ? Theme.Current.Background : Theme.Current.Surface);
            e.Graphics.FillRectangle(tabBrush, tabRect);

            if (selected)
            {
                using var borderPen = new Pen(Theme.Current.Primary, 2);
                e.Graphics.DrawLine(borderPen, tabRect.Left, tabRect.Bottom - 1, tabRect.Right, tabRect.Bottom - 1);
            }

            var textColor = selected ? Theme.Current.TextPrimary : Theme.Current.TextSecondary;
            TextRenderer.DrawText(e.Graphics, _tabs.TabPages[i].Text, Theme.Current.BodyFont, tabRect, textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    private void OnDrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        var t = Theme.Current;
        using var bgBrush = new SolidBrush(t.Surface);
        e.Graphics.FillRectangle(bgBrush, e.Bounds);

        using var borderPen = new Pen(t.SurfaceBorder);
        e.Graphics.DrawLine(borderPen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);

        var textBounds = new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, e.Header!.Text, t.BodyFont, textBounds, t.TextSecondary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }

    private void OnDrawListItem(object? sender, DrawListViewItemEventArgs e)
    {
        e.DrawDefault = false;
    }

    private void OnDrawListSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        var t = Theme.Current;
        var item = e.Item!;
        bool selected = item.Selected;

        // Selection highlight
        Color bgColor = selected ? t.PrimaryLight : t.Surface;
        using var bgBrush = new SolidBrush(bgColor);
        e.Graphics.FillRectangle(bgBrush, e.Bounds);

        var color = item.ForeColor;
        var font = item.Font ?? t.BodyFont;

        // For sub-items use their own color if different, otherwise item color
        if (e.SubItem != null && e.ColumnIndex > 0)
        {
            color = selected ? t.TextPrimary : t.TextPrimary;
            font = t.BodyFont;
        }
        else if (selected && color == t.Primary)
        {
            // Keep primary (blue) color for selected primary row
        }

        var textBounds = new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, e.SubItem?.Text ?? item.Text, font, textBounds, color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

        if (_serverList.GridLines)
        {
            using var gridPen = new Pen(t.SurfaceBorder);
            e.Graphics.DrawLine(gridPen, e.Bounds.Right - 1, e.Bounds.Top, e.Bounds.Right - 1, e.Bounds.Bottom);
            e.Graphics.DrawLine(gridPen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        }
    }

    // ─── Theme toggle ─────────────────────────────────────────────────────────

    private void OnThemeToggle(object? sender, EventArgs e)
    {
        Theme.SetTheme(!Theme.IsDark);
        _themeToggle.Text = Theme.IsDark ? Strings.ThemeLight : Strings.ThemeDark;
        _settings.App.Theme = Theme.IsDark ? "dark" : "light";
        SaveSettings();
        ApplyTheme();
    }

    private void OnLangToggle(object? sender, EventArgs e)
    {
        Strings.Lang = Strings.Lang.Equals("ru", StringComparison.OrdinalIgnoreCase) ? "en" : "ru";
        _settings.App.Language = Strings.Lang;
        SaveSettings();
        ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        var t = Theme.Current;
        SuspendLayout();

        // Header
        _langToggle.Text = Strings.Lang.Equals("ru", StringComparison.OrdinalIgnoreCase) ? "ENG" : "RUS";
        _checkUpdateLink.Text = Strings.CheckForUpdates;
        _themeToggle.Text = Theme.IsDark ? Strings.ThemeLight : Strings.ThemeDark;
        var isExp = _settings.Update.IsExperimental;
        _channelToggle.Text = isExp ? Strings.ChannelExp : Strings.ChannelStable;
        RepositionHeaderLinks();

        // Tabs
        _serversPage.Text = Strings.TabServers;
        _appsPage.Text = Strings.TabApps;

        // Config mode
        _vlessRadio.Text = Strings.VlessServers;
        _customConfigRadio.Text = Strings.CustomConfigJson;
        _addServerBtn.Text = Strings.AddServers;
        _addCustomConfigBtn.Text = Strings.AddConfig;
        _removeCustomConfigBtn.Text = Strings.Remove;

        // Server list column headers (★, Name, Role, Server — matches Custom Config)
        // Column 0 is ★ marker (no text)
        _serverList.Columns[1].Text = Strings.ColName;
        _serverList.Columns[2].Text = Strings.ColRole;
        _serverList.Columns[3].Text = Strings.ColServer;

        // Custom config list column headers
        _customConfigList.Columns[1].Text = Strings.ColName;
        _customConfigList.Columns[2].Text = Strings.ColProtocols;
        _customConfigList.Columns[3].Text = Strings.ColServer;

        // Server buttons
        _removeBtn.Text = Strings.Remove;

        // Apps tab
        _splitRadio.Text = Strings.SplitTunnel;
        _fullRadio.Text = Strings.FullTunnel;
        _bypassRuCheck.Text = Strings.BypassRussianTraffic;
        _appsLabel.Text = Strings.AppsHint;
        _customLabel.Text = Strings.CustomAppLabel;
        _addCustomBtn.Text = Strings.BtnAdd;
        _removeCustomBtn.Text = Strings.RemoveChecked;

        // Action panel
        _autostartCheck.Text = Strings.AutostartWindows;
        _restartServiceBtn.Text = Strings.RestartService;
        _reinstallServiceBtn.Text = Strings.ReinstallService;
        _updateBtn.Text = Strings.Update;

        // Refresh lists to update role/hint text
        RefreshServerList();

        // Update status
        bool running = _engine.IsRunning || _tray.RunningAsService;
        UpdateUI(running);

        // Update custom apps tree node text
        var customNode = FindCustomNode();
        if (customNode != null)
            customNode.Text = Strings.CustomAppsNode;

        // Refresh tray menu text
        _tray.ApplyLanguage();

        ResumeLayout(true);
        _tabs.Invalidate();
    }

    private async void OnChannelToggle(object? sender, EventArgs e)
    {
        var t = Theme.Current;
        bool wasExp = _settings.Update.IsExperimental;
        _settings.Update.Channel = wasExp ? "stable" : "experimental";
        bool isExp = _settings.Update.IsExperimental;
        _channelToggle.Text = isExp ? Strings.ChannelExp : Strings.ChannelStable;
        _channelToggle.LinkColor = isExp ? t.AmberButton : t.TextMuted;
        _channelToggle.VisitedLinkColor = isExp ? t.AmberButton : t.TextMuted;
        SaveSettings();

        // Re-check for updates with new channel setting
        _pendingUpdate = null;
        _updatePanel.Visible = false;
        try { await CheckForUpdateAsync(); } catch { }
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
        var chExp = _settings.Update.IsExperimental;
        _channelToggle.LinkColor = chExp ? t.AmberButton : t.TextMuted;
        _channelToggle.ActiveLinkColor = t.Primary;
        _channelToggle.VisitedLinkColor = chExp ? t.AmberButton : t.TextMuted;
        _checkUpdateLink.LinkColor = t.TextMuted;
        _checkUpdateLink.ActiveLinkColor = t.Primary;
        _checkUpdateLink.VisitedLinkColor = t.TextMuted;

        // ── Tabs ──
        _tabs.Font = t.BodyFont;
        _serversPage.BackColor = t.Background;
        _appsPage.BackColor = t.Background;

        // ── Servers tab ──
        Theme.ApplyPrimary(_addServerBtn);
        _serverList.BackColor = t.Surface;
        _serverList.ForeColor = t.TextPrimary;
        _serverList.Font = t.BodyFont;
        _serverHintLabel.Font = t.SmallFont;
        _vlessBtnPanel.BackColor = t.Background;
        Theme.ApplySecondary(_removeBtn);

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

        // Force repaint of owner-drawn controls and border lines
        _tabs.Invalidate();
        _serverList.Invalidate();
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
            if (_applyBtn.Visible)
            {
                _applyBtn.Visible = false;
                LayoutActionButtons();
            }
            return;
        }

        if (!_applyBtn.Visible)
        {
            _applyBtn.Visible = true;
            LayoutActionButtons();
        }
    }

    private async void OnApplyChanges(object? sender, EventArgs e)
    {
        SaveSettings();
        _applyBtn.Enabled = false;
        _applyBtn.Text = Strings.Applying;
        _startStopBtn.Enabled = false;

        try
        {
            if (_tray.RunningAsService)
            {
                // Service mode: stop → start service
                _statusLabel.Text = Strings.RestartingService;
                await Task.Run(() =>
                {
                    ServiceInstaller.Stop();
                    ServiceInstaller.Start();
                });
            }
            else
            {
                // In-process mode: stop engine → start engine
                _statusLabel.Text = Strings.ApplyingChanges;
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
            MessageBox.Show($"{Strings.FailedApply}\n{ex.Message}", AppBranding.AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _applyBtn.Enabled = true;
            _applyBtn.Text = Strings.Apply;
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
    private bool _isLoadingUI;

    private void LoadSettingsIntoUI()
    {
        _isLoadingUI = true;
        try
        {
        // Load config mode
        var isCustomConfig = (_settings.App.ConfigMode ?? "generated")
            .Equals("custom", StringComparison.OrdinalIgnoreCase);

        // Migrate single custom_config to multi-config list (backward compat)
        if (isCustomConfig && _settings.App.CustomConfigs.Count == 0
            && !string.IsNullOrEmpty(_settings.App.CustomConfig))
        {
            var name = Path.GetFileNameWithoutExtension(_settings.App.CustomConfig);
            if (string.IsNullOrEmpty(name)) name = "custom";
            var localPath = CustomConfigInjector.GetProgramDataPath(name);
            if (!File.Exists(localPath) && File.Exists(_settings.App.CustomConfig))
                localPath = CustomConfigInjector.CopyToProgramData(_settings.App.CustomConfig, name);
            _settings.App.CustomConfigs.Add(new CustomConfigEntry { Name = name, Path = localPath });
            _settings.App.ActiveCustomConfig = name;
        }

        _vlessRadio.Checked = !isCustomConfig;
        _customConfigRadio.Checked = isCustomConfig;
        RefreshCustomConfigList();

        // Toggle visibility based on config mode
        _vlessPanel.Visible = !isCustomConfig;
        _customConfigPanel.Visible = isCustomConfig;

        // Load routing mode
        var isFullTunnel = (_settings.App.RoutingMode ?? "split")
            .Equals("full", StringComparison.OrdinalIgnoreCase);
        _splitRadio.Checked = !isFullTunnel;
        _fullRadio.Checked = isFullTunnel;

        // Russian geo bypass
        _bypassRuCheck.Checked = _settings.App.BypassRussianTraffic;

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
        finally
        {
            _isLoadingUI = false;
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
        var downloadBytes = info.HasLiteUpdate ? info.LiteSizeBytes : info.SizeBytes;
        var sizeMb = downloadBytes > 0 ? $"  ({downloadBytes / 1024 / 1024} MB)" : "";
        var updateType = info.HasLiteUpdate ? Strings.UpdateTypeLite : Strings.UpdateTypeFull;
        _updateLabel.Text = Strings.UpdateAvailable(updateType, info.LatestVersion, sizeMb);
        _updatePanel.Visible = true;
    }

    private async void OnUpdateClick(object? sender, EventArgs e)
    {
        if (_pendingUpdate == null || _updateChecker == null) return;

        var msg = Strings.UpdateConfirm(_pendingUpdate.LatestVersion) + "\n";

        if (!string.IsNullOrWhiteSpace(_pendingUpdate.ReleaseNotes))
            msg += $"\n--- Changelog ---\n{_pendingUpdate.ReleaseNotes}\n-----------------\n";

        bool vpnRunning = _engine.IsRunning || _tray.RunningAsService;
        bool serviceInstalled = ServiceInstaller.IsInstalled();

        if (vpnRunning || serviceInstalled)
            msg += Strings.UpdateVpnWillStop;
        if (serviceInstalled)
            msg += Strings.UpdateServiceWillRemove;
        msg += Strings.UpdateWillRestart;

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
                _updateLabel.Text = Strings.StoppingVpn;
                _engine.Stop();
            }

            if (_tray.RunningAsService)
            {
                _updateLabel.Text = Strings.StoppingService;
                await Task.Run(() => ServiceInstaller.Stop());
            }

            // Uninstall service before applying — binary path may change
            // (e.g. migration from service\ subfolder to root, or single-file to shared-runtime).
            // User can re-enable autostart after update via the checkbox.
            if (ServiceInstaller.IsInstalled())
            {
                _updateLabel.Text = Strings.RemovingOldService;
                await Task.Run(() => ServiceInstaller.Uninstall());
                await Task.Delay(500);
            }

            // Apply in-process: copies files directly, renames locked exes
            _updateLabel.Text = Strings.ApplyingUpdate;
            _updateChecker.ApplyUpdate(extractedDir);

            // ApplyUpdate already launched the new GUI — just exit
            Application.Exit();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"{Strings.UpdateFailed}\n{ex.Message}", AppBranding.AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            _updateBtn.Visible = true;
            _updateBtn.Enabled = true;
            _updateProgress.Visible = false;
            var dlBytes = _pendingUpdate.HasLiteUpdate ? _pendingUpdate.LiteSizeBytes : _pendingUpdate.SizeBytes;
            var dlMb = dlBytes > 0 ? $"  ({dlBytes / 1024 / 1024} MB)" : "";
            var updType = _pendingUpdate.HasLiteUpdate ? Strings.UpdateTypeLite : Strings.UpdateTypeFull;
            _updateLabel.Text = Strings.UpdateAvailable(updType, _pendingUpdate.LatestVersion, dlMb);
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

        // Group servers by IP — each row represents one server address
        var groups = _servers
            .Select((s, i) => (server: s, index: i))
            .GroupBy(x => x.server.Server, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Context-sensitive hint
        bool hasFlow = _servers.Any(s => !string.IsNullOrEmpty(s.Flow));
        bool hasNoFlow = _servers.Any(s => string.IsNullOrEmpty(s.Flow));
        bool udpSplit = hasFlow && hasNoFlow;

        if (udpSplit)
        {
            _serverHintLabel.Text = Strings.TcpUdpHint;
            _serverHintLabel.ForeColor = t.Primary;
        }
        else
        {
            _serverHintLabel.Text = Strings.VlessHint;
            _serverHintLabel.ForeColor = t.TextMuted;
        }

        bool isFirst = true;
        foreach (var group in groups)
        {
            var entries = group.ToList();
            var indices = entries.Select(e => e.index).ToList();

            // Build role: TCP, UDP, or TCP+UDP
            bool groupHasFlow = entries.Any(e => !string.IsNullOrEmpty(e.server.Flow));
            bool groupHasNoFlow = entries.Any(e => string.IsNullOrEmpty(e.server.Flow));
            string role;
            if (groupHasFlow && groupHasNoFlow)
                role = "TCP+UDP";
            else if (groupHasFlow)
                role = "TCP";
            else if (udpSplit)
                role = "UDP";
            else
                role = isFirst ? Strings.RolePrimary : Strings.RoleFallback(groups.IndexOf(group));

            // Build name: combine names from all entries
            var names = entries
                .Select(e => string.IsNullOrEmpty(e.server.Name) ? null : e.server.Name)
                .Where(n => n != null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var displayName = names.Count > 0 ? string.Join(" / ", names) : Strings.NoName;

            // ★ marker for first group (primary)
            var item = new ListViewItem(isFirst ? "\u2605" : "") { Tag = indices };
            item.SubItems.Add(displayName);
            item.SubItems.Add(role);
            item.SubItems.Add(group.Key); // server IP

            if (isFirst)
            {
                item.ForeColor = t.Primary;
                item.Font = t.BoldBodyFont;
            }

            _serverList.Items.Add(item);
            isFirst = false;
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
        // Block ALL saves during UI loading — event handlers fire during
        // LoadSettingsIntoUI and would overwrite settings with partial state.
        if (_isLoadingUI) return;

        // Safe to read radio state here — _isLoadingUI blocks during loading
        _settings.App.ConfigMode = !_vlessRadio.Checked ? "custom" : "generated";

        // Set CustomConfig for backward compat (single path from active entry)
        var activeEntry = _settings.App.CustomConfigs
            .FirstOrDefault(c => c.Name == _settings.App.ActiveCustomConfig);
        _settings.App.CustomConfig = activeEntry?.Path ?? "";

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

        // Russian geo bypass
        _settings.App.BypassRussianTraffic = _bypassRuCheck.Checked;

        SettingsLoader.Save(_settings);
    }

    // ─── Config mode switching ─────────────────────────────────────────────

    private void OnConfigModeChanged(object? sender, EventArgs e)
    {
        if (_isLoadingUI) return;

        // Use _vlessRadio state directly — it's already updated when its CheckedChanged fires.
        var isCustom = !_vlessRadio.Checked;

        // Toggle panels (identical structure, just swap visibility)
        _vlessPanel.Visible = !isCustom;
        _customConfigPanel.Visible = isCustom;

        // Save mode
        _settings.App.ConfigMode = isCustom ? "custom" : "generated";
        SaveSettings();
        ShowApplyIfNeeded();
    }

    private void OnAddCustomConfig(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = Strings.SelectSingBoxConfig,
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            FilterIndex = 1
        };

        if (dialog.ShowDialog() != DialogResult.OK) return;

        var filePath = dialog.FileName;
        var name = Path.GetFileNameWithoutExtension(filePath);

        // Check for duplicate name
        if (_settings.App.CustomConfigs.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(Strings.ConfigExists(name), AppBranding.AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Validate JSON
        var rawJson = File.ReadAllText(filePath);
        var (isValid, errors) = CustomConfigInjector.Validate(rawJson);
        if (!isValid)
        {
            MessageBox.Show($"{Strings.InvalidConfig}\n{string.Join("\n", errors)}", AppBranding.AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // Copy to ProgramData
        var localPath = CustomConfigInjector.CopyToProgramData(filePath, name);

        // Add to list
        _settings.App.CustomConfigs.Add(new CustomConfigEntry { Name = name, Path = localPath });

        // Set as active if first config
        if (_settings.App.CustomConfigs.Count == 1)
            _settings.App.ActiveCustomConfig = name;

        RefreshCustomConfigList();
        SaveSettings();
    }

    private void OnRemoveCustomConfig(object? sender, EventArgs e)
    {
        if (_customConfigList.SelectedItems.Count == 0) return;

        var selectedName = _customConfigList.SelectedItems[0].SubItems[1].Text;

        if (MessageBox.Show(Strings.ConfirmRemove(selectedName), AppBranding.AppName,
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        _settings.App.CustomConfigs.RemoveAll(c => c.Name == selectedName);

        // If removed active, select first remaining
        if (_settings.App.ActiveCustomConfig == selectedName)
            _settings.App.ActiveCustomConfig = _settings.App.CustomConfigs.FirstOrDefault()?.Name ?? "";

        RefreshCustomConfigList();
        SaveSettings();
    }

    private void OnCustomConfigDoubleClick(object? sender, EventArgs e)
    {
        if (_customConfigList.SelectedItems.Count == 0) return;

        var selectedName = _customConfigList.SelectedItems[0].SubItems[1].Text;
        _settings.App.ActiveCustomConfig = selectedName;

        RefreshCustomConfigList();
        SaveSettings();
        ShowApplyIfNeeded();
    }

    private void RefreshCustomConfigList()
    {
        _customConfigList.Items.Clear();

        foreach (var config in _settings.App.CustomConfigs)
        {
            var isActive = config.Name == _settings.App.ActiveCustomConfig;

            // Parse protocol/server info from the ProgramData copy
            var (protocols, server) = ("?", "?");
            var pdPath = CustomConfigInjector.GetProgramDataPath(config.Name);
            if (File.Exists(pdPath))
            {
                try
                {
                    var json = File.ReadAllText(pdPath);
                    (protocols, server) = CustomConfigInjector.ParseConfigInfo(json);
                }
                catch { }
            }

            var item = new ListViewItem(isActive ? "\u2605" : "");  // ★ marker
            item.SubItems.Add(config.Name);
            item.SubItems.Add(protocols);
            item.SubItems.Add(server);
            _customConfigList.Items.Add(item);
        }
    }

    // ─── Event handlers ──────────────────────────────────────────────────────

    private void OnAddServer(object? sender, EventArgs e)
    {
        // Show input dialog (matches Add Config... pattern)
        using var dlg = new Form
        {
            Text = Strings.AddServers,
            Size = new Size(500, 200),
            MinimumSize = new Size(400, 180),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Theme.Current.Background
        };

        var label = new Label
        {
            Text = Strings.PasteVlessUri,
            Dock = DockStyle.Top,
            Height = 20,
            ForeColor = Theme.Current.TextSecondary,
            Font = Theme.Current.BodyFont
        };

        var input = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            PlaceholderText = Strings.PlaceholderVless,
            BackColor = Theme.Current.InputBackground,
            ForeColor = Theme.Current.TextPrimary,
            Font = Theme.Current.BodyFont
        };

        var okBtn = new Button { Text = "OK", DialogResult = DialogResult.OK, Dock = DockStyle.Bottom, Height = 32 };
        Theme.ApplyPrimary(okBtn);
        dlg.AcceptButton = okBtn;

        dlg.Controls.Add(input);
        dlg.Controls.Add(okBtn);
        dlg.Controls.Add(label);

        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var text = input.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        try
        {
            var entries = VlessUriParser.ParseMultiple(text);
            if (entries.Count == 0)
            {
                MessageBox.Show(Strings.NoValidVless, AppBranding.AppName,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var added = 0;
            var skipped = 0;
            foreach (var entry in entries)
            {
                bool isDuplicate = _servers.Any(s =>
                    s.Server.Equals(entry.Server, StringComparison.OrdinalIgnoreCase) &&
                    s.Port == entry.Port &&
                    s.Uuid.Equals(entry.Uuid, StringComparison.OrdinalIgnoreCase));

                if (isDuplicate) skipped++;
                else { _servers.Add(entry); added++; }
            }

            RefreshServerList();
            SaveSettings();

            if (skipped > 0)
                MessageBox.Show(Strings.AddedSkipped(added, skipped),
                    AppBranding.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"{Strings.FailedParseVless}\n{ex.Message}", AppBranding.AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnRemoveServer(object? sender, EventArgs e)
    {
        if (_serverList.SelectedItems.Count == 0) return;

        var selectedName = _serverList.SelectedItems[0].SubItems[1].Text;
        if (MessageBox.Show(Strings.ConfirmRemove(selectedName), AppBranding.AppName,
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        // Collect all server indices from grouped Tag (List<int>)
        var indicesToRemove = _serverList.SelectedItems.Cast<ListViewItem>()
            .Where(item => item.Tag is List<int>)
            .SelectMany(item => (List<int>)item.Tag!)
            .OrderByDescending(idx => idx)
            .ToList();

        foreach (var idx in indicesToRemove)
            _servers.RemoveAt(idx);

        RefreshServerList();
        SaveSettings();
    }

    private void OnServerDoubleClick(object? sender, EventArgs e)
    {
        if (_serverList.SelectedItems.Count == 0) return;
        var selected = _serverList.SelectedItems[0];
        if (selected.Tag is not List<int> indices) return;

        // Already first group — nothing to do
        if (indices.Count > 0 && indices.Min() == 0) return;

        // Move all servers of this IP group to the top of the list
        var movedServers = indices.OrderBy(i => i).Select(i => _servers[i]).ToList();
        foreach (var idx in indices.OrderByDescending(i => i))
            _servers.RemoveAt(idx);
        _servers.InsertRange(0, movedServers);

        RefreshServerList();
        SaveSettings();
        ShowApplyIfNeeded();
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
                    MessageBox.Show(Strings.ServiceExeNotFound,
                        AppBranding.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    _autostartCheck.Checked = false;
                    return;
                }

                _statusLabel.Text = Strings.InstallingService;

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
                    MessageBox.Show($"{Strings.FailedSetupService}\n{result.Message}",
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
                _statusLabel.Text = Strings.RemovingService;

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
        _restartServiceBtn.Text = Strings.Restarting;
        _startStopBtn.Enabled = false;
        _statusLabel.Text = Strings.RestartingService;

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
                MessageBox.Show($"{Strings.FailedRestartService}\n{result.Message}",
                    AppBranding.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _tray.SyncTrayState(null);
            UpdateUI(true);
        }
        finally
        {
            _restartServiceBtn.Enabled = true;
            _restartServiceBtn.Text = Strings.RestartService;
            _startStopBtn.Enabled = true;
        }
    }

    private async void OnReinstallService(object? sender, EventArgs e)
    {
        var confirm = MessageBox.Show(Strings.ReinstallConfirm,
            AppBranding.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);

        if (confirm != DialogResult.Yes) return;

        _reinstallServiceBtn.Enabled = false;
        _reinstallServiceBtn.Text = Strings.Reinstalling;
        _restartServiceBtn.Enabled = false;
        _startStopBtn.Enabled = false;
        _autostartCheck.Enabled = false;
        _statusLabel.Text = Strings.ReinstallingService;

        try
        {
            var serviceExe = Path.Combine(AppContext.BaseDirectory, "service", "VPNRouter.Service.exe");
            if (!File.Exists(serviceExe))
                serviceExe = Path.Combine(AppContext.BaseDirectory, "VPNRouter.Service.exe");

            if (!File.Exists(serviceExe))
            {
                MessageBox.Show(Strings.ServiceExeNotFoundReinstall,
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
                MessageBox.Show($"{Strings.ServiceReinstallFailed}\n{result.Message}",
                    AppBranding.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _tray.RunningAsService = true;
            _tray.SyncTrayState(null);
            UpdateUI(true);
            _autostartCheck.Checked = true;
            MessageBox.Show(Strings.ServiceReinstalled,
                AppBranding.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        finally
        {
            _reinstallServiceBtn.Enabled = true;
            _reinstallServiceBtn.Text = Strings.ReinstallService;
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
            _startStopBtn.Text = Strings.Stopping;
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
            MessageBox.Show(Strings.AddServerFirst, AppBranding.AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (_splitRadio.Checked)
        {
            var checkedCount = _profileTree.Nodes.Cast<TreeNode>().Count(n => n.Checked);
            if (checkedCount == 0)
            {
                MessageBox.Show(Strings.SelectAppGroup, AppBranding.AppName,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        try
        {
            _startStopBtn.Enabled = false;
            _startStopBtn.Text = Strings.Starting;

            var settings = SettingsLoader.Load();
            await _engine.StartAsync(settings);

            UpdateUI(true);
        }
        catch (Exception ex)
        {
            UpdateUI(false);
            MessageBox.Show($"{Strings.FailedStartVpn}\n{ex.Message}", AppBranding.AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateUI(bool running)
    {
        var t = Theme.Current;
        _startStopBtn.Enabled = true;

        if (running)
        {
            _startStopBtn.Text = Strings.StopVPN;
            _startStopBtn.BackColor = t.Danger;
            _startStopBtn.ForeColor = t.TextOnPrimary;
        }
        else
        {
            ApplyStartStyle();
            _applyBtn.Visible = false;
        }

        LayoutActionButtons();

        _restartServiceBtn.Visible = _tray.RunningAsService;
        _reinstallServiceBtn.Visible = _tray.RunningAsService || ServiceInstaller.IsInstalled();

        if (_tray.RunningAsService)
        {
            SetStatus(Strings.ConnectedService, t.Success, t.SuccessLight);
        }
        else if (running)
        {
            var configMode = _engine.ActiveConfigMode == "custom" ? Strings.ModeCustom : Strings.ModeVless;
            var routingMode = _engine.ActiveRoutingMode == "full" ? Strings.ModeFull : Strings.ModeSplit;
            var server = _engine.ActiveServerAddress;
            // Short status: mode + server (fits in one line)
            var shortText = string.IsNullOrEmpty(server)
                ? $"{configMode} | {routingMode}"
                : $"{configMode} | {routingMode} \u2192 {server}";
            // Full tooltip with all details
            var tooltip = Strings.Connected(_engine.ActiveProfileName, _engine.SingBoxPid ?? 0)
                + $"  [{configMode} | {routingMode}]"
                + (string.IsNullOrEmpty(server) ? "" : $" \u2192 {server}");
            SetStatus(shortText, t.Success, t.SuccessLight, tooltip);
        }
        else
        {
            SetStatus(Strings.NotConnected, t.TextMuted, t.Background);
        }
    }

    private void ApplyStartStyle()
    {
        var t = Theme.Current;
        _startStopBtn.Text = Strings.StartVPN;
        _startStopBtn.BackColor = t.Primary;
        _startStopBtn.ForeColor = t.TextOnPrimary;
    }

    private void SetStatus(string text, Color foreColor, Color bgColor, string? tooltip = null)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = foreColor;
        _statusDot.ForeColor = foreColor;
        _statusPanel.BackColor = bgColor;
        _statusTooltip.SetToolTip(_statusLabel, tooltip ?? text);
        _statusTooltip.SetToolTip(_statusDot, tooltip ?? text);
    }

    /// <summary>
    /// Dynamically position action panel buttons based on panel width and text sizes.
    /// </summary>
    private void LayoutActionButtons()
    {
        const int margin = 14;
        const int gap = 6;

        int applyTextW = _applyBtn.Visible
            ? TextRenderer.MeasureText(_applyBtn.Text, _applyBtn.Font).Width
            : 0;

        var (startW, applyX, applyW) = LayoutHelper.CalculateActionLayout(
            _actionPanel.ClientSize.Width, margin, gap, 120, applyTextW, _applyBtn.Visible);

        _startStopBtn.Size = new Size(startW, 36);
        _startStopBtn.Location = new Point(margin, 4);

        if (_applyBtn.Visible)
        {
            _applyBtn.Size = new Size(applyW, 36);
            _applyBtn.Location = new Point(applyX, 4);
        }

        // Row 2: service buttons flow after autostart checkbox
        _autostartCheck.Location = new Point(margin, 50);
        int x = _autostartCheck.Right + 10;

        if (_restartServiceBtn.Visible)
        {
            int w = TextRenderer.MeasureText(_restartServiceBtn.Text, _restartServiceBtn.Font).Width + 20;
            _restartServiceBtn.Size = new Size(Math.Max(100, w), 24);
            _restartServiceBtn.Location = new Point(x, 50);
            x = _restartServiceBtn.Right + 6;
        }
        if (_reinstallServiceBtn.Visible)
        {
            int w = TextRenderer.MeasureText(_reinstallServiceBtn.Text, _reinstallServiceBtn.Font).Width + 20;
            _reinstallServiceBtn.Size = new Size(Math.Max(100, w), 24);
            _reinstallServiceBtn.Location = new Point(x, 50);
        }
    }

    private void OnEngineStatus(string msg)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnEngineStatus(msg));
            return;
        }

        // When engine reports final "Connected" status after warm-up,
        // refresh with full detail (mode, server) instead of plain PID text.
        if (msg.StartsWith("Connected") && _engine.IsRunning)
        {
            UpdateUI(true);
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
