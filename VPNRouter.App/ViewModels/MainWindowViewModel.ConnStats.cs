using System.Globalization;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using VPNRouter.Core.Services;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel
{
    private ClashSingBoxApi? _statsApi;
    private long _statsPrevDown, _statsPrevUp;
    private DateTimeOffset? _statsPrevAt;
    private int _statsInFlight;

    private AcceptedTunnelInfo? _statsTunnel;
    private string? _observedMemberTag;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConnectionStats))]
    private string _connectionStatsText = string.Empty;

    public bool HasConnectionStats => !string.IsNullOrEmpty(ConnectionStatsText);

    partial void OnIsConnectedChanged(bool value)
    {
        if (!value)
        {
            _displayExternalTunnel = false;
            CancelCategoryVerification();
        }
        SyncStatsTunnel();
        RefreshActiveIndicator();
        OnConnectedForProblems(value);
    }

    private void SyncStatsTunnel()
    {
        var tunnel = DisplayedTunnel;
        if (ReferenceEquals(tunnel, _statsTunnel)) return;
        var oldApi = _statsApi;
        _statsApi = null;
        _statsTunnel = tunnel;
        try { oldApi?.Dispose(); } catch { }

        _statsPrevAt = null;
        _statsPrevDown = 0;
        _statsPrevUp = 0;
        _observedMemberTag = null;
        _autoSelectPollTick = 0;
        ConnectionStatsText = string.Empty;

        try { _statsApi = tunnel?.OpenController(_logger); }
        catch (Exception ex) { _logger.Debug(ex, "[ConnStats] Could not open the accepted controller"); }
    }

    private void MaybePollConnStats()
    {
        if (!IsConnected || IsApplying || IsConnecting || _statsApi is null) return;

        var window = GetMainWindow();
        if (window is null || !window.IsVisible || window.WindowState == WindowState.Minimized) return;

        if (Interlocked.CompareExchange(ref _statsInFlight, 1, 0) != 0) return;
        _ = PollConnStatsAsync();
    }

    private async Task PollConnStatsAsync()
    {
        var api = _statsApi;
        var tunnel = _statsTunnel;

        try
        {
            if (api == null || tunnel == null || !ReferenceEquals(tunnel, DisplayedTunnel)) return;

            await MaybeRefreshAutoSelectedAsync(api, tunnel).ConfigureAwait(false);

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
            if (tunnel != null) ObserveAutoMember(tunnel, null);
        }
    }

    private int _autoSelectPollTick;

    private async Task MaybeRefreshAutoSelectedAsync(ClashSingBoxApi api, AcceptedTunnelInfo tunnel)
    {
        if (!tunnel.UsesAutoGroup || Interlocked.Increment(ref _autoSelectPollTick) % 3 != 1) return;

        var nowTag = await api.GetGroupNowAsync("proxy").ConfigureAwait(false);
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(api, _statsApi)) ObserveAutoMember(tunnel, nowTag);
        });
    }

    internal void ObserveAutoMember(AcceptedTunnelInfo tunnel, string? tag)
    {
        if (_disposed || IsApplying || IsConnecting || !ReferenceEquals(tunnel, _statsTunnel) ||
            !ReferenceEquals(tunnel, DisplayedTunnel) || _observedMemberTag == tag) return;
        _observedMemberTag = tag;
        RestoreConnectedStatus();
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
