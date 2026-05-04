using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using VPNRouter.Core.Services;

namespace VPNRouter.Android;

/// <summary>
/// v3.0 Android port — Phase 1.G view.
///
/// <para>Phase 1.G (2026-05-04) — adds an in-app settings UI for the
/// VLESS URI. Pre-1.G the only way to provide a server config was
/// <c>adb shell run-as ... &gt; files/shared_prefs/vpnrouter_settings.xml</c>
/// — a developer-only flow. 1.G surfaces a TextBox where the user can
/// paste a <c>vless://</c> share-link, validates it via
/// <see cref="VlessUriParser"/>, and stores it via
/// <see cref="AndroidStorage.SetVlessUri"/>. Connect-button now uses
/// the stored URI; the placeholder fallback in MainActivity remains as
/// a safety net for fresh installs but normal users never hit it.</para>
///
/// <para>Phase 1.D-1.F (still in effect):
/// <list type="bullet">
///   <item>1.D: Connect/Disconnect button → MainActivity intent →
///   VpnService.Prepare consent → libbox tunnel</item>
///   <item>1.E: VPNRouter.Core ConfigGenerator integration via
///   <see cref="AndroidConfigBuilder"/></item>
///   <item>1.F: SharedPreferences persistence via
///   <see cref="AndroidStorage"/></item>
/// </list></para>
///
/// <para>Status is "intent-level only" through 1.G — the button reflects
/// what we asked the OS to do, not whether the tunnel is currently
/// carrying packets. Real state sync (libbox callbacks → broadcast →
/// UI) is Phase 1.H. The system-level VPN-key icon in the status bar is
/// the authoritative tunnel-up signal for now.</para>
///
/// <para>Phase 2+ will start linking shared XAML from
/// <c>VPNRouter.App</c> so we get the same theme + design tokens
/// across desktop and Android. Until then this view is local to the
/// Android project.</para>
/// </summary>
public partial class AndroidApp : Avalonia.Application
{
    private TextBlock? _statusBlock;
    private Avalonia.Controls.Button? _toggleButton;
    private TextBox? _serverUriBox;
    private TextBlock? _serverUriStatus;
    private Avalonia.Controls.Button? _saveServerButton;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = BuildPhase1gView();
            // React to Activity-side connect/disconnect intent flips so
            // user-driven Disconnect from the system VPN settings (which
            // also routes through MainActivity.RequestDisconnect via the
            // button below) keeps the view in sync.
            MainActivity.IntentChanged += OnIntentChanged;
            // Apply the current intent state at view-construction time —
            // covers the case where the Activity already had a tunnel up
            // when the Avalonia view first attached.
            UpdateButtonState(MainActivity.IntendedConnected);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// v3.0 Phase 1.G view: Title / status / Connect / Disconnect button +
    /// VLESS URI Settings panel below. Wrapped in ScrollViewer because on
    /// short screens (e.g. landscape mode) the URI box + buttons run off
    /// the bottom otherwise.
    /// </summary>
    private Control BuildPhase1gView()
    {
        var title = new TextBlock
        {
            Text = "VPNRouter v3.0",
            FontSize = 26,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 32, 0, 0),
        };

        var subtitle = new TextBlock
        {
            Text = "Android Phase 1.G — paste VLESS URI to connect",
            FontSize = 12,
            Opacity = 0.6,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(24, 4, 24, 0),
            TextWrapping = TextWrapping.Wrap,
        };

