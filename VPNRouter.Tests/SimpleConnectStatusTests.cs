using System.Reflection;
using Avalonia.Headless.XUnit;
using VPNRouter.App.ViewModels;
using VPNRouter.App.ViewModels.Internals;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class SimpleConnectStatusTests
{
    [AvaloniaFact]
    public async Task PendingRoutingAndServerEditsStayOutOfTheConnectedStatus()
    {
        var settings = new AppSettings();
        settings.App.ConfigMode = "subscribe";
        settings.App.RoutingMode = "split";
        settings.App.Subscriptions = [new()
        {
            Enabled = true, Url = "https://feed.invalid/status", Servers =
            [
                new() { Name = "accepted", Server = "accepted.invalid" },
                new() { Name = "pending", Server = "pending.invalid" }
            ]
        }];
        var store = new InMemorySettingsStore();
        store.Save(settings, AppPaths.ConfigYamlPath);
        using var vm = ProductEdition.IsBusiness
            ? new MainWindowViewModel(store, null, null, null, businessBootstrap: (_, _) => null)
            : new MainWindowViewModel(store);
        var engine = Assert.IsType<VpnEngine>(typeof(MainWindowViewModel)
            .GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm));
        var probing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.SimpleProbe = async (rows, _) =>
        {
            probing.TrySetResult();
            await resume.Task;
            return rows.Select(s => new ServerLiveness(s, true, 1)).ToList();
        };
        vm.SimpleTunnelStart = (submission, _, _) =>
        {
            VlessServersResolver.Resolve(submission);
            var selected = Assert.Single(submission.Vless.GetActiveServers());
            engine.CommitTunnelInfo(submission, "{}", selected.Server, null);
            return Task.FromResult((Task.CompletedTask, TwoPhaseStartOutcome.Connected));
        };

        var start = vm.SmpToggleConnectCommand.ExecuteAsync(null);
        try
        {
            await probing.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            vm.IsSplitTunnel = false;
            vm.SelectedSubscriptionServer = vm.SubscriptionServers.Single(s => s.Name == "pending");
        }
        finally
        {
            resume.TrySetResult();
            await start.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }

        Assert.True(vm.HasPendingAppChanges);
        Assert.False(vm.IsSplitTunnel);
        Assert.Equal("pending", vm.SelectedSubscriptionServer?.Name);
        Assert.Contains("subscribe/split", vm.StatusText);
        Assert.Contains("accepted.invalid", vm.StatusText);
        Assert.DoesNotContain("pending", vm.StatusText);
        Assert.Contains("accepted.invalid", vm.SmpActiveServerLine);
        Assert.Contains("accepted.invalid", vm.SimpleActiveOutboundLine);
        Assert.Equal("accepted", vm.SmpConfigServer?.Name);
        Assert.False(vm.SelectedSubscriptionServer!.IsActive);
        Assert.True(vm.SubscriptionServers.Single(s => s.Name == "accepted").IsActive);
    }

    [Fact]
    public void AutoSelectionLooksUpTheReportedTagInTheAcceptedPool()
    {
        var settings = new AppSettings();
        settings.App.ConfigMode = "subscribe";
        settings.Vless.AutoSelectBestServer = true;
        settings.Vless.Servers = [
            new() { Name = "same", Server = "one.invalid", Port = 443 },
            new() { Name = "same", Server = "two.invalid", Port = 443 }
        ];
        const string json = """
            {"outbounds":[{"tag":"vless-same","server":"one.invalid","server_port":443},
                          {"tag":"vless-same-2","server":"two.invalid","server_port":443},
                          {"type":"urltest","tag":"proxy","outbounds":["vless-same","vless-same-2"]}]}
            """;
        var accepted = new AcceptedTunnelInfo(settings, json, "one.invalid", null);
        settings.Vless.Servers.Clear();
        settings.App.RoutingMode = "full";
        Assert.Equal("split", accepted.RoutingMode);
        Assert.Equal(2, accepted.SubscriptionServerCount);
        Assert.Equal(new TunnelServerLabel("same", "two.invalid", 443), accepted.FindMember("vless-same-2"));
        Assert.Null(accepted.FindMember("another-session"));
    }
}
