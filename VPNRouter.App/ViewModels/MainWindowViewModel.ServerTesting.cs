using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.App.ViewModels;

/// <summary>
/// Server/subscription connectivity testing (v2.15.2).
/// Reuses <see cref="TcpTlsProbe"/> — the same TCP+TLS logic Free Configs
/// uses — so the user can vet their own saved servers without going to the
/// Free tab. Concurrency capped at 20 (v. 80 for Free) since typical Servers
/// lists are small and we don't want to hammer their own remotes.
/// </summary>
public partial class MainWindowViewModel
{
    private CancellationTokenSource? _serverTestCts;
    private CancellationTokenSource? _serverDeepCts;
    private VlessDeepVerifier? _deepVerifier;

    private const int ServerTestConcurrency = 20;
    private const int ServerDeepConcurrency = 5;

    /// <summary>Progress text shown in status area during a Test All run
    /// for the Manual VLESS list (Servers tab). v2.31.6-r15: split out
    /// from the shared field used by Subscribe — the iter#6 audit caught
    /// status text leaking from one tab to the other.</summary>
    [ObservableProperty] private string _serverTestProgressText = string.Empty;

    /// <summary>v2.31.6-r15: Subscribe-tab Test all progress, isolated
    /// from <see cref="ServerTestProgressText"/>. Pre-r15 both batches
    /// wrote to the same field, so a Test all on Servers left "Готово.
    /// Пинг прошёл: N/M" on the Subscribe tab status row even though
    /// Subscribe servers had never been tested. Computer-use audit
    /// 2026-05-04 confirmed the leak.</summary>
    [ObservableProperty] private string _subscriptionTestProgressText = string.Empty;

    /// <summary>True while any TestAll* operation is in flight — disables other test buttons.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServerTestButtonText))]
    private bool _isTestingServers;

    /// <summary>"Test all" vs "Cancel" based on IsTestingServers.</summary>
    public string ServerTestButtonText => IsTestingServers
        ? (IsRussian ? "Отмена" : "Cancel")
        : (IsRussian ? "Проверить все" : "Test all");

    /// <summary>Progress text for deep verify passes (Manual VLESS).</summary>
    [ObservableProperty] private string _serverDeepProgressText = string.Empty;

    /// <summary>v2.31.6-r15: Subscribe-tab deep-verify progress, isolated
    /// from <see cref="ServerDeepProgressText"/>.</summary>
    [ObservableProperty] private string _subscriptionDeepProgressText = string.Empty;

    /// <summary>v2.31.6-r15 (iter#6): Manual-tab active-VPN warning.
    /// Empty when fewer than 50% of tested servers came back Implausible
    /// after a Test all run. Pre-r15 the warning was concatenated as a
    /// suffix to the status text, but the status line is in a non-wrap
    /// horizontal stack panel — the suffix got clipped on narrow windows
    /// (computer-use confirmed the warning was invisible at 536-px page
    /// width). Separate property surfaces it as a wrap-able banner
    /// rendered below the action buttons.</summary>
    [ObservableProperty] private string _serverTestImplausibleWarning = string.Empty;

    /// <summary>v2.31.6-r15: Subscribe-tab equivalent.</summary>
    [ObservableProperty] private string _subscriptionTestImplausibleWarning = string.Empty;

    /// <summary>True while a deep-verify batch is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServerDeepButtonText))]
    private bool _isDeepTestingServers;

    /// <summary>"Deep verify" vs "Stop" text for the button.</summary>
    /// v2.30.5-r1 (UX-24 fix): localize button label in Russian.
    public string ServerDeepButtonText => IsDeepTestingServers
        ? (IsRussian ? "Стоп" : "Stop")
        : (IsRussian ? "Глубокая проверка" : "Deep verify");

    // ── Single-server test (invoked per row) ──────────────────────────────

    [RelayCommand]
    private async Task TestServerAsync(ServerViewModel? server)
    {
        if (server == null) return;
        if (server.IsTesting) return;

        server.IsTesting = true;
        try
        {
            var sni = PickSni(server);
            var result = await TcpTlsProbe.ProbeAsync(
                server.Server, server.Port, sni,
                requireTls: true);
            server.ApplyProbeResult(result);
        }
        catch (Exception ex)
        {
            server.ApplyProbeResult(new ServerProbeResult(
                ServerProbeStatus.Timeout, 0, ex.GetType().Name));
        }
        finally
        {
            server.IsTesting = false;
        }
    }

    // ── Batch test: all manual VLESS servers ─────────────────────────────

