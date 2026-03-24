using VPNRouter.GUI;

namespace VPNRouter.Tests;

public class LayoutHelperTests
{
    // ── Right-to-left positioning ──────────────────────────────────────────

    [Fact]
    public void RightToLeft_ThreeLinks_NoOverlap()
    {
        var positions = LayoutHelper.CalculateRightToLeftPositions(500, 10, new[] { 30, 50, 100 });
        Assert.Equal(3, positions.Length);
        Assert.Equal(470, positions[0]); // rightmost: 500-30
        Assert.Equal(410, positions[1]); // 470-10-50
        Assert.Equal(300, positions[2]); // 410-10-100
    }

    [Fact]
    public void RightToLeft_SingleLink()
    {
        var positions = LayoutHelper.CalculateRightToLeftPositions(500, 10, new[] { 80 });
        Assert.Single(positions);
        Assert.Equal(420, positions[0]);
    }

    [Fact]
    public void RightToLeft_WideRussianStrings_AllPositive()
    {
        // Simulating Russian: "ENG"=40, "Тёмная"=90, "Проверить обновления"=170
        var positions = LayoutHelper.CalculateRightToLeftPositions(510, 10, new[] { 40, 90, 170 });
        Assert.All(positions, p => Assert.True(p >= 0));
        // Verify no overlap: each position + width < next position
        Assert.True(positions[1] + 90 <= positions[0]);
        Assert.True(positions[2] + 170 <= positions[1]);
    }

    [Fact]
    public void RightToLeft_VeryWide_ClampsToZero()
    {
        // Total width exceeds right edge — positions should clamp to 0
        var positions = LayoutHelper.CalculateRightToLeftPositions(100, 10, new[] { 60, 60, 60 });
        Assert.True(positions[2] >= 0);
    }

    [Fact]
    public void RightToLeft_EmptyArray_ReturnsEmpty()
    {
        var positions = LayoutHelper.CalculateRightToLeftPositions(500, 10, Array.Empty<int>());
        Assert.Empty(positions);
    }

    // ── Proportional column widths ────────────────────────────────────────

    [Fact]
    public void ProportionalWidths_SumsToAvailable()
    {
        var widths = LayoutHelper.CalculateProportionalWidths(500, 20, new[] { 2, 3, 4, 1, 2 });
        Assert.Equal(5, widths.Length);
        // Available = 480, sum should be close to 480 (integer rounding)
        Assert.True(widths.Sum() <= 480);
        Assert.All(widths, w => Assert.True(w >= 20));
    }

    [Fact]
    public void ProportionalWidths_AllPositive()
    {
        var widths = LayoutHelper.CalculateProportionalWidths(300, 17, new[] { 1, 4, 3, 5 });
        Assert.All(widths, w => Assert.True(w > 0));
    }

    [Fact]
    public void ProportionalWidths_LargerProportion_GetsMoreWidth()
    {
        var widths = LayoutHelper.CalculateProportionalWidths(500, 0, new[] { 1, 3 });
        Assert.True(widths[1] > widths[0]);
    }

    [Fact]
    public void ProportionalWidths_SmallTotal_MinimumWidth()
    {
        var widths = LayoutHelper.CalculateProportionalWidths(50, 20, new[] { 1, 1, 1 });
        Assert.All(widths, w => Assert.True(w >= 20)); // minimum enforced
    }

    // ── Action layout ─────────────────────────────────────────────────────

    [Fact]
    public void ActionLayout_ApplyHidden_FullWidth()
    {
        var (startW, applyX, applyW) = LayoutHelper.CalculateActionLayout(540, 14, 6, 120, 80, false);
        Assert.Equal(512, startW); // 540 - 14*2
        Assert.Equal(0, applyX);
        Assert.Equal(0, applyW);
    }

    [Fact]
    public void ActionLayout_ApplyVisible_SplitsWidth()
    {
        var (startW, applyX, applyW) = LayoutHelper.CalculateActionLayout(540, 14, 6, 120, 80, true);
        Assert.Equal(120, applyW); // max(120, 80+30=110) = 120
        Assert.True(startW > 0);
        Assert.True(startW + applyW + 6 <= 512); // fits in available
    }

    [Fact]
    public void ActionLayout_WideApplyText_GrowsApply()
    {
        var (_, _, applyW) = LayoutHelper.CalculateActionLayout(540, 14, 6, 120, 130, true);
        Assert.Equal(160, applyW); // max(120, 130+30=160) = 160
    }

    [Fact]
    public void ActionLayout_MinimumSize_StillFits()
    {
        var (startW, _, applyW) = LayoutHelper.CalculateActionLayout(480, 14, 6, 120, 80, true);
        Assert.True(startW >= 100, "Start button should have minimum 100px width");
        Assert.True(startW + applyW + 6 <= 480 - 28);
    }

    [Fact]
    public void ActionLayout_VeryNarrow_ClampsMinimum()
    {
        var (startW, _, _) = LayoutHelper.CalculateActionLayout(200, 14, 6, 120, 80, true);
        Assert.True(startW >= 100); // minimum enforced
    }
}
