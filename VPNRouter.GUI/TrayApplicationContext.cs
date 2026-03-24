using VPNRouter.Core.Services;
using VPNRouter.GUI.Localization;
using VPNRouter.Service;

namespace VPNRouter.GUI;

/// <summary>
/// System tray application context. Manages NotifyIcon and MainForm lifecycle.
/// </summary>
public class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly VpnEngine _engine = new();
    private MainForm? _mainForm;
    private bool _runningAsService;

    private readonly ToolStripMenuItem _startItem;
    private readonly ToolStripMenuItem _stopItem;
    private readonly ToolStripMenuItem _statusItem;

    public TrayApplicationContext()
    {
        // Build context menu
        _startItem = new ToolStripMenuItem(Strings.TrayStart, null, OnStartVpn);
        _stopItem = new ToolStripMenuItem(Strings.TrayStop, null, OnStopVpn) { Enabled = false };
        _statusItem = new ToolStripMenuItem(Strings.TrayNotRunning) { Enabled = false };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Strings.TraySettings, null, OnOpenSettings);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startItem);
        menu.Items.Add(_stopItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Strings.TrayExit, null, OnExit);

        _trayIcon = new NotifyIcon
        {
            Icon = AppBranding.GetIcon(16),
            Text = AppBranding.TrayTooltip,
            ContextMenuStrip = menu,
            Visible = true
        };

        _trayIcon.DoubleClick += OnOpenSettings;

        // Subscribe to engine events — sync tray state whenever engine starts/stops
        _engine.StatusChanged += msg =>
        {
            InvokeOnUI(() => SyncTrayState(msg));
        };

        // Check if service is already running
        _runningAsService = ServiceInstaller.IsRunning();
        SyncTrayState(null);

        // Always open settings window on launch
        OnOpenSettings(this, EventArgs.Empty);
    }

    /// <summary>
    /// Single source of truth: updates tray icon, tooltip, start/stop buttons based on engine state.
    /// Called from engine events AND from manual start/stop actions.
    /// </summary>
    internal void SyncTrayState(string? statusMessage)
    {
        bool running = _runningAsService || _engine.IsRunning;

        _startItem.Enabled = !running;
        _stopItem.Enabled = running;

        if (_runningAsService)
        {
            _statusItem.Text = statusMessage ?? Strings.TrayRunningService;
            SetTrayTooltip($"{AppBranding.AppName} — Service");
        }
        else if (_engine.IsRunning)
        {
            var profile = _engine.ActiveProfileName;
            _statusItem.Text = statusMessage ?? Strings.TrayRunning(profile);
            SetTrayTooltip($"{AppBranding.ShortName} — {profile} (PID {_engine.SingBoxPid})");
        }
        else
        {
            _statusItem.Text = statusMessage ?? Strings.TrayNotRunning;
            SetTrayTooltip(AppBranding.TrayTooltip);
        }

        // Also update MainForm if open
        _mainForm?.RefreshStatus();
    }

    internal bool RunningAsService
    {
        get => _runningAsService;
        set => _runningAsService = value;
    }

    internal void ApplyLanguage()
    {
        _startItem.Text = Strings.TrayStart;
        _stopItem.Text = Strings.TrayStop;

        // Update Settings/Exit items (indices 2 and 6 in menu)
        var menu = _trayIcon.ContextMenuStrip!;
        if (menu.Items.Count >= 8)
        {
            menu.Items[2].Text = Strings.TraySettings;
            menu.Items[7].Text = Strings.TrayExit;
        }

        SyncTrayState(null);
    }

    private void SetTrayTooltip(string text)
    {
        _trayIcon.Text = text.Length > 63 ? text[..63] : text;
    }

    private void InvokeOnUI(Action action)
    {
        if (_mainForm != null && !_mainForm.IsDisposed && _mainForm.InvokeRequired)
            _mainForm.BeginInvoke(action);
        else
            action();
    }

    // ─── Menu handlers ───────────────────────────────────────────────────────

    private void OnOpenSettings(object? sender, EventArgs e)
    {
        if (_mainForm != null && !_mainForm.IsDisposed)
        {
            _mainForm.Show();
            _mainForm.WindowState = FormWindowState.Normal;
            _mainForm.Activate();
            return;
        }

        _mainForm = new MainForm(_engine, this);
        _mainForm.FormClosed += (_, _) => _mainForm = null;
        _mainForm.Show();
    }

    private async void OnStartVpn(object? sender, EventArgs e)
    {
        try
        {
            _startItem.Enabled = false;
            var settings = SettingsLoader.Load();
            await _engine.StartAsync(settings);
            _runningAsService = false;
            SyncTrayState(null);
            _trayIcon.ShowBalloonTip(2000, AppBranding.AppName, Strings.TrayVpnStarted, ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            SyncTrayState(null);
            MessageBox.Show($"{Strings.FailedStartVpn}\n{ex.Message}", AppBranding.AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnStopVpn(object? sender, EventArgs e)
    {
        if (_runningAsService)
        {
            ServiceInstaller.Stop();
            _runningAsService = false;
        }
        else
        {
            _engine.Stop();
        }

        SyncTrayState(null);
    }

    private void OnExit(object? sender, EventArgs e)
    {
        if (_engine.IsRunning)
        {
            if (MessageBox.Show(Strings.TrayStopAndExit, AppBranding.AppName,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.No)
                return;

            _engine.Stop();
        }

        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _engine.Dispose();
            _trayIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}
