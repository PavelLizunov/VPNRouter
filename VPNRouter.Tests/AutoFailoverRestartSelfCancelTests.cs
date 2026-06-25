using System;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// v2.44.3 (P0): pins the auto-failover restart-delegate contract the VpnEngine
/// self-cancel fix relies on. The post-start failover invokes
/// <see cref="AutoFailoverEngine.HandleDeadConfigAsync"/> with the PROBE token,
/// which the engine's teardown (<c>Stop()</c>) cancels. Pre-fix the restart
/// delegate then re-entered <c>StartAsync</c> UNDER that cancelled token and
/// self-cancelled, so a GENUINE outage tore the link down with no replacement
/// (diag 20260624-235243). The engine fix runs the replacement bring-up under
/// the SESSION token instead. These tests model both sides of that contract at
/// the deterministic, cross-platform AutoFailoverEngine seam (the full engine
/// integration pin lives in VpnEngineLifecycleTests, Windows-only).
/// </summary>
public sealed class AutoFailoverRestartSelfCancelTests
{
    private static AppSettings TwoServerSubscribe()
    {
        var s = new AppSettings();
        s.App.ConfigMode = "subscribe";
        s.Vless.ActiveServer = "srv-1";
        s.App.ActiveSubscriptionServer = "srv-1";
        s.App.Subscriptions.Add(new SubscriptionEntry
        {
            Name = "main",
            Url = "https://example.com/sub",
            Enabled = true,
            Servers = new()
            {
                new() { Name = "srv-1", Server = "1.2.3.1", Port = 443, Uuid = "u1" },
                new() { Name = "srv-2", Server = "1.2.3.2", Port = 443, Uuid = "u2" },
            },
        });
        return s;
    }

    /// <summary>
    /// Documents the v2.44.2 self-cancel: a restart delegate that (like the
    /// pre-fix WireFailoverWithStop) tears down — cancelling the probe token —
    /// and then runs the bring-up UNDER that same token throws
    /// OperationCanceledException, so the replacement never starts.
    /// HandleDeadConfigAsync rethrows the OCE (AutoFailoverEngine.cs catch).
    /// </summary>
    [Fact]
    public async Task PreFixWiring_BringUpUnderProbeToken_SelfCancels()
    {
        var settings = TwoServerSubscribe();
        using var probeCts = new CancellationTokenSource();
        var replacementStarted = false;

        var engine = new AutoFailoverEngine(
            settings,
            new ConfigSanityCheck(),
            restart: async probeCt =>
            {
                probeCts.Cancel();                       // teardown cancels the probe token
                probeCt.ThrowIfCancellationRequested();  // pre-fix: bring-up runs under it
                await Task.Yield();
                replacementStarted = true;
                return true;
            },
            store: new InMemorySettingsStore());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => engine.HandleDeadConfigAsync("delay-test dead", probeCts.Token));
        Assert.False(replacementStarted, "pre-fix bring-up should have self-cancelled");
    }

    /// <summary>
    /// Pins the post-fix behaviour: running the bring-up under a token that is
    /// NOT the cancelled probe token (the engine uses its session CTS) brings
    /// the replacement up and the swap is persisted.
    /// </summary>
    [Fact]
    public async Task PostFixWiring_BringUpUnderSessionToken_ReplacementStarts()
    {
        var settings = TwoServerSubscribe();
        using var probeCts = new CancellationTokenSource();
        using var sessionCts = new CancellationTokenSource();
        var replacementStarted = false;
        var ranUnderCancelledToken = false;

        var engine = new AutoFailoverEngine(
            settings,
            new ConfigSanityCheck(),
            restart: async probeCt =>
            {
                probeCts.Cancel();                  // teardown cancels the probe token...
                var bringUp = sessionCts.Token;     // ...but bring-up uses the SESSION token
                ranUnderCancelledToken = bringUp.IsCancellationRequested;
                bringUp.ThrowIfCancellationRequested();
                await Task.Yield();
                replacementStarted = true;
                return true;
            },
            store: new InMemorySettingsStore());

        var outcome = await engine.HandleDeadConfigAsync("delay-test dead", probeCts.Token);

        Assert.True(outcome.Switched);
        Assert.Equal("srv-2", outcome.NewActiveServer);
        Assert.True(replacementStarted, "replacement should come up under the session token");
        Assert.False(ranUnderCancelledToken, "bring-up ran under a cancelled token");
        Assert.Equal("srv-2", settings.Vless.ActiveServer);
    }

    /// <summary>
    /// Pins the resurrection guard at the contract level: if the bring-up token
    /// is already cancelled (the user disconnected — the engine's _sessionCts),
    /// the delegate aborts without "starting" the replacement.
    /// </summary>
    [Fact]
    public async Task PostFixWiring_SessionCancelledBeforeBringUp_AbortsNoResurrection()
    {
        var settings = TwoServerSubscribe();
        using var probeCts = new CancellationTokenSource();
        using var sessionCts = new CancellationTokenSource();
        sessionCts.Cancel();                          // user disconnected before the restart ran
        var replacementStarted = false;

        var engine = new AutoFailoverEngine(
            settings,
            new ConfigSanityCheck(),
            restart: probeCt =>
            {
                probeCts.Cancel();
                if (sessionCts.IsCancellationRequested) return Task.FromResult(false); // abort guard
                replacementStarted = true;
                return Task.FromResult(true);
            },
            store: new InMemorySettingsStore());

        var outcome = await engine.HandleDeadConfigAsync("delay-test dead", probeCts.Token);

        Assert.False(replacementStarted, "must NOT resurrect after user disconnect");
        // The swap itself still happens in-memory (Switched=true); the bring-up is what's gated.
        Assert.True(outcome.Switched);
    }
}
