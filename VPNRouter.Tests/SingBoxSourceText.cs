using System;
using System.IO;

namespace VPNRouter.Tests;

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
