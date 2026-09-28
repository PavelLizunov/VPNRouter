namespace VPNRouter.App.ViewModels.Internals;

public static class ToolTabAvailability
{
    public static bool ToolsTabVisible(bool zapret, bool tgProxy)
        => zapret || tgProxy;

    public static int DefaultToolIndex(bool zapret, bool tgProxy)
        => zapret ? 0 : tgProxy ? 1 : 0;
}
