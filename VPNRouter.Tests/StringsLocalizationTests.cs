using System.Reflection;
using VPNRouter.GUI.Localization;

namespace VPNRouter.Tests;

public class StringsLocalizationTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void AllStringProperties_NonEmpty(string lang)
    {
        Strings.Lang = lang;

        var props = typeof(Strings)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(string) && p.GetMethod?.GetParameters().Length == 0);

        foreach (var prop in props)
        {
            var value = (string?)prop.GetValue(null);
            Assert.False(string.IsNullOrEmpty(value),
                $"Strings.{prop.Name} is null or empty for lang=\"{lang}\"");
        }
    }

    [Fact]
    public void LanguageToggle_SwitchesCorrectly()
    {
        Strings.Lang = "en";
        Assert.Equal("Not connected", Strings.NotConnected);

        Strings.Lang = "ru";
        Assert.Equal("\u041d\u0435 \u043f\u043e\u0434\u043a\u043b\u044e\u0447\u0435\u043d\u043e", Strings.NotConnected); // Не подключено

        Strings.Lang = "en"; // reset
    }

    [Fact]
    public void ParameterizedStrings_NonEmpty()
    {
        foreach (var lang in new[] { "en", "ru" })
        {
            Strings.Lang = lang;

            Assert.False(string.IsNullOrEmpty(Strings.Connected("TestProfile", 1234)));
            Assert.False(string.IsNullOrEmpty(Strings.RoleFallback(2)));
            Assert.False(string.IsNullOrEmpty(Strings.AddedSkipped(3, 1)));
            Assert.False(string.IsNullOrEmpty(Strings.ConfigExists("test")));
            Assert.False(string.IsNullOrEmpty(Strings.UpdateConfirm("1.0.0")));
            Assert.False(string.IsNullOrEmpty(Strings.UpdateAvailable("Update", "1.0.0", " (5 MB)")));
            Assert.False(string.IsNullOrEmpty(Strings.TrayRunning("TestProfile")));
        }

        Strings.Lang = "en"; // reset
    }
}
