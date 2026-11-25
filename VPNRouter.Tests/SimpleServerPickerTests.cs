using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using VPNRouter.App.ViewModels;
using VPNRouter.App.Views.Pages;
using VPNRouter.Core.Models;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

[Collection(SafeModeStateCollection.Name)]
public sealed class SimpleServerPickerTests
{
    private static VlessServerEntry Entry(string name, string protocol = "vless", string transport = "tcp",
        string? host = null) =>
        new()
        {
            Name = name,
            Server = host ?? $"{name}.invalid",
            Port = 443,
            Protocol = protocol,
            Security = "none",
            Uuid = "00000000-0000-4000-8000-000000000001",
            Transport = new VlessTransportConfig { Type = transport },
        };

    private static MainWindowViewModel CreateViewModel(params VlessServerEntry[] servers)
    {
        var settings = new AppSettings();
        settings.App.ConfigMode = "subscribe";
        settings.App.Language = "en";
        settings.App.Subscriptions =
        [
            new()
            {
                Name = "fixture",
                Url = "https://feed.invalid/sub",
                Enabled = true,
                Servers = [.. servers],
            }
        ];
        var store = new InMemorySettingsStore();
        store.Save(settings, AppPaths.ConfigYamlPath);
        return new MainWindowViewModel(store);
    }

    private static VlessServerEntry[] MixedCategories() =>
    [
        Entry("daily-a"),
        Entry("stealth-a", transport: "xhttp"),
        Entry("games-a", protocol: "hysteria2"),
        Entry("same", host: "daily-same.invalid"),
        Entry("same", protocol: "tuic", host: "games-same.invalid"),
        Entry("web-a", protocol: "naive"),
    ];

    [Fact]
    public void Picker_groups_rows_by_category_and_keeps_the_exact_duplicate_row()
    {
        using var vm = CreateViewModel(MixedCategories());

        Assert.Equal(
            ["Daily", "Stealth web", "Games/voice", "Web only"],
            vm.SimpleServerRows.Where(row => row.IsSimpleCategoryStart).Select(row => row.SimpleCategoryName));
        Assert.Equal(vm.SubscriptionServers.Count, vm.SimpleServerRows.Count);
        Assert.Equal(4, vm.SimpleServerRows.Count(row => row.IsSimpleCategoryStart));

        var duplicates = vm.SubscriptionServers.Where(row => row.Name == "same").ToArray();
        Assert.Equal(2, duplicates.Length);
        Assert.NotEqual(duplicates[0].Server, duplicates[1].Server);

        vm.SelectedSubscriptionServer = duplicates[1];
        Assert.Same(duplicates[1], vm.SelectedSimpleServer);

        vm.SelectedSimpleServer = duplicates[0];
        Assert.Same(duplicates[0], vm.SelectedSubscriptionServer);

        // A ListBox clears SelectedItem while its ItemsSource is rebuilt. That must not clear the real selection.
        vm.SelectedSimpleServer = null;
        Assert.Same(duplicates[0], vm.SelectedSubscriptionServer);
    }

    [AvaloniaFact]
    public void Category_headings_follow_the_language()
    {
        var previous = VPNRouter.App.Localization.Strings.Lang;
        try
        {
            using var vm = CreateViewModel(MixedCategories());
            vm.SetLanguageRussianCommand.Execute(null);

            Assert.Equal(
                ["Повседневно", "Скрытный веб", "Игры/звонки", "Только веб"],
                vm.SimpleServerRows.Where(row => row.IsSimpleCategoryStart).Select(row => row.SimpleCategoryName));
        }
        finally
        {
            VPNRouter.App.Localization.Strings.Lang = previous;
        }
    }

    [AvaloniaFact]
    public void Simple_list_binds_grouped_rows_and_the_exact_selected_server()
    {
        using var vm = CreateViewModel(MixedCategories());
        var page = new SimplePage { DataContext = vm };
        vm.OpenConfigPickerCommand.Execute(null);
        var window = new Window { Width = 520, Height = 800, Content = page };
        window.Show();
        try
        {
            window.UpdateLayout();
            var list = page.FindControl<ListBox>("SimpleServerPicker")!;
            Assert.Equal(vm.SubscriptionServers.Count, list.ItemCount);
            Assert.Equal(vm.SimpleServerRows.Count, list.ItemCount);

            var duplicates = vm.SubscriptionServers.Where(row => row.Name == "same").ToArray();
            vm.SelectedSubscriptionServer = duplicates[1];
            window.UpdateLayout();
            Assert.Same(duplicates[1], list.SelectedItem);

            vm.SelectedSimpleServer = duplicates[0];
            window.UpdateLayout();
            Assert.Same(duplicates[0], list.SelectedItem);
        }
        finally
        {
            window.Close();
        }
    }
}
