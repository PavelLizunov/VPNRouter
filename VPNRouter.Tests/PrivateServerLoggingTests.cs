using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Serilog.Events;
using VPNRouter.App.ViewModels;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class PrivateServerLoggingTests
{
    private const string Marker = "private-fixture";
    private const string InvalidUri = "vless://private-fixture.invalid:bad#label private-fixture-tail";

    [Theory]
    [InlineData(InvalidUri, "Skipped invalid server URI")]
    [InlineData("{\"private-fixture\":", "Failed to parse JSON response")]
    [InlineData("proxies: [private-fixture", "YAML deserialize failed")]
    [InlineData("""{"outbounds":[{"type":"private-fixture","tag":"private-fixture","server":"private-fixture.invalid","server_port":443}]}""",
        "unsupported protocol")]
    [InlineData("""{"outbounds":[{"type":"vless","uuid":"fixture","server":"private-fixture.invalid","server_port":443,"transport":{"type":"private-fixture"}}]}""",
        "unsupported transport")]
    public void ParseBody_ReportsWhyInputWasDropped(string body, string expectedDiagnostic)
    {
        var sink = new CapturingSink();
        using var logger = sink.CreateLogger();

        Assert.Empty(SubscriptionFetcher.ParseBody(body, logger));

        Assert.Contains(sink.Events, e => e.RenderMessage().Contains(expectedDiagnostic, StringComparison.Ordinal));
        sink.AssertExcludes(Marker);
    }

    [Theory]
    [InlineData(ServerProbeStatus.Ok, true)]
    [InlineData(ServerProbeStatus.Unknown, true)]
    [InlineData(ServerProbeStatus.Unreachable, true)]
    [InlineData(ServerProbeStatus.Ok, false)]
    public async Task ResolveAndFailover_KeepSelectionsPrivate(ServerProbeStatus probeStatus, bool restartSucceeds)
    {
        var sink = new CapturingSink();
        using var logger = sink.CreateLogger();
        var store = new InMemorySettingsStore();
        var settings = new AppSettings();
        settings.App.ConfigMode = "subscribe";
        settings.App.ActiveSubscriptionServer = Marker + "-expired";
        var servers = Enumerable.Range(1, 3).Select(i => new VlessServerEntry
        {
            Name = $"{Marker}-{i}", Server = $"{Marker}-{i}.invalid", Port = 443, Uuid = "fixture"
        }).ToList();
        settings.App.Subscriptions = [new SubscriptionEntry { Enabled = true, Servers = servers }];

        Assert.Equal(servers.Count, VlessServersResolver.Resolve(settings, logger).Count);
        Assert.Equal(servers[0].Name, settings.Vless.ActiveServer);
        Assert.Equal(servers[0].Name, settings.App.ActiveSubscriptionServer);
        Assert.Contains(sink.Events, e => e.Properties.ContainsKey("Subs") && e.Properties.ContainsKey("Count"));

        var failover = new AutoFailoverEngine(settings, new ConfigSanityCheck(),
            restart: _ => restartSucceeds ? Task.FromResult(true) : throw new InvalidOperationException(Marker),
            logger: logger, store: store)
        {
            ProbeCandidate = (_, _) => Task.FromResult(new ServerProbeResult(probeStatus, 25, Marker)),
        };

        var outcome = await failover.HandleDeadConfigAsync(Marker + "-failure", TestContext.Current.CancellationToken);
        Assert.Equal(restartSucceeds, outcome.Switched);
        Assert.Equal(servers[restartSucceeds ? 1 : 0].Name, settings.Vless.ActiveServer);
        if (restartSucceeds)
        {
            Assert.Equal(servers[1].Name, outcome.NewActiveServer);
            Assert.Contains(servers[1].Name, outcome.UserFacingMessage!);
            Assert.Equal(servers[1].Name, store.Load().Vless.ActiveServer);
        }
        else
        {
            Assert.Equal(0, store.SaveCount);
            Assert.Null(outcome.NewActiveServer);
            Assert.Contains(sink.Events, e => e.RenderMessage().Contains("restored previous selection"));
        }
        Assert.Contains(sink.Events, e => e.MessageTemplate.Text.Contains("Candidate probe"));
        sink.AssertExcludes(Marker);
    }

    [Fact]
    public async Task CachedSubscriptionStartup_LogsCountAndPreservesSelection()
    {
        var sink = new CapturingSink();
        using var logger = sink.CreateLogger();
        var settings = new AppSettings();
        settings.App.ConfigMode = "subscribe";
        settings.App.ActiveSubscriptionServer = Marker;
        settings.App.Subscriptions = [new SubscriptionEntry
        {
            Servers = [new VlessServerEntry { Name = Marker, Server = Marker + ".invalid" }],
        }];

        var count = await SubscriptionResolver.ResolveAsync(settings, false, logger, TestContext.Current.CancellationToken);

        Assert.Equal(1, count);
        Assert.Equal(Marker, settings.Vless.ActiveServer);
        Assert.Equal("1", Assert.Single(sink.Events).Properties["Count"].ToString());
        sink.AssertExcludes(Marker);
    }

    [AvaloniaFact]
    public void ManualImportSelectionAndNotifications_KeepDetailsInTheUi()
    {
        var sink = new CapturingSink();
        using var logger = sink.CreateLogger();
        var previousProbeLogger = TcpTlsProbe.Logger;
        try
        {
            using var vm = new MainWindowViewModel(new InMemorySettingsStore(), null, null, null,
                businessBootstrap: (_, _) => null, logger: logger);
            sink.Clear();
            vm.VlessUri = InvalidUri;
            vm.AddServerCommand.Execute(null);
            vm.VlessUri = "[Interface]\n# private-fixture\n[Peer]";
            vm.AddServerCommand.Execute(null);
            Assert.Empty(vm.VlessUri);
            Assert.Contains(sink.Events, e => e.RenderMessage().Contains("Failed to import server URI"));
            Assert.Contains(sink.Events, e => e.RenderMessage().Contains("Failed to import WireGuard"));

            vm.VlessUri = "vless://fixture@private-fixture.invalid:8443#private-fixture-node";
            vm.AddServerCommand.Execute(null);
            var row = Assert.Single(vm.Servers, s => s.Name == Marker + "-node");
            vm.SelectedServer = row;
            vm.SelectedSubscriptionServer = row;
            Assert.Contains(sink.Events, e => e.MessageTemplate.Text.Contains("Manual server selected"));
            Assert.Contains(sink.Events, e => e.MessageTemplate.Text.Contains("Subscription server selected"));

            Invoke(vm, "WarnServiceManagedReconnect", row.DisplayName);
            Assert.Contains(row.DisplayName, vm.StatusText);
            var winner = new ServerViewModel(new VlessServerEntry { Name = Marker + "-winner", Server = Marker + ".invalid" });
            vm.SubscriptionServers.Add(winner);
            vm.ApplySmartConnectSelection(winner.ToEntry());
            Assert.Same(winner, vm.SelectedSubscriptionServer);
            Assert.Contains(sink.Events, e => e.RenderMessage().Contains("[SmartConnect] Switching to a reachable server"));
            vm.RemoveServerByEntryCommand.Execute(row);
            Assert.DoesNotContain(row, vm.Servers);
            Assert.Contains(sink.Events, e => e.Properties.TryGetValue("Remaining", out var remaining)
                && remaining is ScalarValue { Value: int count } && count == vm.Servers.Count);

            Invoke(vm, "OnAutoFailoverMessage", Marker + "-notice");
            Invoke(vm, "OnTrueSplitStateChanged", TrueSplitState.Fallback, Marker + "-detail");
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(Marker + "-notice", vm.StatusText);
            Assert.Contains(Marker + "-detail", vm.TrueSplitStatusText);
            Assert.Contains(sink.Events, e => e.RenderMessage().Contains("Displayed automatic failover notification"));
            Assert.Contains(sink.Events, e => e.Properties.TryGetValue("State", out var state)
                && state is ScalarValue { Value: TrueSplitState.Fallback });
            sink.AssertExcludes(Marker, "8443");
        }
        finally
        {
            TcpTlsProbe.Logger = previousProbeLogger;
        }
    }

    [Fact]
    public async Task UnsupportedProbe_ReturnsProtocolDetailAndLogsOnlyStatusAndLatency()
    {
        var sink = new CapturingSink();
        using var logger = sink.CreateLogger();
        var previous = TcpTlsProbe.Logger;
        try
        {
            TcpTlsProbe.Logger = logger;
            var entry = new VlessServerEntry
            {
                Name = Marker, Server = Marker + ".invalid", Port = 18443, Protocol = Marker + "-protocol",
            };

            var result = await TcpTlsProbe.ProbeServerAsync(entry, TestContext.Current.CancellationToken);
            Assert.Equal(ServerProbeStatus.SkippedNotApplicable, result.Status);
            Assert.Contains(entry.Protocol, result.Error!);
            var completion = Assert.Single(sink.Events, e => e.Properties.ContainsKey("Status"));
            Assert.Equal(2, completion.Properties.Count);
            Assert.Equal(result.LatencyMs, Assert.IsType<ScalarValue>(completion.Properties["LatencyMs"]).Value);
            Assert.Equal(result.Status, Assert.IsType<ScalarValue>(completion.Properties["Status"]).Value);
            sink.AssertExcludes(Marker, "18443");
        }
        finally
        {
            TcpTlsProbe.Logger = previous;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SmartConnectProbeFailures_LogOnlyClassificationAndCounts(bool deadline)
    {
        var sink = new CapturingSink();
        using var logger = sink.CreateLogger();
        var probe = new ServerHealthProbe(logger, (_, _) => deadline
            ? throw new OperationCanceledException(Marker)
            : throw new InvalidOperationException(Marker));
        var entry = new VlessServerEntry { Name = Marker, Server = Marker + ".invalid", Port = 18443 };

        var results = await probe.ProbeAllAsync([entry], TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.Same(entry, result.Server);
        Assert.False(result.Alive);
        Assert.Contains(sink.Events, e => e.RenderMessage().Contains(deadline ? "deadline expired" : "Candidate probe failed"));
        Assert.Contains(sink.Events, e => e.Properties.ContainsKey("Alive") && e.Properties.ContainsKey("Total"));
        sink.AssertExcludes(Marker, "18443");
    }

    private static void Invoke(MainWindowViewModel vm, string name, params object[] arguments)
    {
        var method = typeof(MainWindowViewModel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(vm, arguments);
    }
}
