#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VPNRouter.App.Services;

internal static partial class SteamLibraryScanner
{
    private static readonly string[] SkipExePrefixes =
    [
        "unins",
        "uninstall",
        "crash",
        "unitycrashhandler",
        "vc_redist",
        "setup",
        "dxsetup",
    ];

    private const int MaxGameDirectories = 256;

    private static readonly EnumerationOptions GameEntries = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private static readonly HashSet<string> SupportDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "_CommonRedist", "Redist", "Redistributables", "Engine", "Prerequisites", "DirectX", "EasyAntiCheat", "BattlEye",
    };

    internal static IReadOnlyList<SteamGameExecutable> FindInstalledGames(CancellationToken ct = default)
    {
        var roots = FindSteamRoots();
        return FindGames(roots, ct);
    }

    internal static IReadOnlyList<SteamGameExecutable> FindGames(IEnumerable<string> steamRoots, CancellationToken ct = default)
    {
        var results = new List<SteamGameExecutable>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var library in FindLibraryFolders(steamRoots))
        {
            var steamApps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamApps)) continue;

            foreach (var manifest in SafeEnumerateFiles(steamApps, "appmanifest_*.acf"))
            {
                ct.ThrowIfCancellationRequested();
                var (name, installDir) = ReadManifest(manifest);
                if (string.IsNullOrWhiteSpace(installDir) || installDir is "." or ".." ||
                    installDir.IndexOfAny(['/', '\\', ':']) >= 0) continue;

                var gameDir = Path.Combine(steamApps, "common", installDir);
                if (!Directory.Exists(gameDir)) continue;

                foreach (var exe in EnumerateCandidateExecutables(gameDir, ct))
                {
                    var processName = Path.GetFileName(exe);
                    if (!seen.Add(processName)) continue;
                    results.Add(new SteamGameExecutable(name ?? installDir, processName, exe));
                }
            }
        }

        return results.OrderBy(g => g.GameName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> FindSteamRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string steamPath && Directory.Exists(steamPath))
                yield return steamPath;
        }

        const string defaultSteam = @"C:\Program Files (x86)\Steam";
        if (Directory.Exists(defaultSteam))
            yield return defaultSteam;
    }

    private static IEnumerable<string> FindLibraryFolders(IEnumerable<string> steamRoots)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in steamRoots.Where(Directory.Exists))
        {
            if (seen.Add(root))
                yield return root;

            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;

            foreach (Match match in VdfPathRegex().Matches(ReadAllTextOrEmpty(vdf)))
            {
                var path = UnescapeSteamPath(match.Groups[1].Value);
                if (Directory.Exists(path) && seen.Add(path))
                    yield return path;
            }
        }
    }

    private static (string? Name, string? InstallDir) ReadManifest(string manifestPath)
    {
        var text = ReadAllTextOrEmpty(manifestPath);
        if (text.Length == 0) return (null, null);
        return (
            ReadVdfValue(text, "name"),
            ReadVdfValue(text, "installdir"));
    }

    private static string? ReadVdfValue(string text, string key)
    {
        var match = Regex.Match(text, $"\"{Regex.Escape(key)}\"\\s+\"([^\"]+)\"");
        return match.Success ? UnescapeSteamPath(match.Groups[1].Value) : null;
    }

    private static IEnumerable<string> EnumerateCandidateExecutables(string gameDir, CancellationToken ct)
    {
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((gameDir, 0));
        int scheduled = 1;
        while (pending.TryDequeue(out var current))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var exe in SafeEnumerateFiles(current.Path, "*.exe"))
                if (!ShouldSkipExe(Path.GetFileNameWithoutExtension(exe)))
                    yield return exe;

            if (current.Depth == 3 || scheduled == MaxGameDirectories) continue;
            bool inBin = IsBinaryDirectory(Path.GetFileName(current.Path));
            foreach (var child in SafeEnumerateDirectories(current.Path))
            {
                var name = Path.GetFileName(child);
                if (SupportDirectories.Contains(name)) continue;
                if (current.Depth > 0 && !IsBinaryDirectory(name) && !(inBin && IsWindowsArchitecture(name))) continue;
                pending.Enqueue((child, current.Depth + 1));
                if (++scheduled == MaxGameDirectories) break;
            }
        }
    }

    private static bool IsBinaryDirectory(string name) =>
        name.Equals("bin", StringComparison.OrdinalIgnoreCase) || name.Equals("binaries", StringComparison.OrdinalIgnoreCase);

    private static bool IsWindowsArchitecture(string name) =>
        name.ToLowerInvariant() is "win32" or "win64" or "x86" or "x64" or "wingdk" or "windows";

    private static bool ShouldSkipExe(string name)
        => SkipExePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private static string ReadAllTextOrEmpty(string path)
    {
        try { return File.ReadAllText(path); }
        catch (IOException) { return string.Empty; }
        catch (UnauthorizedAccessException) { return string.Empty; }
        catch (System.Security.SecurityException) { return string.Empty; }
    }

    private static IReadOnlyList<string> SafeEnumerateFiles(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern, GameEntries).ToArray(); }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
        catch (System.Security.SecurityException) { return Array.Empty<string>(); }
    }

    private static IReadOnlyList<string> SafeEnumerateDirectories(string dir)
    {
        try { return Directory.EnumerateDirectories(dir, "*", GameEntries).Take(MaxGameDirectories).ToArray(); }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
        catch (System.Security.SecurityException) { return Array.Empty<string>(); }
    }

    private static string UnescapeSteamPath(string path)
        => path.Replace(@"\\", @"\");

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex VdfPathRegex();
}

internal sealed record SteamGameExecutable(string GameName, string ProcessName, string Path);
