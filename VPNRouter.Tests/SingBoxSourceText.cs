using System;
using System.IO;

namespace VPNRouter.Tests;

// Shared by SingBoxManager source-characterization tests. T1-B split the class
// into partial files: SingBoxManager.cs (anchor) + SingBoxManager.Health.cs +
// .HotReload.cs + .LinuxStop.cs + .CrashDetect.cs + .Lifecycle.cs. A source
// assertion (e.g. "OnProcessExited contains the suppression guard") must search
// the whole class, so given the anchor path this concatenates every partial in
// its directory. Stem-derived, so it is also correct (a no-op concatenation of
// one file) for non-split source files.
internal static class SingBoxSourceText
{
    public static string ReadAll(string anchorPath)
    {
        var dir = Path.GetDirectoryName(anchorPath)!;
        var stem = Path.GetFileNameWithoutExtension(anchorPath);
        var files = Directory.GetFiles(dir, stem + "*.cs");
        Array.Sort(files, StringComparer.Ordinal);
        return string.Join("\n", Array.ConvertAll(files, File.ReadAllText));
    }
}
