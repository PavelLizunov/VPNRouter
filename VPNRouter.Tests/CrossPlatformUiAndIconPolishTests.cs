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
        // On Linux, always white icon
        if (OperatingSystem.IsLinux())
        {
            var uriLight = AppClass.GetTrayIconUri(ThemeVariant.Light);
            var uriDark = AppClass.GetTrayIconUri(ThemeVariant.Dark);
            Assert.Contains("penguin_mascot_white.ico", uriLight);
            Assert.Contains("penguin_mascot_white.ico", uriDark);
        }

        // On macOS: dark appearance uses white icon to avoid invisible dark-on-dark in Menu Bar
        if (OperatingSystem.IsMacOS())
        {
            var uriDark = AppClass.GetTrayIconUri(ThemeVariant.Dark);
            var uriLight = AppClass.GetTrayIconUri(ThemeVariant.Light);
            Assert.Contains("penguin_mascot_white.ico", uriDark);
            Assert.Contains("penguin_mascot.ico", uriLight);
        }
    }

    [Fact]
    public void Strings_OsDisplayName_IncludesAndroid()
    {
        var src = LoadSource("VPNRouter.Core", "Localization", "Strings.cs");

        // Verify OsDisplayName explicitly checks OperatingSystem.IsAndroid()
        Assert.Contains("OperatingSystem.IsAndroid() ? \"Android\"", src);
    }

    [Fact]
    public void Strings_AutostartCard_DoesNotLeakWindowsOnAndroid()
    {
        var src = LoadSource("VPNRouter.Core", "Localization", "Strings.cs");

        var offStart = src.IndexOf("public static string SmpAutostartCardOff", StringComparison.Ordinal);
        var offEnd = src.IndexOf(";", offStart, StringComparison.Ordinal);
        var offBody = src[offStart..offEnd];

        Assert.Contains("OperatingSystem.IsAndroid()", offBody);
        Assert.Contains("Configure VPN autostart on device boot", offBody);
    }

    [Fact]
    public void AndroidApp_AdvancedShell_WrapsTabsInScrollViewerWithMinWidth()
    {
        var src = LoadSource("VPNRouter.Android", "AndroidApp.AdvancedShell.cs");

        // Verify ScrollViewer wraps the tab strip
        Assert.Contains("var tabScroll = new ScrollViewer", src);
        Assert.Contains("HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden", src);
        Assert.Contains("VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled", src);
        Assert.Contains("Content = tabPanel", src);
        Assert.Contains("Child = tabScroll", src);

        // Verify tab button has MinWidth
        Assert.Contains("MinWidth = 62", src);
    }

    [Theory]
    [InlineData("mipmap-mdpi", 48, 48)]
    [InlineData("mipmap-hdpi", 72, 72)]
    [InlineData("mipmap-xhdpi", 96, 96)]
    [InlineData("mipmap-xxhdpi", 144, 144)]
    [InlineData("mipmap-xxxhdpi", 192, 192)]
    public void Android_LauncherIcons_ExistAsRgbaAtCorrectDimensions(string density, int expectedW, int expectedH)
    {
        foreach (var fileName in new[] { "ic_launcher.png", "ic_launcher_round.png" })
        {
            var path = Path.Combine(FindAndroidResourcesDir(), density, fileName);
            Assert.True(File.Exists(path), $"Missing launcher icon at: {path}");

            using var fs = File.OpenRead(path);
            var header = new byte[26];
            var read = fs.Read(header, 0, 26);
            Assert.Equal(26, read);

            // PNG signature
            Assert.Equal(0x89, header[0]);
            Assert.Equal((byte)'P', header[1]);
            Assert.Equal((byte)'N', header[2]);
            Assert.Equal((byte)'G', header[3]);

            // Width and height in IHDR chunk (bytes 16..23)
            var w = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
            var h = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];

            Assert.Equal(expectedW, w);
            Assert.Equal(expectedH, h);

            // Bit depth (offset 24) and color type (offset 25: 6 = RGBA).
            Assert.Equal(8, header[24]);
            Assert.Equal(6, header[25]);
        }
    }

    [Fact]
    public void IconSystem_HasFlatAccessibleSvgMasters()
    {
        var assetsDir = FindDesignAssetsDir();
        foreach (var fileName in new[] { "mascot-master.svg", "mascot-master-dark.svg", "penguin.svg", "logo-lockup.svg" })
        {
            var path = Path.Combine(assetsDir, fileName);
            Assert.True(File.Exists(path), $"Missing SVG master at: {path}");
            var svg = File.ReadAllText(path);

            Assert.Contains("<title", svg, StringComparison.Ordinal);
            Assert.Contains("<desc", svg, StringComparison.Ordinal);
            Assert.DoesNotContain("linearGradient", svg, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("radialGradient", svg, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<image", svg, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("xlink:href", svg, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("href=", svg, StringComparison.OrdinalIgnoreCase);
        }
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

    private static string FindDesignAssetsDir()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 8 && directory != null; i++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "design", "project", "assets");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("Could not locate design/project/assets directory");
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
