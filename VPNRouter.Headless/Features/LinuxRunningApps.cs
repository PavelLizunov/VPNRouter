using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace VPNRouter.Headless.Features;

internal sealed record RunningApp(string name, string displayName, string? icon, string? desktopId, bool hasWindow);
internal sealed record DesktopApp(string Id, string Name, string? Icon, string[] MatchNames);

/// <summary>Best-effort, bounded Linux process discovery. Desktop files are metadata, never executed.</summary>
internal sealed class LinuxRunningApps
{
    private readonly string _procRoot;
    private readonly uint _uid;
    private readonly string[] _applicationDirs;
    private readonly Func<DateTime> _now;
    private readonly object _indexLock = new();
    private IReadOnlyDictionary<string, DesktopApp>? _index;
    private DateTime _indexExpires;

    internal LinuxRunningApps(string procRoot, uint uid, string[] applicationDirs, Func<DateTime>? now = null)
    {
        _procRoot = procRoot;
        _uid = uid;
        _applicationDirs = applicationDirs;
        _now = now ?? (() => DateTime.UtcNow);
    }

    internal static LinuxRunningApps CreateDefault() => new("/proc", GetUid(), ApplicationDirectories(
        Environment.GetEnvironmentVariable("XDG_DATA_DIRS"),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));

    // User entries override system entries, including Hidden/NoDisplay tombstones.
    internal static string[] ApplicationDirectories(string? xdgDataDirs, string home) =>
        new[] { Path.Combine(home, ".local/share/applications") }
            .Concat((string.IsNullOrWhiteSpace(xdgDataDirs) ? "/usr/local/share:/usr/share" : xdgDataDirs)
                .Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Where(Path.IsPathFullyQualified).Select(p => Path.Combine(p, "applications")))
            .Distinct(StringComparer.Ordinal).Take(32).ToArray();

