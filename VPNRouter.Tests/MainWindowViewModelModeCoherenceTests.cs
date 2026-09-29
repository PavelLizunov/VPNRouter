using System.Reflection;
using Avalonia.Headless.XUnit;
using VPNRouter.App.Localization;
using VPNRouter.App.ViewModels;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class MainWindowViewModelModeCoherenceTests
{
    [AvaloniaFact]
    public void Subscribe_CustomPeek_KeepsRuntimeSubscriptionBehavior()
    {
        var store = new InMemorySettingsStore();
        store.Save(BuildSubscribeSettings(), AppPaths.ConfigYamlPath);

        using var vm = new MainWindowViewModel(store);
        Assert.True(vm.IsSubscribeMode);

        vm.SelectedTabIndex = 0;
        vm.SelectedServerModeIndex = 1;

        Assert.False(vm.IsSubscribeMode);
        Assert.False(vm.IsVlessMode);
        Assert.Equal("subscribe", store.Load(AppPaths.ConfigYamlPath).App.ConfigMode);
        Assert.StartsWith(Strings.SmpCfgSubscribe, vm.SimpleConfigModeSummary, StringComparison.Ordinal);

        Invoke(vm, "StartSubRefreshTimer");
        Assert.NotNull(GetField<System.Threading.Timer>(vm, "_subRefreshTimer"));
        Invoke(vm, "StopSubRefreshTimer");

        vm.IsSimpleMode = false;
        vm.NavigateToVpnCommand.Execute(null);
        Assert.Equal(1, vm.SelectedTabIndex);
    }

    private static AppSettings BuildSubscribeSettings()
    {
        var settings = new AppSettings().EnsureSane();
        settings.App.ConfigMode = "subscribe";
        settings.App.ActiveSubscriptionServer = "primary";
        settings.App.Subscriptions.Add(new SubscriptionEntry
        {
            Name = "test",
            Url = "https://example.invalid/subscription",
            Enabled = true,
            Servers =
            [
                new VlessServerEntry
                {
                    Name = "primary",
                    Server = "192.0.2.1",
                    Port = 443,
                    Uuid = "00000000-0000-0000-0000-000000000001",
                },
            ],
        });
        return settings;
    }

    private static void Invoke(MainWindowViewModel vm, string methodName)
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(vm, null);
    }

    private static T? GetField<T>(MainWindowViewModel vm, string fieldName) where T : class
    {
        var field = typeof(MainWindowViewModel).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!.GetValue(vm) as T;
    }

    private static string ReadSource(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VPNRouter.sln")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        var path = Path.Combine(directory!.FullName, "VPNRouter.App", "ViewModels", fileName);
        Assert.True(File.Exists(path), $"Source file not found: {path}");
        return File.ReadAllText(path);
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start marker not found: {startMarker}");
        Assert.True(end > start, $"End marker not found after {startMarker}: {endMarker}");
        return source[start..end];
    }
}
