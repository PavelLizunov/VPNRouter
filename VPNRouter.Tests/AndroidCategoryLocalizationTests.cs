using VPNRouter.Core.Localization;
using Xunit;

namespace VPNRouter.Tests;

[Trait("Category", "Unit")]
[Trait("Phase", "Phase0")]
[Trait("Layer", "Core")]
public class AndroidCategoryLocalizationTests
{
    private static readonly object Sync = new();

    private static string LookupAs(string internalId, bool ru)
    {
        lock (Sync)
        {
            var prev = Strings.Lang;
            try
            {
                Strings.Lang = ru ? "ru" : "en";
                return Strings.GroupDisplayName(internalId);
            }
            finally
            {
                Strings.Lang = prev;
            }
        }
    }

    public static IEnumerable<object[]> KnownCategoryIds => new[]
    {
        new object[] { "Discord_Privacy" },
        new object[] { "Messengers" },
        new object[] { "AI_Tools" },
        new object[] { "Browsers" },
        new object[] { "Work_Suite" },
        new object[] { "Streaming" },
        new object[] { "Gaming" },
        new object[] { "Virtualization" },
        new object[] { "Privacy_Shell" },
        new object[] { "Custom Apps" },
    };

    [Theory]
    [MemberData(nameof(KnownCategoryIds))]
    public void GroupDisplayName_HasEnglishLabel(string id)
    {
        var en = LookupAs(id, ru: false);
        Assert.False(string.IsNullOrEmpty(en), $"EN label for {id} is empty");
        Assert.False(en.Contains('_'),
            $"EN label for {id} fell through to default branch (got '{en}')");
    }

    [Theory]
    [MemberData(nameof(KnownCategoryIds))]
    public void GroupDisplayName_HasRussianLabel(string id)
    {
        var ru = LookupAs(id, ru: true);
        Assert.False(string.IsNullOrEmpty(ru), $"RU label for {id} is empty");
        Assert.False(ru.Contains('_'),
            $"RU label for {id} fell through to default branch (got '{ru}')");
    }

    [Fact]
    public void GroupDisplayName_UnknownIdReturnsInternalNameVerbatim()
    {
        Assert.Equal("MyStuff", Strings.GroupDisplayName("MyStuff"));
        Assert.Equal("", Strings.GroupDisplayName(""));
    }

    [Theory]
    [InlineData("Discord_Privacy", "Discord", "Discord")]
    [InlineData("Browsers",        "Browsers", "Браузеры")]
    [InlineData("Work_Suite",      "Work",     "Работа")]
    [InlineData("Messengers",      "Messengers", "Мессенджеры")]
    [InlineData("AI_Tools",        "AI tools", "AI-инструменты")]
    [InlineData("Streaming",       "Streaming", "Стриминг")]
    [InlineData("Gaming",          "Gaming",   "Игры")]
    [InlineData("Virtualization",  "Virtualization", "Виртуализация")]
    [InlineData("Privacy_Shell",   "Privacy",  "Приватность")]
    [InlineData("Custom Apps",     "Custom",   "Свои")]
    public void GroupDisplayName_LocksCanonicalTranslations(string id, string en, string ru)
    {
        Assert.Equal(en, LookupAs(id, ru: false));
        Assert.Equal(ru, LookupAs(id, ru: true));
    }
}
