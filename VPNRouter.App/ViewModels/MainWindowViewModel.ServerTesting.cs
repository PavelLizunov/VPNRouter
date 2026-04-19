using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    private const int ServerTestConcurrency = 20;

    /// <summary>Progress text shown in status area during a Test All run.</summary>
    [ObservableProperty] private string _serverTestProgressText = string.Empty;

    /// <summary>True while any TestAll* operation is in flight — disables other test buttons.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServerTestButtonText))]
    private bool _isTestingServers;

    /// <summary>"Test all" vs "Cancel" based on IsTestingServers.</summary>
    public string ServerTestButtonText => IsTestingServers
        ? (IsRussian ? "Отмена" : "Cancel")
        : (IsRussian ? "Проверить все" : "Test all");

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

        await TestServerCollectionAsync(
            Servers.ToList(),
            IsRussian ? "Проверка Manual-серверов" : "Testing Manual servers");
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
            IsRussian ? "Проверка подписочных серверов" : "Testing subscription servers");
    }

    // ── Core batch implementation ────────────────────────────────────────

    private async Task TestServerCollectionAsync(
        IReadOnlyList<ServerViewModel> servers,
        string labelPrefix)
    {
        if (servers.Count == 0)
        {
            ServerTestProgressText = IsRussian ? "Нет серверов" : "No servers";
            return;
        }

        _serverTestCts = new CancellationTokenSource();
        var ct = _serverTestCts.Token;

        IsTestingServers = true;
        ServerTestProgressText = $"{labelPrefix}: 0 / {servers.Count}";

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
                        ServerTestProgressText = $"{labelPrefix}: {n} / {total}";
                    });
                }
            });

            await Task.WhenAll(tasks);

            // Summary
            var ok = servers.Count(s => s.TestStatus is ServerProbeStatus.Ok or ServerProbeStatus.Slow);
            ServerTestProgressText = IsRussian
                ? $"Готово. Работают: {ok} / {total}"
                : $"Done. Working: {ok} / {total}";
        }
        catch (OperationCanceledException)
        {
            ServerTestProgressText = IsRussian ? "Отменено" : "Cancelled";
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
}
