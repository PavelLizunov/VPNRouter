using System.Text.Json;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.FreeConfigs;

namespace VPNRouter.Tests;

public sealed class DeepVerifierDnsPrivacyTests
{
    [Fact]
    public void TemporaryConfigsUseEncryptedDirectBootstrapDns()
    {
        var entry = VlessDeepVerifierTests.CleanVlessEntry();
        var configs = new[]
        {
            VlessDeepVerifier.BuildSingleOutboundConfig(entry, 10808, 9090),
            FreeConfigDeepVerifier.BuildSingleOutboundConfig(entry, 10809, 9091)
        };

        foreach (var config in configs)
        {
            using var document = JsonDocument.Parse(config);
            var server = document.RootElement
                .GetProperty("dns")
                .GetProperty("servers")[0];

            Assert.Equal("https", server.GetProperty("type").GetString());
            Assert.Equal("1.1.1.1", server.GetProperty("server").GetString());
            Assert.Equal("/dns-query", server.GetProperty("path").GetString());
            Assert.Equal("dns-direct-out", server.GetProperty("detour").GetString());
        }
    }
}
