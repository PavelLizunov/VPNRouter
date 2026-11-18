#if PLATFORM_WINDOWS
using Avalonia.Headless.XUnit;
using VPNRouter.App.Services;
using VPNRouter.App.ViewModels;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class ServiceFlyoutOwnershipTests : IDisposable
{
    private readonly Func<string, uint, IWindowsRegistration?> _open = WindowsServiceHelper.OpenRegistration;
    private readonly Func<string[], (int, string)> _sc = WindowsServiceHelper.SystemSc;
    private readonly Func<string, bool> _exists = WindowsServiceHelper.ServiceExeExists;
    private readonly List<string[]> _commands = [];
    private readonly string _expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        WindowsServiceHelper.ServiceName, "app", "VPNRouter.Service.exe");
    private FakeRegistration? _current;
    private string _name = WindowsServiceHelper.ServiceName;
    private int _stops;
    private int _deletes;

    public ServiceFlyoutOwnershipTests()
    {
        WindowsServiceHelper.OpenRegistration = (name, _) => name == _name ? _current : null;
        WindowsServiceHelper.ServiceExeExists = path => path == _expected;
        WindowsServiceHelper.SystemSc = args =>
        {
            _commands.Add(args);
            Assert.Equal(_name, args[1]);
            switch (args[0])
            {
                case "create":
                    Assert.Null(_current);
                    Assert.Contains(WindowsServiceCommand.FormatImagePath(_expected), args);
                    _current = Registration(_expected);
                    _current.IsActive = false;
                    return (0, "");
                case "description":
                case "failure":
                    return (0, "");
                default:
                    throw new InvalidOperationException("Unexpected SCM command");
            }
        };
    }

    public void Dispose()
    {
        WindowsServiceHelper.OpenRegistration = _open;
        WindowsServiceHelper.SystemSc = _sc;
        WindowsServiceHelper.ServiceExeExists = _exists;
    }

    private FakeRegistration Registration(string path) =>
        new(new(WindowsServiceCommand.FormatImagePath(path), 0x10, 2))
        {
            IsActive = true,
            Pid = 4521,
            OnStop = () => _stops++,
            OnDelete = () => { _deletes++; _current = null; }
        };

    private static async Task ToggleOff(ServiceViewModel vm)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.IsBusy) && !vm.IsBusy) done.TrySetResult();
        };
        vm.AutostartChecked = false;
        await done.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    [AvaloniaTheory]
    [InlineData("restart")]
    [InlineData("reinstall")]
    [InlineData("remove")]
    public async Task FlyoutRechecksImageAfterRefreshBeforeEachAction(string action)
    {
        _current = Registration(_expected);
        var vm = new ServiceViewModel(Serilog.Log.Logger, eagerRefresh: true);
        Assert.Equal(4521, vm.ServicePid);
        _commands.Clear();
        _current = Registration(Path.Combine(Path.GetTempPath(), "relocated", "VPNRouter.Service.exe"));

        if (action == "restart") await vm.RestartServiceCommand.ExecuteAsync(null);
        else if (action == "reinstall") await vm.ReinstallServiceCommand.ExecuteAsync(null);
        else await ToggleOff(vm);

        Assert.False(vm.IsInstalled);
        Assert.Equal(VPNRouter.Core.Localization.Strings.MainServiceOwnerConflict, vm.StatusMessage);
        Assert.True(_current!.IsActive);
        Assert.Equal(0, _stops);
        Assert.Equal(0, _deletes);
        Assert.Empty(_commands);
        Assert.False(WindowsServiceHelper.EnsureCurrentBinPath().Success);
        Assert.Equal(0, _current.Changes);
    }

    [AvaloniaFact]
    public async Task OwnedServiceCanRestartReinstallAndUninstall()
    {
        _current = Registration(_expected);
        var vm = new ServiceViewModel(Serilog.Log.Logger, eagerRefresh: true);
        await vm.RestartServiceCommand.ExecuteAsync(null);
        Assert.Equal(1, _stops);
        Assert.True(vm.IsRunning);

        await vm.ReinstallServiceCommand.ExecuteAsync(null);
        Assert.Equal(1, _deletes);
        Assert.Single(_commands, args => args[0] == "create");
        Assert.True(vm.IsRunning);

        await ToggleOff(vm);
        Assert.Equal(2, _deletes);
        Assert.False(vm.IsInstalled);
        Assert.Null(_current);
    }

    [Theory]
    [InlineData("other-edition")]
    [InlineData("extra-option")]
    [InlineData("wrong-type")]
    public void ForeignOrUnrecognizedRegistrationBlocksAllGuiMaintenance(string scenario)
    {
        var other = WindowsServiceHelper.ServiceName == "VPNRouter" ? "VPNRouterBusiness" : "VPNRouter";
        var otherExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            other, "app", "VPNRouter.Service.exe");
        var image = WindowsServiceCommand.FormatImagePath(scenario == "other-edition" ? otherExe : _expected);
        if (scenario == "extra-option") image += " --other";
        _current = new(new(image, scenario == "wrong-type" ? 1u : 0x10u, 2)) { IsActive = true };

        Assert.False(WindowsServiceHelper.IsInstalled());
        Assert.False(WindowsServiceHelper.Start().Success);
        Assert.False(WindowsServiceHelper.Stop().Success);
        Assert.False(WindowsServiceHelper.Uninstall().Success);
        Assert.False(WindowsServiceHelper.Install().Success);
        Assert.False(WindowsServiceHelper.EnsureCurrentBinPath().Success);
        Assert.True(_current.IsActive);
        Assert.Equal(0, _current.Changes);
        Assert.Empty(_commands);
    }

    [Fact]
    public void HistoricalBusinessRegistrationKeepsItsNameAcrossLiveApplyAndReinstall()
    {
        if (!VPNRouter.Core.ProductEdition.IsBusiness) return;
        _name = "VPNRouter";
        _current = Registration(_expected);
        var service = WindowsServiceHelper.GetState();
        Assert.Equal(_name, service.Name);
        Assert.True(service.Running);
        Assert.Equal(4521, service.Pid);
        Assert.True(WindowsServiceHelper.Reinstall().Success);
        Assert.Single(_commands, args => args[0] == "create" && args[1] == _name);
        Assert.True(WindowsServiceHelper.Start().Success);
        Assert.Equal(_name, WindowsServiceHelper.GetState().Name);
    }

    [Fact]
    public void GuiHealNormalizesOnlyTheKnownEditionImageAndPreservesStartMode()
    {
        _current = new(new($"\"{_expected}\"", 0x10, 3));
        Assert.True(WindowsServiceHelper.EnsureCurrentBinPath().Success);
        Assert.Equal(new ServiceConfig(WindowsServiceCommand.FormatImagePath(_expected), 0x10, 3), _current.Config);
        Assert.Equal(1, _current.Changes);
    }

    [Fact]
    public void UninstallRechecksThePinnedImageAfterStop()
    {
        var deleted = false;
        _current = new FakeRegistration(new(WindowsServiceCommand.FormatImagePath(_expected), 0x10, 2))
        {
            IsActive = true,
            OnStop = () => _current!.Configure("changed image", 2),
            OnDelete = () => deleted = true
        };
        Assert.False(WindowsServiceHelper.Uninstall().Success);
        Assert.False(deleted);
    }
}
#endif
