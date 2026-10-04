using System;
using System.Linq;
using VPNRouter.Core.Localization;
using Xunit;

namespace VPNRouter.Tests;

public sealed class FailoverStringsTests
{
    private static bool HasCyrillic(string s) => s.Any(c => c is >= 'Ѐ' and <= 'ӿ');

    [Fact]
    public void EveryFailoverMessage_ExistsInBothLanguages_AndEnglishHasNoCyrillic()
    {
        var pairs = new (string Ru, string En)[]
        {
            (Strings.FailoverSwitching("srv", "ru"), Strings.FailoverSwitching("srv", "en")),
            (Strings.FailoverCustomConfigUnusableFor("ru"), Strings.FailoverCustomConfigUnusableFor("en")),
            (Strings.FailoverManualServerSilent("srv", "why", "ru"), Strings.FailoverManualServerSilent("srv", "why", "en")),
            (Strings.FailoverAllServersDown(3, "ru"), Strings.FailoverAllServersDown(3, "en")),
            (Strings.FailoverNoOtherInSubscriptionFor("ru"), Strings.FailoverNoOtherInSubscriptionFor("en")),
            (Strings.FailoverNoOtherInListFor("ru"), Strings.FailoverNoOtherInListFor("en")),
        };

        foreach (var (ru, en) in pairs)
        {
            Assert.True(HasCyrillic(ru), ru);
            Assert.False(HasCyrillic(en), en);
            Assert.NotEqual(ru, en);
        }
    }

    [Fact]
    public void ParameterisedMessages_CarryTheirArguments()
    {
        foreach (var lang in new[] { "ru", "en" })
        {
            Assert.Contains("srv-7", Strings.FailoverSwitching("srv-7", lang));
            Assert.Contains("3", Strings.FailoverAllServersDown(3, lang));
            var silent = Strings.FailoverManualServerSilent("srv-7", "timeout", lang);
            Assert.Contains("srv-7", silent);
            Assert.Contains("timeout", silent);
        }
    }
}
