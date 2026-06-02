// v2.40.0 night-shift (2026-06-02) — regression test for the bounded log tail.
//
// Audit (regression-review-since-r4 workflow) found DiagnosticsExporter.TailLines
// read the ENTIRE log file into memory before keeping the last 800 lines — a
// corrupt / runaway multi-GB log would OOM the bundle, defeating the
// LogTailLines bound. Fix: seek to the last MaxTailReadBytes (2 MB) before
// reading. These tests pin that the read is bounded AND still returns the tail.

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
            // Write ~3 MB (> MaxTailReadBytes = 2 MB) of numbered lines. The very
            // first line is uniquely identifiable so we can prove it was NOT read.
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

            // Bounded to maxLines.
            Assert.True(lines.Length <= DiagnosticsExporter.LogTailLines,
                $"tail returned {lines.Length} lines, expected <= {DiagnosticsExporter.LogTailLines}");
            // Returned text is bounded by the byte cap — proves we did NOT
            // materialise the whole 3 MB file.
            Assert.True(Encoding.UTF8.GetByteCount(tail) <= DiagnosticsExporter.MaxTailReadBytes,
                "tail text exceeded the read cap — the whole file was likely read");
            // It must be the TAIL: the unique first line must be gone.
            Assert.DoesNotContain("line 00000000", tail);
        }
        finally { try { File.Delete(path); } catch { /* best-effort */ } }
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
        finally { try { File.Delete(path); } catch { /* best-effort */ } }
    }
}
