#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

[Collection(SubscriptionFetcherCollection.Name)]
public class DesktopSubscriptionCoreVersionTests
{
    [Theory]
    [InlineData("sing-box version 1.14.4-vpnctl.3\r\nTags: with_awg", "1.14.4-vpnctl.3")]
    [InlineData("sing-box version 1.15.0-vpnctl.12\n", "1.15.0-vpnctl.12")]
    [InlineData("sing-box version 1.14.4\n", null)]
    [InlineData("sing-box version 1.14.4-vpnctl.03\n", null)]
    [InlineData("sing-box version 1.14.4-vpnctl.0\n", null)]
    [InlineData("sing-box version 1.14.4-vpnctl.3 custom\n", null)]
    [InlineData("", null)]
    public void ReportsOnlyCanonicalManagedVersion(string output, string? expected)
    {
        Assert.Equal(expected, SingBoxFeatures.ParseManagedCoreVersion(output));
    }

    [Theory]
    [InlineData("https://provider.example/api/v1/app/config/test-device", true)]
    [InlineData("https://provider.example/vpn/api/v1/app/config/test-device", true)]
    [InlineData("http://provider.example/api/v1/app/config/test-device", false)]
    [InlineData("https://provider.example/sub/test-token", false)]
    public async Task SelectedHeadlessRuntimeIsReportedOnlyToHttpsAppConfig(string url, bool expected)
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "The pinned headless runtime is Linux-only");
        var runtime = Environment.GetEnvironmentVariable("VPNROUTER_TEST_VERIFIER_RUNTIME");
        Assert.SkipWhen(string.IsNullOrEmpty(runtime), "Requires the pinned sing-box runtime");
        Assert.True(File.Exists(runtime));

        using var stream = File.OpenRead(runtime!);
        var sha = Convert.ToHexString(SHA256.HashData(stream));
        using var scope = SingBoxRuntimePolicy.EnterScope(SingBoxRuntimePolicy.ForTestFile(runtime!, sha));
        var fake = new FakeHttpClient().Setup(url, string.Empty);
        var previous = SubscriptionFetcher.Http;
        SubscriptionFetcher.Http = fake;
        try
        {
            await SubscriptionFetcher.FetchAsync(url, ct: TestContext.Current.CancellationToken);
            var headers = Assert.Single(fake.SentRequests).Headers;
            Assert.NotNull(headers);
            Assert.Equal(expected, headers.ContainsKey("X-VPNRouter-Core-Version"));
            Assert.Equal(expected, headers.ContainsKey("X-VPNRouter-Platform"));
            if (expected)
            {
                Assert.Equal("1.14.4-vpnctl.3", headers["X-VPNRouter-Core-Version"]);
                Assert.Equal("linux", headers["X-VPNRouter-Platform"]);
            }
        }
        finally
        {
            SubscriptionFetcher.Http = previous;
        }
    }
}
