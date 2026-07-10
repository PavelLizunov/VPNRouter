#nullable enable
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// r9 P2 (brat 2026-07-10): the four batch-test commands carry a
/// "press again to cancel" re-entry branch (<c>if (IsTestingServers) Cancel()</c>),
/// but a bare <c>[RelayCommand]</c> on an async method generates an
/// AsyncRelayCommand that DISABLES the bound button while the command runs —
/// making the cancel branch unreachable from the UI (live-proven: four cancel
/// attempts on brat, the 21-server deep batch completed every time). These
/// source pins keep <c>AllowConcurrentExecutions = true</c> on all four so the
/// second click can reach the cancel branch.
/// </summary>
public class ServerTestCancelReentryTests
{
    [Theory]
    [InlineData("TestAllServersAsync")]
    [InlineData("TestAllSubscriptionServersAsync")]
    [InlineData("DeepVerifyAllServersAsync")]
    [InlineData("DeepVerifyAllSubscriptionServersAsync")]
    public void BatchTestCommand_AllowsConcurrentExecutions_SoCancelIsReachable(string method)
    {
        var src = LoadSource("VPNRouter.App", "ViewModels", "MainWindowViewModel.ServerTesting.cs");
        if (src == null) return; // partial checkout

        var idx = src.IndexOf($"private async Task {method}()", StringComparison.Ordinal);
        Assert.True(idx > 0, $"method {method} not found");

        // The nearest preceding [RelayCommand...] attribute must opt into
        // concurrent executions, or the running command disables its button.
        var attrStart = src.LastIndexOf("[RelayCommand", idx, StringComparison.Ordinal);
        Assert.True(attrStart > 0, $"no RelayCommand attribute before {method}");
        var attr = src.Substring(attrStart, src.IndexOf(']', attrStart) - attrStart + 1);
        Assert.Contains("AllowConcurrentExecutions = true", attr);
    }

    private static string? LoadSource(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        return null;
    }
}
