namespace VPNRouter.Tests;

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Xunit;

public sealed class VpnctlPackagingCharacterizationTests
{
    [Fact]
    [Trait("Category", "PackagingCharacterization")]
    [Trait("Baseline", "LegacyDefectGuards")]
    public void SigningWorkflow_NoLegacyBuilderOrSingBoxPath()
    {
        var workflow = StripComments(Read(".github", "workflows", "sign-windows.yml"));
        Assert.DoesNotContain("build-singbox-lx.ps1", workflow);
        Assert.DoesNotContain("SingBoxPath", workflow);
        Assert.DoesNotContain("publish/sing-box-lx.exe", workflow);
        Assert.Contains("./build.ps1 -Version $env:VERSION -BundleSplitDriver", workflow);
    }

    [Fact]
    [Trait("Category", "PackagingCharacterization")]
    [Trait("Baseline", "LegacyDefectGuards")]
    public void BuildScript_DefaultNoAutoSelect_PublishSingBoxLxRemoved()
    {
        var script = StripComments(Read("build.ps1"));
        Assert.DoesNotContain(@"publish\sing-box-lx.exe", script);
        Assert.DoesNotContain("Auto-selected local sing-box-lx", script);
        Assert.DoesNotContain("$effectiveSingBoxPath", script);
    }

    [Fact]
    [Trait("Category", "PackagingCharacterization")]
    [Trait("Baseline", "LegacyDefectGuards")]
    public void BuildScript_MandatoryVersionAndHashValidation_BeforeFirstPublish()
    {
        var script = StripComments(Read("build.ps1"));
        const string uploadGuard = "SingBoxPath override is for local builds only and cannot be used with -Upload.";
        const string versionPattern = @"^[0-9]+\.[0-9]+\.[0-9]+-vpnctl\.[0-9]+$";
        const string sha256Pattern = "SingBoxSha256 must be a non-blank 64-character hex string";
        const string cleanStep = "[1/9] Cleaning previous build...";

        Assert.Contains(uploadGuard, script);
        Assert.Contains(versionPattern, script);
        Assert.Contains(sha256Pattern, script);

        var shaIndex = script.IndexOf(sha256Pattern, StringComparison.Ordinal);
        var cleanIndex = script.IndexOf(cleanStep, StringComparison.Ordinal);
        Assert.True(shaIndex >= 0 && cleanIndex >= 0 && shaIndex < cleanIndex,
            "Mandatory version/hash validation must occur before cleaning or publishing artifacts.");
    }

    [Fact]
    [Trait("Category", "PackagingCharacterization")]
    [Trait("Baseline", "LegacyDefectGuards")]
    public void BuildScript_HashCheckAndExtraction_OutsideCachedExeExistenceBranch()
    {
        var script = Read("build.ps1");
        var idx1 = script.IndexOf("# ── Bundle sing-box.exe ──", StringComparison.Ordinal);
        var idx2 = script.IndexOf("# ── slipstream-client.exe", idx1, StringComparison.Ordinal);
        Assert.True(idx1 >= 0 && idx2 > idx1, "Could not locate sing-box bundling section.");

        var bundling = StripComments(script.Substring(idx1, idx2 - idx1));
        var hashIndex = bundling.IndexOf("Get-FileHash", StringComparison.Ordinal);
        var expandIndex = bundling.IndexOf("Expand-Archive", StringComparison.Ordinal);
        Assert.True(hashIndex >= 0 && expandIndex > hashIndex, "Get-FileHash must execute before Expand-Archive.");
        Assert.DoesNotContain("if (-not (Test-Path $cachedExe))", bundling[..hashIndex]);
    }

    [Fact]
    [Trait("Category", "PackagingCharacterization")]
    [Trait("Baseline", "LegacyDefectGuards")]
    public void Behavioral_CorrectCachedZip_WithTamperedExtractedExe_RestoresFromZip()
    {
        var (temp, distDir, pwsh, script) = SetupBehavioral();
        try
        {
            var cacheDir = Path.Combine(temp, "tools", "singbox-cache");
            var zipPath = Path.Combine(cacheDir, "sing-box-1.14.0-vpnctl.3-windows-amd64.zip");
            var hash = CreateZipWithEntry(zipPath, "sing-box-1.14.0-vpnctl.3-windows-amd64/sing-box.exe", "AUTHENTIC_EXE");

            var extractDir = Path.Combine(cacheDir, "sing-box-1.14.0-vpnctl.3-windows-amd64");
            Directory.CreateDirectory(extractDir);
            File.WriteAllText(Path.Combine(extractDir, "sing-box.exe"), "TAMPERED_EXE");

            var (exitCode, stdout, stderr) = RunPwsh(pwsh, script,
                "-Root", temp, "-DistDir", distDir,
                "-SingBoxVersion", "1.14.0-vpnctl.3", "-SingBoxSha256", hash);

            Assert.True(exitCode == 0, $"Exit code {exitCode}: {stderr}\n{stdout}");
            var bundledExe = Path.Combine(distDir, "sing-box.exe");
            Assert.True(File.Exists(bundledExe));
            Assert.Equal("AUTHENTIC_EXE", File.ReadAllText(bundledExe));
        }
        finally { Cleanup(temp); }
    }

