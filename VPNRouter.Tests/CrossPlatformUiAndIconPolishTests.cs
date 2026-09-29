using System;
using System.IO;
using Avalonia.Styling;
using AppClass = VPNRouter.App.App;
using VPNRouter.Core.Localization;
using Xunit;

namespace VPNRouter.Tests;

public sealed class CrossPlatformUiAndIconPolishTests
{
    [Fact]
    public void App_GetTrayIconUri_SelectsWhiteIconForLinuxAndDarkMacOS()
    {
        if (OperatingSystem.IsLinux())
        {
            var uriLight = AppClass.GetTrayIconUri(ThemeVariant.Light);
            var uriDark = AppClass.GetTrayIconUri(ThemeVariant.Dark);
            Assert.Contains("penguin_mascot_white.ico", uriLight);
            Assert.Contains("penguin_mascot_white.ico", uriDark);
        }

        if (OperatingSystem.IsMacOS())
        {
            var uriDark = AppClass.GetTrayIconUri(ThemeVariant.Dark);
            var uriLight = AppClass.GetTrayIconUri(ThemeVariant.Light);
            Assert.Contains("penguin_mascot_white.ico", uriDark);
            Assert.Contains("penguin_mascot.ico", uriLight);
        }
    }

    [Theory]
    [InlineData("mipmap-mdpi", 48, 48)]
    [InlineData("mipmap-hdpi", 72, 72)]
    [InlineData("mipmap-xhdpi", 96, 96)]
    [InlineData("mipmap-xxhdpi", 144, 144)]
    [InlineData("mipmap-xxxhdpi", 192, 192)]
    public void Android_RoundIcons_ExistAndHaveCorrectDimensions(string density, int expectedW, int expectedH)
    {
        var path = Path.Combine(FindAndroidResourcesDir(), density, "ic_launcher_round.png");
        Assert.True(File.Exists(path), $"Missing round icon at: {path}");

        using var fs = File.OpenRead(path);
        var header = new byte[26];
        var read = fs.Read(header, 0, 26);
        Assert.Equal(26, read);

        Assert.Equal(0x89, header[0]);
        Assert.Equal((byte)'P', header[1]);
        Assert.Equal((byte)'N', header[2]);
        Assert.Equal((byte)'G', header[3]);

        var w = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
        var h = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];

        Assert.Equal(expectedW, w);
        Assert.Equal(expectedH, h);

        Assert.Equal(8, header[24]);
        Assert.Equal(6, header[25]);
    }

    private static string FindAndroidResourcesDir()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 8 && directory != null; i++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "VPNRouter.Android", "Resources");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("Could not locate VPNRouter.Android/Resources directory");
    }

    private static string LoadSource(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 8 && directory != null; i++, directory = directory.Parent)
        {
            var candidate = Path.Combine(
                new[] { directory.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException(
            $"Could not locate repository source: {Path.Combine(relativeParts)}");
    }
}
