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
/// Baseline-compatible characterization test for NIGHT-11 ConnStats failure handling.
/// Verifies that PollConnStatsAsync clears ConnectionStatsText and resets baseline on API failure (500).
/// On baseline, parser/API failure retains stale text and baseline (expected RED).
/// </summary>
public sealed class NightBaselineStatsTests
{
    private static MainWindowViewModel CreateIsolatedVm(ClashSingBoxApi? api)
    {
        var vm = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));

        var settings = new AppSettings
        {
            App = new AppConfig
            {
                ConfigMode = "generated",
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
        SetField(vm, "_connectionStatsText", "oldtext");
        SetField(vm, "_statsPrevDown", 1000L);
        SetField(vm, "_statsPrevUp", 500L);
        SetField(vm, "_statsPrevAt", DateTimeOffset.Now);
        SetField(vm, "_autoSelectBestServer", false);
        SetField(vm, "_autoSelectPollTick", 0);
        SetField(vm, "_statsInFlight", 0);
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
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private sealed class Fake500HttpHandler : HttpMessageHandler
    {
        public int RequestCount;
        public string? LastPath;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref RequestCount);
            LastPath = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }

    [AvaloniaFact]
    public async Task Night11_PollConnStatsAsync_OnApiFailure_ClearsConnectionStatsTextAndResetsBaseline()
    {
        Fake500HttpHandler? fakeHandler = null;
        HttpClient? httpClient = null;
        ClashSingBoxApi? api = null;

        try
        {
            fakeHandler = new Fake500HttpHandler();
            httpClient = new HttpClient(fakeHandler) { Timeout = Timeout.InfiniteTimeSpan };
            api = new ClashSingBoxApi(httpClient, baseUrl: "http://127.0.0.1:9090");
            var vm = CreateIsolatedVm(api);

            var pollMethod = typeof(MainWindowViewModel).GetMethod(
                "PollConnStatsAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(pollMethod);

            var pollTask = (Task)pollMethod!.Invoke(vm, null)!;
            await pollTask.WaitAsync(TimeSpan.FromSeconds(5));
            await DrainUiQueueAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(string.Empty, vm.ConnectionStatsText);
            Assert.Null(GetField<DateTimeOffset?>(vm, "_statsPrevAt"));
            Assert.Equal(1, fakeHandler.RequestCount);
            Assert.Equal("/connections", fakeHandler.LastPath);
        }
        finally
        {
            try { api?.Dispose(); } catch { }
            try { httpClient?.Dispose(); } catch { }
            try { fakeHandler?.Dispose(); } catch { }
            try { await DrainUiQueueAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
    }
}
