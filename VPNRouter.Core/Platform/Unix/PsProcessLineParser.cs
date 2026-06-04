using System.IO;

namespace VPNRouter.Core.Platform.Unix;

/// <summary>
/// Parses a single line of <c>ps -eo pid,ppid,comm</c> output into pid / ppid /
/// command base-name. Lives outside the <c>#if !PLATFORM_WINDOWS</c> platform
/// guard on purpose: it is pure string manipulation with no OS dependency, so it
/// compiles into the Windows test build and can be unit-tested headless.
///
/// Why this exists: the <c>comm</c> column is the LAST field and on macOS it is
/// the full executable path, which routinely contains spaces
/// (e.g. <c>/Applications/Google Chrome.app/Contents/MacOS/Google Chrome</c>).
/// A naive whitespace split + <c>Path.GetFileName(parts[2])</c> stops at the first
/// space and yields "Google" instead of "Google Chrome" — which then never matches
/// sing-box's exact <c>process_name</c> ("Google Chrome" / "Google Chrome Helper"),
/// silently breaking split-tunnel routing for every space-named app on macOS.
///
/// The fix: pid and ppid are the first two whitespace-delimited integer columns;
/// everything after them is the command path (spaces and all). We scan past exactly
/// two integer columns and take the entire remainder as the path, so embedded spaces
/// survive intact. The scan is column-count agnostic (handles the right-aligned
/// multi-space padding ps emits) and never relies on String.Split's subtle
/// count+RemoveEmptyEntries semantics.
/// </summary>
internal static class PsProcessLineParser
{
    /// <summary>
    /// Try to parse one <c>ps -eo pid,ppid,comm</c> data line (header already skipped).
    /// </summary>
    /// <param name="line">Raw line, possibly with leading/trailing whitespace.</param>
    /// <param name="pid">Parsed process id.</param>
    /// <param name="ppid">Parsed parent process id.</param>
    /// <param name="comm">Command base-name (last path component), spaces preserved.</param>
    /// <returns><c>true</c> if the line was a well-formed pid/ppid/comm row.</returns>
    public static bool TryParseLine(string? line, out int pid, out int ppid, out string comm)
    {
        pid = 0;
        ppid = 0;
        comm = string.Empty;

        if (string.IsNullOrWhiteSpace(line))
            return false;

        var s = line!.Trim();

        // Parse into locals; the out-params are only assigned once all three
        // columns validate, so a malformed row leaves them at their 0/empty
        // defaults (a clean failure contract for callers).

        // Column 1: pid — run of non-whitespace.
        int c1End = IndexOfWhitespace(s, 0);
        if (c1End <= 0) return false;
        if (!int.TryParse(s.AsSpan(0, c1End), out var parsedPid)) return false;

        // Column 2: ppid — skip padding, then run of non-whitespace.
        int c2Start = SkipWhitespace(s, c1End);
        if (c2Start >= s.Length) return false;
        int c2End = IndexOfWhitespace(s, c2Start);
        if (c2End < 0) return false;
        if (!int.TryParse(s.AsSpan(c2Start, c2End - c2Start), out var parsedPpid)) return false;

        // Column 3: comm — the entire remainder (full executable path, may contain
        // spaces). Base-name only — sing-box matches filepath.Base(processPath).
        int c3Start = SkipWhitespace(s, c2End);
        if (c3Start >= s.Length) return false;
        var commandPath = s.Substring(c3Start);
        var parsedComm = Path.GetFileName(commandPath);

        // Kernel threads render as "(kernel_task)" with no path separator;
        // GetFileName returns them verbatim, which is acceptable.
        if (parsedComm.Length == 0) return false;

        pid = parsedPid;
        ppid = parsedPpid;
        comm = parsedComm;
        return true;
    }

    private static int IndexOfWhitespace(string s, int start)
    {
        for (int i = start; i < s.Length; i++)
            if (s[i] == ' ' || s[i] == '\t')
                return i;
        return -1;
    }

    private static int SkipWhitespace(string s, int start)
    {
        int i = start;
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t'))
            i++;
        return i;
    }
}
