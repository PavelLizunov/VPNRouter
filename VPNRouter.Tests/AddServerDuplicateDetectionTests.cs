#nullable enable

using System.Reflection;
using Avalonia.Headless.XUnit;
using VPNRouter.App.ViewModels;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class AddServerDuplicateDetectionTests : IDisposable
{
    private readonly bool? _previousXhttpOverride;

    public AddServerDuplicateDetectionTests()
    {
        _previousXhttpOverride = SingBoxFeatures.OverrideXhttp;
        SingBoxFeatures.OverrideXhttp = true;
    }

    public void Dispose() => SingBoxFeatures.OverrideXhttp = _previousXhttpOverride;

    private static void InvokeAddServer(MainWindowViewModel vm)
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            "AddServer", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(vm, null);
    }

    [AvaloniaFact]
    public void SameNameSameHost_DifferentPort_IsNotTreatedAsDuplicate()
    {
        var vm = new MainWindowViewModel();
        vm.Servers.Clear();
        vm.Servers.Add(new ServerViewModel(new VlessServerEntry
        {
            Name = "main-brat",
            Protocol = "amneziawg",
            Server = "93.95.226.167",
            Port = 51822,
        }));

        vm.VlessUri =
            "vless://5550051c-2b10-4c11-8d73-b918118f86ef@93.95.226.167:9443?encryption=none&security=reality" +
            "&sni=yahoo.com&fp=randomized&pbk=4xRS--elmOVx36HHH2J_xEUY3An7Mnuu2tf7N6MykVw&sid=fb86a31808abe3f7" +
            "&type=xhttp&path=%2FU4YILqMxZg2N6sIKQFLt4w&mode=auto#main-brat";

        InvokeAddServer(vm);

        Assert.Equal(2, vm.Servers.Count);
        Assert.Contains(vm.Servers, s => s.Name == "main-brat" && s.Port == 9443);
        Assert.Contains(vm.Servers, s => s.Name == "main-brat" && s.Port == 51822);
        Assert.Equal(string.Empty, vm.VlessUri);
    }

    [AvaloniaFact]
    public void SameNameSameHostSamePort_IsStillTreatedAsDuplicate()
    {
        var vm = new MainWindowViewModel();
        vm.Servers.Clear();
        vm.Servers.Add(new ServerViewModel(new VlessServerEntry
        {
            Name = "main-brat",
            Server = "93.95.226.167",
            Port = 443,
            Uuid = "11111111-1111-1111-1111-111111111111",
        }));

        vm.VlessUri =
            "vless://22222222-2222-2222-2222-222222222222@93.95.226.167:443?encryption=none&security=reality" +
            "&sni=yahoo.com&fp=chrome&pbk=gDawCMB0X6iGXZkG8nZIFW5TaaW29x0DMzWijN-gc2A&sid=d86e92a0c6dd2271" +
            "&type=tcp#main-brat";

        InvokeAddServer(vm);

        Assert.Single(vm.Servers);
    }
}
