#nullable enable
using System;
using System.IO;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Helper fact (skipped by default) — dump the AndroidApp member set
/// to a file so the integrator can diff pre- and post-split member
/// lists when the characterization hash drifts.
///
/// To run: comment out the Skip attribute and execute
/// <c>dotnet test --filter AndroidAppDumpMembers_ToTempFile</c>.
/// The dump lands in <c>%TEMP%\androidapp-members.txt</c>.
/// </summary>
public class AndroidAppDumpMembersFact
{
    [Fact(Skip = "Diagnostic only — remove Skip to capture a member dump to %TEMP%/androidapp-members.txt.")]
    public void AndroidAppDumpMembers_ToTempFile()
    {
        var dir = AndroidAppSourceSurfaceHashHelper.FindAndroidProjectDir();
        Assert.NotNull(dir);
        var members = AndroidAppSourceSurfaceHashHelper.DumpMembers(dir!);
        var outPath = Path.Combine(Path.GetTempPath(), "androidapp-members.txt");
        File.WriteAllLines(outPath, members);
    }
}
