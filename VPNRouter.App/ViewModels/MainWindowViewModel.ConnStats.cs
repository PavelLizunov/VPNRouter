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

public partial class MainWindowViewModel
{
    private ClashSingBoxApi? _statsApi;
    private long _statsPrevDown, _statsPrevUp;
    private DateTimeOffset? _statsPrevAt;
    private int _statsInFlight;

    private ServerViewModel? _autoSelectedServer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConnectionStats))]
    private string _connectionStatsText = string.Empty;

    public bool HasConnectionStats => !string.IsNullOrEmpty(ConnectionStatsText);

    partial void OnIsConnectedChanged(bool value)
    {
        var oldApi = _statsApi;
        _statsApi = null;
        try { oldApi?.Dispose(); } catch {  }

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

    private void MaybePollConnStats()
    {
        if (!IsConnected || _statsApi is null) return;

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

    private int _autoSelectPollTick;

    private async Task MaybeRefreshAutoSelectedAsync(ClashSingBoxApi api)
    {
        if (!AutoSelectBestServer || !(_settings.App.ConfigMode ?? "generated")
                .Equals("subscribe", StringComparison.OrdinalIgnoreCase))
            return;

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
