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
}
