using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;

namespace VPNRouter.Android;

/// <summary>
/// v3.0 Android port — Phase 1.D view.
///
/// <para>Phase 1.D wires a real Connect / Disconnect UI on top of the
/// Phase 1.C libbox runtime. The view is intentionally minimal — title,
/// status text, one big toggle button — so we can validate the bridge
/// from Avalonia button-click → <see cref="MainActivity.RequestConnect"/>
/// → Android <c>VpnService.Prepare</c> consent → libbox tunnel without
/// dragging in the desktop App's full XAML surface yet.</para>
///
/// <para>Status is "intent-level only" in 1.D — the button reflects
/// what we asked the OS to do, not whether the tunnel is currently
/// carrying packets. Real state sync (libbox callbacks → broadcast →
/// UI) is Phase 1.E. The system-level VPN-key icon in the status bar is
/// the authoritative tunnel-up signal for now.</para>
///
/// <para>Phase 1.F+ will start linking shared XAML from
/// <c>VPNRouter.App</c> so we get the same theme + design tokens
/// across desktop and Android. Until then this view is local to the
/// Android project.</para>
/// </summary>
public partial class AndroidApp : Avalonia.Application
{
    private TextBlock? _statusBlock;
    private Avalonia.Controls.Button? _toggleButton;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = BuildPhase1dView();
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

    private Control BuildPhase1dView()
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
            Text = "Android Phase 1.D — smoke-test (TUN + direct outbound)",
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
            Margin = new Thickness(0, 48, 0, 0),
        };

        _toggleButton = new Avalonia.Controls.Button
        {
            Content = "Connect",
            FontSize = 18,
            Padding = new Thickness(48, 16),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 24, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        _toggleButton.Click += OnToggleClicked;

        var hint = new TextBlock
        {
            Text = "Connection status mirrors the system VPN-key icon in the status bar.",
            FontSize = 11,
            Opacity = 0.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(24, 32, 24, 24),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Bottom,
        };

        return new StackPanel
        {
            Spacing = 0,
            Margin = new Thickness(0),
            Children = { title, subtitle, _statusBlock, _toggleButton, hint },
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
}
