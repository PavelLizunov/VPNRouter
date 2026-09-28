using System;
using VPNRouter.Core.Localization;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public class ServerHealthStringsTests
{
    private static string WithLang(string lang, Func<string> get)
    {
        var prev = Strings.Lang;
        try { Strings.Lang = lang; return get(); }
        finally { Strings.Lang = prev; }
    }

    [Fact]
    public void ProtocolBlocked_RuLabel_IsTheExactAuditWording()
        => Assert.Equal("Хост доступен, но VPN-протокол не прошёл проверку",
            WithLang("ru", () => Strings.HealthVerdictLabel(ServerHealthVerdict.ProtocolHandshakeBlockedLikely)));

    [Fact]
    public void TcpOnly_RuLabel_NeverClaimsTheServerWorks()
    {
        var label = WithLang("ru", () => Strings.HealthVerdictLabel(ServerHealthVerdict.TcpOpenProtocolUntested));
        Assert.DoesNotContain("Сервер работает", label);
        Assert.DoesNotContain("работает", label, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("не проверен", label);
    }

    [Fact]
    public void Healthy_RuLabel_SaysWorksViaVpn()
        => Assert.Equal("Работает через VPN",
            WithLang("ru", () => Strings.HealthVerdictLabel(ServerHealthVerdict.Healthy)));

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

    [Fact]
    public void EveryVerdict_HasNonEmptyDistinctLabels_InBothLanguages()
    {
        foreach (var verdict in Enum.GetValues<ServerHealthVerdict>())
        {
            var ru = WithLang("ru", () => Strings.HealthVerdictLabel(verdict));
            var en = WithLang("en", () => Strings.HealthVerdictLabel(verdict));
            Assert.False(string.IsNullOrWhiteSpace(ru), $"RU label missing for {verdict}");
            Assert.False(string.IsNullOrWhiteSpace(en), $"EN label missing for {verdict}");
            Assert.NotEqual(ru, en);
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
