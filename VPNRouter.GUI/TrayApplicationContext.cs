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

    private readonly ToolStripMenuItem _startItem;
    private readonly ToolStripMenuItem _stopItem;
    private readonly ToolStripMenuItem _statusItem;

    public TrayApplicationContext()
    {
        // Build context menu
        _startItem = new ToolStripMenuItem("Start VPN", null, OnStartVpn);
        _stopItem = new ToolStripMenuItem("Stop VPN", null, OnStopVpn) { Enabled = false };
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

        // Subscribe to engine events
        _engine.StatusChanged += msg =>
        {
            _statusItem.Text = msg;
            // NotifyIcon.Text max 63 chars
            var trayText = $"VPNRouter — {msg}";
            _trayIcon.Text = trayText.Length > 63 ? trayText[..63] : trayText;
        };

        // Check if service is already running
        if (ServiceInstaller.IsRunning())
        {
            _statusItem.Text = "Running as Service";
            _startItem.Enabled = false;
            _stopItem.Enabled = true;
        }

        // Auto-open settings on first launch if no config exists
        var configPath = Environment.ExpandEnvironmentVariables(@"%ProgramData%\VPNRouter\config.yaml");
        if (!File.Exists(configPath))
        {
            // First run — open settings immediately
            BeginInvoke(() => OnOpenSettings(this, EventArgs.Empty));
        }
    }

    private void BeginInvoke(Action action)
    {
        if (_mainForm != null && _mainForm.InvokeRequired)
            _mainForm.BeginInvoke(action);
        else
            action();
    }

    // ─── Menu handlers ───────────────────────────────────────────────────────

    private void OnOpenSettings(object? sender, EventArgs e)
    {
        if (_mainForm != null && !_mainForm.IsDisposed)
        {
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
            _stopItem.Enabled = true;
            _trayIcon.ShowBalloonTip(2000, "VPNRouter", "VPN started", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _startItem.Enabled = true;
            MessageBox.Show($"Failed to start VPN:\n{ex.Message}", "VPNRouter",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnStopVpn(object? sender, EventArgs e)
    {
        if (ServiceInstaller.IsRunning())
        {
            ServiceInstaller.Stop();
        }
        else
        {
            _engine.Stop();
        }

        _startItem.Enabled = true;
        _stopItem.Enabled = false;
        _statusItem.Text = "Not running";
        _trayIcon.Text = "VPNRouter";
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
                    _startItem.Enabled = false;
                    _stopItem.Enabled = true;
                    _statusItem.Text = "Running as Service";
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
