using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using VPNRouter.Core.Services;

namespace VPNRouter.App.Services;

internal sealed record AssistantRunningApplication(string Executable, string? ProductName, string? DisplayName);

internal static class AssistantApplicationTarget
{
    private const string NameStart = @"(?<![\p{L}\p{N}_.:/\\-])";
    private const string NameEnd = @"(?![\p{L}\p{N}_:/\\-]|\.[\p{L}\p{N}])";
    private const RegexOptions NameOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly Dictionary<string, string> WindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = "chrome.exe", ["google chrome"] = "chrome.exe",
        ["хром"] = "chrome.exe", ["гугл хром"] = "chrome.exe",
        ["edge"] = "msedge.exe", ["microsoft edge"] = "msedge.exe", ["msedge"] = "msedge.exe", ["эдж"] = "msedge.exe",
        ["firefox"] = "firefox.exe", ["mozilla firefox"] = "firefox.exe", ["файрфокс"] = "firefox.exe", ["фаерфокс"] = "firefox.exe",
        ["brave"] = "brave.exe", ["brave browser"] = "brave.exe", ["брейв"] = "brave.exe",
        ["opera"] = "opera.exe", ["опера"] = "opera.exe",
        ["vivaldi"] = "vivaldi.exe", ["вивальди"] = "vivaldi.exe",
        ["yandex browser"] = "browser.exe", ["яндекс браузер"] = "browser.exe", ["яндекс.браузер"] = "browser.exe",
        ["chromium"] = "chromium.exe", ["хромиум"] = "chromium.exe",
        ["telegram"] = "Telegram.exe", ["телеграм"] = "Telegram.exe",
        ["discord"] = "Discord.exe", ["дискорд"] = "Discord.exe",
        ["steam"] = "steam.exe", ["стим"] = "steam.exe",
        ["slack"] = "slack.exe", ["слак"] = "slack.exe",
        ["zoom"] = "Zoom.exe", ["зум"] = "Zoom.exe",
        ["spotify"] = "Spotify.exe", ["спотифай"] = "Spotify.exe",
        ["vs code"] = "Code.exe", ["visual studio code"] = "Code.exe",
    };

    private static readonly HashSet<string> WindowsBrowsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe", "browser.exe", "chromium.exe",
    };

    private static readonly Regex NamedApps = new(NameStart + "(?:" +
        string.Join("|", WindowsNames.Keys.OrderByDescending(name => name.Length).Select(Regex.Escape)) + ")" + NameEnd, NameOptions);
    private static readonly Regex Executables = new(NameStart +
        """(?:"(?<exe>[\p{L}\p{N}_. -]{1,124}\.exe)"|(?<exe>[\p{L}\p{N}_.-]{1,124}\.exe))""" + NameEnd, NameOptions);
    private static readonly Regex Browser = new(NameStart +
        "(?:browser|браузер(?:а|у|ом|е)?)" + NameEnd, NameOptions);
    private static readonly Regex SharedExecutableVariants = new(NameStart +
        @"(?:(?:google\s+)?chrome\s+(?:canary|beta|dev|chromium)|хром\s+(?:канари|бета|дев)|(?:microsoft\s+)?edge\s+(?:canary|beta|dev|webview2?)|эдж\s+(?:канари|бета|дев|вебвью)|(?:mozilla\s+)?firefox\s+(?:developer(?:\s+edition)?|nightly|beta|esr)|opera\s+(?:gx|developer|beta|air|neon)|опера\s+(?:gx|джи\s+икс)|brave\s+(?:nightly|beta)|vivaldi\s+snapshot|discord\s+(?:canary|ptb)|telegram\s+(?:portable|beta)|(?:vs\s+code|visual\s+studio\s+code)\s+insiders)" +
        NameEnd, NameOptions);

    internal static string? ResolveRequested(string goal, string proposal)
    {
        var windows = OperatingSystem.IsWindows();
        var defaultBrowser = windows && Browser.IsMatch(goal) ? ReadWindowsDefaultBrowser() : null;
        return ResolveRequested(goal, proposal, windows,
            name => string.Equals(name, defaultBrowser, StringComparison.OrdinalIgnoreCase) ? defaultBrowser : FindProcess(name, windows),
            defaultBrowser, windows ? ReadRunningApplications : null);
    }

    internal static string? ResolveRequested(string goal, string proposal, bool windows,
        Func<string, string?> findProcess, string? defaultBrowser = null,
        Func<IReadOnlyList<AssistantRunningApplication>>? readRunningApplications = null)
    {
        proposal = proposal.Trim();
        if (goal.Length > 768 || !ValidName(proposal) || goal.Any(c => char.GetUnicodeCategory(c) == UnicodeCategory.Format))
            return null;
        // Both Windows and Unix route on a process name. A file path, launcher argument, or
        // variant that shares a process name with a different edition cannot refine the rule.
        if (windows && SharedExecutableVariants.IsMatch(goal))
            return null;

        if (!windows)
            return ContainsName(goal, proposal) && !Browser.IsMatch(proposal)
                ? VerifyProcess(proposal, findProcess, StringComparison.Ordinal)
                : null;

        var target = WindowsNames.GetValueOrDefault(proposal) ?? RoutingAppListEditor.NormalizeManualProcessName(proposal, windows: true);
        var mentioned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Executables.Matches(goal))
            mentioned.Add(match.Groups["exe"].Value);
        foreach (Match match in NamedApps.Matches(goal))
            if (WindowsNames.TryGetValue(match.Value, out var name))
                mentioned.Add(name);

        if (mentioned.Count == 0 && Browser.IsMatch(goal))
        {
            if (defaultBrowser == null || !WindowsBrowsers.Contains(defaultBrowser)
                || (!IsBrowserPlaceholder(proposal) && !string.Equals(target, defaultBrowser, StringComparison.OrdinalIgnoreCase)))
                return null;
            return VerifyProcess(defaultBrowser, findProcess, StringComparison.OrdinalIgnoreCase);
        }

        if (mentioned.Count == 0 && readRunningApplications != null && !IsBrowserPlaceholder(proposal))
        {
            return ResolveRunningApplication(goal, proposal, target, readRunningApplications());
        }

        if (target == null || mentioned.Count != 1 || !mentioned.Contains(target)
            || (Browser.IsMatch(goal) && !WindowsBrowsers.Contains(target))
            || (IsBrowserPlaceholder(proposal) && !proposal.Equals("browser.exe", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }
        return VerifyProcess(target, findProcess, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveRunningApplication(string goal, string proposal, string? target,
        IReadOnlyList<AssistantRunningApplication> apps)
    {
        var matches = apps.Where(app => NameMatches(app.ProductName, goal) || NameMatches(app.DisplayName, goal))
            .DistinctBy(app => app.Executable, StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        if (matches is not [var app] || !ValidName(app.Executable) || !app.Executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return null;
        return string.Equals(app.Executable, target, StringComparison.OrdinalIgnoreCase)
            || NameMatches(app.ProductName, proposal, complete: true)
            || NameMatches(app.DisplayName, proposal, complete: true)
            ? app.Executable : null;

        static bool NameMatches(string? name, string value, bool complete = false)
        {
            name = name?.Trim();
            if (name is not { Length: >= 3 } || !ValidName(name) || IsBrowserPlaceholder(name))
                return false;
            return complete ? name.Equals(value, StringComparison.OrdinalIgnoreCase)
                : Regex.IsMatch(value, NameStart + Regex.Escape(name) + NameEnd, NameOptions);
        }
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<AssistantRunningApplication> ReadRunningApplications()
    {
        using var current = Process.GetCurrentProcess();
        var session = current.SessionId;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var apps = new List<AssistantRunningApplication>();
        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (process.SessionId != session || process.MainWindowHandle == IntPtr.Zero
                        || process.MainModule?.FileName is not { } path || !paths.Add(path))
                        continue;
                    var metadata = FileVersionInfo.GetVersionInfo(path);
                    apps.Add(new(Path.GetFileName(path), metadata.ProductName, metadata.FileDescription));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                    or IOException or UnauthorizedAccessException)
                {
                    // The process or its file may disappear while the assistant reads it.
                }
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
        return apps;
    }

    private static bool ContainsName(string goal, string name) =>
        Regex.IsMatch(goal, NameStart + Regex.Escape(name) + NameEnd, RegexOptions.CultureInvariant);

    private static bool IsBrowserPlaceholder(string proposal) =>
        proposal.Equals("browser", StringComparison.OrdinalIgnoreCase)
        || proposal.Equals("default browser", StringComparison.OrdinalIgnoreCase)
        || proposal.Equals("browser.exe", StringComparison.OrdinalIgnoreCase)
        || proposal.Equals("браузер", StringComparison.OrdinalIgnoreCase)
        || proposal.Equals("браузер по умолчанию", StringComparison.OrdinalIgnoreCase);

    private static bool ValidName(string name) => name.Length is > 0 and <= 128
        && name.Any(char.IsLetterOrDigit)
        && name.All(c => char.IsLetterOrDigit(c) || c is ' ' or '.' or '_' or '-');

    private static string? VerifyProcess(string requested, Func<string, string?> findProcess, StringComparison comparison)
    {
        if (!ValidName(requested)) return null;
        var found = findProcess(requested);
        return string.Equals(found, requested, comparison) ? found : null;
    }

    private static string? FindProcess(string name, bool windows)
    {
        var processes = Process.GetProcessesByName(windows ? Path.GetFileNameWithoutExtension(name) : name);
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    var actual = Path.GetFileName(process.MainModule?.FileName);
                    if (string.Equals(actual, name, windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        return actual;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // The process can exit or become inaccessible while it is being inspected.
                }
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }

        return OperatingSystem.IsWindows() ? FindWindowsRegistration(name) : null;
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadWindowsDefaultBrowser()
    {
        try
        {
            var https = ReadBrowserHandler("https");
            var http = ReadBrowserHandler("http");
            if (https == null || !string.Equals(http, https, StringComparison.OrdinalIgnoreCase))
                return null;
            var name = Path.GetFileName(https);
            return WindowsBrowsers.Contains(name) ? name : null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadBrowserHandler(string scheme)
    {
        using var choice = Registry.CurrentUser.OpenSubKey(
            $@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\{scheme}\UserChoice");
        if (choice?.GetValue("ProgId") is not string progId || !ValidName(progId) || progId.Contains(' '))
            return null;
        using var handler = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
        if (handler?.GetValue(null) is not string registration)
            return null;
        var command = registration.Trim();
        if (command.Length == 0) return null;
        var end = command[0] == '"' ? command.IndexOf('"', 1) : command.IndexOf(' ');
        var executable = end >= 0 ? command[..end].TrimStart('"') :
            command[0] == '"' ? null : command;
        if (executable == null) return null;
        executable = Environment.ExpandEnvironmentVariables(executable);
        return executable.Length >= 4 && char.IsAsciiLetter(executable[0]) && executable[1] == ':'
            && Path.IsPathFullyQualified(executable) && File.Exists(executable)
            ? executable : null;
    }

    [SupportedOSPlatform("windows")]
    private static string? FindWindowsRegistration(string name)
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{name}");
            if (key?.GetValue(null) is string path && Path.IsPathFullyQualified(path) && File.Exists(path)
                && string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase))
                return Path.GetFileName(path);
        }

        return null;
    }
}
