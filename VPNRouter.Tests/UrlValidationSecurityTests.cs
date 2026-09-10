using System;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Serilog;
using VPNRouter.App.Localization;
using VPNRouter.App.ViewModels;
using VPNRouter.App.ViewModels.FreeConfigs;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

[Collection(SubscriptionFetcherCollection.Name)]
public class UrlValidationSecurityTests
{
    [AvaloniaTheory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/sub.txt")]
    [InlineData("javascript:alert(1)")]
    [InlineData("gopher://example.com")]
    [InlineData("not-a-url")]
    [InlineData("data:text/plain;base64,SGVsbG8=")]
    [InlineData("/configs/sub.txt")]
    [InlineData("../configs/sub.txt")]
    public void FreeConfigsPageViewModel_AddUserSource_RejectsInvalidOrNonHttpSchemes(string invalidUrl)
    {
        var settings = new AppSettings();
        var store = new InMemorySettingsStore();
        using var logger = new LoggerConfiguration().CreateLogger();
        using var vm = new FreeConfigsPageViewModel(
            logger,
            _ => Task.FromResult(true),
            getSettings: () => settings,
            settingsStore: store);
        var savesBefore = store.SaveCount;

        vm.NewUserSourceName = "Test Source";
        vm.NewUserSourceUrl = invalidUrl;

        vm.AddUserSourceCommand.Execute(null);

        Assert.Empty(vm.UserSources);
        Assert.Empty(settings.App.UserFreeSources);
        Assert.Equal(savesBefore, store.SaveCount);
        Assert.Equal(Strings.FcUserSrcInvalidUrl, vm.StatusText);
    }

    [AvaloniaTheory]
    [InlineData("https://example.com/configs.txt", "Valid Source", "Valid Source")]
    [InlineData("http://127.0.0.1:8080/vless", "Valid Source", "Valid Source")]
    [InlineData("  hTtPs://example.com/configs.txt  ", "  Valid Source  ", "Valid Source")]
    [InlineData("\tHtTp://example.com/configs.txt\r\n", "Valid Source", "Valid Source")]
    [InlineData("https://example.com/configs.txt", "", "")]
    [InlineData("http://example.com/configs.txt", "   ", "")]
    public void FreeConfigsPageViewModel_AddUserSource_AcceptsValidHttpAndHttpsSchemes(
        string validUrl, string name, string expectedName)
    {
        var settings = new AppSettings();
        var store = new InMemorySettingsStore();
        using var logger = new LoggerConfiguration().CreateLogger();
        using var vm = new FreeConfigsPageViewModel(
            logger,
            _ => Task.FromResult(true),
            getSettings: () => settings,
            settingsStore: store);
        var savesBefore = store.SaveCount;

        vm.NewUserSourceName = name;
        vm.NewUserSourceUrl = validUrl;

        vm.AddUserSourceCommand.Execute(null);

        Assert.Single(vm.UserSources);
        var source = Assert.Single(settings.App.UserFreeSources);
        Assert.Equal(validUrl.Trim(), source.Url);
        Assert.Equal(expectedName, source.Name);
        Assert.True(source.Enabled);
        Assert.Equal(savesBefore + 1, store.SaveCount);
        Assert.Equal(string.Empty, vm.NewUserSourceName);
        Assert.Equal(string.Empty, vm.NewUserSourceUrl);
        Assert.Equal(Strings.FcUserSrcAdded, vm.StatusText);
    }

    [AvaloniaTheory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/sub.txt")]
    [InlineData("javascript:alert(1)")]
    [InlineData("gopher://example.com")]
    [InlineData("not-a-url")]
    [InlineData("data:text/plain;base64,SGVsbG8=")]
    [InlineData("/configs/sub.txt")]
    [InlineData("../configs/sub.txt")]
    public async Task MainWindowViewModel_AddSubscription_RejectsInvalidOrNonHttpSchemes(string invalidUrl)
    {
        var fake = new FakeHttpClient();
        var previous = SubscriptionFetcher.Http;
        SubscriptionFetcher.Http = fake;
        try
        {
            var store = new InMemorySettingsStore();
            using var vm = new MainWindowViewModel(store);
            var savesBefore = store.SaveCount;
            vm.NewSubName = "Test Sub";
            vm.NewSubUrl = invalidUrl;

            await vm.AddSubscriptionCommand.ExecuteAsync(null);

            Assert.Empty(vm.Subscriptions);
            Assert.Equal(savesBefore, store.SaveCount);
            Assert.Empty(fake.SentRequests);
            Assert.Empty(fake.SentStreamingRequests);
            Assert.Equal(Strings.SubscriptionEnterUrl, vm.StatusText);
        }
        finally
        {
            SubscriptionFetcher.Http = previous;
        }
    }

    [AvaloniaTheory]
    [InlineData("https://example.com/sub.txt", "Valid Sub", "Valid Sub")]
    [InlineData("http://127.0.0.1:8080/vless", "Valid Sub", "Valid Sub")]
    [InlineData("  hTtPs://example.com/sub.txt  ", "  Valid Sub  ", "Valid Sub")]
    [InlineData("\tHtTp://example.com/sub.txt\r\n", "Valid Sub", "Valid Sub")]
    [InlineData("https://example.com/sub.txt", "", "Sub 1")]
    [InlineData("http://example.com/sub.txt", "   ", "Sub 1")]
    public async Task MainWindowViewModel_AddSubscription_AcceptsValidHttpAndHttpsSchemes(
        string validUrl, string name, string expectedName)
    {
        const string body = "vless://uuid1@server1.example:443?security=tls&type=tcp&flow=xtls-rprx-vision#one\n";
        var expectedUri = new Uri(validUrl.Trim());
        var fake = new FakeHttpClient().Setup(expectedUri.AbsoluteUri, body);
        var previous = SubscriptionFetcher.Http;
        SubscriptionFetcher.Http = fake;
        try
        {
            var store = new InMemorySettingsStore();
            using var vm = new MainWindowViewModel(store);
            var savesBefore = store.SaveCount;
            vm.NewSubName = name;
            vm.NewSubUrl = validUrl;

            await vm.AddSubscriptionCommand.ExecuteAsync(null);

            var subscription = Assert.Single(vm.Subscriptions);
            Assert.Equal(validUrl.Trim(), subscription.Url);
            Assert.Equal(expectedName, subscription.Name);
            Assert.False(subscription.LastRefreshFailed);
            Assert.Equal(1, subscription.LastServerCount);
            Assert.Single(vm.SubscriptionServers);
            Assert.True(store.SaveCount > savesBefore);
            Assert.NotNull(store.LastSave);
            var saved = Assert.Single(store.LastSave.Value.Settings.App.Subscriptions);
            Assert.Equal(validUrl.Trim(), saved.Url);
            Assert.Equal(expectedName, saved.Name);
            Assert.True(saved.Enabled);
            Assert.Single(saved.Servers);
            var request = Assert.Single(fake.SentRequests);
            Assert.Equal(expectedUri, request.Uri);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Empty(fake.SentStreamingRequests);
            Assert.Equal(string.Empty, vm.NewSubName);
            Assert.Equal(string.Empty, vm.NewSubUrl);
        }
        finally
        {
            SubscriptionFetcher.Http = previous;
        }
    }
}
