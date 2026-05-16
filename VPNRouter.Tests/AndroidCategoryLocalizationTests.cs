using VPNRouter.Core.Localization;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Bug-AND-009 (2026-05-16) — locks the localization contract for the
/// Android Applications tab. The Android app's category chip strip
/// renders each chip via <c>Localization.GroupDisplayName(def.Id)</c>
/// where <c>def.Id</c> matches one of the internal names used in
/// <c>AndroidCategoryDefaults.All</c> (Discord_Privacy, Messengers,
/// AI_Tools, Browsers, Work_Suite, Streaming, Gaming, Virtualization,
/// Privacy_Shell, Custom Apps).
///
/// <para>Regression risk: if someone renames an entry in
/// AndroidCategoryDefaults OR removes a case from GroupDisplayName,
/// the chip strip falls back to the internal id ("Privacy_Shell")
/// instead of the localized label ("Privacy" / "Приватность"). These
/// tests fail loudly before such a rename ships.</para>
///
/// <para>The companion live-test playbook
/// (plans/android-test-coverage-plan.md §Live-test playbook step 4)
/// covers the runtime refresh path — switch RU↔EN while on the
/// Applications tab and confirm the chips translate. This unit test
/// covers the static mapping.</para>
/// </summary>
[Trait("Category", "Unit")]
[Trait("Phase", "Phase0")]
[Trait("Layer", "Core")]
public class AndroidCategoryLocalizationTests
{
    /// <summary>Helper: snapshot Localization.Ru, flip to the requested
    /// language, evaluate the lookup, then restore. xUnit runs tests in
    /// parallel within a class by default; ToggleAndPersist mutates
    /// process-global state so we serialize via a static lock.</summary>
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

    /// <summary>Every internal category id used by AndroidCategoryDefaults
    /// (mirrored verbatim — this test project can't reference the Android
    /// assembly, so the list is duplicated). Keep in sync if either side
    /// adds a category.</summary>
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
        // The EN label should never equal the underscore-bearing internal id
        // for any of the known categories (that'd mean the case fell through
        // to the default _ → internalName branch).
        Assert.False(en.Contains('_'),
            $"EN label for {id} fell through to default branch (got '{en}')");
    }

    [Theory]
    [MemberData(nameof(KnownCategoryIds))]
    public void GroupDisplayName_HasRussianLabel(string id)
    {
        var ru = LookupAs(id, ru: true);
        Assert.False(string.IsNullOrEmpty(ru), $"RU label for {id} is empty");
        // Discord is a brand name and stays "Discord" in both languages,
        // so we don't assert RU != EN universally. We only assert that
        // the RU lookup didn't fall through to the underscored internal
        // id — which would mean the case wasn't covered.
        Assert.False(ru.Contains('_'),
            $"RU label for {id} fell through to default branch (got '{ru}')");
    }

    [Fact]
    public void GroupDisplayName_UnknownIdReturnsInternalNameVerbatim()
    {
        // Fallback contract: if a chip ever holds an id NOT in the lookup
        // (e.g., a hand-edited custom category named "MyStuff"), the
        // chip should render the raw id rather than blank.
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
