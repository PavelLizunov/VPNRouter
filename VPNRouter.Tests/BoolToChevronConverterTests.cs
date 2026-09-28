using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;
public class BoolToChevronConverterTests
{
    [Fact]
    public void DefaultParameter_ReturnsArrowGlyphs()
    {
        var c = VPNRouter.App.BoolToChevronConverter.Instance;
        Assert.Equal("▲", c.Convert(true, typeof(string), null,
            System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("▼", c.Convert(false, typeof(string), null,
            System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void RightDownParameter_ReturnsCardChevronGlyphs()
    {
        var c = VPNRouter.App.BoolToChevronConverter.Instance;
        Assert.Equal("▽", c.Convert(true, typeof(string), "▽|›",
            System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("›", c.Convert(false, typeof(string), "▽|›",
            System.Globalization.CultureInfo.InvariantCulture));
    }
}
