#nullable enable

using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

[Trait("Category", "Unit")]
[Trait("Layer", "Core")]
public sealed class UiIconsTests
{
    [Fact]
    public void Registry_NamesAreLucideFileNames_AndConstantsPointIntoTheRegistry()
    {
        Assert.NotEmpty(UiIcons.PathData);
        foreach (var name in UiIcons.PathData.Keys)
            Assert.Matches(new Regex("^[a-z0-9]+(-[a-z0-9]+)*$"), name);

        var constants = typeof(UiIcons).GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();
        Assert.Equal(constants.Count, constants.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(UiIcons.PathData.Count, constants.Count);
        Assert.All(constants, c => Assert.True(UiIcons.Exists(c), c));
    }

    [AvaloniaFact]
    public void EveryPath_ParsesWithAvalonia_AndStaysOnTheGrid()
    {
        foreach (var (name, data) in UiIcons.PathData)
        {
            var geometry = Geometry.Parse(data);
            var b = geometry.Bounds;
            Assert.True(b.Width > 0 || b.Height > 0, name);
            Assert.True(b.Left >= -0.5 && b.Top >= -0.5 && b.Right <= 24.5 && b.Bottom <= 24.5,
                $"{name} leaves the 24 grid: {b}");
        }
    }

    [Fact]
    public void Power_IsMirrorSymmetric()
    {
        Assert.Equal("M12,2 L12,12 M18.4,6.6 A9,9 0 1 1 5.6,6.6", UiIcons.PathData[UiIcons.Power]);
    }

    [Theory]
    [InlineData("↻ Apply", "Apply")]
    [InlineData("↻  Применить изменения", "Применить изменения")]
    [InlineData("✓✓ Find working configs", "Find working configs")]
    [InlineData("Advanced ▸", "Advanced")]
    [InlineData("◂ Simple", "Simple")]
    [InlineData("▾ Settings", "Settings")]
    [InlineData("⏹  Stop VPN", "Stop VPN")]
    [InlineData("Wi-Fi ↔ cellular", "Wi-Fi ↔ cellular")]
    [InlineData("VPN → gear", "VPN → gear")]
    [InlineData("✕", "")]
    [InlineData("+ Add Server(s)", "Add Server(s)")]
    [InlineData("\u2b1b  Stop VPN", "Stop VPN")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void StripSymbols_RemovesOnlyEdgeSymbols(string? input, string expected)
    {
        Assert.Equal(expected, UiIcons.StripSymbols(input));
    }
}
