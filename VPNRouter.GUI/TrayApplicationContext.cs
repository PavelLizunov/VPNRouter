using VPNRouter.Core.Services;
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
        _startItem = new ToolStripMenuItem("▶ Start VPN", null, OnStartVpn);
        _stopItem = new ToolStripMenuItem("⬛ Stop VPN", null, OnStopVpn) { Enabled = false };
        _statusItem = new ToolStripMenuItem("Not running") { Enabled = false };

        var serviceInstalled = ServiceInstaller.IsInstalled();
        var installServiceItem = new ToolStripMenuItem(
            serviceInstalled ? "Uninstall Service" : "Install as Service",
            null, OnToggleService);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings...", null, OnOpenSettings);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startItem);
        menu.Items.Add(_stopItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(installServiceItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, OnExit);

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = "VPNRouter",
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

        // Auto-open settings on first launch if no config exists
        var configPath = Environment.ExpandEnvironmentVariables(@"%ProgramData%\VPNRouter\config.yaml");
        if (!File.Exists(configPath))
        {
            InvokeOnUI(() => OnOpenSettings(this, EventArgs.Empty));
        }
    }

    /// <summary>
    /// Single source of truth: updates tray icon, tooltip, start/stop buttons based on engine state.
    /// Called from engine events AND from manual start/stop actions.
    /// </summary>
    private void SyncTrayState(string? statusMessage)
    {
        bool running = _runningAsService || _engine.IsRunning;

        _startItem.Enabled = !running;
        _stopItem.Enabled = running;

        if (_runningAsService)
        {
            _statusItem.Text = statusMessage ?? $"Running as Windows Service";
            SetTrayTooltip("VPNRouter — Service");
        }
        else if (_engine.IsRunning)
        {
            var profile = _engine.ActiveProfileName;
            _statusItem.Text = statusMessage ?? $"Running (in-process) — {profile}";
            SetTrayTooltip($"VPNRouter — {profile} (PID {_engine.SingBoxPid})");
        }
        else
        {
            _statusItem.Text = statusMessage ?? "Not running";
            SetTrayTooltip("VPNRouter");
        }

        // Also update MainForm if open
        _mainForm?.RefreshStatus();
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

        _mainForm = new MainForm(_engine);
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
            _trayIcon.ShowBalloonTip(2000, "VPNRouter", "VPN started (in-process)", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            SyncTrayState(null);
            MessageBox.Show($"Failed to start VPN:\n{ex.Message}", "VPNRouter",
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

    private void OnToggleService(object? sender, EventArgs e)
    {
        var item = (ToolStripMenuItem)sender!;

        if (ServiceInstaller.IsInstalled())
        {
            var result = ServiceInstaller.Uninstall();
            MessageBox.Show(result.Message, "VPNRouter",
                MessageBoxButtons.OK, result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
            if (result.Success) item.Text = "Install as Service";
        }
        else
        {
            // Find service exe relative to GUI
            var serviceExe = Path.Combine(AppContext.BaseDirectory, "service", "VPNRouter.Service.exe");
            if (!File.Exists(serviceExe))
                serviceExe = Path.Combine(AppContext.BaseDirectory, "VPNRouter.Service.exe");

            if (!File.Exists(serviceExe))
            {
                MessageBox.Show("VPNRouter.Service.exe not found.", "VPNRouter",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var result = ServiceInstaller.Install(serviceExe);
            MessageBox.Show(result.Message, "VPNRouter",
                MessageBoxButtons.OK, result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Error);

            if (result.Success)
            {
                item.Text = "Uninstall Service";
                if (MessageBox.Show("Start service now?", "VPNRouter",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    ServiceInstaller.Start();
                    _runningAsService = true;
                    SyncTrayState(null);
                }
            }
        }
    }

    private void OnExit(object? sender, EventArgs e)
    {
        if (_engine.IsRunning)
        {
            if (MessageBox.Show("VPN is running. Stop and exit?", "VPNRouter",
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
