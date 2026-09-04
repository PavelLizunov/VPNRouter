using System;
using System.Linq;
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
    [InlineData("ss+v2ray://YWVzLTI1Ni1nY206cGFzc0BleGFtcGxlLmNvbToxMjM0")]
    [InlineData("shadowsocks://YWVzLTI1Ni1nY206cGFzc0BleGFtcGxlLmNvbToxMjM0")]
    [InlineData("shadowsocks+obfs://YWVzLTI1Ni1nY206cGFzc0BleGFtcGxlLmNvbToxMjM0")]
    [InlineData("hysteria2://user:pass@server.example:443")]
    [InlineData("hy2://user:pass@server.example:443")]
    [InlineData("tuic://uuid:pass@server.example:443")]
    [InlineData("wireguard://privatekey@server.example:51820")]
    [InlineData("socks://user:pass@127.0.0.1:1080")]
    [InlineData("socks4://user:pass@127.0.0.1:1080")]
    [InlineData("socks4a://user:pass@127.0.0.1:1080")]
    [InlineData("socks5://user:pass@127.0.0.1:1080")]
    [InlineData("socks5h://user:pass@127.0.0.1:1080")]
    [InlineData("snell://pass@server.example:1080")]
    [InlineData("shadowtls://pass@server.example:1080")]
    [InlineData("ssh://user:pass@server.example:22")]
    [InlineData("dns-tunnel://user:pass@example.com:443")]
    [InlineData("awg://privatekey@example.com:51820")]
    [InlineData("amneziawg://privatekey@example.com:51820")]
    [InlineData("naive://user:pass@example.com:443")]
    [InlineData("naive+quic://user:pass@example.com:443")]
    [InlineData("naive+https://user:pass@example.com:443")]
    [InlineData("tg://proxy?server=1.2.3.4&port=1080&secret=abc")]
    public void ScrubSecrets_RedactsAllProxyProtocols(string uri)
    {
        var s = CrashReporter.ScrubSecrets($"connection error: {uri}");
        Assert.Contains("[redacted]", s);
        // The URI body proper must not survive the scrub.
        Assert.DoesNotContain(uri, s);
    }

    [Fact]
    public void ScrubSecrets_RedactsTgUri()
    {
        const string input = "telegram proxy tg://proxy?server=194.87.222.111&port=1080&secret=ee123";
        var s = CrashReporter.ScrubSecrets(input);
        Assert.DoesNotContain("194.87.222.111", s);
        Assert.DoesNotContain("1080", s);
        Assert.DoesNotContain("ee123", s);
        Assert.Contains("tg://[redacted]", s);
    }

    [Fact]
    public void ScrubSecrets_StripsHttpBasicAuthCredentials()
    {
        const string input = "auth request https://user:secretpass@sub.example.com:8443/api/v1";
        var s = CrashReporter.ScrubSecrets(input);
        Assert.DoesNotContain("user:secretpass@", s);
        Assert.DoesNotContain("secretpass", s);
        Assert.DoesNotContain("/api/v1", s);
        Assert.Contains("https://sub.example.com:8443/[redacted]", s);
    }

    [Fact]
    public void ScrubSecrets_RedactsUrlWithPath()
    {
        const string input = "fetch failed: https://sub.example.com/users/abc/sub.json";
        var s = CrashReporter.ScrubSecrets(input);
        Assert.DoesNotContain("/users/abc/sub.json", s);
        Assert.Contains("https://sub.example.com/[redacted]", s);
    }

    [Fact]
    public void ScrubSecrets_RedactsQueryWithoutPath()
    {
        const string input = "request error: https://sub.example.com?token=secret123&foo=bar";
        var s = CrashReporter.ScrubSecrets(input);
        Assert.DoesNotContain("token=secret123", s);
        Assert.DoesNotContain("foo=bar", s);
        Assert.Contains("https://sub.example.com/[redacted]", s);
    }

    [Fact]
    public void ScrubSecrets_RedactsFragmentWithoutPath()
    {
        const string input = "navigation error: https://sub.example.com#secret-anchor";
        var s = CrashReporter.ScrubSecrets(input);
        Assert.DoesNotContain("secret-anchor", s);
        Assert.Contains("https://sub.example.com/[redacted]", s);
    }

    // R13-B: wgturn:// carries wireguard key material. WriteReport (crash tail)
    // and RedactLogText (diagnostics bundle) both funnel through ScrubSecrets,
    // so this shared-regex pin closes both export paths.
    [Fact]
    public void ScrubSecrets_RedactsWgturnUri()
    {
        var s = CrashReporter.ScrubSecrets("add config wgturn://abc123/xyz?k=secret failed");
        Assert.Contains("wgturn://[redacted]", s);
        Assert.DoesNotContain("secret", s);
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

    [Theory]
    [InlineData("ws://example.com/api?secret=supersecret123", "secret=[REDACTED]")]
    [InlineData("wss://example.com/api?api_key=mykey999", "api_key=[REDACTED]")]
    [InlineData("ws://example.com/api?apikey=mykey999", "apikey=[REDACTED]")]
    [InlineData("wss://example.com/api?access_token=tok12345", "access_token=[REDACTED]")]
    [InlineData("wss://example.com/api?refresh_token=tok67890", "refresh_token=[REDACTED]")]
    [InlineData("ws://example.com/api?auth_token=tok11223", "auth_token=[REDACTED]")]
    [InlineData("wss://example.com/api?client_secret=sec99887", "client_secret=[REDACTED]")]
    [InlineData("ws://example.com/api?api_secret=sec77665", "api_secret=[REDACTED]")]
    [InlineData("wss://example.com/api?private_key=key33221", "private_key=[REDACTED]")]
    [InlineData("ws://example.com/api?auth=credential_data", "auth=[REDACTED]")]
    [InlineData("wss://example.com/api?password=mypassword", "password=[REDACTED]")]
    [InlineData("ws://example.com/api?passwd=mypassword", "passwd=[REDACTED]")]
    [InlineData("ws://example.com/api?session_id=sess_abc123", "session_id=[REDACTED]")]
    [InlineData("wss://example.com/api?sig=sig_xyz789", "sig=[REDACTED]")]
    [InlineData("ws://example.com/api?signature=signature_def456", "signature=[REDACTED]")]
    [InlineData("wss://example.com/api?sid=sid_998877", "sid=[REDACTED]")]
    [InlineData("ws://example.com/api?short_id=short123", "short_id=[REDACTED]")]
    [InlineData("wss://example.com/api?key=my_key_value", "key=[REDACTED]")]
    [InlineData("ws://example.com/api?pbk=pubkey12345", "pbk=[REDACTED]")]
    [InlineData("wss://example.com/api?public_key=pubkey67890", "public_key=[REDACTED]")]
    [InlineData("ws://example.com/api?uuid=user_uuid_123", "uuid=[REDACTED]")]
    public void ScrubSecrets_RedactsSensitiveQueryParams(string input, string expectedSubstring)
    {
        var s = CrashReporter.ScrubSecrets(input);
        Assert.Contains(expectedSubstring, s);
        Assert.DoesNotContain("supersecret123", s);
        Assert.DoesNotContain("mykey999", s);
        Assert.DoesNotContain("tok12345", s);
        Assert.DoesNotContain("tok67890", s);
        Assert.DoesNotContain("tok11223", s);
        Assert.DoesNotContain("sec99887", s);
        Assert.DoesNotContain("sec77665", s);
        Assert.DoesNotContain("key33221", s);
        Assert.DoesNotContain("credential_data", s);
        Assert.DoesNotContain("mypassword", s);
        Assert.DoesNotContain("sess_abc123", s);
        Assert.DoesNotContain("sig_xyz789", s);
        Assert.DoesNotContain("signature_def456", s);
        Assert.DoesNotContain("sid_998877", s);
        Assert.DoesNotContain("short123", s);
        Assert.DoesNotContain("my_key_value", s);
        Assert.DoesNotContain("pubkey12345", s);
        Assert.DoesNotContain("pubkey67890", s);
        Assert.DoesNotContain("user_uuid_123", s);
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

    // ── OBS-2 (audit R06): bounded crash-tail regression tests ─────────────

    /// <summary>
    /// Sets up a temp DataDir with a logs/ subdirectory containing a single
    /// vpnrouter-test.log file, calls WriteReport, and returns the report text.
    /// Restores AppPaths.DataDir in all cases.
    /// </summary>
    private static string WriteReportWithLog(string logContent)
    {
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"vpnrouter-crashreporter-obs2-{Guid.NewGuid():N}");
        var previous = VPNRouter.Core.AppPaths.DataDir;
        try
        {
            VPNRouter.Core.AppPaths.OverrideDataDir(tmp);
            var logsDir = System.IO.Path.Combine(tmp, "logs");
            System.IO.Directory.CreateDirectory(logsDir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(logsDir, "vpnrouter-test.log"), logContent);

            var reportPath = CrashReporter.WriteReport(new InvalidOperationException("test crash"));
            Assert.NotNull(reportPath);
            return System.IO.File.ReadAllText(reportPath!);
        }
        finally
        {
            VPNRouter.Core.AppPaths.OverrideDataDir(previous);
            try { if (System.IO.Directory.Exists(tmp)) System.IO.Directory.Delete(tmp, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public void WriteReport_LargeLog_OnlyLast200Lines()
    {
        // 300 numbered lines; the report must contain lines 101-300 and NOT lines 1-100.
        var lines = Enumerable.Range(1, 300).Select(i => $"log-line-{i:D4}");
        var report = WriteReportWithLog(string.Join(Environment.NewLine, lines));

        Assert.Contains("log-line-0101", report);
        Assert.Contains("log-line-0300", report);
        Assert.DoesNotContain("log-line-0001", report);
        Assert.DoesNotContain("log-line-0100", report);
    }

    [Fact]
    public void WriteReport_SmallLog_AllLinesIncluded()
    {
        var report = WriteReportWithLog("alpha\nbeta\ngamma");

        Assert.Contains("alpha", report);
        Assert.Contains("beta", report);
        Assert.Contains("gamma", report);
    }

    [Fact]
    public void WriteReport_TailStillScrubbed()
    {
        // A vless:// URI in the last log line must be redacted in the report
        // (preserves P09 scrubber behavior after the OBS-2 tail change).
        const string secret = "vless://2d54442d-158f-49e2-b225-67ba1a5b77f4@194.87.222.111:443";
        var logContent = string.Join(Environment.NewLine,
            Enumerable.Range(1, 5).Select(i => $"benign line {i}")
                .Append($"dial error: {secret}"));

        var report = WriteReportWithLog(logContent);

        Assert.DoesNotContain("2d54442d-158f-49e2-b225-67ba1a5b77f4", report);
        Assert.DoesNotContain("194.87.222.111", report);
        Assert.Contains("vless://[redacted]", report);
        Assert.Contains("benign line 5", report);
    }
}
