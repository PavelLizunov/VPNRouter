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

    internal static IReadOnlyList<SteamGameExecutable> FindInstalledGames()
    {
        var roots = FindSteamRoots();
        return FindGames(roots);
    }

    internal static IReadOnlyList<SteamGameExecutable> FindGames(IEnumerable<string> steamRoots)
    {
        var results = new List<SteamGameExecutable>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var library in FindLibraryFolders(steamRoots))
        {
            var steamApps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamApps)) continue;

            foreach (var manifest in SafeEnumerateFiles(steamApps, "appmanifest_*.acf"))
            {
                var (name, installDir) = ReadManifest(manifest);
                if (string.IsNullOrWhiteSpace(installDir)) continue;

                var gameDir = Path.Combine(steamApps, "common", installDir);
                if (!Directory.Exists(gameDir)) continue;

                foreach (var exe in EnumerateCandidateExecutables(gameDir))
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

    private static IEnumerable<string> EnumerateCandidateExecutables(string gameDir)
    {
        var searchDirs = SafeEnumerateDirectories(gameDir).Prepend(gameDir);
        foreach (var dir in searchDirs)
        {
            foreach (var exe in SafeEnumerateFiles(dir, "*.exe"))
            {
                if (!ShouldSkipExe(Path.GetFileNameWithoutExtension(exe)))
                    yield return exe;
            }
        }
    }

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
        try { return Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly).ToArray(); }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
        catch (System.Security.SecurityException) { return Array.Empty<string>(); }
    }

    private static IReadOnlyList<string> SafeEnumerateDirectories(string dir)
    {
        try { return Directory.EnumerateDirectories(dir).ToArray(); }
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
