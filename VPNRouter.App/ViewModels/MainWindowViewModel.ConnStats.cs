using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
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
/// non-fatal (cleared failure).</para>
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
        var oldApi = _statsApi;
        _statsApi = null;
        try { oldApi?.Dispose(); } catch { /* best-effort */ }

        _statsPrevAt = null;
        _statsPrevDown = 0;
        _statsPrevUp = 0;
        _autoSelectedServer = null;
        _autoSelectPollTick = 0;
        ConnectionStatsText = string.Empty;

        if (value)
        {
            try
            {
                var hostPort = string.IsNullOrWhiteSpace(_settings?.SingBox?.ClashApi)
                    ? "127.0.0.1:9090" : _settings!.SingBox.ClashApi;
                _statsApi = new ClashSingBoxApi(baseUrl: $"http://{hostPort}", logger: _logger,
                    secret: _settings?.SingBox?.ClashApiSecret);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[ConnStats] clash_api init failed — live stats disabled this session");
                _statsApi = null;
            }
        }
    }

    /// <summary>
    /// Called from the 2 s runtime-status timer tick (UI thread). Fire-and-forget,
    /// in-flight-guarded so a slow poll never stacks.
    /// </summary>
    private void MaybePollConnStats()
    {
        if (!IsConnected || _statsApi is null) return;

        // OPEN-DEFECTS.md:108 (perf-hunt F2): skip the /connections poll while the
        // window is hidden/minimized (or null) — the stats line is off-screen.
        var window = GetMainWindow();
        if (window is null || !window.IsVisible || window.WindowState == WindowState.Minimized) return;

        if (Interlocked.CompareExchange(ref _statsInFlight, 1, 0) != 0) return;
        _ = PollConnStatsAsync();
    }

    private async Task PollConnStatsAsync()
    {
        var api = _statsApi;

        try
        {
            if (api is null || !IsConnected) return;

            // v2.44.1-r6: when AutoSelectBestServer builds a urltest "proxy"
            // group, resolve which member it's actually routing through so the
            // status line + list highlight show the REAL server (not the stale
            // first-in-list). Independent of + before the traffic poll so it
            // runs even on an idle tunnel (which skips the traffic tick below).
            await MaybeRefreshAutoSelectedAsync(api).ConfigureAwait(false);

            var snap = await api.GetConnectionsAsync().ConfigureAwait(false);
            var now = snap.CapturedAt;

            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(api, _statsApi) || !IsConnected)
                    return;

                if (!snap.IsValid)
                {
                    ClearStatsState();
                    return;
                }

                // Counter regression => sing-box restarted / counters reset (e.g. an
                // in-place tunnel reconfigure while IsConnected stays true). Re-baseline
                // and clear text instead of keeping stale rate text.
                if (_statsPrevAt is not null && (snap.TotalDownloadBytes < _statsPrevDown || snap.TotalUploadBytes < _statsPrevUp))
                {
                    _statsPrevDown = snap.TotalDownloadBytes;
                    _statsPrevUp = snap.TotalUploadBytes;
                    _statsPrevAt = now;
                    ConnectionStatsText = string.Empty;
                    return;
                }

                if (_statsPrevAt is { } prevAt)
                {
                    var dt = (now - prevAt).TotalSeconds;
                    if (dt > 0.1)
                    {
                        var dRate = Math.Max(0, snap.TotalDownloadBytes - _statsPrevDown) / dt;
                        var uRate = Math.Max(0, snap.TotalUploadBytes - _statsPrevUp) / dt;
                        ConnectionStatsText = $"↓ {HumanRate(dRate)}   ↑ {HumanRate(uRate)}   · {snap.ActiveCount} conn";
                    }
                }
                else
                {
                    // Next good sample after failure/connect: baseline only, no spike.
                    ConnectionStatsText = string.Empty;
                }

                _statsPrevDown = snap.TotalDownloadBytes;
                _statsPrevUp = snap.TotalUploadBytes;
                _statsPrevAt = now;
            });
        }
        catch
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(api, _statsApi) || !IsConnected)
                    return;

                ClearStatsState();
            });
        }
        finally
        {
            Interlocked.Exchange(ref _statsInFlight, 0);
        }

        void ClearStatsState()
        {
            ConnectionStatsText = string.Empty;
            _statsPrevAt = null;
            _statsPrevDown = 0;
            _statsPrevUp = 0;
            if (_autoSelectedServer is not null)
            {
                _autoSelectedServer = null;
                RestoreConnectedStatus();
                RefreshActiveIndicator();
            }
        }
    }

    /// <summary>
    /// v2.44.1-r6: refresh <see cref="_autoSelectedServer"/> from the urltest
    /// "proxy" group's current member (clash_api <c>/proxies/proxy</c> →
    /// <c>"now"</c>) when AutoSelectBestServer is on, then push a status + list
    /// highlight refresh on the UI thread if it changed. Best-effort: a null /
    /// failed query clears the prior pick (status falls back to a generic label).
    /// </summary>
    /// <summary>R5 / perf-hunt F3 follow-up: poll the group's "now" member only
    /// every 3rd stats tick (~6s at the 2s poll) — the auto-pick doesn't move
    /// faster than urltest's own 3m interval, so per-tick polling was waste.</summary>
    private int _autoSelectPollTick;

    private async Task MaybeRefreshAutoSelectedAsync(ClashSingBoxApi api)
    {
        if (!AutoSelectBestServer || !(_settings.App.ConfigMode ?? "generated")
                .Equals("subscribe", StringComparison.OrdinalIgnoreCase))
            return;

        // Every 3rd tick only (the FIRST tick fires immediately so the label
        // appears right after connect, not 6 s later).
        if (Interlocked.Increment(ref _autoSelectPollTick) % 3 != 1)
            return;

        string? nowTag = null;
        try
        {
            nowTag = await api.GetGroupNowAsync("proxy").ConfigureAwait(false);
        }
        catch
        {
            nowTag = null;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(api, _statsApi) || !IsConnected)
                return;

            var resolved = ResolveAutoSelectedServer(nowTag);
            if (!ReferenceEquals(_autoSelectedServer, resolved))
            {
                _autoSelectedServer = resolved;
                RestoreConnectedStatus();
                RefreshActiveIndicator();
            }
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
        if (SubscriptionServers is null || string.IsNullOrEmpty(nowTag)) return null;
        var idx = SuffixMatch.LongestSuffixIndex(SubscriptionServers, static s => s.Name, nowTag);
        return idx >= 0 && idx < SubscriptionServers.Count ? SubscriptionServers[idx] : null;
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
