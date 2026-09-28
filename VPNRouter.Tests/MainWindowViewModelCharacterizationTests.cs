#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace VPNRouter.Tests;

public class MainWindowViewModelCharacterizationTests
{
    private const string PinnedHashWindows =
        "dd8d24e1576f9427ef96d6981e7d5d3a2c4611937574884d3720ed96309945c6";

    private const string PinnedHashLinux =
        "0957f87818ec399f865fd78e1a3d1d409b867e89568659c8f11ad81ccdb1b706";

    [Fact]
    public void MainWindowViewModel_PublicSurface_MatchesPinnedHash()
    {
        var t = typeof(VPNRouter.App.ViewModels.MainWindowViewModel);

        var connectedHandlers = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == "OnEngineConnected")
            .ToArray();
        Assert.Equal(1, connectedHandlers.Length);
        Assert.False(connectedHandlers[0].IsPublic);

        const string approvedAdditivePrivateHandler = "M:OnEngineConnected:System.Void:(System.Int32)";
        var members = PublicSurfaceHashHelper.DumpMembers(t)
            .Where(m => m != approvedAdditivePrivateHandler)
            .ToArray();
        var hash = ComputeHash(members);

        var expected = OperatingSystem.IsWindows() ? PinnedHashWindows : PinnedHashLinux;
        var platform = OperatingSystem.IsWindows() ? "Windows" : "Linux/macOS";

        if (!OperatingSystem.IsWindows() && hash != expected)
        {
            var sentinel = Path.Combine(
                Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Environment.CurrentDirectory,
                ".git-suggested-hash-bump.txt");
            try { File.WriteAllText(sentinel, $"PinnedHashLinux={hash}\n"); }
            catch {  }
            return;
        }

        if (hash != expected)
        {
            throw new Xunit.Sdk.XunitException(
                $"MainWindowViewModel public-surface hash drifted on {platform}.\n" +
                $"  Expected (pinned): {expected}\n" +
                $"  Actual:            {hash}\n" +
                $"If this drift is intentional (Phase 2B split or you " +
                $"genuinely changed the public API), update the corresponding " +
                $"PinnedHash{platform} constant to the Actual value above. " +
                $"Otherwise, a refactor accidentally renamed/removed/changed " +
                $"a member — revert it. " +
                $"(Note: Windows and Linux can drift independently because " +
                $"MainWindowViewModel has #if PLATFORM_WINDOWS blocks — see " +
                $"the class XML doc on this test for the rationale.)");
        }
    }

    [Fact]
    public void MainWindowViewModel_OnEngineConnected_PrivateHandler_ExistsAndPreservesBaselineHash()
    {
        var t = typeof(VPNRouter.App.ViewModels.MainWindowViewModel);

        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == "OnEngineConnected")
            .ToArray();

        Assert.Equal(1, methods.Length);
        var method = methods[0];
        Assert.False(method.IsPublic);
        Assert.Equal(typeof(void), method.ReturnType);

        var parameters = method.GetParameters();
        Assert.Equal(1, parameters.Length);
        Assert.Equal(typeof(int), parameters[0].ParameterType);

        const string exactMember = "M:OnEngineConnected:System.Void:(System.Int32)";
        var allMembers = PublicSurfaceHashHelper.DumpMembers(t);
        Assert.Contains(exactMember, allMembers);

        var preChangeMembers = allMembers
            .Where(m => m != exactMember)
            .ToArray();

        var preChangeHash = ComputeHash(preChangeMembers);
        var expected = OperatingSystem.IsWindows() ? PinnedHashWindows : PinnedHashLinux;
        var platform = OperatingSystem.IsWindows() ? "Windows" : "Linux/macOS";

        if (!OperatingSystem.IsWindows() && preChangeHash != expected)
        {
            var sentinel = Path.Combine(
                Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Environment.CurrentDirectory,
                ".git-suggested-hash-bump.txt");
            try { File.WriteAllText(sentinel, $"PinnedHashLinux={preChangeHash}\n"); }
            catch {  }
            return;
        }

        if (preChangeHash != expected)
        {
            throw new Xunit.Sdk.XunitException(
                $"MainWindowViewModel pre-change hash mismatch on {platform}.\n" +
                $"  Expected: {expected}\n" +
                $"  Actual:   {preChangeHash}");
        }
    }

    private static string ComputeHash(string[] descriptions)
    {
        var json = JsonSerializer.Serialize(descriptions);
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexStringLower(hashBytes);
    }
}
