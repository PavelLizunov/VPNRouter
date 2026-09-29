#nullable enable

#if PLATFORM_WINDOWS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

[Collection(SafeModeStateCollection.Name)]
public sealed class WindowsDnsLockdownOrderingCharacterizationTests : IDisposable
{
    private readonly IProcessRunner _previousRunner = FirewallManager.Runner;

    private static readonly FieldInfo EffectiveField = typeof(WindowsDnsHardening).GetField(
        "_lockdownEffective", BindingFlags.NonPublic | BindingFlags.Static)!;

    public void Dispose()
    {
        FirewallManager.Runner = _previousRunner;
        EffectiveField.SetValue(null, false);
    }

    [Fact]
    public async Task EnableThenDisable_RunInCallOrder_EvenWhenEnableIsSlow()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows firewall commands only.");

        var commands = new List<string>();
        var gate = new object();
        var firstAddEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstAdd = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var runner = new FakeProcessRunner().OnRun(_ => true, async request =>
        {
            var op = request.Arguments.Contains("add") ? "add" : "delete";
            bool first;
            lock (gate)
            {
                first = commands.Count == 0;
                commands.Add(op);
            }

            if (first)
            {
                firstAddEntered.TrySetResult(true);
                await releaseFirstAdd.Task;
            }

            return new ProcessResult(0, string.Empty, string.Empty, TimeSpan.Zero, false);
        });

        FirewallManager.Runner = runner;
        EffectiveField.SetValue(null, false);

        var settings = new AppSettings();
        settings.App.DnsLeakLockdown = true;

        try
        {
            WindowsDnsHardening.ReconcileLockdownForHealth(tunnelServing: true, settings);
            await firstAddEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            WindowsDnsHardening.ReconcileLockdownForHealth(tunnelServing: false, settings);
            await Task.Delay(300);

            lock (gate)
                Assert.DoesNotContain("delete", commands);
        }
        finally
        {
            releaseFirstAdd.TrySetResult(true);
        }

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            lock (gate)
                if (commands.Count(c => c == "delete") >= 9) break;
            await Task.Delay(50);
        }

        lock (gate)
        {
            Assert.True(commands.Count(c => c == "delete") >= 9, "The disable never completed.");
            Assert.True(commands.LastIndexOf("add") < commands.IndexOf("delete"),
                "Every add must finish before the first delete.");
        }
    }
}
#endif
