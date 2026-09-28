#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Text;
using VPNRouter.Core.Services.Diagnostics;
using Xunit;

namespace VPNRouter.Tests;

public sealed class DiagnosticsExporterTailBoundedTests
{
    [Fact]
    public void TailLines_FileLargerThanCap_ReadsBoundedTailNotWholeFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"diag-tail-{Guid.NewGuid():N}.log");
        try
        {
            using (var w = new StreamWriter(path))
            {
                long bytes = 0;
                int i = 0;
                var pad = new string('x', 60);
                while (bytes < 3L * 1024 * 1024)
                {
                    var line = $"line {i:D8} {pad}";
                    w.WriteLine(line);
                    bytes += line.Length + 2;
                    i++;
                }
            }

            var tail = DiagnosticsExporter.TailLines(path, DiagnosticsExporter.LogTailLines);
            var lines = tail.Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0).ToArray();

            Assert.True(lines.Length <= DiagnosticsExporter.LogTailLines,
                $"tail returned {lines.Length} lines, expected <= {DiagnosticsExporter.LogTailLines}");
            Assert.True(Encoding.UTF8.GetByteCount(tail) <= DiagnosticsExporter.MaxTailReadBytes,
                "tail text exceeded the read cap — the whole file was likely read");
            Assert.DoesNotContain("line 00000000", tail);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void TailLines_SmallFile_ReturnsAllContent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"diag-tail-small-{Guid.NewGuid():N}.log");
        try
        {
            File.WriteAllText(path, "alpha\nbeta\ngamma\n");
            var tail = DiagnosticsExporter.TailLines(path, 800);
            Assert.Contains("alpha", tail);
            Assert.Contains("gamma", tail);
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
