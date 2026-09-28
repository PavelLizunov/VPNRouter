#nullable enable
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace VPNRouter.Tests;

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
        if (src == null) return;

        var idx = src.IndexOf($"private async Task {method}()", StringComparison.Ordinal);
        Assert.True(idx > 0, $"method {method} not found");

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
