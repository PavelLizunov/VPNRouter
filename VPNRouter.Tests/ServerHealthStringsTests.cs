using System;
using VPNRouter.Core.Localization;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Pins the RU/EN server-health copy (Strings.ServerHealth.cs) against the audit's
/// wording rules (plans/audit-import-2026-07-09/01-audit-vector-map-batch1.md):
/// ping/SSH/TCP liveness must never read as "the server works"; the RU-block and
/// blocked-target-canary explanations keep their exact audit phrasing. Audit
/// regression 6: "UI string test pins wording: ping/SSH do not prove VPN protocol works".
/// </summary>
public class ServerHealthStringsTests
{
    private static string WithLang(string lang, Func<string> get)
    {
        var prev = Strings.Lang;
        try { Strings.Lang = lang; return get(); }
        finally { Strings.Lang = prev; }
    }

    // ── Audit regression 6: the load-bearing RU wording ─────────────────────

    [Fact]
    public void ProtocolBlocked_RuLabel_IsTheExactAuditWording()
        => Assert.Equal("Хост доступен, но VPN-протокол не прошёл проверку",
            WithLang("ru", () => Strings.HealthVerdictLabel(ServerHealthVerdict.ProtocolHandshakeBlockedLikely)));

    [Fact]
    public void TcpOnly_RuLabel_NeverClaimsTheServerWorks()
    {
        // Avoid "Сервер работает" when only ping/TCP passed — TCP-only is untested, not working.
        var label = WithLang("ru", () => Strings.HealthVerdictLabel(ServerHealthVerdict.TcpOpenProtocolUntested));
        Assert.DoesNotContain("Сервер работает", label);
        Assert.DoesNotContain("работает", label, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("не проверен", label);
    }

    [Fact]
    public void Healthy_RuLabel_SaysWorksViaVpn()
        => Assert.Equal("Работает через VPN",
            WithLang("ru", () => Strings.HealthVerdictLabel(ServerHealthVerdict.Healthy)));

    // ── RU-block warning (DPI/TSPU) — verbatim audit copy ───────────────────

    [Fact]
    public void RuBlockWarning_Ru_KeepsTheAuditPhrasing()
    {
        var s = WithLang("ru", () => Strings.HealthRuBlockWarning);
        Assert.StartsWith("Сервер доступен по сети, но VPN-протокол не проходит.", s);
        Assert.Contains("DPI/ТСПУ", s);
        Assert.Contains("Ping/SSH", s);
        Assert.Contains("VLESS/Reality/AWG/HY2", s);
        Assert.Contains("XHTTP/gRPC/Naive/HY2/AWG 2.0", s);
    }

    [Fact]
    public void RuBlockWarning_En_ExplainsPingSshDoNotProveProtocol()
    {
        var s = WithLang("en", () => Strings.HealthRuBlockWarning);
        Assert.Contains("Ping/SSH may still work", s);
        Assert.Contains("VLESS/Reality/AWG/HY2", s);
    }

    // ── Blocked-target canary UX — verbatim audit copy ──────────────────────

    [Fact]
    public void CanaryFailedWarning_Ru_KeepsTheAuditPhrasing()
    {
        var s = WithLang("ru", () => Strings.HealthCanaryFailedWarning);
        Assert.StartsWith("VPN подключился, но проверка заблокированного сервиса не прошла.", s);
        Assert.Contains("Обычный интернет через VPN работает", s);
        Assert.Contains("Попробуйте другой сервер, ASN/хостинг или транспорт.", s);
    }

    [Fact]
    public void YoutubeCaveat_SaysUsefulButNotAbsolute_BothLangs()
    {
        Assert.Contains("но не абсолютная", WithLang("ru", () => Strings.HealthYoutubeCanaryCaveat));
        Assert.Contains("not an absolute", WithLang("en", () => Strings.HealthYoutubeCanaryCaveat));
    }

    // ── Coverage: every verdict has a distinct RU + EN label ────────────────

    [Fact]
    public void EveryVerdict_HasNonEmptyDistinctLabels_InBothLanguages()
    {
        foreach (var verdict in Enum.GetValues<ServerHealthVerdict>())
        {
            var ru = WithLang("ru", () => Strings.HealthVerdictLabel(verdict));
            var en = WithLang("en", () => Strings.HealthVerdictLabel(verdict));
            Assert.False(string.IsNullOrWhiteSpace(ru), $"RU label missing for {verdict}");
            Assert.False(string.IsNullOrWhiteSpace(en), $"EN label missing for {verdict}");
            Assert.NotEqual(ru, en); // a real translation, not a copy-paste
        }
    }

    [Fact]
    public void AsnHighRisk_And_PartialNote_AreLocalized()
    {
        Assert.Contains("ASN", WithLang("ru", () => Strings.HealthAsnHighRisk));
        Assert.Contains("ASN", WithLang("en", () => Strings.HealthAsnHighRisk));
        Assert.Contains("частично", WithLang("ru", () => Strings.HealthCanaryPartialNote));
        Assert.Contains("partially", WithLang("en", () => Strings.HealthCanaryPartialNote));
    }
}