        _statusBlock = new TextBlock
        {
            Text = "Disconnected",
            FontSize = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 32, 0, 0),
        };

        _toggleButton = new Avalonia.Controls.Button
        {
            Content = "Connect",
            FontSize = 18,
            Padding = new Thickness(48, 16),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        _toggleButton.Click += OnToggleClicked;

        // ── Server settings card ────────────────────────────────────────
        var settingsHeader = new TextBlock
        {
            Text = "Server",
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Opacity = 0.8,
            Margin = new Thickness(24, 32, 24, 8),
        };

        _serverUriBox = new TextBox
        {
            Watermark = "vless://uuid@host:port?...#name",
            FontSize = 13,
            AcceptsReturn = false,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24, 0, 24, 0),
            MinHeight = 80,
        };
        _serverUriBox.Text = AndroidStorage.GetVlessUri() ?? string.Empty;

        _saveServerButton = new Avalonia.Controls.Button
        {
            Content = "Save server",
            FontSize = 14,
            Padding = new Thickness(20, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(24, 8, 24, 0),
        };
        _saveServerButton.Click += OnSaveServerClicked;

        _serverUriStatus = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(_serverUriBox.Text)
                ? "No server configured — placeholder will be used until you paste a vless:// URI."
                : "Server stored. Tap Connect to start the tunnel.",
            FontSize = 11,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24, 8, 24, 0),
        };

        var hint = new TextBlock
        {
            Text = "Tunnel state mirrors the system VPN-key icon in the status bar.",
            FontSize = 11,
            Opacity = 0.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(24, 32, 24, 24),
            TextWrapping = TextWrapping.Wrap,
        };

        var stack = new StackPanel
        {
            Spacing = 0,
            Margin = new Thickness(0),
            Children =
            {
                title, subtitle,
                _statusBlock, _toggleButton,
                settingsHeader, _serverUriBox, _saveServerButton, _serverUriStatus,
                hint
            },
        };

        return new ScrollViewer
        {
            Content = stack,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
    }

    private void OnToggleClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var activity = MainActivity.Instance;
        if (activity is null)
        {
            // Should never happen in practice — the Activity is always
            // alive while the Avalonia view is rendered — but defend
            // against it in case Avalonia's lifecycle ever changes.
            return;
        }

        if (MainActivity.IntendedConnected)
            activity.RequestDisconnect();
        else
            activity.RequestConnect();
    }

    private void OnIntentChanged(bool connected)
    {
        // IntentChanged can fire from any thread (the OS callback that
        // resolves StartActivityForResult lands on the main looper, but
        // direct invokes from Disconnect could be on a worker). Marshal
        // back to the dispatcher to be safe.
        Dispatcher.UIThread.Post(() => UpdateButtonState(connected));
    }

    private void UpdateButtonState(bool connected)
    {
        if (_toggleButton is null || _statusBlock is null) return;

        if (connected)
        {
            _toggleButton.Content = "Disconnect";
            _statusBlock.Text = "Connected";
        }
        else
        {
            _toggleButton.Content = "Connect";
            _statusBlock.Text = "Disconnected";
        }
    }

    /// <summary>
    /// v3.0 Phase 1.G — Save-server handler. Validates the pasted URI
    /// via <see cref="VlessUriParser.Parse"/>, stores via
    /// <see cref="AndroidStorage.SetVlessUri"/>, updates the status
    /// hint with success / failure feedback. Empty input clears the
    /// stored URI (Connect will then fall back to the placeholder).
    /// </summary>
    private void OnSaveServerClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_serverUriBox is null || _serverUriStatus is null) return;

        var raw = (_serverUriBox.Text ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(raw))
        {
            // Clear stored URI — Connect falls back to placeholder.
            AndroidStorage.SetVlessUri(null);
            _serverUriStatus.Text = "Stored server cleared. Connect will use the built-in placeholder.";
            _serverUriStatus.Opacity = 0.65;
            return;
        }

        // Validate before persisting — a syntactically broken URI
        // stored to SharedPreferences would crash the next Connect
        // attempt with a parser exception.
        try
        {
            var parsed = VlessUriParser.Parse(raw);
            if (string.IsNullOrEmpty(parsed.Server) || parsed.Port <= 0)
            {
                _serverUriStatus.Text = "Parsed but missing host or port — please double-check the URI.";
                _serverUriStatus.Opacity = 0.85;
                return;
            }

            var saved = AndroidStorage.SetVlessUri(raw);
            _serverUriStatus.Text = saved
                ? $"Saved. Server: {parsed.Server}:{parsed.Port}. Tap Connect."
                : "Could not write to storage (permissions?). Try again.";
            _serverUriStatus.Opacity = saved ? 0.65 : 0.95;
        }
        catch (System.Exception ex)
        {
            _serverUriStatus.Text = $"Invalid VLESS URI — {ex.Message}";
            _serverUriStatus.Opacity = 0.95;
        }
    }
}
