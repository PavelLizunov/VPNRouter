using System;
using System.Globalization;
using System.Linq;
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

    // v2.44.1-r6: when AutoSelectBestServer builds a urltest "proxy" group, the
    // REAL member it routes through (resolved from clash_api /proxies/proxy ->
    // "now" by the ConnStats poll). Consumed by DeriveConnectedServerLabel +
    // RefreshActiveIndicator so the status line + list highlight show the actual
    // server instead of the stale first-in-list. Null until resolved / non-auto.
    private ServerViewModel? _autoSelectedServer;

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
            _autoSelectedServer = null;
            _autoSelectPollTick = 0;   // R5: next session's first tick polls immediately
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

            // v2.44.1-r6: when AutoSelectBestServer builds a urltest "proxy"
            // group, resolve which member it's actually routing through so the
            // status line + list highlight show the REAL server (not the stale
            // first-in-list). Independent of + before the traffic poll so it
            // runs even on an idle tunnel (which skips the traffic tick below).
            await MaybeRefreshAutoSelectedAsync(api).ConfigureAwait(false);

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

    /// <summary>
    /// v2.44.1-r6: refresh <see cref="_autoSelectedServer"/> from the urltest
    /// "proxy" group's current member (clash_api <c>/proxies/proxy</c> →
    /// <c>"now"</c>) when AutoSelectBestServer is on, then push a status + list
    /// highlight refresh on the UI thread if it changed. Best-effort: a null /
    /// failed query keeps the prior pick (status falls back to a generic label).
    /// </summary>
    /// <summary>R5 / perf-hunt F3 follow-up: poll the group's "now" member only
    /// every 3rd stats tick (~6s at the 2s poll) — the auto-pick doesn't move
    /// faster than urltest's own 3m interval, so per-tick polling was waste.</summary>
    private int _autoSelectPollTick;

    private async Task MaybeRefreshAutoSelectedAsync(ClashSingBoxApi api)
    {
        if (!AutoSelectBestServer || !IsSubscribeMode)
            return;

        // Every 3rd tick only (the FIRST tick fires immediately so the label
        // appears right after connect, not 6 s later).
        if (Interlocked.Increment(ref _autoSelectPollTick) % 3 != 1)
            return;

        var nowTag = await api.GetGroupNowAsync("proxy").ConfigureAwait(false);
        var resolved = ResolveAutoSelectedServer(nowTag);
        if (resolved is null) return; // unresolved → keep the prior pick

        Dispatcher.UIThread.Post(() =>
        {
            if (!IsConnected) return;
            if (ReferenceEquals(_autoSelectedServer, resolved)) return;
            _autoSelectedServer = resolved;
            RestoreConnectedStatus();
            RefreshActiveIndicator();
        });
    }

    /// <summary>
    /// Map a urltest member tag (e.g. <c>"vless-Iceland VLESS ~main-brat"</c>)
    /// back to its subscription row. The member tag is
    /// <c>"&lt;protocol&gt;-&lt;ServerName&gt;"</c>; server names can contain
    /// '-', so the row whose Name is the longest matching suffix wins.
    /// </summary>
    private ServerViewModel? ResolveAutoSelectedServer(string? nowTag)
    {
        // F3 (v2.45.0): single-pass longest-suffix match instead of a per-poll
        // LINQ Where + OrderByDescending allocation/sort over SubscriptionServers.
        var idx = SuffixMatch.LongestSuffixIndex(SubscriptionServers, static s => s.Name, nowTag);
        return idx >= 0 ? SubscriptionServers[idx] : null;
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
