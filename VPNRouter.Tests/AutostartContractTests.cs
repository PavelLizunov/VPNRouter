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
        SeedPinnedRuntime(sandbox.Root);

        Assert.True(
            TgProxyUpdater.IsInstalledAt(sandbox.Root),
            "IsInstalled should be true when python.exe, proxy/ and pinned dependencies are present.");
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

    [Fact]
    public void IsInstalled_FalseForLegacyMarkerOnlyRuntime()
    {
        using var sandbox = new TempSandbox();
        var pythonDir = Path.Combine(sandbox.Root, "python");
        Directory.CreateDirectory(pythonDir);
        File.WriteAllText(Path.Combine(pythonDir, "python.exe"), "stub");
        Directory.CreateDirectory(Path.Combine(pythonDir, "Lib", "certifi"));
        File.WriteAllText(Path.Combine(pythonDir, "Lib", "certifi", "__init__.py"), "stub");
        Directory.CreateDirectory(Path.Combine(sandbox.Root, "proxy"));

        Assert.False(
            TgProxyUpdater.IsInstalledAt(sandbox.Root),
            "An old certifi __init__.py marker must not make the vulnerable runtime appear installed.");
    }

    [Fact]
    public void IsInstalled_FalseForLegacyPythonVersion()
    {
        using var sandbox = new TempSandbox();
        SeedPinnedRuntime(sandbox.Root, pythonVersion: "3.12.7");

        Assert.False(
            TgProxyUpdater.IsInstalledAt(sandbox.Root),
            "A legacy Python embeddable must be upgraded before the runtime is launched.");
    }

    [Fact]
    public void IsInstalled_FalseWhenCryptographyBinaryMissing()
    {
        using var sandbox = new TempSandbox();
        SeedPinnedRuntime(sandbox.Root);
        File.Delete(Path.Combine(
            sandbox.Root, "python", "Lib", "cryptography", "hazmat", "bindings", "_rust.pyd"));

        Assert.False(TgProxyUpdater.IsInstalledAt(sandbox.Root));
    }

    [Fact]
    public void IsInstalled_RejectsStaleSourceAndAnInterruptedActivation()
    {
        using var sandbox = new TempSandbox();
        SeedPinnedRuntime(sandbox.Root);
        var version = Path.Combine(sandbox.Root, "version.txt");
        File.WriteAllText(version, "older");
        Assert.False(TgProxyUpdater.IsInstalledAt(sandbox.Root));
        File.WriteAllText(version, TgProxyUpdater.SupportedProxyVersion);
        Directory.CreateDirectory(Path.Combine(sandbox.Root, ".activating"));
        Assert.False(TgProxyUpdater.IsInstalledAt(sandbox.Root));
    }

    internal static void SeedPinnedRuntime(string root, string pythonVersion = TgProxyUpdater.PythonVersion)
    {
        var pythonDir = Path.Combine(root, "python");
        Directory.CreateDirectory(pythonDir);
        foreach (var (name, size) in TgProxyUpdater.PythonRuntimeFiles)
        {
            using var file = File.Create(Path.Combine(pythonDir, name));
            file.SetLength(size);
        }
        File.WriteAllLines(Path.Combine(pythonDir, "python314._pth"), ["python314.zip", ".", "..", "Lib"]);
        File.WriteAllText(
            Path.Combine(pythonDir, TgProxyUpdater.PythonVersionMarkerFileName),
            pythonVersion);
        SeedFile(root, "proxy/tg_ws_proxy.py");
        File.WriteAllText(Path.Combine(root, "version.txt"), TgProxyUpdater.SupportedProxyVersion);

        var lib = Path.Combine(pythonDir, "Lib");
        SeedFile(lib, "certifi/__init__.py");
        SeedFile(lib, "certifi/cacert.pem");
        SeedFile(lib, "cryptography/__init__.py");
        SeedFile(lib, "cryptography/hazmat/bindings/_rust.pyd");
        SeedFile(lib, "cffi/__init__.py");
        SeedFile(lib, "_cffi_backend.cp314-win_amd64.pyd");
        SeedFile(lib, "pycparser/__init__.py");
        SeedDirectory(lib, "certifi-2026.11.20.dist-info");
        SeedDirectory(lib, "cryptography-50.0.2.dist-info");
        SeedDirectory(lib, "cffi-2.1.1.dist-info");
        SeedDirectory(lib, "pycparser-3.0.dist-info");
    }

    private static void SeedFile(string root, string relativePath)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "stub");
    }

    private static void SeedDirectory(string root, string relativePath) =>
        Directory.CreateDirectory(Path.Combine(root, relativePath));

    private static string? ReadVpnRouterServiceSourceOrSkip()
    {
        var path = FindSource("VPNRouter.Service", "VPNRouterService.cs");
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
