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
    public void TailLines_CustomReadCapAndLineLimit_PreservesExactLastLinesWithCrlf()
    {
        var path = Path.Combine(Path.GetTempPath(), $"diag-tail-cap-{Guid.NewGuid():N}.log");
        try
        {
            var sb = new StringBuilder();
            for (var i = 0; i < 500; i++)
                sb.Append($"entry-{i:D4}-payload-abcdef0123456789\r\n");
            File.WriteAllText(path, sb.ToString());

            var tail = DiagnosticsExporter.TailLines(path, maxLines: 10, maxReadBytes: 1024);
            var lines = tail.Split(Environment.NewLine);

            Assert.Equal(10, lines.Length);
            Assert.All(lines, l => Assert.DoesNotContain("\r", l));
            for (var i = 0; i < 10; i++)
                Assert.Equal($"entry-{490 + i:D4}-payload-abcdef0123456789", lines[i]);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void TailLines_EmptySmallAndSingleLongLine_HandledCleanly()
    {
        var emptyPath = Path.Combine(Path.GetTempPath(), $"diag-tail-empty-{Guid.NewGuid():N}.log");
        var smallPath = Path.Combine(Path.GetTempPath(), $"diag-tail-small-{Guid.NewGuid():N}.log");
        var singlePath = Path.Combine(Path.GetTempPath(), $"diag-tail-single-{Guid.NewGuid():N}.log");
        try
        {
            File.WriteAllText(emptyPath, string.Empty);
            Assert.Equal(string.Empty, DiagnosticsExporter.TailLines(emptyPath, 10));

            File.WriteAllText(smallPath, "alpha\nbeta\ngamma\n");
            Assert.Equal(string.Join(Environment.NewLine, "alpha", "beta", "gamma"),
                DiagnosticsExporter.TailLines(smallPath, 10));
            Assert.Equal(string.Join(Environment.NewLine, "beta", "gamma"),
                DiagnosticsExporter.TailLines(smallPath, 2));

            File.WriteAllText(singlePath, new string('z', 2048));
            var singleTail = DiagnosticsExporter.TailLines(singlePath, maxLines: 5, maxReadBytes: 256);
            Assert.Equal(256, singleTail.Length);
        }
        finally
        {
            try { File.Delete(emptyPath); } catch { }
            try { File.Delete(smallPath); } catch { }
            try { File.Delete(singlePath); } catch { }
        }
    }
}
