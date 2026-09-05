#nullable enable
using System;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Serilog;
using VPNRouter.App.ViewModels;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// NIGHT-11 behavioral session tests for ConnStats polling and auto-selected node resolution:
/// - Distinguish API failure from valid zero metrics.
/// - Clear stale rate on valid zero or failure; establish fresh baseline.
/// - Prevent phantom spikes on recovery after failure (baseline only, then normal delta).
/// - Counter regression resets baseline and clears text instead of retaining stale rate.
/// - Generation/API guards drop deferred/stale HTTP responses across disconnect/reconnect.
/// - Unresolved urltest group clears previous selection instead of retaining it.
///
/// <para>Harness pattern: uses <see cref="RuntimeHelpers.GetUninitializedObject"/> + reflection
/// backing fields to avoid invoking MainWindowViewModel's constructor (which triggers disk paths,
/// bundled profile deployment, native VpnEngine creation, and background timers).</para>
/// </summary>
public sealed class NightConnStatsSessionTests
{
    private static MainWindowViewModel CreateIsolatedVm(ClashSingBoxApi? api = null)
    {
        var vm = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));

        var settings = new AppSettings
        {
            App = new AppConfig
            {
                ConfigMode = "subscribe",
                ActiveSubscriptionServer = string.Empty,
                RoutingMode = "split"
            },
            SingBox = new SingBoxSettings { ClashApi = "127.0.0.1:9090" },
            Vless = new VlessConfig()
        };

        var engine = (VpnEngine)RuntimeHelpers.GetUninitializedObject(typeof(VpnEngine));
        SetField(engine, "ActiveServerAddress", string.Empty);

        SetField(vm, "_settings", settings);
        SetField(vm, "_engine", engine);
        SetField(vm, "_logger", Log.Logger);
        SetField(vm, "_statsApi", api);
        SetField(vm, "_isConnected", true);
        SetField(vm, "_connectionStatsText", string.Empty);
        SetField(vm, "_statusText", string.Empty);
        SetField(vm, "SubscriptionServers", new ObservableCollection<ServerViewModel>());
        SetField(vm, "Servers", new ObservableCollection<ServerViewModel>());
        SetField(vm, "CustomConfigs", new ObservableCollection<CustomConfigViewModel>());
        SetField(vm, "Subscriptions", new ObservableCollection<SubscriptionViewModel>());

        return vm;
    }

    private static void SetField(object target, string name, object? value)
    {
        var type = target.GetType();
        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? type.GetField($"<{name}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (field is null)
            throw new InvalidOperationException($"Field '{name}' not found on {type.FullName}.");
        field.SetValue(target, value);
    }

    private static T? GetField<T>(object target, string name)
    {
        var type = target.GetType();
        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? type.GetField($"<{name}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (field is null)
            throw new InvalidOperationException($"Field '{name}' not found on {type.FullName}.");
        return (T?)field.GetValue(target);
    }

    private static async Task DrainUiQueueAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { /* drain queued UI posts */ }, DispatcherPriority.Background);
    }

    private static async Task InvokePollAsync(MainWindowViewModel vm)
    {
        var pollMethod = typeof(MainWindowViewModel).GetMethod(
            "PollConnStatsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(pollMethod);
        await (Task)pollMethod!.Invoke(vm, null)!;
        await DrainUiQueueAsync();
    }

    private sealed class FakeClashHttpHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? Responder { get; set; }
        public TaskCompletionSource<HttpResponseMessage>? DeferredResponse { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (DeferredResponse is not null)
            {
                var tcs = DeferredResponse;
                using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
                return await tcs.Task.ConfigureAwait(false);
            }

            if (Responder is not null)
            {
                return Responder(request);
            }

            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/connections", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"downloadTotal\": 1000, \"uploadTotal\": 500, \"connections\": [{}]}",
                        System.Text.Encoding.UTF8,
                        "application/json")
                };
            }

            if (path.Contains("/proxies/"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"now\": \"vless-ServerA\"}",
                        System.Text.Encoding.UTF8,
                        "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    [AvaloniaFact]
    public async Task ValidNonZero_Then_ValidZero_ClearsStaleRateAndEstablishesFreshBaseline()
    {
        var handler = new FakeClashHttpHandler();
        using var http = new HttpClient(handler);
        using var api = new ClashSingBoxApi(httpClient: http, baseUrl: "http://127.0.0.1:9090");
        var vm = CreateIsolatedVm(api);

        // Step 1: All-zero totals valid zero snapshot (0 conn, 0 bytes) establishes initial baseline
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"downloadTotal\": 0, \"uploadTotal\": 0, \"connections\": []}")
        };
        await InvokePollAsync(vm);

        Assert.Equal(0L, GetField<long>(vm, "_statsPrevDown"));
        Assert.Equal(0L, GetField<long>(vm, "_statsPrevUp"));
        Assert.NotNull(GetField<DateTimeOffset?>(vm, "_statsPrevAt"));
        Assert.Equal(string.Empty, vm.ConnectionStatsText);

        // Step 2: Second all-zero totals snapshot with 2s baseline computes zero rate and displays 0 conn
        SetField(vm, "_statsPrevAt", DateTimeOffset.UtcNow.AddSeconds(-2));
        await InvokePollAsync(vm);

        Assert.Equal(0L, GetField<long>(vm, "_statsPrevDown"));
        Assert.Equal(0L, GetField<long>(vm, "_statsPrevUp"));
        Assert.NotNull(GetField<DateTimeOffset?>(vm, "_statsPrevAt"));
        Assert.Contains("0 conn", vm.ConnectionStatsText);
        Assert.Contains("0 B/s", vm.ConnectionStatsText);

        // Step 3: Traffic delta sample (1 connection, 20,000 bytes down, 10,000 bytes up) computes active rate
        SetField(vm, "_statsPrevAt", DateTimeOffset.UtcNow.AddSeconds(-2));
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"downloadTotal\": 20000, \"uploadTotal\": 10000, \"connections\": [{}]}")
        };
        await InvokePollAsync(vm);

        Assert.False(string.IsNullOrEmpty(vm.ConnectionStatsText));
        Assert.Contains("conn", vm.ConnectionStatsText);
        Assert.DoesNotContain("0 conn", vm.ConnectionStatsText);

        // Step 4: Valid zero snapshot (0 active connections, unchanged totals 20,000 / 10,000)
        SetField(vm, "_statsPrevAt", DateTimeOffset.UtcNow.AddSeconds(-2));
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"downloadTotal\": 20000, \"uploadTotal\": 10000, \"connections\": []}")
        };
        await InvokePollAsync(vm);

        // Stale rate is replaced/cleared and displays 0 conn with 0 B/s rate
        Assert.Contains("0 conn", vm.ConnectionStatsText);
        Assert.DoesNotContain("1 conn", vm.ConnectionStatsText);

        // Step 5: Counter regression to all-zero totals (sing-box restart with zero traffic) resets baseline and clears text
        SetField(vm, "_statsPrevAt", DateTimeOffset.UtcNow.AddSeconds(-2));
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"downloadTotal\": 0, \"uploadTotal\": 0, \"connections\": []}")
        };
        await InvokePollAsync(vm);

        Assert.Equal(string.Empty, vm.ConnectionStatsText);
        Assert.Equal(0L, GetField<long>(vm, "_statsPrevDown"));
        Assert.Equal(0L, GetField<long>(vm, "_statsPrevUp"));
    }

    [AvaloniaFact]
    public async Task FailureClear_Then_RecoveryBaseline_Then_NormalDelta_NoSpike()
    {
        var handler = new FakeClashHttpHandler();
        using var http = new HttpClient(handler);
        using var api = new ClashSingBoxApi(httpClient: http, baseUrl: "http://127.0.0.1:9090");
        var vm = CreateIsolatedVm(api);

        // Step 1: Baseline sample
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"downloadTotal\": 1000, \"uploadTotal\": 1000, \"connections\": [{}]}")
        };
        await InvokePollAsync(vm);

        // Step 2: Active rate text with 2s baseline
        SetField(vm, "_statsPrevAt", DateTimeOffset.UtcNow.AddSeconds(-2));
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"downloadTotal\": 2000, \"uploadTotal\": 2000, \"connections\": [{}]}")
        };
        await InvokePollAsync(vm);

        Assert.False(string.IsNullOrEmpty(vm.ConnectionStatsText));

        // Step 3: API failure (HTTP 500 => failureSnapshot with IsValid=false)
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        await InvokePollAsync(vm);

        // Failure must clear text and reset baseline
        Assert.True(string.IsNullOrEmpty(vm.ConnectionStatsText));
        Assert.Null(GetField<DateTimeOffset?>(vm, "_statsPrevAt"));
        Assert.Equal(0L, GetField<long>(vm, "_statsPrevDown"));
        Assert.Equal(0L, GetField<long>(vm, "_statsPrevUp"));

        // Step 4: Recovery sample (first good sample after failure with large counter)
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"downloadTotal\": 100000000, \"uploadTotal\": 100000000, \"connections\": [{}]}")
        };
        await InvokePollAsync(vm);

        // Recovery sample establishes baseline only: NO phantom spike! Text remains empty
        Assert.True(string.IsNullOrEmpty(vm.ConnectionStatsText),
            $"Expected no spike on first recovery sample, got '{vm.ConnectionStatsText}'");
        Assert.NotNull(GetField<DateTimeOffset?>(vm, "_statsPrevAt"));
        Assert.Equal(100000000L, GetField<long>(vm, "_statsPrevDown"));
        Assert.Equal(100000000L, GetField<long>(vm, "_statsPrevUp"));

        // Step 5: Subsequent good sample computes normal delta with 2s baseline
        SetField(vm, "_statsPrevAt", DateTimeOffset.UtcNow.AddSeconds(-2));
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"downloadTotal\": 100002000, \"uploadTotal\": 100001000, \"connections\": [{}]}")
        };
        await InvokePollAsync(vm);

        Assert.False(string.IsNullOrEmpty(vm.ConnectionStatsText));
        Assert.Contains("conn", vm.ConnectionStatsText);
        Assert.DoesNotContain("GB/s", vm.ConnectionStatsText);
        Assert.DoesNotContain("100.0 MB/s", vm.ConnectionStatsText);
    }

    [AvaloniaFact]
    public async Task CounterRegression_ResetsBaselineAndClearsText()
    {
        var handler = new FakeClashHttpHandler();
        using var http = new HttpClient(handler);
        using var api = new ClashSingBoxApi(httpClient: http, baseUrl: "http://127.0.0.1:9090");
        var vm = CreateIsolatedVm(api);

        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"downloadTotal\": 50000, \"uploadTotal\": 50000, \"connections\": [{}]}")
        };
        await InvokePollAsync(vm);

        SetField(vm, "_statsPrevAt", DateTimeOffset.UtcNow.AddSeconds(-2));
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"downloadTotal\": 60000, \"uploadTotal\": 60000, \"connections\": [{}]}")
        };
        await InvokePollAsync(vm);
        Assert.False(string.IsNullOrEmpty(vm.ConnectionStatsText));

        // Counter regression: sing-box restarted / reconfigured, counters dropped to 1000
        SetField(vm, "_statsPrevAt", DateTimeOffset.UtcNow.AddSeconds(-2));
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"downloadTotal\": 1000, \"uploadTotal\": 1000, \"connections\": [{}]}")
        };
        await InvokePollAsync(vm);

        // Must clear text and reset baseline to new counter
        Assert.True(string.IsNullOrEmpty(vm.ConnectionStatsText),
            $"Expected text cleared on counter regression, got '{vm.ConnectionStatsText}'");
        Assert.Equal(1000L, GetField<long>(vm, "_statsPrevDown"));
        Assert.Equal(1000L, GetField<long>(vm, "_statsPrevUp"));
    }

    [AvaloniaFact]
    public async Task StaleQueuedUpdates_ApiMismatch_DiscardsStaleResponse()
    {
        var deferred = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler1 = new FakeClashHttpHandler { DeferredResponse = deferred };
        using var http1 = new HttpClient(handler1);
        using var api1 = new ClashSingBoxApi(httpClient: http1, baseUrl: "http://127.0.0.1:9090");

        var vm = CreateIsolatedVm(api1);
        SetField(vm, "_isConnected", true);
        SetField(vm, "_statsPrevDown", 888888L);
        SetField(vm, "_connectionStatsText", "current-api-text");

        var pollMethod = typeof(MainWindowViewModel).GetMethod(
            "PollConnStatsAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(pollMethod);
        var pollTask = (Task)pollMethod!.Invoke(vm, null)!;

        var handler2 = new FakeClashHttpHandler();
        using var http2 = new HttpClient(handler2);
        using var api2 = new ClashSingBoxApi(httpClient: http2, baseUrl: "http://127.0.0.1:9090");

        try
        {
            // Swap _statsApi to api2 (new session client instance)
            SetField(vm, "_statsApi", api2);

            // Release deferred HTTP response from the old api1 poll
            deferred.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"downloadTotal\": 12345, \"uploadTotal\": 67890, \"connections\": [{}, {}]}")
            });

            await pollTask;
            await DrainUiQueueAsync();

            // Stale update from API mismatch must be discarded
            Assert.Equal(888888L, GetField<long>(vm, "_statsPrevDown"));
            Assert.Equal("current-api-text", vm.ConnectionStatsText);
            Assert.Same(api2, GetField<ClashSingBoxApi>(vm, "_statsApi"));
        }
        finally
        {
            deferred.TrySetCanceled();
            try
            {
                await pollTask;
            }
            catch
            {
            }
            await DrainUiQueueAsync();
        }
    }

    [AvaloniaFact]
    public async Task OnIsConnectedChanged_FalseTrueTransition_ClearsClient_GeneratesUniqueHandle_AndDropsOldReply()
    {
        var deferred = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeClashHttpHandler { DeferredResponse = deferred };
        using var http = new HttpClient(handler);
        using var initialApi = new ClashSingBoxApi(httpClient: http, baseUrl: "http://127.0.0.1:9090");

        var vm = CreateIsolatedVm(initialApi);
        SetField(vm, "_isConnected", true);
        SetField(vm, "_statsPrevDown", 55555L);
        SetField(vm, "_statsPrevUp", 33333L);
        SetField(vm, "_statsPrevAt", DateTimeOffset.UtcNow);
        SetField(vm, "_connectionStatsText", "pre-transition-stats");

        // Start in-flight poll on old client that is deferred
        var pollMethod = typeof(MainWindowViewModel).GetMethod(
            "PollConnStatsAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(pollMethod);
        var oldPollTask = (Task)pollMethod!.Invoke(vm, null)!;

        // Perform reflection transition: OnIsConnectedChanged(false) -> OnIsConnectedChanged(true)
        var onIsConnectedChanged = typeof(MainWindowViewModel).GetMethod(
            "OnIsConnectedChanged", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(onIsConnectedChanged);

        try
        {
            // Transition 1: false (disconnect) -> clears _statsApi and resets counters/text
            onIsConnectedChanged!.Invoke(vm, new object[] { false });
            Assert.Null(GetField<ClashSingBoxApi>(vm, "_statsApi"));
            Assert.Equal(0L, GetField<long>(vm, "_statsPrevDown"));
            Assert.Equal(0L, GetField<long>(vm, "_statsPrevUp"));
            Assert.Null(GetField<DateTimeOffset?>(vm, "_statsPrevAt"));
            Assert.Equal(string.Empty, vm.ConnectionStatsText);

            // Transition 2: true (reconnect) -> creates unique ClashSingBoxApi handle without sending requests
            onIsConnectedChanged!.Invoke(vm, new object[] { true });
            var createdApi = GetField<ClashSingBoxApi>(vm, "_statsApi");
            Assert.NotNull(createdApi);
            Assert.NotSame(initialApi, createdApi);

            // Dispose the new client created by OnIsConnectedChanged(true) to prevent leaks,
            // then inject fake api afterward
            createdApi!.Dispose();

            var fakeHandler = new FakeClashHttpHandler();
            using var fakeHttp = new HttpClient(fakeHandler);
            using var fakeApi = new ClashSingBoxApi(httpClient: fakeHttp, baseUrl: "http://127.0.0.1:9090");
            SetField(vm, "_statsApi", fakeApi);
            SetField(vm, "_isConnected", true);

            // Verify counters remain reset
            Assert.Equal(0L, GetField<long>(vm, "_statsPrevDown"));
            Assert.Equal(0L, GetField<long>(vm, "_statsPrevUp"));
            Assert.Null(GetField<DateTimeOffset?>(vm, "_statsPrevAt"));
            Assert.Equal(string.Empty, vm.ConnectionStatsText);

            // Release the deferred response from the old poll before transition
            deferred.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"downloadTotal\": 999999, \"uploadTotal\": 888888, \"connections\": [{}]}")
            });

            await oldPollTask;
            await DrainUiQueueAsync();

            // Verify old reply was dropped: counters, text, and auto-selected node remain reset
            Assert.Equal(0L, GetField<long>(vm, "_statsPrevDown"));
            Assert.Equal(0L, GetField<long>(vm, "_statsPrevUp"));
            Assert.Null(GetField<DateTimeOffset?>(vm, "_statsPrevAt"));
            Assert.Equal(string.Empty, vm.ConnectionStatsText);
            Assert.Null(GetField<ServerViewModel>(vm, "_autoSelectedServer"));

            // Same-client current response works
            fakeHandler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"downloadTotal\": 1200, \"uploadTotal\": 800, \"connections\": [{}]}")
            };
            await InvokePollAsync(vm);
            Assert.Equal(1200L, GetField<long>(vm, "_statsPrevDown"));
            Assert.Equal(800L, GetField<long>(vm, "_statsPrevUp"));
            Assert.NotNull(GetField<DateTimeOffset?>(vm, "_statsPrevAt"));

            // Disconnected same-client (IsConnected == false) is rejected
            SetField(vm, "_isConnected", false);
            fakeHandler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"downloadTotal\": 5000, \"uploadTotal\": 4000, \"connections\": [{}]}")
            };
            await InvokePollAsync(vm);
            Assert.Equal(1200L, GetField<long>(vm, "_statsPrevDown"));
            Assert.Equal(800L, GetField<long>(vm, "_statsPrevUp"));
        }
        finally
        {
            deferred.TrySetCanceled();
            try
            {
                await oldPollTask;
            }
            catch
            {
            }
            await DrainUiQueueAsync();
        }
    }

    [AvaloniaFact]
    public async Task GroupUnresolved_ClearsPreviousSelection_NotRetain()
    {
        var handler = new FakeClashHttpHandler();
        using var http = new HttpClient(handler);
        using var api = new ClashSingBoxApi(httpClient: http, baseUrl: "http://127.0.0.1:9090");
        var vm = CreateIsolatedVm(api);

        var settings = GetField<AppSettings>(vm, "_settings")!;
        settings.App.ConfigMode = "subscribe";
        SetField(vm, "_autoSelectBestServer", true);

        var servers = GetField<ObservableCollection<ServerViewModel>>(vm, "SubscriptionServers")!;
        var serverA = new ServerViewModel(new VlessServerEntry { Name = "ServerAlpha", Server = "1.1.1.1" });
        servers.Add(serverA);

        // Pre-populate _autoSelectedServer with serverA
        SetField(vm, "_autoSelectedServer", serverA);
        Assert.Same(serverA, GetField<ServerViewModel>(vm, "_autoSelectedServer"));

        // Unresolved / 404 response from /proxies/proxy
        handler.Responder = req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("/proxies/"))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"downloadTotal\": 1000, \"uploadTotal\": 1000, \"connections\": []}")
            };
        };

        SetField(vm, "_autoSelectPollTick", 0);

        var refreshMethod = typeof(MainWindowViewModel).GetMethod(
            "MaybeRefreshAutoSelectedAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(refreshMethod);

        await (Task)refreshMethod!.Invoke(vm, new object[] { api })!;
        await DrainUiQueueAsync();

        // Unresolved group must CLEAR previous selection, not retain it
        Assert.Null(GetField<ServerViewModel>(vm, "_autoSelectedServer"));
    }

    [AvaloniaFact]
    public async Task CatchPollExceptions_ClearsState_OnlyIfCurrent()
    {
        var handler = new FakeClashHttpHandler();
        using var http = new HttpClient(handler);
        using var api = new ClashSingBoxApi(httpClient: http, baseUrl: "http://127.0.0.1:9090");
        var vm = CreateIsolatedVm(api);

        // Establish initial state
        SetField(vm, "_connectionStatsText", "active-stats");
        SetField(vm, "_statsPrevDown", 5000L);
        SetField(vm, "_statsPrevAt", DateTimeOffset.UtcNow);

        // Network exception thrown during connection poll
        handler.Responder = _ => throw new HttpRequestException("Socket connection refused");

        await InvokePollAsync(vm);

        // Exception caught must clear state when current
        Assert.Equal(string.Empty, vm.ConnectionStatsText);
        Assert.Null(GetField<DateTimeOffset?>(vm, "_statsPrevAt"));
        Assert.Equal(0L, GetField<long>(vm, "_statsPrevDown"));
    }

    [AvaloniaFact]
    public async Task CatchPollExceptions_DoesNotClearState_WhenApiMismatch()
    {
        var deferred = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler1 = new FakeClashHttpHandler { DeferredResponse = deferred };
        using var http1 = new HttpClient(handler1);
        using var api1 = new ClashSingBoxApi(httpClient: http1, baseUrl: "http://127.0.0.1:9090");
        var vm = CreateIsolatedVm(api1);

        // Establish initial state
        SetField(vm, "_connectionStatsText", "persisted-stats");
        SetField(vm, "_statsPrevDown", 5000L);
        SetField(vm, "_statsPrevAt", DateTimeOffset.UtcNow);

        var pollMethod = typeof(MainWindowViewModel).GetMethod(
            "PollConnStatsAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(pollMethod);
        var pollTask = (Task)pollMethod!.Invoke(vm, null)!;

        var handler2 = new FakeClashHttpHandler();
        using var http2 = new HttpClient(handler2);
        using var api2 = new ClashSingBoxApi(httpClient: http2, baseUrl: "http://127.0.0.1:9090");

        try
        {
            // Swap client identity to api2
            SetField(vm, "_statsApi", api2);

            // Fault the deferred task
            deferred.TrySetException(new HttpRequestException("Network failure"));

            await pollTask;
            await DrainUiQueueAsync();

            // Exception caught must NOT clear state because API client is stale
            Assert.Equal("persisted-stats", vm.ConnectionStatsText);
            Assert.NotNull(GetField<DateTimeOffset?>(vm, "_statsPrevAt"));
            Assert.Equal(5000L, GetField<long>(vm, "_statsPrevDown"));
        }
        finally
        {
            deferred.TrySetCanceled();
            try
            {
                await pollTask;
            }
            catch
            {
            }
            await DrainUiQueueAsync();
        }
    }
}
