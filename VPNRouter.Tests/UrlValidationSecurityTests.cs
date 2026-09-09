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
    [InlineData("file:///etc/passwd")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("ftp://example.com/source.txt")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/plain;base64,SGVsbG8=")]
    [InlineData("invalid-url-format")]
    public void AddUserSource_RejectsNonHttpAndInvalidUrls(string invalidUrl)
    {
        var settings = new AppSettings();
        var vm = new FreeConfigsPageViewModel(
            SilentLogger,
            entry => Task.FromResult(true),
            getSettings: () => settings,
            settingsStore: new InMemorySettingsStore());

        vm.NewUserSourceUrl = invalidUrl;
        vm.AddUserSourceCommand.Execute(null);

        Assert.Equal(Strings.FcUserSrcInvalidUrl, vm.StatusText);
        Assert.Empty(vm.UserSources);
        Assert.Empty(settings.App.UserFreeSources);
    }

    [Theory]
    [InlineData("http://example.com/configs.txt")]
    [InlineData("https://raw.githubusercontent.com/user/repo/main/vless.txt")]
    public void AddUserSource_AcceptsHttpAndHttpsUrls(string validUrl)
    {
        var settings = new AppSettings();
        var vm = new FreeConfigsPageViewModel(
            SilentLogger,
            entry => Task.FromResult(true),
            getSettings: () => settings,
            settingsStore: new InMemorySettingsStore());

        vm.NewUserSourceUrl = validUrl;
        vm.AddUserSourceCommand.Execute(null);

        Assert.Equal(Strings.FcUserSrcAdded, vm.StatusText);
        Assert.Single(vm.UserSources);
        Assert.Equal(validUrl, vm.UserSources[0].Url);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("ftp://example.com/sub")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a url")]
    public async Task AddSubscriptionAsync_RejectsNonHttpAndInvalidUrls(string invalidUrl)
    {
        using var vm = new MainWindowViewModel(new InMemorySettingsStore());

        vm.NewSubUrl = invalidUrl;
        await vm.AddSubscriptionCommand.ExecuteAsync(null);

        Assert.Empty(vm.Subscriptions);
    }
}