    internal List<RunningApp> Read()
    {
        var timer = Stopwatch.StartNew();
        var index = GetDesktopIndex();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_procRoot).Take(8192))
            {
                if (timer.ElapsedMilliseconds >= 150) break;
                if (!uint.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out _)) continue;
                try
                {
                    if (ParseUid(ReadBounded(Path.Combine(dir, "status"), 8192)) != _uid) continue;
                    // Empty cmdline denotes a kernel thread or an already-exited process.
                    if (string.IsNullOrEmpty(ReadBounded(Path.Combine(dir, "cmdline"), 4096))) continue;
                    string? exe = null;
                    try { exe = new FileInfo(Path.Combine(dir, "exe")).LinkTarget; }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    var name = ProcessName(exe, string.IsNullOrEmpty(exe) ? ReadBounded(Path.Combine(dir, "comm"), 512) : "");
                    if (name != null) names.Add(name);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return Match(names, index);
    }

    internal static uint? ParseUid(string status)
    {
        foreach (var line in status.Split('\n'))
        {
            if (!line.StartsWith("Uid:", StringComparison.Ordinal)) continue;
            var field = line[4..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return uint.TryParse(field, out var uid) ? uid : null;
        }
        return null;
    }

    internal static string? ProcessName(string? exe, string comm)
    {
        if (exe?.EndsWith(" (deleted)", StringComparison.Ordinal) == true) exe = exe[..^10];
        var name = string.IsNullOrEmpty(exe) ? comm.TrimEnd('\r', '\n') : Path.GetFileName(exe);
        return string.IsNullOrWhiteSpace(name) || name.Length > 256 ? null : name;
    }

    internal static List<RunningApp> Match(IEnumerable<string> names, IReadOnlyDictionary<string, DesktopApp> index) =>
        names.Distinct(StringComparer.OrdinalIgnoreCase).Select(name =>
            index.TryGetValue(name, out var entry)
                ? new RunningApp(name, entry.Name, entry.Icon, entry.Id, false)
                : new RunningApp(name, name, null, null, false))
            .OrderBy(a => a.desktopId == null).ThenBy(a => a.displayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.name, StringComparer.Ordinal).Take(200).ToList();

    private IReadOnlyDictionary<string, DesktopApp> GetDesktopIndex()
    {
        lock (_indexLock)
        {
            var now = _now();
            if (_index != null && now < _indexExpires) return _index;
            _index = ReadDesktopIndex(_applicationDirs);
            _indexExpires = now.AddSeconds(30);
            return _index;
        }
    }

    internal static IReadOnlyDictionary<string, DesktopApp> ReadDesktopIndex(IEnumerable<string> directories)
    {
        var result = new Dictionary<string, DesktopApp>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var timer = Stopwatch.StartNew();
        int count = 0;
        foreach (var dir in directories)
        {
            try
            {
                // Bound recursion even when application directories contain links.
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true,
                    AttributesToSkip = 0, MaxRecursionDepth = 4 };
                foreach (var path in Directory.EnumerateFiles(dir, "*.desktop", options))
                {
                    if (++count > 4096 || timer.ElapsedMilliseconds >= 70) return result;
                    var id = Path.GetRelativePath(dir, path).Replace(Path.DirectorySeparatorChar, '-');
                    if (id.Length > 256 || !ids.Add(id)) continue;
                    try
                    {
                        var entry = ParseDesktop(id, ReadBounded(path, 32768));
                        if (entry == null) continue;
                        foreach (var key in entry.MatchNames) result.TryAdd(key, entry);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return result;
    }

    internal static DesktopApp? ParseDesktop(string id, string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        bool inEntry = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('#') || line.Length == 0) continue;
            if (line.StartsWith('[')) { inEntry = line == "[Desktop Entry]"; continue; }
            var equals = line.IndexOf('=');
            if (inEntry && equals > 0) fields[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }
        if (fields.GetValueOrDefault("Hidden") == "true" || fields.GetValueOrDefault("NoDisplay") == "true" ||
            (fields.TryGetValue("Type", out var type) && type != "Application")) return null;
        var name = Unescape(fields.GetValueOrDefault("Name", ""));
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256) return null;
        var keys = new[] { ExecBasename(fields.GetValueOrDefault("Exec", "")),
                fields.GetValueOrDefault("StartupWMClass"), Path.GetFileNameWithoutExtension(id) }
            .Where(s => !string.IsNullOrWhiteSpace(s) && s.Length <= 256).Select(s => s!).ToArray();
        var icon = Unescape(fields.GetValueOrDefault("Icon", ""));
        // Only icon theme names go on the wire. Never read an Icon path.
        if (icon.Length == 0 || icon.Length > 256 || icon.Contains('/') || icon.Contains('\\')) icon = null;
        return new DesktopApp(id, name, icon, keys);
    }

    internal static string? ExecBasename(string exec)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        bool quoted = false, escaped = false;
        foreach (var c in exec)
        {
            if (escaped) { word.Append(c); escaped = false; }
            else if (c == '\\') escaped = true;
            else if (c == '"') quoted = !quoted;
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (word.Length > 0) { words.Add(word.ToString()); word.Clear(); }
            }
            else word.Append(c);
        }
        if (word.Length > 0) words.Add(word.ToString());
        if (words.Count == 0 || quoted || escaped) return null;
        var command = words[0];
        if (Path.GetFileName(command) == "env")
            command = words.Skip(1).FirstOrDefault(w => !w.Contains('=') && !w.StartsWith('-'));
        return string.IsNullOrWhiteSpace(command) || command.StartsWith('%') ? null : Path.GetFileName(command);
    }

    private static string Unescape(string value) => value.Replace("\\s", " ").Replace("\\n", "\n")
        .Replace("\\t", "\t").Replace("\\r", "\r").Replace("\\\\", "\\");

    private static string ReadBounded(string path, int limit)
    {
        using var stream = File.OpenRead(path);
        var bytes = new byte[limit];
        int read = stream.ReadAtLeast(bytes, limit, throwOnEndOfStream: false);
        return Encoding.UTF8.GetString(bytes, 0, read);
    }

    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUid();
}
