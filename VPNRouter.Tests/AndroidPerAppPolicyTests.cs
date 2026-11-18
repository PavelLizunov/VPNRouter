using System.Text.Json;
using VPNRouter.Core.Localization;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class AndroidPerAppPolicyTests
{
    // Mirrors the service's connected record after one app was accepted by VpnService.Builder.
    private const string ConnectedInclude = """
        {"ActiveMode":"include","ActivePackages":["fixture.reader"],
         "RequestedMode":"include","RequestedPackages":["fixture.reader"]}
        """;

    [Theory]
    [InlineData("en", "Active · Selected only (1)", "After reconnect · Exclude selected (1)",
        "Next connection · Exclude selected (1)", "Active · Exclude selected (1)")]
    [InlineData("ru", "Сейчас · Только выбранные (1)", "После переподключения · Кроме выбранных (1)",
        "При подключении · Кроме выбранных (1)", "Сейчас · Кроме выбранных (1)")]
    public void SavedSwitchUsesLastConfirmedSessionUntilReconnect(string lang, string activeText,
        string pendingText, string disconnectedText, string reconnectedText)
    {
        var previousLanguage = Strings.Lang;
        try
        {
            Strings.Lang = lang;
            string[] apps = ["fixture.reader"];
            var active = JsonSerializer.Deserialize<AndroidPerAppPolicy>(ConnectedInclude)!;
            Assert.Equal(activeText, Strings.PerAppPolicySummary(active, "include", apps));
            Assert.Equal(activeText + "\n" + pendingText, Strings.PerAppPolicySummary(active, "exclude", apps));
            // A recreated view or a rejected switch reads the same successful session record.
            var reopened = JsonSerializer.Deserialize<AndroidPerAppPolicy>(ConnectedInclude)!;
            Assert.Equal(activeText + "\n" + pendingText, Strings.PerAppPolicySummary(reopened, "exclude", apps));
            // A down notification can follow the button press, or an up notification can arrive late.
            // Consult both the service record and its live instance even when an old policy is still available.
            string OnServiceUpdate(VpnConnectionState state, bool live, AndroidPerAppPolicy recorded)
            {
                var snapshot = new VpnStateSnapshot(state, null, 123, DateTimeOffset.UtcNow);
                var effective = TunnelStateResync.IsConfirmedConnected(snapshot, live) ? recorded : null;
                return Strings.PerAppPolicySummary(effective, "exclude", apps);
            }
            Assert.Equal(activeText + "\n" + pendingText, OnServiceUpdate(VpnConnectionState.Connected, true, reopened));
            Assert.Equal(disconnectedText, OnServiceUpdate(VpnConnectionState.Disconnected, false, reopened));
            Assert.Equal(disconnectedText, OnServiceUpdate(VpnConnectionState.Disconnected, true, reopened));
            Assert.Equal(disconnectedText, OnServiceUpdate(VpnConnectionState.Connected, false, reopened));
            Assert.Equal(disconnectedText, OnServiceUpdate(VpnConnectionState.Connecting, true, reopened));
            var reconnected = new AndroidPerAppPolicy("exclude", apps, "exclude", apps);
            Assert.Equal(reconnectedText, OnServiceUpdate(VpnConnectionState.Connected, true, reconnected));
            var off = Strings.PerAppPolicySummary(reconnected, "off", apps);
            Assert.StartsWith(reconnectedText + "\n", off);
            Assert.EndsWith(lang == "ru" ? "Фильтр приложений выключен" : "App filter off", off);
            // Reverting a saved edit also removes the pending indication.
            Assert.Equal(reconnectedText, Strings.PerAppPolicySummary(reconnected, "exclude", apps));
        }
        finally { Strings.Lang = previousLanguage; }
    }

    [Fact]
    public void ComparesPackageIdentitiesAndIgnoresOrderDuplicatesAndInactiveRememberedLists()
    {
        string[] apps = ["fixture.reader", "fixture.maps"];
        var active = new AndroidPerAppPolicy("include", apps, "include", apps);
        Assert.False(active.HasSavedChanges("include", ["fixture.maps", "fixture.reader", "fixture.maps"]));
        Assert.True(active.HasSavedChanges("include", ["fixture.maps", "fixture.browser"]));
        var swapped = Strings.PerAppPolicySummary(active, "include", ["fixture.maps", "fixture.browser"]);
        Assert.Contains("+ fixture.browser", swapped);
        Assert.Contains("− fixture.reader", swapped);
        Assert.True(active.HasSavedChanges("off", apps));
        // Split packages may come from the profile itself while the picker's saved mode is off.
        var profile = new AndroidPerAppPolicy("exclude", apps, "off", []);
        Assert.False(profile.HasSavedChanges("off", ["fixture.browser"]));
        // Include can succeed with just the installed subset of the requested packages.
        var partial = new AndroidPerAppPolicy("include", ["fixture.reader"], "include", apps);
        Assert.Contains("(1)", Strings.PerAppPolicySummary(partial, "include", apps));
        Assert.False(partial.HasSavedChanges("include", apps));
    }
}
