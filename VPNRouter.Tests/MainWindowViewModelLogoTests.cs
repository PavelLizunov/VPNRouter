using VPNRouter.App.ViewModels;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

[Collection(SafeModeStateCollection.Name)]
public sealed class MainWindowViewModelLogoTests
{
    [Fact]
    public void LogoSource_NeverThrows_InEitherTheme_WhateverThePlatform()
    {
        using var vm = new MainWindowViewModel(new InMemorySettingsStore());

        var exception = Record.Exception(() =>
        {
            vm.SetThemeLightCommand.Execute(null);
            _ = vm.LogoSource;
            vm.SetThemeDarkCommand.Execute(null);
            _ = vm.LogoSource;
        });

        Assert.Null(exception);
    }
}
