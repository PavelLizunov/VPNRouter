#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.App.ViewModels.Internals;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Minimal baseline-compatible regression tests for overnight audit findings:
/// <list type="bullet">
///   <item><description>NIGHT-02: Custom WireGuard endpoint split/include preserves wireguard detour in synthesized DNS.</description></item>
///   <item><description>NIGHT-03: StrictDns overrides smart DNS mode to route through vpn-dns.</description></item>
///   <item><description>NIGHT-07: Phase B awaits typed Connected even when startTask completes cleanly first.</description></item>
///   <item><description>NIGHT-09: ServerHealthProbe bounds peak concurrency to 8 workers across all candidate servers.</description></item>
///   <item><description>NIGHT-10: UDP probe cancelled after datagram send rethrows OperationCanceledException.</description></item>
/// </list>
/// Note: NIGHT-11 is omitted due to >200-line fixture complexity and uninitialized MVM private field drift across baseline and fixed commits.
/// Synthetic fixture baselinecompat: statically inspected, expected RED, unexecuted (do not claim executed compilation).
/// </summary>
public sealed class NightBaselineRegressionTests
{
    private const string PlainWireGuardEndpointConfig = /*lang=json*/ """
    {
      "endpoints": [
        {
          "type": "wireguard",
          "tag": "wg",
          "address": ["10.0.0.2/32"],
          "private_key": "AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE=",
          "peers": [
            {
              "address": "198.51.100.10",
              "port": 51820,
              "public_key": "AgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgI=",
              "allowed_ips": ["0.0.0.0/0"]
            }
          ]
        }
      ],
      "outbounds": [
        { "type": "direct", "tag": "direct" },
        { "type": "direct", "tag": "dns-direct", "udp_fragment": true }
      ],
      "dns": {
        "servers": [
          { "tag": "local-https", "type": "https", "server": "1.1.1.1", "detour": "dns-direct" }
        ],
        "rules": []
      }
    }
    """;

