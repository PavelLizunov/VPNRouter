using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
public class BoolToChevronConverterTests
{
    /// <summary>
    /// v2.31.0-r4 (F-3): the Simple-mode "Конфиг·Режим" card chevron now
    /// flips ▽ ↔ › via <see cref="VPNRouter.App.BoolToChevronConverter"/>
    /// with a parameter. The pre-fix converter only returned ▲/▼ — the
    /// regression test ensures both default + parameter paths stay correct
    /// so a refactor doesn't silently break F-3 again.
    /// </summary>
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
