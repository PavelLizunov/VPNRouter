using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Serilog;
using VPNRouter.Core.Services;

namespace VPNRouter.App.ViewModels;

/// <summary>
/// Desktop STATS parity (2026-06-21) — live download/upload rate + active
/// connection count on the connection status, mirroring the Android P1
/// status-card line. Polled from sing-box's clash_api (<see cref="ISingBoxApi.GetConnectionsAsync"/>)
/// on the EXISTING 2 s runtime-status timer while connected — no new timer.
///
/// <para>Unlike Android (where the app's own loopback is captured by its
/// VpnService tun, forcing a Java-side protected socket — see P1), a desktop
/// process reaches the in-process clash_api at 127.0.0.1:&lt;port&gt; directly, so a
/// plain <see cref="ClashSingBoxApi"/> (loopback-guarded, owned HttpClient) works.</para>
///
/// <para>Cumulative totals → rate is the per-poll delta / elapsed; change-only
/// property writes keep the UI quiet when idle. The poll is fire-and-forget with
/// an in-flight guard so a slow clash_api never stacks ticks; failures are
/// non-fatal (the line just stops updating).</para>
/// </summary>
public partial class MainWindowViewModel
{
    private ClashSingBoxApi? _statsApi;
    private long _statsPrevDown, _statsPrevUp;
    private DateTimeOffset? _statsPrevAt;
    private int _statsInFlight;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConnectionStats))]
    private string _connectionStatsText = string.Empty;

    /// <summary>True while a live-stats line is available (drives IsVisible).</summary>
    public bool HasConnectionStats => !string.IsNullOrEmpty(ConnectionStatsText);

    /// <summary>
    /// MVVM-Toolkit hook on the generated <c>IsConnected</c> setter: spin up /
    /// tear down the clash_api stats client on connect / disconnect.
    /// </summary>
    partial void OnIsConnectedChanged(bool value)
    {
        if (value)
        {
            try
            {
                var hostPort = string.IsNullOrWhiteSpace(_settings?.SingBox?.ClashApi)
                    ? "127.0.0.1:9090" : _settings!.SingBox.ClashApi;
                _statsApi?.Dispose();
                _statsApi = new ClashSingBoxApi(baseUrl: $"http://{hostPort}", logger: _logger);
                _statsPrevAt = null; _statsPrevDown = 0; _statsPrevUp = 0;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[ConnStats] clash_api init failed — live stats disabled this session");
                _statsApi = null;
            }
        }
        else
        {
            try { _statsApi?.Dispose(); } catch { /* best-effort */ }
            _statsApi = null;
            _statsPrevAt = null;
            ConnectionStatsText = string.Empty;
        }
    }

    /// <summary>
    /// Called from the 2 s runtime-status timer tick (UI thread). Fire-and-forget,
    /// in-flight-guarded so a slow poll never stacks.
    /// </summary>
    private void MaybePollConnStats()
    {
        if (!IsConnected || _statsApi is null) return;
        if (Interlocked.CompareExchange(ref _statsInFlight, 1, 0) != 0) return;
        _ = PollConnStatsAsync();
    }

    private async Task PollConnStatsAsync()
    {
        try
        {
            var api = _statsApi;
            if (api is null) return;
            var snap = await api.GetConnectionsAsync().ConfigureAwait(false);
            var now = snap.CapturedAt;

            // GetConnectionsAsync returns an all-zero snapshot both on failure
            // (timeout / non-2xx / parse error) and for a brand-new idle tunnel.
            // Neither carries a real rate signal — skip the tick WITHOUT advancing
            // the baseline. Otherwise a single dropped poll zeroes _statsPrev* and
            // the next good poll renders a phantom multi-MB/s spike (delta computed
            // against a zeroed baseline). Most visible during reconnect/hot-reload.
            if (snap.ActiveCount == 0 && snap.TotalDownloadBytes == 0 && snap.TotalUploadBytes == 0)
                return;

            // Counter regression => sing-box restarted / counters reset (e.g. an
            // in-place tunnel reconfigure while IsConnected stays true). Re-baseline
            // silently so we don't emit a bogus spike (old large baseline vs new
            // small counter); the next poll computes a correct delta.
            if (snap.TotalDownloadBytes < _statsPrevDown || snap.TotalUploadBytes < _statsPrevUp)
            {
                _statsPrevDown = snap.TotalDownloadBytes;
                _statsPrevUp = snap.TotalUploadBytes;
                _statsPrevAt = now;
                return;
            }

            string? text = null;
            if (_statsPrevAt is { } prevAt)
            {
                var dt = (now - prevAt).TotalSeconds;
                if (dt > 0.1)
                {
                    var dRate = Math.Max(0, snap.TotalDownloadBytes - _statsPrevDown) / dt;
                    var uRate = Math.Max(0, snap.TotalUploadBytes - _statsPrevUp) / dt;
                    text = $"↓ {HumanRate(dRate)}   ↑ {HumanRate(uRate)}   · {snap.ActiveCount} conn";
                }
            }
            _statsPrevDown = snap.TotalDownloadBytes;
            _statsPrevUp = snap.TotalUploadBytes;
            _statsPrevAt = now;
            if (text is not null)
                Dispatcher.UIThread.Post(() => { if (IsConnected) ConnectionStatsText = text!; });
        }
        catch { /* poll failures non-fatal — line just stops updating */ }
        finally { Interlocked.Exchange(ref _statsInFlight, 0); }
    }

    private static string HumanRate(double bytesPerSec)
    {
        string[] u = { "B", "KB", "MB", "GB" };
        double v = bytesPerSec; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return (i >= 2 ? v.ToString("0.0", CultureInfo.InvariantCulture)
                       : v.ToString("0", CultureInfo.InvariantCulture)) + " " + u[i] + "/s";
    }
}
