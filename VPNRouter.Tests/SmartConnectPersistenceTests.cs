using System.Text.RegularExpressions;

namespace VPNRouter.Tests;

/// <summary>
/// FLOW-1 (P06) source-ordering pin: Smart Connect must synchronize
/// SelectedSubscriptionServer to the probed winner BEFORE SaveSettings
/// re-derives ActiveSubscriptionServer from it.
/// </summary>
public sealed class SmartConnectPersistenceTests
{
    [Fact]
    public void SmartConnect_SyncsSelectionBeforeSaveSettings_SourcePin()
    {
        var src = LoadSimpleModeSource();
        Assert.SkipUnless(src != null,
            "MainWindowViewModel.SimpleMode.cs not reachable from test cwd");

        var stripped = StripLineComments(src!);
        var oneline = Regex.Replace(stripped, @"\s+", " ");

        // Locate the Smart Connect winner-switch branch.
        var branchIdx = oneline.IndexOf(
            "_settings.App.ActiveSubscriptionServer = chosen.Name",
            StringComparison.Ordinal);
        Assert.True(branchIdx >= 0,
            "SimpleMode.cs must contain the Smart Connect winner assignment");

        // Region from the winner assignment to the next catch (bounded).
        var catchIdx = oneline.IndexOf("catch (Exception", branchIdx, StringComparison.Ordinal);
        var end = catchIdx < 0 ? Math.Min(oneline.Length, branchIdx + 800) : catchIdx;
        var region = oneline.Substring(branchIdx, end - branchIdx);

        // The fix: winner lookup + selection sync must appear ...
        var lookupIdx = region.IndexOf(
            "SubscriptionServers.FirstOrDefault(s => s.Name == chosen.Name)",
            StringComparison.Ordinal);
        Assert.True(lookupIdx >= 0,
            "Smart Connect must look up the winner VM in SubscriptionServers");

        var assignIdx = region.IndexOf(
            "SelectedSubscriptionServer = winnerVm",
            StringComparison.Ordinal);
        Assert.True(assignIdx >= 0,
            "Smart Connect must assign SelectedSubscriptionServer to the winner");

        // ... BEFORE SaveSettings, which re-derives from the selection.
        var saveIdx = region.IndexOf("SaveSettings()", StringComparison.Ordinal);
        Assert.True(saveIdx >= 0,
            "Smart Connect branch must call SaveSettings()");

        Assert.True(lookupIdx < saveIdx && assignIdx < saveIdx,
            $"Winner sync (lookup={lookupIdx}, assign={assignIdx}) must precede " +
            $"SaveSettings (at {saveIdx}) — otherwise the stale selection clobbers " +
            "the probed winner during re-derivation");
    }

    // ─── helpers (mirror sibling source-pin loaders) ────────────────────

    private static string? LoadSimpleModeSource()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(
                dir.FullName,
                "VPNRouter.App", "ViewModels", "MainWindowViewModel.SimpleMode.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        return null;
    }

    private static string StripLineComments(string src) =>
        string.Join('\n',
            src.Split('\n').Select(l =>
                l.Contains("//") ? l[..l.IndexOf("//")] : l));
}