    [RelayCommand]
    private async Task TestAllServersAsync()
    {
        if (IsTestingServers)
        {
            _serverTestCts?.Cancel();
            return;
        }

        // v2.31.6-r15: per-tab progress + warning surfaces.
        await TestServerCollectionAsync(
            Servers.ToList(),
            IsRussian ? "Проверка Manual-серверов" : "Testing Manual servers",
            setProgress: text => ServerTestProgressText = text,
            setWarning: text => ServerTestImplausibleWarning = text);
    }

    // ── Batch test: all aggregated subscription servers ──────────────────

    [RelayCommand]
    private async Task TestAllSubscriptionServersAsync()
    {
        if (IsTestingServers)
        {
            _serverTestCts?.Cancel();
            return;
        }

        await TestServerCollectionAsync(
            SubscriptionServers.ToList(),
            IsRussian ? "Проверка подписочных серверов" : "Testing subscription servers",
            setProgress: text => SubscriptionTestProgressText = text,
            setWarning: text => SubscriptionTestImplausibleWarning = text);
    }

    // ── Core batch implementation ────────────────────────────────────────

    private async Task TestServerCollectionAsync(
        IReadOnlyList<ServerViewModel> servers,
        string labelPrefix,
        Action<string> setProgress,
        Action<string> setWarning)
    {
        // Reset warning at start of every run.
        setWarning(string.Empty);
        if (servers.Count == 0)
        {
            setProgress(IsRussian ? "Нет серверов" : "No servers");
            return;
        }

        _serverTestCts = new CancellationTokenSource();
        var ct = _serverTestCts.Token;

        IsTestingServers = true;
        setProgress($"{labelPrefix}: 0 / {servers.Count}");

        // Mark every row as testing so spinners show immediately
        foreach (var s in servers) s.IsTesting = true;

        var sem = new SemaphoreSlim(ServerTestConcurrency);
        var done = 0;
        var total = servers.Count;

        try
        {
            var tasks = servers.Select(async server =>
            {
                try
                {
                    await sem.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    server.IsTesting = false;
                    return;
                }

                try
                {
                    if (ct.IsCancellationRequested)
                    {
                        server.IsTesting = false;
                        return;
                    }

                    var sni = PickSni(server);
                    var result = await TcpTlsProbe.ProbeAsync(
                        server.Server, server.Port, sni,
                        requireTls: true, ct: ct);

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        server.ApplyProbeResult(result);
                    });
                }
                catch (OperationCanceledException)
                {
                    server.IsTesting = false;
                }
                catch (Exception ex)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        server.ApplyProbeResult(new ServerProbeResult(
                            ServerProbeStatus.Timeout, 0, ex.GetType().Name));
                    });
                }
                finally
                {
                    sem.Release();
                    var n = Interlocked.Increment(ref done);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        setProgress($"{labelPrefix}: {n} / {total}");
                    });
                }
            });

            await Task.WhenAll(tasks);

            // v2.30.7-r2 — AU-5 fix: previous copy "Working: 0/N" was
            // misleading after Test-all. The "ok" count excluded
            // Implausible servers (<5ms RTT — suspicious but the TCP+TLS
            // handshake DID succeed), so users with subscription pools
            // that resolve to local Reality fronts saw "Working: 0/7"
            // even when all 7 servers responded. Now count anything that
            // completed the handshake (Ok+Slow+Implausible) as "passed
            // ping", and clarify that the Deep verify is needed for full
            // validation. Failed buckets (TlsFailed/Timeout/Unreachable)
            // still NOT counted.
            var responded = servers.Count(s => s.TestStatus is
                ServerProbeStatus.Ok or
                ServerProbeStatus.Slow or
                ServerProbeStatus.Implausible);

            setProgress(IsRussian
                ? $"Готово. Пинг прошёл: {responded} / {total} · полная проверка — «Глубокая проверка»"
                : $"Done. Pinged: {responded} / {total} · full check via Deep verify");

            // v2.31.6-r15 (iter#6): surface the active-VPN-intercept
            // warning when Implausible dominates the results. Iter#6
            // computer-use audit confirmed brat's pool of 7 servers
            // returned 7/7 Implausible while showing as "passed ping",
            // which the user reasonably interpreted as "all 7 working"
            // but was actually "all 7 traffic was intercepted by the
            // active VPN's TUN before reaching the real servers".
            //
            // v2.31.6-r15 follow-up after computer-use: the warning was
            // initially concatenated as a suffix to the status text but
            // got clipped on narrow windows because the status row uses
            // a non-wrap horizontal StackPanel. Now surfaced via a
            // separate ImplausibleWarning property bound to a
            // wrap-able TextBlock.
            var implausible = servers.Count(s => s.TestStatus is ServerProbeStatus.Implausible);
            if (implausible > 0 && implausible >= total / 2)
            {
                setWarning(IsRussian
                    ? "⚠ Активный VPN или прокси перехватывает соединения — реальные пинги недоступны. Отключите VPN для честных результатов или нажмите «Глубокая проверка» (она запускает sing-box отдельно и не зависит от текущего туннеля)."
                    : "⚠ Active VPN or proxy intercepting connections — real pings unavailable. Disconnect for true results or click Deep verify (it spawns sing-box independently and bypasses the current tunnel).");
            }
        }
        catch (OperationCanceledException)
        {
            setProgress(IsRussian ? "Отменено" : "Cancelled");
            foreach (var s in servers) s.IsTesting = false;
        }
        finally
        {
            IsTestingServers = false;
            _serverTestCts?.Dispose();
            _serverTestCts = null;
        }
    }

    /// <summary>Pick the right SNI for TLS validation based on the server's security mode.</summary>
    private static string? PickSni(ServerViewModel server)
    {
        // Reality: cert is presented for the masked SNI (e.g. yahoo.com) — use ServerName.
        // TLS: cert is for the actual server hostname — also use ServerName (often = Server).
        // Fall back to Server if ServerName is empty.
        return !string.IsNullOrWhiteSpace(server.ServerName) ? server.ServerName : server.Server;
    }

    // ── Deep verify (sing-box spawn + HTTP probe + bandwidth) ─────────────

    [RelayCommand]
    private async Task DeepVerifyAllServersAsync()
    {
        if (IsDeepTestingServers)
        {
            _serverDeepCts?.Cancel();
            return;
        }

        // v2.31.6-r15: Manual / Subscribe progress isolated (iter#6).
        await DeepVerifyCollectionAsync(
            Servers.ToList(),
            IsRussian ? "Deep verify Manual" : "Deep verify Manual",
            setProgress: text => ServerDeepProgressText = text);
    }

    [RelayCommand]
    private async Task DeepVerifyAllSubscriptionServersAsync()
    {
        if (IsDeepTestingServers)
        {
            _serverDeepCts?.Cancel();
            return;
        }

        await DeepVerifyCollectionAsync(
            SubscriptionServers.ToList(),
            IsRussian ? "Deep verify подписки" : "Deep verify subscription",
            setProgress: text => SubscriptionDeepProgressText = text);
    }

    private async Task DeepVerifyCollectionAsync(
        IReadOnlyList<ServerViewModel> servers,
        string labelPrefix,
        Action<string> setProgress)
    {
        if (servers.Count == 0)
        {
            setProgress(IsRussian ? "Нет серверов" : "No servers");
            return;
        }

        _deepVerifier ??= new VlessDeepVerifier(_logger);

        if (!_deepVerifier.IsAvailable)
        {
            setProgress(IsRussian
                ? "sing-box не найден"
                : "sing-box binary missing");
            return;
        }

        _serverDeepCts = new CancellationTokenSource();
        var ct = _serverDeepCts.Token;

        IsDeepTestingServers = true;
        setProgress($"{labelPrefix}: 0 / {servers.Count}");

        // Map VM → entry; remember the mapping so we can push results back.
        var entryToVm = new Dictionary<VlessServerEntry, ServerViewModel>();
        var entries = new List<VlessServerEntry>(servers.Count);
        foreach (var vm in servers)
        {
            vm.IsDeepTesting = true;
            var entry = vm.ToEntry();
            entries.Add(entry);
            entryToVm[entry] = vm;
        }

        var done = 0;
        var total = entries.Count;

        try
        {
            await _deepVerifier.VerifyBatchAsync(
                entries,
                onOneDone: (entry, result) =>
                {
                    if (entryToVm.TryGetValue(entry, out var vm))
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            vm.ApplyDeepResult(result);
                        });
                    }
                },
                measureBandwidth: true,
                progress: new Progress<(int done, int total)>(p =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        setProgress($"{labelPrefix}: {p.done} / {p.total}");
                    });
                }),
                ct: ct);

            var verified = servers.Count(s => s.IsDeepVerified);
            setProgress(IsRussian
                ? $"Готово. Verified: {verified} / {total}"
                : $"Done. Verified: {verified} / {total}");
        }
        catch (OperationCanceledException)
        {
            setProgress(IsRussian ? "Отменено" : "Cancelled");
            foreach (var vm in servers)
            {
                if (vm.IsDeepTesting) vm.IsDeepTesting = false;
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[DeepVerifyAll] failed");
            setProgress($"Error: {ex.GetType().Name}");
        }
        finally
        {
            IsDeepTestingServers = false;
            _serverDeepCts?.Dispose();
            _serverDeepCts = null;

            // Safety: clear any lingering IsDeepTesting flags
            foreach (var vm in servers)
            {
                if (vm.IsDeepTesting) vm.IsDeepTesting = false;
            }
        }
    }
}
