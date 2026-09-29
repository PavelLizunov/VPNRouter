using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class AutostartContractTests
{
    [Fact]
    public void IsInstalled_FalseWhenProxyDirMissing()
    {
        using var sandbox = new TempSandbox();

        var pythonDir = Path.Combine(sandbox.Root, "python");
        Directory.CreateDirectory(pythonDir);
        File.WriteAllText(Path.Combine(pythonDir, "python.exe"), "stub");

        Assert.False(
            TgProxyUpdater.IsInstalledAt(sandbox.Root),
            "IsInstalled should be false when proxy/ directory is missing.");
    }

    [Fact]
    public void IsInstalled_FalseWhenPythonMissing()
    {
        using var sandbox = new TempSandbox();

        Directory.CreateDirectory(Path.Combine(sandbox.Root, "proxy"));

        Assert.False(
            TgProxyUpdater.IsInstalledAt(sandbox.Root),
            "IsInstalled should be false when python/python.exe is missing.");
    }

    [Fact]
    public void IsInstalled_TrueWhenBothPresent()
    {
        using var sandbox = new TempSandbox();

        var pythonDir = Path.Combine(sandbox.Root, "python");
        Directory.CreateDirectory(pythonDir);
        File.WriteAllText(Path.Combine(pythonDir, "python.exe"), "stub");
        var certifiDir = Path.Combine(pythonDir, "Lib", "certifi");
        Directory.CreateDirectory(certifiDir);
        File.WriteAllText(Path.Combine(certifiDir, "__init__.py"), "stub");
        Directory.CreateDirectory(Path.Combine(sandbox.Root, "proxy"));

        Assert.True(
            TgProxyUpdater.IsInstalledAt(sandbox.Root),
            "IsInstalled should be true when both python.exe and proxy/ are present.");
    }

    [Fact]
    public void IsInstalled_FalseWhenCertifiMissing()
    {
        using var sandbox = new TempSandbox();
        var pythonDir = Path.Combine(sandbox.Root, "python");
        Directory.CreateDirectory(pythonDir);
        File.WriteAllText(Path.Combine(pythonDir, "python.exe"), "stub");
        Directory.CreateDirectory(Path.Combine(sandbox.Root, "proxy"));

        Assert.False(TgProxyUpdater.IsInstalledAt(sandbox.Root));
    }

    private static string? ReadVpnRouterServiceSourceOrSkip()
    {
        var path = FindSource("VPNRouter.Service", "VPNRouterService.cs");
        return path == null ? null : File.ReadAllText(path);
    }

    private static string? ReadMainWindowViewModelSourceOrSkip()
    {
        var path = FindSource(
            Path.Combine("VPNRouter.App", "ViewModels"),
            "MainWindowViewModel.cs");
        return path == null ? null : File.ReadAllText(path);
    }

    private static string? FindSource(string relativeProjectDir, string fileName)
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relativeProjectDir, fileName);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static string StripLineComments(string src) =>
        string.Join("\n",
            src.Split('\n')
               .Select(l => l.Contains("//") ? l[..l.IndexOf("//")] : l));

    private static string? ExtractMethodBody(string src, string methodName)
    {
        var stripped = StripLineComments(src);

        var declRegex = new Regex(
            @"\b(?:[A-Za-z_][A-Za-z0-9_]*(?:<[^>]+>)?)\s+" +
                Regex.Escape(methodName) + @"\s*\(",
            RegexOptions.Multiline);

        foreach (Match m in declRegex.Matches(stripped))
        {
            var precIdx = m.Index - 1;
            while (precIdx >= 0 && char.IsWhiteSpace(stripped[precIdx]))
                precIdx--;
            if (precIdx >= 0)
            {
                var prev = stripped[precIdx];
                if (prev == '=' || prev == '(' || prev == ',' || prev == '.')
                    continue;
            }

            var openBraceIdx = stripped.IndexOf('{', m.Index + m.Length);
            if (openBraceIdx < 0) continue;

            int depth = 0;
            for (int i = openBraceIdx; i < stripped.Length; i++)
            {
                if (stripped[i] == '{') depth++;
                else if (stripped[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                        return stripped.Substring(openBraceIdx, i - openBraceIdx + 1);
                }
            }
        }
        return null;
    }

    private static void AssertHasBoolPropertyWithYamlAlias(
        Type t, string propertyName, string expectedAlias)
    {
        var prop = t.GetProperty(propertyName,
            BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(prop);
        Assert.Equal(typeof(bool), prop!.PropertyType);

        var yamlAttr = prop.GetCustomAttributes(inherit: false)
            .FirstOrDefault(a => a.GetType().Name == "YamlMemberAttribute");
        Assert.NotNull(yamlAttr);

        var aliasProp = yamlAttr!.GetType()
            .GetProperty("Alias", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(aliasProp);

        var actualAlias = aliasProp!.GetValue(yamlAttr) as string;
        Assert.Equal(expectedAlias, actualAlias);
    }

    private sealed class TempSandbox : IDisposable
    {
        public string Root { get; }
        public TempSandbox()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "vpnrouter-autostart-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }
        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
