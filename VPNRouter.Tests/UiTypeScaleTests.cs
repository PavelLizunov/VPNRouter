using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

[Trait("Category", "Unit")]
[Trait("Layer", "Core")]
public sealed class UiTypeScaleTests
{
    [Theory]
    [InlineData(9, 11)]
    [InlineData(10, 12)]
    [InlineData(11, 13)]
    [InlineData(12, 14)]
    [InlineData(16, 18)]
    [InlineData(22, 24)]
    public void FontSize_AtDefaultScale_LiftsByTwo(double source, double expected)
    {
        Assert.Equal(expected, UiTypeScale.FontSize(source, 1.0));
    }

    [Theory]
    [InlineData(0.85, 1.0)]
    [InlineData(1.0, 1.0)]
    [InlineData(1.15, 1.15)]
    [InlineData(1.3, 1.3)]
    [InlineData(2.0, 1.4)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    [InlineData(-1.0, 1.0)]
    public void ClampFactor_KeepsTheScaleInTheSupportedRange(double system, double expected)
    {
        Assert.Equal(expected, UiTypeScale.ClampFactor(system));
    }

    [Fact]
    public void FontSize_FollowsTheSystemScale()
    {
        Assert.Equal(Math.Round(14 * 1.3, 1), UiTypeScale.FontSize(12, 1.3));
        Assert.Equal(Math.Round(14 * 1.4, 1), UiTypeScale.FontSize(12, 3.0));
    }

    [Fact]
    public void LineHeight_IsLiftedAndScaledLikeTheFont()
    {
        Assert.Equal(15, UiTypeScale.LineHeight(13, 1.0));
        Assert.Equal(Math.Round(15 * 1.15, 1), UiTypeScale.LineHeight(13, 1.15));
    }
}
