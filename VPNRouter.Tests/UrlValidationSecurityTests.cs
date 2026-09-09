using System.Threading.Tasks;
using Serilog;
using VPNRouter.App.Localization;
using VPNRouter.App.ViewModels;
using VPNRouter.App.ViewModels.FreeConfigs;
using VPNRouter.Core.Models;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

public class UrlValidationSecurityTests
{
    private static ILogger SilentLogger => new LoggerConfiguration().CreateLogger();

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/sub.txt")]
    [InlineData("javascript:alert(1)")]
    [InlineData("gopher://example.com")]
    [InlineData("not-a-url")]
    public void FreeConfigsPageViewModel_AddUserSource_RejectsInvalidOrNonHttpSchemes(string invalidUrl)
    {
        var settings = new AppSettings();
        var vm = new FreeConfigsPageViewModel(
            SilentLogger,
            _ => Task.FromResult(true),
            getSettings: () => settings,
            settingsStore: new InMemorySettingsStore());

        vm.NewUserSourceName = "Test Source";
        vm.NewUserSourceUrl = invalidUrl;

        vm.AddUserSourceCommand.Execute(null);

        Assert.Empty(vm.UserSources);
        Assert.Empty(settings.App.UserFreeSources);
        Assert.Equal(Strings.FcUserSrcInvalidUrl, vm.StatusText);
    }

    [Theory]
    [InlineData("https://example.com/configs.txt")]
    [InlineData("http://127.0.0.1:8080/vless")]
    public void FreeConfigsPageViewModel_AddUserSource_AcceptsValidHttpAndHttpsSchemes(string validUrl)
    {
        var settings = new AppSettings();
        var vm = new FreeConfigsPageViewModel(
            SilentLogger,
            _ => Task.FromResult(true),
            getSettings: () => settings,
            settingsStore: new InMemorySettingsStore());

        vm.NewUserSourceName = "Valid Source";
        vm.NewUserSourceUrl = validUrl;

        vm.AddUserSourceCommand.Execute(null);

        Assert.Single(vm.UserSources);
        Assert.Single(settings.App.UserFreeSources);
        Assert.Equal(validUrl, settings.App.UserFreeSources[0].Url);
        Assert.Equal(Strings.FcUserSrcAdded, vm.StatusText);
    }

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/sub.txt")]
    [InlineData("javascript:alert(1)")]
    [InlineData("gopher://example.com")]
    [InlineData("not-a-url")]
    public async Task MainWindowViewModel_AddSubscription_RejectsInvalidOrNonHttpSchemes(string invalidUrl)
    {
        using var vm = new MainWindowViewModel(new InMemorySettingsStore());
        vm.NewSubName = "Test Sub";
        vm.NewSubUrl = invalidUrl;

        await vm.AddSubscriptionCommand.ExecuteAsync(null);

        Assert.Empty(vm.Subscriptions);
        Assert.Equal(Strings.SubscriptionEnterUrl, vm.StatusText);
    }
}
