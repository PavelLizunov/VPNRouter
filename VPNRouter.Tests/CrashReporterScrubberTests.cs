using System;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// v2.32.0 (AND-CRASH-HOOK, 2026-05-08) — pin contract for
/// <see cref="CrashReporter.ScrubSecrets"/>. Crash reports are written
/// to disk and may be shared with support, so any new code path that
/// regresses the scrub patterns silently leaks the user's vless URI,
/// subscription URL or Reality public key. These tests enforce the
/// minimum redaction surface.
/// </summary>
public sealed class CrashReporterScrubberTests
{
    [Fact]
    public void ScrubSecrets_RedactsVlessUri()
    {
        const string input =
            "System.Exception: failed to dial vless://2d54442d-158f-49e2-b225-67ba1a5b77f4@194.87.222.111:443?security=reality";
        var s = CrashReporter.ScrubSecrets(input);
        Assert.DoesNotContain("2d54442d-158f-49e2-b225-67ba1a5b77f4", s);
        Assert.DoesNotContain("194.87.222.111", s);
        Assert.Contains("vless://[redacted]", s);
    }

    [Theory]
    [InlineData("vmess://eyJhZGQiOiAiMS4xLjEuMSJ9")]
    [InlineData("trojan://password@example.com:443")]
    [InlineData("ss://YWVzLTI1Ni1nY206cGFzc0BleGFtcGxlLmNvbToxMjM0")]
    [InlineData("hysteria2://user:pass@server.example:443")]
    [InlineData("tuic://uuid:pass@server.example:443")]
    public void ScrubSecrets_RedactsAllProxyProtocols(string uri)
    {
        var s = CrashReporter.ScrubSecrets($"connection error: {uri}");
        Assert.Contains("[redacted]", s);
        // The URI body proper must not survive the scrub.
        Assert.DoesNotContain(uri, s);
    }

    [Fact]
    public void ScrubSecrets_KeepsHttpHostButRedactsPath()
    {
        const string input = "fetch failed: https://sub.example.com/users/abc/sub.json";
        var s = CrashReporter.ScrubSecrets(input);
        Assert.Contains("https://sub.example.com", s);
        Assert.Contains("/[redacted]", s);
        Assert.DoesNotContain("/users/abc/sub.json", s);
    }

    [Fact]
    public void ScrubSecrets_RedactsBareUuid()
    {
        const string input = "user UUID 12345678-1234-1234-1234-123456789abc not found";
        var s = CrashReporter.ScrubSecrets(input);
        Assert.DoesNotContain("12345678-1234-1234-1234-123456789abc", s);
        Assert.Contains("<uuid>", s);
    }

    [Fact]
    public void ScrubSecrets_RedactsRealityPublicKey()
    {
        // 43-char base64url Reality pbk
        const string pbk = "DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU";
        var s = CrashReporter.ScrubSecrets($"reality pbk={pbk} sid=78ca7952");
        Assert.DoesNotContain(pbk, s);
        Assert.Contains("<key>", s);
    }

    [Fact]
    public void ScrubSecrets_HandlesNullAndEmpty()
    {
        Assert.Equal(string.Empty, CrashReporter.ScrubSecrets(null));
        Assert.Equal(string.Empty, CrashReporter.ScrubSecrets(string.Empty));
    }

    [Fact]
    public void ScrubSecrets_LeavesShortBenignTextAlone()
    {
        const string input = "TUN setup failed: stack=system, mtu=1500";
        var s = CrashReporter.ScrubSecrets(input);
        Assert.Equal(input, s);
    }

    // ── OBS-1: token= query-param scrubbing (ws/wss clash_api secret) ────

    [Fact]
    public void ScrubSecrets_RedactsWsTokenUri()
    {
        const string input = "connected ws://127.0.0.1:9090/logs?level=info&token=abc123";
        var s = CrashReporter.ScrubSecrets(input);
        Assert.DoesNotContain("token=abc123", s);
        Assert.Contains("ws://127.0.0.1:9090/logs", s);
        Assert.Contains("token=[REDACTED]", s);
    }

    [Fact]
    public void ScrubSecrets_RedactsWssTokenUri()
    {
        const string input = "connected wss://127.0.0.1:9090/logs?level=info&token=deadbeef00112233";
        var s = CrashReporter.ScrubSecrets(input);
        Assert.DoesNotContain("deadbeef00112233", s);
        Assert.Contains("wss://127.0.0.1:9090/logs", s);
        Assert.Contains("token=[REDACTED]", s);
    }

    [Fact]
    public void ScrubSecrets_RedactsTokenAsFirstQueryParam()
    {
        const string input = "dial ws://127.0.0.1:9090/logs?token=abc123&level=info";
        var s = CrashReporter.ScrubSecrets(input);
        Assert.DoesNotContain("token=abc123", s);
        Assert.Contains("?token=[REDACTED]", s);
        Assert.Contains("&level=info", s);
    }

    [Fact]
    public void OverrideDataDir_RoundTripsThroughCrashesPath()
    {
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"vpnrouter-crashreporter-tests-{Guid.NewGuid():N}");
        try
        {
            // Must capture the previous resolved value so the rest of the
            // test suite (other classes touching AppPaths.DataDir) sees
            // the same view it had before.
            var previous = VPNRouter.Core.AppPaths.DataDir;

            VPNRouter.Core.AppPaths.OverrideDataDir(tmp);
            Assert.Equal(tmp, VPNRouter.Core.AppPaths.DataDir);
            Assert.Equal(System.IO.Path.Combine(tmp, "logs"),
                VPNRouter.Core.AppPaths.LogsDir);

            // Restore so other tests keep their assumptions.
            VPNRouter.Core.AppPaths.OverrideDataDir(previous);
            Assert.Equal(previous, VPNRouter.Core.AppPaths.DataDir);
        }
        finally
        {
            try { if (System.IO.Directory.Exists(tmp)) System.IO.Directory.Delete(tmp, recursive: true); }
            catch { }
        }
    }
}
