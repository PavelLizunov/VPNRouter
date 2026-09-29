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