    [Fact]
    [Trait("Category", "PackagingCharacterization")]
    [Trait("Baseline", "LegacyDefectGuards")]
    public void Behavioral_CorruptedArchive_ExistingExe_Rejects()
    {
        var (temp, distDir, pwsh, script) = SetupBehavioral();
        try
        {
            var cacheDir = Path.Combine(temp, "tools", "singbox-cache");
            Directory.CreateDirectory(cacheDir);
            File.WriteAllBytes(Path.Combine(cacheDir, "sing-box-1.14.0-vpnctl.3-windows-amd64.zip"), new byte[] { 0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0 });

            var extractDir = Path.Combine(cacheDir, "sing-box-1.14.0-vpnctl.3-windows-amd64");
            Directory.CreateDirectory(extractDir);
            File.WriteAllText(Path.Combine(extractDir, "sing-box.exe"), "EXISTING_EXE");

            var (exitCode, stdout, stderr) = RunPwsh(pwsh, script,
                "-Root", temp, "-DistDir", distDir, "-SingBoxVersion", "1.14.0-vpnctl.3",
                "-SingBoxSha256", "8094929df6c4b061dc9c360b1641474d41bdea16845d604a26d3721feefc6f74");

            Assert.NotEqual(0, exitCode);
            Assert.Contains("SHA256 mismatch", stdout + stderr);
        }
        finally { Cleanup(temp); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid_short_hash")]
    [InlineData("0123456789abcdef")]
    [Trait("Category", "PackagingCharacterization")]
    [Trait("Baseline", "LegacyDefectGuards")]
    public void Behavioral_EmptyOrMalformedHash_Rejects(string malformedHash)
    {
        var (temp, distDir, pwsh, script) = SetupBehavioral();
        try
        {
            var (exitCode, stdout, stderr) = RunPwsh(pwsh, script,
                "-Root", temp, "-DistDir", distDir,
                "-SingBoxVersion", "1.14.0-vpnctl.3", "-SingBoxSha256", malformedHash);

            Assert.NotEqual(0, exitCode);
            Assert.Contains("SingBoxSha256 must be a non-blank 64-character hex string", stdout + stderr);
        }
        finally { Cleanup(temp); }
    }

    [Theory]
    [InlineData("../escape")]
    [Trait("Category", "PackagingCharacterization")]
    [Trait("Baseline", "LegacyDefectGuards")]
    public void Behavioral_InvalidVersion_RejectedBeforeIO(string invalidVersion)
    {
        var (temp, distDir, pwsh, script) = SetupBehavioral();
        try
        {
            var (exitCode, stdout, stderr) = RunPwsh(pwsh, script,
                "-Root", temp, "-DistDir", distDir,
                "-SingBoxVersion", invalidVersion,
                "-SingBoxSha256", "8094929df6c4b061dc9c360b1641474d41bdea16845d604a26d3721feefc6f74");

            Assert.NotEqual(0, exitCode);
            Assert.Contains("SingBoxVersion must match pattern", stdout + stderr);
        }
        finally { Cleanup(temp); }
    }

    [Fact]
    [Trait("Category", "PackagingCharacterization")]
    [Trait("Baseline", "LegacyDefectGuards")]
    public void Behavioral_CustomPathWithUpload_RejectedEarlyNoExec()
    {
        var (temp, distDir, pwsh, script) = SetupBehavioral();
        try
        {
            var customExe = Path.Combine(temp, "custom-sing-box.exe");
            File.WriteAllText(customExe, "CUSTOM_PAYLOAD");

            var (exitCode, stdout, stderr) = RunPwsh(pwsh, script,
                "-Root", temp, "-DistDir", distDir,
                "-SingBoxPath", customExe, "-Upload");

            Assert.NotEqual(0, exitCode);
            Assert.Contains("SingBoxPath override is for local builds only and cannot be used with -Upload", stdout + stderr);
        }
        finally { Cleanup(temp); }
    }

    [Fact]
    [Trait("Category", "PackagingCharacterization")]
    [Trait("Baseline", "LegacyDefectGuards")]
    public void Behavioral_OptionalLocalCustomOverride_CopiesTestPayload_NoHttp()
    {
        var (temp, distDir, pwsh, script) = SetupBehavioral();
        try
        {
            var customExe = Path.Combine(temp, "custom-sing-box.exe");
            File.WriteAllText(customExe, "CUSTOM_PAYLOAD");

            var (exitCode, stdout, stderr) = RunPwsh(pwsh, script,
                "-Root", temp, "-DistDir", distDir,
                "-SingBoxPath", customExe);

            Assert.True(exitCode == 0, $"Exit code {exitCode}: {stderr}\n{stdout}");
            var bundledExe = Path.Combine(distDir, "sing-box.exe");
            Assert.True(File.Exists(bundledExe));
            Assert.Equal("CUSTOM_PAYLOAD", File.ReadAllText(bundledExe));
        }
        finally { Cleanup(temp); }
    }

    [Fact]
    [Trait("Category", "PackagingCharacterization")]
    [Trait("Baseline", "LegacyDefectGuards")]
    public void Behavioral_ImplicitPublishSingBoxLx_Ignored()
    {
        var (temp, distDir, pwsh, script) = SetupBehavioral();
        try
        {
            var publishDir = Path.Combine(temp, "publish");
            Directory.CreateDirectory(publishDir);
            File.WriteAllText(Path.Combine(publishDir, "sing-box-lx.exe"), "LEGACY_LX_PAYLOAD");

            var cacheDir = Path.Combine(temp, "tools", "singbox-cache");
            var zipPath = Path.Combine(cacheDir, "sing-box-1.14.0-vpnctl.3-windows-amd64.zip");
            var hash = CreateZipWithEntry(zipPath, "sing-box-1.14.0-vpnctl.3-windows-amd64/sing-box.exe", "OFFICIAL_VPNCTL_PAYLOAD");

            var (exitCode, stdout, stderr) = RunPwsh(pwsh, script,
                "-Root", temp, "-DistDir", distDir,
                "-SingBoxVersion", "1.14.0-vpnctl.3", "-SingBoxSha256", hash);

            Assert.True(exitCode == 0, $"Exit code {exitCode}: {stderr}\n{stdout}");
            Assert.DoesNotContain("Auto-selected local sing-box-lx", stdout);
            var bundledExe = Path.Combine(distDir, "sing-box.exe");
            Assert.True(File.Exists(bundledExe));
            Assert.Equal("OFFICIAL_VPNCTL_PAYLOAD", File.ReadAllText(bundledExe));
        }
        finally { Cleanup(temp); }
    }

    private static (string tempDir, string distDir, string pwsh, string script) SetupBehavioral()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows pwsh required (observed unexecuted until CI).");
        var pwsh = ResolvePwsh();
        Assert.NotNull(pwsh);
        var tempDir = Directory.CreateTempSubdirectory("vpnrouter-pkg-").FullName;
        var distDir = Path.Combine(tempDir, "dist");
        Directory.CreateDirectory(distDir);
        var script = Path.Combine(tempDir, "fixture.ps1");
        File.WriteAllText(script, ExtractHarnessScript(Read("build.ps1")));
        return (tempDir, distDir, pwsh, script);
    }

    private static void Cleanup(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }

    private static string ExtractHarnessScript(string buildPs1)
    {
        var idx1 = buildPs1.IndexOf("# ── Sing-box supply-chain validation", StringComparison.Ordinal);
        var idx1End = buildPs1.IndexOf("$DistDir = Join-Path", idx1, StringComparison.Ordinal);
        Assert.True(idx1 >= 0 && idx1End > idx1, "Could not locate early validation section in build.ps1.");
        var sec1 = buildPs1.Substring(idx1, idx1End - idx1);

        var idx2 = buildPs1.IndexOf("# ── Bundle sing-box.exe ──", StringComparison.Ordinal);
        var idx2End = buildPs1.IndexOf("# ── slipstream-client.exe", idx2, StringComparison.Ordinal);
        Assert.True(idx2 >= 0 && idx2End > idx2, "Could not locate sing-box bundling section in build.ps1.");
        var sec2 = buildPs1.Substring(idx2, idx2End - idx2);

        return "param(\n" +
            "    [string]$Root,\n    [string]$DistDir,\n    [string]$SingBoxVersion = \"1.14.0-vpnctl.3\",\n" +
            "    [string]$SingBoxSha256 = \"\",\n    [string]$SingBoxPath = \"\",\n    [switch]$Upload\n)\n" +
            "$ErrorActionPreference = 'Stop'\nfunction Invoke-WebRequest { throw 'Network download prohibited in test' }\n\n" +
            sec1 + "\n" + sec2;
    }

    private static string StripComments(string src) =>
        string.Join("\n", Regex.Replace(src, @"<#[\s\S]*?#>", "")
            .Split('\n').Select(l => l.Contains('#') ? l[..l.IndexOf('#')] : l));

    private static string CreateZipWithEntry(string zipPath, string entryPath, string content)
    {
        var dir = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry(entryPath).Open());
            writer.Write(content);
        }
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(zipPath);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static string? ResolvePwsh()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var name in new[] { "pwsh.exe", "powershell.exe" })
            foreach (var dir in pathDirs)
            {
                var p = Path.Combine(dir, name);
                if (File.Exists(p)) return p;
            }
        return null;
    }

    private static (int ExitCode, string Stdout, string Stderr) RunPwsh(string pwsh, string scriptPath, params string[] args)
    {
        var psi = new ProcessStartInfo(pwsh)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start pwsh.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            process.WaitForExit(5_000);
            throw new TimeoutException($"PowerShell execution timed out after 30 seconds: {scriptPath}");
        }
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        return (process.ExitCode, stdout, stderr);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { FindRoot() }.Concat(parts).ToArray()));

    private static string FindRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "VPNRouter.sln"))) return d.FullName;
        for (var d = new DirectoryInfo(Directory.GetCurrentDirectory()); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "VPNRouter.sln"))) return d.FullName;
        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
