using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using VPNRouter.App.ViewModels;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

[Collection(ProbeStaticsCollection.Name)]
public sealed class TcpTlsProbeTrustTests : IDisposable
{
    private readonly Func<bool> _tunnel = TcpTlsProbe.IsTunnelActive;
    private readonly Func<int?> _iface = TcpTlsProbe.OutboundInterfaceIndex;
    private readonly TcpListener _listener;
    private readonly int _port;

    public TcpTlsProbeTrustTests()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try { (await _listener.AcceptTcpClientAsync()).Close(); }
                catch { break; }
            }
        });
    }

    public void Dispose()
    {
        TcpTlsProbe.IsTunnelActive = _tunnel;
        TcpTlsProbe.OutboundInterfaceIndex = _iface;
        _listener.Stop();
    }

    [Theory]
    [InlineData(2, false, true, true)]
    [InlineData(2, true, true, false)]
    [InlineData(2, false, false, false)]
    [InlineData(40, false, true, false)]
    [InlineData(4, false, true, true)]
    [InlineData(5, false, true, false)]
    public void LooksIntercepted_OnlyForAFastUnboundAnswerWhileTheTunnelIsUp(int latencyMs, bool bound, bool tunnelUp, bool expected)
    {
        TcpTlsProbe.IsTunnelActive = () => tunnelUp;

        Assert.Equal(expected, TcpTlsProbe.LooksIntercepted(latencyMs, bound));
    }

    [Fact]
    public async Task FastLocalServer_WithoutATunnel_IsReachable_NotImplausible()
    {
        TcpTlsProbe.IsTunnelActive = () => false;

        var result = await TcpTlsProbe.ProbeTcpOnlyAsync("127.0.0.1", _port);

        Assert.True(result.IsReachable, $"status was {result.Status}");
        Assert.NotEqual(ServerProbeStatus.Implausible, result.Status);
    }

    [Fact]
    public async Task FastAnswer_WhileTheTunnelIsUpAndNotBound_IsFlaggedImplausible()
    {
        TcpTlsProbe.IsTunnelActive = () => true;
        TcpTlsProbe.OutboundInterfaceIndex = () => null;

        var result = await TcpTlsProbe.ProbeTcpOnlyAsync("127.0.0.1", _port);

        Assert.Equal(ServerProbeStatus.Implausible, result.Status);
        Assert.False(result.IsReachable);
    }

    [Fact]
    public async Task HostName_IsResolvedBeforeTheTiming_AndStillConnects()
    {
        TcpTlsProbe.IsTunnelActive = () => false;

        var (ok, latency, err) = await TcpTlsProbe.ProbeTcpAsync("localhost", _port, TimeSpan.FromSeconds(3), default);

        Assert.True(ok, err);
        Assert.InRange(latency, 0, 1500);
    }

    [Fact]
    public async Task UnknownHost_IsReportedAsAFailureNotAPing()
    {
        var (ok, latency, err) = await TcpTlsProbe.ProbeTcpAsync("no-such-host.invalid", 443, TimeSpan.FromSeconds(3), default);

        Assert.False(ok);
        Assert.Equal(0, latency);
        Assert.False(string.IsNullOrEmpty(err));
    }

    [Fact]
    public void InternetInterfaceIndex_NeverThrows()
    {
        var index = Record.Exception(() => NetworkInterfaceDetector.GetInternetInterfaceIndex("VPNRouter-TUN"));

        Assert.Null(index);
    }

    [Fact]
    public void SilentUdpResult_TellsABindingAboutTheAmberState_NotOnlyTheText()
    {
        // The pill reads IsPingGood / IsPingSlow / PingDisplay only when told they changed: after the whole result is applied each cached value must be current.
        var vm = new ServerViewModel(new VlessServerEntry { Name = "HY2", Server = "203.0.113.1", Port = 8444, Protocol = "hysteria2" });
        bool good = vm.IsPingGood, slow = vm.IsPingSlow;
        var text = vm.PingDisplay;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ServerViewModel.IsPingGood)) good = vm.IsPingGood;
            if (e.PropertyName == nameof(ServerViewModel.IsPingSlow)) slow = vm.IsPingSlow;
            if (e.PropertyName == nameof(ServerViewModel.PingDisplay)) text = vm.PingDisplay;
        };

        vm.ApplyProbeResult(new ServerProbeResult(ServerProbeStatus.Ok, 2000, TcpTlsProbe.UdpNoReplyNote));

        Assert.False(good);
        Assert.True(slow);
        Assert.Equal("UDP ?", text);
    }

    [Fact]
    public void SilentUdpResult_IsShownAsUnverified_NotAsA2000msPing()
    {
        var vm = new ServerViewModel(new VlessServerEntry { Name = "HY2", Server = "203.0.113.1", Port = 8444, Protocol = "hysteria2" });

        vm.ApplyProbeResult(new ServerProbeResult(ServerProbeStatus.Ok, 2000, TcpTlsProbe.UdpNoReplyNote));

        Assert.Equal("UDP ?", vm.PingDisplay);
        Assert.True(vm.IsPingUnverified);
        Assert.False(vm.IsPingGood);
        Assert.True(vm.IsPingSlow);

        vm.ApplyProbeResult(new ServerProbeResult(ServerProbeStatus.Ok, 45, null));

        Assert.Equal("45 ms", vm.PingDisplay);
        Assert.True(vm.IsPingGood);
        Assert.False(vm.IsPingUnverified);
    }
}
