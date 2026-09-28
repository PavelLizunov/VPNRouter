using System.IO;

namespace VPNRouter.Core.Platform.Unix;

internal static class PsProcessLineParser
{
    public static bool TryParseLine(string? line, out int pid, out int ppid, out string comm)
    {
        pid = 0;
        ppid = 0;
        comm = string.Empty;

        if (string.IsNullOrWhiteSpace(line))
            return false;

        var s = line!.Trim();

        int c1End = IndexOfWhitespace(s, 0);
        if (c1End <= 0) return false;
        if (!int.TryParse(s.AsSpan(0, c1End), out var parsedPid)) return false;

        int c2Start = SkipWhitespace(s, c1End);
        if (c2Start >= s.Length) return false;
        int c2End = IndexOfWhitespace(s, c2Start);
        if (c2End < 0) return false;
        if (!int.TryParse(s.AsSpan(c2Start, c2End - c2Start), out var parsedPpid)) return false;

        int c3Start = SkipWhitespace(s, c2End);
        if (c3Start >= s.Length) return false;
        var commandPath = s.Substring(c3Start);
        var parsedComm = Path.GetFileName(commandPath);

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