    [Fact]
    public void Night02_CustomConfigInjector_WireGuardEndpointDns_PreservesWgDetour()
    {
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                RoutingMode = "split",
                RoutingAppsMode = "include",
                StrictDns = false,
                BypassRussianTraffic = false
            },
            Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" }
        };

        var injectedJson = CustomConfigInjector.Inject(
            PlainWireGuardEndpointConfig,
            new[] { "Firefox.exe" },
            settings);

        var root = JsonNode.Parse(injectedJson)!.AsObject();

        var routeRules = root["route"]?["rules"]?.AsArray();
        Assert.NotNull(routeRules);

        var procRoute = routeRules!.OfType<JsonObject>().FirstOrDefault(r =>
            r["process_name"] is JsonArray pa && pa.Any(p => (string?)p == "Firefox.exe"));
        Assert.NotNull(procRoute);
        Assert.Equal("wg", (string?)procRoute!["outbound"]);

        var dnsRules = root["dns"]?["rules"]?.AsArray();
        Assert.NotNull(dnsRules);

        var procDns = dnsRules!.OfType<JsonObject>().FirstOrDefault(r =>
            r["process_name"] is JsonArray pa && pa.Any(p => (string?)p == "Firefox.exe"));
        Assert.NotNull(procDns);

        var resolverTag = (string?)procDns!["server"];
        Assert.False(string.IsNullOrEmpty(resolverTag));

        var dnsServers = root["dns"]?["servers"]?.AsArray();
        Assert.NotNull(dnsServers);

        var resolverServer = dnsServers!.OfType<JsonObject>().FirstOrDefault(s =>
            (string?)s["tag"] == resolverTag);
        Assert.NotNull(resolverServer);
        Assert.Equal("wg", (string?)resolverServer!["detour"]);
    }

    [Fact]
    public void Night03_ConfigGenerator_StrictDnsOverridesSmartMode_RoutesVpnDns()
    {
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                LogLevel = "info",
                RoutingMode = "split",
                RoutingAppsMode = "include",
                StrictDns = true,
                BypassRussianTraffic = false,
                RoutingAppsInclude = new List<string> { "Firefox.exe" }
            },
            Tun = new TunSettings(),
            Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
            SingBox = new SingBoxSettings { ClashApi = "127.0.0.1:9090" },
            Vless = new VlessConfig
            {
                Servers = new List<VlessServerEntry>
                {
                    new()
                    {
                        Name = "primary",
                        Server = "198.51.100.1",
                        Port = 443,
                        Uuid = "00000000-0000-0000-0000-000000000001",
                        Security = "reality",
                        Reality = new VlessRealityConfig
                        {
                            PublicKey = "AgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgI=",
                            ShortId = "abcd"
                        }
                    }
                }
            }
        };

        var profile = new Profile
        {
            Name = "NightTestProfile",
            DnsMode = "smart",
            Processes = new List<ProcessRule>
            {
                new() { Name = "Firefox.exe", ScanPatterns = new[] { "Firefox.exe" } }
            }
        };

        var config = ConfigGenerator.Generate(
            profile,
            new[] { "Firefox.exe" },
            settings);

        var procRule = config.Dns.Rules.FirstOrDefault(r =>
            r.ProcessName != null && r.ProcessName.Contains("Firefox.exe"));
        Assert.NotNull(procRule);
        Assert.Equal("vpn-dns", procRule!.Server);
    }

    private sealed class FakeCoordinatorEvents
    {
        private Action<int>? _startedHandler;
        private Action<int>? _connectedHandler;

        public Action SubscribeStarted(Action<int> handler)
        {
            _startedHandler = handler;
            return () => _startedHandler = null;
        }

        public Action SubscribeConnected(Action<int> handler)
        {
            _connectedHandler = handler;
            return () => _connectedHandler = null;
        }

        public void FireStarted(int pid) => _startedHandler?.Invoke(pid);
        public void FireConnected(int pid) => _connectedHandler?.Invoke(pid);
    }

    [Fact]
    public async Task Night07_TwoPhaseStartCoordinator_CleanStartTask_AwaitsTypedConnected()
    {
        var fake = new FakeCoordinatorEvents();
        var startTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var coordinatorTask = TwoPhaseStartCoordinator.RunAsync(
            startTask: startTcs.Task,
            subscribeStarted: fake.SubscribeStarted,
            subscribeConnected: fake.SubscribeConnected,
            phaseABudget: TimeSpan.FromMilliseconds(500),
            phaseBBudget: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await Task.Delay(20);
        fake.FireStarted(12345);

        await Task.Delay(20);
        startTcs.TrySetResult(true);

        await Task.Delay(20);
        fake.FireConnected(12345);

        var outcome = await coordinatorTask;
        Assert.Equal(TwoPhaseStartOutcome.Connected, outcome);
    }

    [Fact]
    public async Task Night09_ServerHealthProbe_ProbeAllAsync_PeakConcurrencyBoundedToEight()
    {
        var servers = Enumerable.Range(1, 20)
            .Select(i => new VlessServerEntry
            {
                Name = $"S{i:D2}",
                Server = $"198.51.100.{i}",
                Port = 443
            })
            .ToList();

        int currentActive = 0;
        int peakActive = 0;
        var gateTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var probe = new ServerHealthProbe(probeOverride: async (s, ct) =>
        {
            var active = Interlocked.Increment(ref currentActive);
            int prev;
            do
            {
                prev = peakActive;
                if (active <= prev) break;
            } while (Interlocked.CompareExchange(ref peakActive, active, prev) != prev);

            if (active >= 8)
            {
                gateTcs.TrySetResult();
            }

            await releaseTcs.Task.ConfigureAwait(false);
            Interlocked.Decrement(ref currentActive);
            return new ServerProbeResult(ServerProbeStatus.Ok, 50, null);
        });

        var probeTask = probe.ProbeAllAsync(servers, TimeSpan.FromSeconds(5));

        List<ServerLiveness> results;
        try
        {
            await Task.WhenAny(gateTcs.Task, Task.Delay(2000));
            await Task.Delay(100);
        }
        finally
        {
            releaseTcs.TrySetResult();
            results = await probeTask;
        }

        Assert.Equal(8, peakActive);
        Assert.Equal(20, results.Count);
        Assert.All(results, r => Assert.True(r.Alive));
    }

    [Fact]
    public async Task Night10_TcpTlsProbe_ProbeUdpAsync_CancelAfterSend_ThrowsOperationCanceledException()
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(testCts.Token);
        var probeTask = TcpTlsProbe.ProbeUdpAsync("127.0.0.1", port, probeCts.Token);

        try
        {
            var received = await listener.ReceiveAsync(testCts.Token);
            Assert.NotNull(received.Buffer);

            probeCts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probeTask);
        }
        finally
        {
            probeCts.Cancel();
            try
            {
                await probeTask;
            }
            catch
            {
                // observe probeTask
            }
        }
    }
}
