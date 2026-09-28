#nullable enable
using System;
using System.Collections.ObjectModel;
using System.IO;
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
        await Dispatcher.UIThread.InvokeAsync(() => {  }, DispatcherPriority.Background);
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

        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"downloadTotal\": 0, \"uploadTotal\": 0, \"connections\": []}")
        };
        await InvokePollAsync(vm);

        Assert.Equal(0L, GetField<long>(vm, "_statsPrevDown"));
        Assert.Equal(0L, GetField<long>(vm, "_statsPrevUp"));
        Assert.NotNull(GetField<DateTimeOffset?>(vm, "_statsPrevAt"));
        Assert.Equal(string.Empty, vm.ConnectionStatsText);

        SetField(vm, "_statsPrevAt", DateTimeOffset.UtcNow.AddSeconds(-2));
        await InvokePollAsync(vm);

        Assert.Equal(0L, GetField<long>(vm, "_statsPrevDown"));
        Assert.Equal(0L, GetField<long>(vm, "_statsPrevUp"));
        Assert.NotNull(GetField<DateTimeOffset?>(vm, "_statsPrevAt"));
        Assert.Contains("0 conn", vm.ConnectionStatsText);
        Assert.Contains("0 B/s", vm.ConnectionStatsText);

        SetField(vm, "_statsPrevAt", DateTimeOffset.UtcNow.AddSeconds(-2));
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"downloadTotal\": 20000, \"uploadTotal\": 10000, \"connections\": [{}]}")
        };
        await InvokePollAsync(vm);

        Assert.False(string.IsNullOrEmpty(vm.ConnectionStatsText));
        Assert.Contains("conn", vm.ConnectionStatsText);
        Assert.DoesNotContain("0 conn", vm.ConnectionStatsText);

        SetField(vm, "_statsPrevAt", DateTimeOffset.UtcNow.AddSeconds(-2));
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"downloadTotal\": 20000, \"uploadTotal\": 10000, \"connections\": []}")
        };
        await InvokePollAsync(vm);

        Assert.Contains("0 conn", vm.ConnectionStatsText);
        Assert.DoesNotContain("1 conn", vm.ConnectionStatsText);

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

        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"downloadTotal\": 1000, \"uploadTotal\": 1000, \"connections\": [{}]}")
        };
        await InvokePollAsync(vm);

        SetField(vm, "_statsPrevAt", DateTimeOffset.UtcNow.AddSeconds(-2));
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"downloadTotal\": 2000, \"uploadTotal\": 2000, \"connections\": [{}]}")
        };
        await InvokePollAsync(vm);

        Assert.False(string.IsNullOrEmpty(vm.ConnectionStatsText));

        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        await InvokePollAsync(vm);

        Assert.True(string.IsNullOrEmpty(vm.ConnectionStatsText));
        Assert.Null(GetField<DateTimeOffset?>(vm, "_statsPrevAt"));
        Assert.Equal(0L, GetField<long>(vm, "_statsPrevDown"));
        Assert.Equal(0L, GetField<long>(vm, "_statsPrevUp"));

        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"downloadTotal\": 100000000, \"uploadTotal\": 100000000, \"connections\": [{}]}")
        };
        await InvokePollAsync(vm);

        Assert.True(string.IsNullOrEmpty(vm.ConnectionStatsText),
            $"Expected no spike on first recovery sample, got '{vm.ConnectionStatsText}'");
        Assert.NotNull(GetField<DateTimeOffset?>(vm, "_statsPrevAt"));
        Assert.Equal(100000000L, GetField<long>(vm, "_statsPrevDown"));
        Assert.Equal(100000000L, GetField<long>(vm, "_statsPrevUp"));

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

        SetField(vm, "_statsPrevAt", DateTimeOffset.UtcNow.AddSeconds(-2));
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"downloadTotal\": 1000, \"uploadTotal\": 1000, \"connections\": [{}]}")
        };
        await InvokePollAsync(vm);

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
            SetField(vm, "_statsApi", api2);

            deferred.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"downloadTotal\": 12345, \"uploadTotal\": 67890, \"connections\": [{}, {}]}")
            });

            await pollTask;
            await DrainUiQueueAsync();

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

        var pollMethod = typeof(MainWindowViewModel).GetMethod(
            "PollConnStatsAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(pollMethod);
        var oldPollTask = (Task)pollMethod!.Invoke(vm, null)!;

        var onIsConnectedChanged = typeof(MainWindowViewModel).GetMethod(
            "OnIsConnectedChanged", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(onIsConnectedChanged);

        ClashSingBoxApi? createdApi = null;
        try
        {
            onIsConnectedChanged!.Invoke(vm, new object[] { false });
            Assert.Null(GetField<ClashSingBoxApi>(vm, "_statsApi"));
            Assert.Equal(0L, GetField<long>(vm, "_statsPrevDown"));
            Assert.Equal(0L, GetField<long>(vm, "_statsPrevUp"));
            Assert.Null(GetField<DateTimeOffset?>(vm, "_statsPrevAt"));
            Assert.Equal(string.Empty, vm.ConnectionStatsText);

            onIsConnectedChanged!.Invoke(vm, new object[] { true });
            createdApi = GetField<ClashSingBoxApi>(vm, "_statsApi");
            Assert.NotNull(createdApi);
            Assert.NotSame(initialApi, createdApi);

            createdApi!.Dispose();
            createdApi = null;

            var fakeHandler = new FakeClashHttpHandler();
            using var fakeHttp = new HttpClient(fakeHandler);
            using var fakeApi = new ClashSingBoxApi(httpClient: fakeHttp, baseUrl: "http://127.0.0.1:9090");
            SetField(vm, "_statsApi", fakeApi);
            SetField(vm, "_isConnected", true);

            Assert.Equal(0L, GetField<long>(vm, "_statsPrevDown"));
            Assert.Equal(0L, GetField<long>(vm, "_statsPrevUp"));
            Assert.Null(GetField<DateTimeOffset?>(vm, "_statsPrevAt"));
            Assert.Equal(string.Empty, vm.ConnectionStatsText);

            deferred.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"downloadTotal\": 999999, \"uploadTotal\": 888888, \"connections\": [{}]}")
            });

            await oldPollTask;
            await DrainUiQueueAsync();

            Assert.Equal(0L, GetField<long>(vm, "_statsPrevDown"));
            Assert.Equal(0L, GetField<long>(vm, "_statsPrevUp"));
            Assert.Null(GetField<DateTimeOffset?>(vm, "_statsPrevAt"));
            Assert.Equal(string.Empty, vm.ConnectionStatsText);
            Assert.Null(GetField<ServerViewModel>(vm, "_autoSelectedServer"));

            fakeHandler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"downloadTotal\": 1200, \"uploadTotal\": 800, \"connections\": [{}]}")
            };
            await InvokePollAsync(vm);
            Assert.Equal(1200L, GetField<long>(vm, "_statsPrevDown"));
            Assert.Equal(800L, GetField<long>(vm, "_statsPrevUp"));
            Assert.NotNull(GetField<DateTimeOffset?>(vm, "_statsPrevAt"));

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
            createdApi?.Dispose();
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

        SetField(vm, "_autoSelectedServer", serverA);
        Assert.Same(serverA, GetField<ServerViewModel>(vm, "_autoSelectedServer"));

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

        Assert.Null(GetField<ServerViewModel>(vm, "_autoSelectedServer"));
    }

    [AvaloniaFact]
    public async Task CatchPollExceptions_ClearsState_OnlyIfCurrent()
    {
        var handler = new FakeClashHttpHandler();
        using var http = new HttpClient(handler);
        using var api = new ClashSingBoxApi(httpClient: http, baseUrl: "http://127.0.0.1:9090");
        var vm = CreateIsolatedVm(api);

        SetField(vm, "_connectionStatsText", "active-stats");
        SetField(vm, "_statsPrevDown", 5000L);
        SetField(vm, "_statsPrevAt", DateTimeOffset.UtcNow);

        handler.Responder = _ => throw new HttpRequestException("Socket connection refused");

        await InvokePollAsync(vm);

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
            SetField(vm, "_statsApi", api2);

            deferred.TrySetException(new HttpRequestException("Network failure"));

            await pollTask;
            await DrainUiQueueAsync();

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

    [AvaloniaFact]
    public async Task OnEngineStatus_AppliedHotReload_WhileConnected_ReplacesApi_ClearsBaseline_AndDropsOldReply()
    {
        await AssertAppliedReplacesApiAndDropsOldReplyAsync("Applied (hot-reload, PID 1234)");
    }

    [AvaloniaFact]
    public async Task OnEngineStatus_AppliedRestart_WhileConnected_ReplacesApi_ClearsBaseline_AndDropsOldReply()
    {
        await AssertAppliedReplacesApiAndDropsOldReplyAsync("Applied (restart, PID 5678)");
    }

    private static async Task AssertAppliedReplacesApiAndDropsOldReplyAsync(string status)
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
        SetField(vm, "_connectionStatsText", "pre-apply-stats");

        var servers = GetField<ObservableCollection<ServerViewModel>>(vm, "SubscriptionServers")!;
        var serverA = new ServerViewModel(new VlessServerEntry { Name = "ServerAlpha", Server = "1.1.1.1" });
        servers.Add(serverA);
        SetField(vm, "_autoSelectedServer", serverA);

        var pollMethod = typeof(MainWindowViewModel).GetMethod(
            "PollConnStatsAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(pollMethod);
        var oldPollTask = (Task)pollMethod!.Invoke(vm, null)!;

        var onEngineStatus = typeof(MainWindowViewModel).GetMethod(
            "OnEngineStatus", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(onEngineStatus);

        ClashSingBoxApi? createdApi = null;
        try
        {
            onEngineStatus!.Invoke(vm, new object[] { status });
            await DrainUiQueueAsync();

            createdApi = GetField<ClashSingBoxApi>(vm, "_statsApi");
            Assert.NotNull(createdApi);
            Assert.NotSame(initialApi, createdApi);

            Assert.Equal(0L, GetField<long>(vm, "_statsPrevDown"));
            Assert.Equal(0L, GetField<long>(vm, "_statsPrevUp"));
            Assert.Null(GetField<DateTimeOffset?>(vm, "_statsPrevAt"));
            Assert.Equal(string.Empty, vm.ConnectionStatsText);
            Assert.Null(GetField<ServerViewModel>(vm, "_autoSelectedServer"));
            Assert.Equal(status, vm.StatusText);

            createdApi!.Dispose();
            createdApi = null;

            deferred.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"downloadTotal\": 999999, \"uploadTotal\": 888888, \"connections\": [{}]}")
            });

            await oldPollTask;
            await DrainUiQueueAsync();

            Assert.Equal(0L, GetField<long>(vm, "_statsPrevDown"));
            Assert.Equal(0L, GetField<long>(vm, "_statsPrevUp"));
            Assert.Null(GetField<DateTimeOffset?>(vm, "_statsPrevAt"));
            Assert.Equal(string.Empty, vm.ConnectionStatsText);
            Assert.Null(GetField<ServerViewModel>(vm, "_autoSelectedServer"));

            var fakeHandler = new FakeClashHttpHandler();
            using var fakeHttp = new HttpClient(fakeHandler);
            using var fakeApi = new ClashSingBoxApi(httpClient: fakeHttp, baseUrl: "http://127.0.0.1:9090");
            SetField(vm, "_statsApi", fakeApi);

            fakeHandler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"downloadTotal\": 1000, \"uploadTotal\": 500, \"connections\": [{}]}")
            };
            await InvokePollAsync(vm);

            Assert.Equal(1000L, GetField<long>(vm, "_statsPrevDown"));
            Assert.Equal(500L, GetField<long>(vm, "_statsPrevUp"));
            Assert.NotNull(GetField<DateTimeOffset?>(vm, "_statsPrevAt"));
            Assert.Equal(string.Empty, vm.ConnectionStatsText);
        }
        finally
        {
            createdApi?.Dispose();
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
    public async Task OnEngineStatus_FailedApplyOrNonCommitStatus_DoesNotResetApiOrBaseline()
    {
        var handler = new FakeClashHttpHandler();
        using var http = new HttpClient(handler);
        using var initialApi = new ClashSingBoxApi(httpClient: http, baseUrl: "http://127.0.0.1:9090");
        var vm = CreateIsolatedVm(initialApi);

        SetField(vm, "_isConnected", true);
        SetField(vm, "_statsPrevDown", 5000L);
        SetField(vm, "_statsPrevUp", 3000L);
        SetField(vm, "_statsPrevAt", DateTimeOffset.UtcNow);
        SetField(vm, "_connectionStatsText", "active-stats");

        var onEngineStatus = typeof(MainWindowViewModel).GetMethod(
            "OnEngineStatus", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(onEngineStatus);

        onEngineStatus!.Invoke(vm, new object[] { "Apply failed: sing-box reload or restart was not confirmed" });
        await DrainUiQueueAsync();

        Assert.Same(initialApi, GetField<ClashSingBoxApi>(vm, "_statsApi"));
        Assert.Equal(5000L, GetField<long>(vm, "_statsPrevDown"));
        Assert.Equal(3000L, GetField<long>(vm, "_statsPrevUp"));
        Assert.NotNull(GetField<DateTimeOffset?>(vm, "_statsPrevAt"));
        Assert.Equal("active-stats", vm.ConnectionStatsText);
        Assert.Equal("Apply failed: sing-box reload or restart was not confirmed", vm.StatusText);

        onEngineStatus!.Invoke(vm, new object[] { "Applying configuration..." });
        await DrainUiQueueAsync();

        Assert.Same(initialApi, GetField<ClashSingBoxApi>(vm, "_statsApi"));
        Assert.Equal(5000L, GetField<long>(vm, "_statsPrevDown"));
        Assert.Equal(3000L, GetField<long>(vm, "_statsPrevUp"));
        Assert.NotNull(GetField<DateTimeOffset?>(vm, "_statsPrevAt"));
        Assert.Equal("active-stats", vm.ConnectionStatsText);
        Assert.Equal("Applying configuration...", vm.StatusText);
    }

    [AvaloniaFact]
    public async Task OnEngineStatus_AppliedWhileDisconnected_DoesNotInstantiateApiOrPromote()
    {
        var vm = CreateIsolatedVm(null);
        SetField(vm, "_isConnected", false);

        var onEngineStatus = typeof(MainWindowViewModel).GetMethod(
            "OnEngineStatus", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(onEngineStatus);

        onEngineStatus!.Invoke(vm, new object[] { "Applied (hot-reload, PID 1234)" });
        await DrainUiQueueAsync();

        Assert.False(vm.IsConnected);
        Assert.Null(GetField<ClashSingBoxApi>(vm, "_statsApi"));
        Assert.Equal("Applied (hot-reload, PID 1234)", vm.StatusText);
    }

    [AvaloniaFact]
    public async Task OnEngineStatus_ConnectedBranch_WhileAlreadyConnected_ReplacesApiAndResetsBaseline()
    {
        var handler = new FakeClashHttpHandler();
        using var http = new HttpClient(handler);
        using var initialApi = new ClashSingBoxApi(httpClient: http, baseUrl: "http://127.0.0.1:9090");
        var vm = CreateIsolatedVm(initialApi);

        SetField(vm, "_isConnected", true);
        SetField(vm, "_statsPrevDown", 55555L);
        SetField(vm, "_statsPrevUp", 33333L);
        SetField(vm, "_statsPrevAt", DateTimeOffset.UtcNow);
        SetField(vm, "_connectionStatsText", "pre-connected-stats");

        var onEngineStatus = typeof(MainWindowViewModel).GetMethod(
            "OnEngineStatus", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(onEngineStatus);

        ClashSingBoxApi? createdApi = null;
        try
        {
            onEngineStatus!.Invoke(vm, new object[] { "Connected to server" });
            await DrainUiQueueAsync();

            createdApi = GetField<ClashSingBoxApi>(vm, "_statsApi");
            Assert.NotNull(createdApi);
            Assert.NotSame(initialApi, createdApi);

            Assert.Null(GetField<System.Threading.Timer>(vm, "_subRefreshTimer"));

            Assert.Equal(0L, GetField<long>(vm, "_statsPrevDown"));
            Assert.Equal(0L, GetField<long>(vm, "_statsPrevUp"));
            Assert.Null(GetField<DateTimeOffset?>(vm, "_statsPrevAt"));
            Assert.Equal(string.Empty, vm.ConnectionStatsText);
        }
        finally
        {
            createdApi?.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task OnEngineStatus_ConnectedBranch_WhileDisconnected_DoesNotPromote()
    {
        var vm = CreateIsolatedVm(null);
        SetField(vm, "_isConnected", false);

        var onEngineStatus = typeof(MainWindowViewModel).GetMethod(
            "OnEngineStatus", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(onEngineStatus);

        onEngineStatus!.Invoke(vm, new object[] { "Connected to server" });
        await DrainUiQueueAsync();

        Assert.False(vm.IsConnected);
        Assert.Null(GetField<ClashSingBoxApi>(vm, "_statsApi"));
    }

    [Fact]
    public void OnEngineStatus_ConnectedBranch_CallsOnIsConnectedChangedBeforeRefreshAndRestore_SourceGuard()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        string? source = null;
        for (var depth = 0; depth < 8 && directory != null; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "VPNRouter.App", "ViewModels", "MainWindowViewModel.Connection.cs");
            if (File.Exists(candidate))
            {
                source = File.ReadAllText(candidate);
                break;
            }
        }
        Assert.NotNull(source);

        var connectedIdx = source!.IndexOf("status.StartsWith(\"Connected\")", StringComparison.Ordinal);
        Assert.True(connectedIdx >= 0);

        var onIsConnectedChangedIdx = source.IndexOf("OnIsConnectedChanged(true);", connectedIdx, StringComparison.Ordinal);
        Assert.True(onIsConnectedChangedIdx >= 0);

        var refreshIdx = source.IndexOf("RefreshActiveIndicator();", onIsConnectedChangedIdx, StringComparison.Ordinal);
        Assert.True(refreshIdx > onIsConnectedChangedIdx);

        var restoreIdx = source.IndexOf("RestoreConnectedStatus();", refreshIdx, StringComparison.Ordinal);
        Assert.True(restoreIdx > refreshIdx);
    }
}
