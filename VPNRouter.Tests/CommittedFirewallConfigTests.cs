using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Platform.Linux;
using VPNRouter.Core.Platform.macOS;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Architectural and contract tests for the <see cref="ICommittedFirewallConfig"/> capability
/// introduced for NIGHT-05 runtime freshness.
/// </summary>
public sealed class CommittedFirewallConfigTests
{
    [Fact]
    public void Interface_ContractAndAccessibility_IsNarrowAndInternal()
    {
        var interfaceType = typeof(ICommittedFirewallConfig);

        Assert.True(interfaceType.IsNotPublic, "ICommittedFirewallConfig must be internal, not public.");
        Assert.True(interfaceType.IsInterface, "ICommittedFirewallConfig must be an interface.");

        var method = interfaceType.GetMethod("UpdateCommittedConfig", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
        Assert.Equal(typeof(void), method!.ReturnType);

        var parameters = method.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal("configJson", parameters[0].Name);
        Assert.Equal(typeof(string), parameters[0].ParameterType);
        Assert.Equal("enabledForFullTunnel", parameters[1].Name);
        Assert.Equal(typeof(bool), parameters[1].ParameterType);
    }

    [Fact]
    public void UnixManagers_ImplementCapabilityExplicitly_ForwardingInternalMethod()
    {
        // Linux
        Assert.True(typeof(ICommittedFirewallConfig).IsAssignableFrom(typeof(LinuxFirewallManager)),
            "LinuxFirewallManager must implement ICommittedFirewallConfig.");
        // Public method should not be exposed on class
        Assert.Null(typeof(LinuxFirewallManager).GetMethod("UpdateCommittedConfig", BindingFlags.Public | BindingFlags.Instance));
        // Internal method must exist
        var linuxInternalMethod = typeof(LinuxFirewallManager).GetMethod(
            "UpdateCommittedConfig", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(linuxInternalMethod);
        Assert.True(linuxInternalMethod!.IsAssembly, "LinuxFirewallManager.UpdateCommittedConfig must be internal.");

        // macOS
        Assert.True(typeof(ICommittedFirewallConfig).IsAssignableFrom(typeof(MacFirewallManager)),
            "MacFirewallManager must implement ICommittedFirewallConfig.");
        // Public method should not be exposed on class
        Assert.Null(typeof(MacFirewallManager).GetMethod("UpdateCommittedConfig", BindingFlags.Public | BindingFlags.Instance));
        // Internal method must exist
        var macInternalMethod = typeof(MacFirewallManager).GetMethod(
            "UpdateCommittedConfig", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(macInternalMethod);
        Assert.True(macInternalMethod!.IsAssembly, "MacFirewallManager.UpdateCommittedConfig must be internal.");
    }

    [Fact]
    public void WindowsAndNullManagers_Unaffected_DoNotImplementCapability()
    {
        Assert.False(typeof(ICommittedFirewallConfig).IsAssignableFrom(typeof(FirewallManager)),
            "Windows FirewallManager must NOT implement ICommittedFirewallConfig (Windows per-process rules unaffected).");
#if !PLATFORM_WINDOWS
        Assert.False(typeof(ICommittedFirewallConfig).IsAssignableFrom(typeof(NullFirewallManager)),
            "NullFirewallManager must NOT implement ICommittedFirewallConfig.");
#endif
    }

    [Fact]
    public void LinuxFirewallManager_CommittedMethod_NeverReadsCurrentConfigPath()
    {
        // Set _currentConfigPath to a non-existent path
        var nonExistentPath = Path.Combine(Path.GetTempPath(), $"non-existent-{Guid.NewGuid():N}.json");
        var fake = new FakeProcessRunner();
        var sut = new LinuxFirewallManager(
            logger: null,
            runner: fake,
            currentConfigPath: nonExistentPath,
            markerPath: Path.Combine(Path.GetTempPath(), $"marker-{Guid.NewGuid():N}.marker"),
            hostResolver: null,
            rulesetPath: Path.Combine(Path.GetTempPath(), $"ruleset-{Guid.NewGuid():N}.conf"));

        var committedJson = """
        {
          "outbounds": [
            { "type": "vless", "tag": "proxy", "server": "192.0.2.1" }
          ]
        }
        """;

        // Must succeed without touching nonExistentPath
        ((ICommittedFirewallConfig)sut).UpdateCommittedConfig(committedJson, enabledForFullTunnel: true);
        Assert.Equal(new[] { "192.0.2.1" }, sut.ServerIps);
    }

    [Fact]
    public void MacFirewallManager_CommittedMethod_NeverReadsCurrentConfigPath()
    {
        var nonExistentPath = Path.Combine(Path.GetTempPath(), $"non-existent-{Guid.NewGuid():N}.json");
        var fake = new FakeProcessRunner();
        var sut = new MacFirewallManager(
            logger: null,
            runner: fake,
            currentConfigPath: nonExistentPath,
            markerPath: Path.Combine(Path.GetTempPath(), $"marker-{Guid.NewGuid():N}.marker"),
            hostResolver: null,
            pfConfPath: Path.Combine(Path.GetTempPath(), $"pf-{Guid.NewGuid():N}.conf"),
            rulesPath: Path.Combine(Path.GetTempPath(), $"rules-{Guid.NewGuid():N}.conf"),
            mainConfPath: Path.Combine(Path.GetTempPath(), $"main-{Guid.NewGuid():N}.conf"));

        var committedJson = """
        {
          "outbounds": [
            { "type": "vless", "tag": "proxy", "server": "192.0.2.2" }
          ]
        }
        """;

        // Must succeed without touching nonExistentPath
        ((ICommittedFirewallConfig)sut).UpdateCommittedConfig(committedJson, enabledForFullTunnel: true);
        Assert.Equal(new[] { "192.0.2.2" }, sut.ServerIps);
    }

    [Fact]
    public void ParseServerIps_ExtractsOutboundsAndWireguardPeers_BothV4AndV6()
    {
        var fake = new FakeProcessRunner();
        var linuxSut = new LinuxFirewallManager(runner: fake);
        var macSut = new MacFirewallManager(runner: fake);

        var configJson = """
        {
          "outbounds": [
            { "type": "vless", "tag": "proxy", "server": "198.51.100.1" },
            { "type": "shadowsocks", "tag": "ss", "server": "2001:db8::1" }
          ],
          "endpoints": [
            {
              "type": "wireguard",
              "address": [ "172.16.0.2/32" ],
              "peers": [
                { "address": "198.51.100.2", "allowed_ips": [ "0.0.0.0/0" ] },
                { "address": "2001:db8::2" }
              ]
            }
          ]
        }
        """;

        var linuxIps = linuxSut.ParseServerIps(configJson);
        var macIps = macSut.ParseServerIps(configJson);

        var expected = new[] { "198.51.100.1", "2001:db8::1", "198.51.100.2", "2001:db8::2" };

        Assert.Equal(expected, linuxIps);
        Assert.Equal(expected, macIps);
    }
}
