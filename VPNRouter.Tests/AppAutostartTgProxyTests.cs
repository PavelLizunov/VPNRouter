using System.IO;
using System.Linq;

namespace VPNRouter.Tests;

/// <summary>
/// v2.31.10 — pin the App-side autostart bootstrap fix.
///
/// <para><b>Bug:</b> <c>VPNRouter.App.MainWindowViewModel</c> read
/// <c>autostart_tgproxy</c> / <c>autostart_zapret</c> from <c>config.yaml</c>
/// into bound properties (<c>LoadSettingsIntoUI</c>) and persisted them
/// (<c>SaveSettings</c>), but never spawned the daemons based on the flags.
/// The flags only worked when the Windows Service was installed (the Service
/// has its own <c>AutostartTgProxyAsync</c> / <c>AutostartZapretAsync</c> in
/// <c>VPNRouterService.cs:331-380</c>). A user who enabled
/// "Autostart Telegram proxy" in Advanced Settings without installing the
/// Service saw the toggle ticked at next launch but no proxy running.</para>
///
/// <para><b>Fix:</b> a new <c>BootstrapAutostartAsync</c> in
/// <c>MainWindowViewModel.AutostartBootstrap.cs</c> spawns the daemons
/// from the App constructor when the Service is NOT running (when it is,
/// it owns the boot-spawn).</para>
///
/// <para><b>Tests are SOURCE-STRING PINS</b> — same pattern as
/// <c>ServiceAppCoexistenceTests</c>. The bootstrap touches Windows-only
/// daemons (tg-ws-proxy, winws.exe) whose end-to-end behaviour can't be
/// asserted in unit tests without process mocking. The string pins are
/// load-bearing for the structural invariants — a future refactor that
/// drops the <c>!ServiceVm.IsRunning</c> guard or removes the bootstrap
/// call from the ctor would silently regress the fix, and these pins
/// would catch it.</para>
/// </summary>
public sealed class AppAutostartTgProxyTests
{
    [Fact]
    public void BootstrapFile_ExistsAndDeclaresPartialClass()
    {
        var src = LoadSource(
            "VPNRouter.App", "ViewModels", "MainWindowViewModel.AutostartBootstrap.cs");
        if (src == null) return; // partial CI checkout

        // Must be a partial of MainWindowViewModel so it shares the
        // [ObservableProperty] surface (AutostartTgProxy, TgProxyPort,
        // TgProxySecret, etc.) with the main file.
        Assert.Contains("partial class MainWindowViewModel", src);
        Assert.Contains("namespace VPNRouter.App.ViewModels", src);

        // Both bootstrap helpers must exist by name. If a refactor renames
        // them, the ctor call will compile-break, but if someone only
        // removes the body we still want a load-bearing test to fail.
        Assert.Contains("BootstrapAutostartAsync", src);
        Assert.Contains("TryAutostartTgProxyAsync", src);
        Assert.Contains("TryAutostartZapretAsync", src);
    }

    [Fact]
    public void Bootstrap_ChecksAutostartTgProxyFlag_BeforeSpawning()
    {
        var src = LoadSource(
            "VPNRouter.App", "ViewModels", "MainWindowViewModel.AutostartBootstrap.cs");
        if (src == null) return;

        var stripped = StripLineComments(src);

        // The TgProxy bootstrap MUST gate on the AutostartTgProxy property.
        // Without this gate the App would spawn the proxy on every launch
        // regardless of the user's setting.
        Assert.Contains("if (!AutostartTgProxy)", stripped);

        // Same gate for Zapret.
        Assert.Contains("if (!AutostartZapret)", stripped);

        // The actual TgProxyManager.Start invocation must still exist —
        // a refactor that "simplifies" by removing the spawn would silently
        // regress the fix. Match flexibly on .Start( so the pin survives
        // a ConfigureAwait/await reshuffle.
        Assert.Matches(
            @"_tgProxy\.Start\s*\(\s*TgProxyPort\s*,\s*TgProxySecret\s*\)",
            stripped);
    }

    [Fact]
    public void Bootstrap_DefersToService_WhenServiceIsRunning()
    {
        var src = LoadSource(
            "VPNRouter.App", "ViewModels", "MainWindowViewModel.AutostartBootstrap.cs");
        if (src == null) return;

        var stripped = StripLineComments(src);

        // CRITICAL: the bootstrap MUST defer to the Service when it's
        // running. Without this guard, App startup would race with the
        // Service-side AutostartTgProxyAsync over the bound port (1443
        // by default) — both would try to spawn tg-ws-proxy and one
        // would fail with "address already in use", but the user-visible
        // failure mode depends on timing. Same risk for Zapret over
        // winws.exe.
        Assert.Contains("ServiceVm.IsRunning", stripped);

        // The early-return on Service-running must be present in the
        // bootstrap entry point. Match the pattern: there must be an
        // `if (ServiceVm.IsRunning) ... return;` somewhere.
        Assert.Matches(
            @"if\s*\(\s*ServiceVm\.IsRunning\s*\)[\s\S]{0,300}?return\s*;",
            stripped);
    }

    [Fact]
    public void Bootstrap_IsInvokedFromConstructor()
    {
        var src = LoadSource("VPNRouter.App", "ViewModels", "MainWindowViewModel.cs");
        if (src == null) return;

        // Pull the constructor body window so a stray call elsewhere
        // (e.g. the bootstrap file mentioning itself in a comment) can't
        // satisfy the pin.
        var ctorRegion = ExtractCtorRegion(src);

        // The ctor must invoke the bootstrap. Pre-r1 there was zero call
        // site; the regression we're guarding against is a refactor that
        // moves the call to a method that's only triggered by user action
        // (defeating the auto-launch use case).
        Assert.Contains("BootstrapAutostartAsync", ctorRegion);

        // Fire-and-forget pattern (`_ = ...`) is required — calling
        // BootstrapAutostartAsync().Wait() in the ctor would deadlock the
        // dispatcher (the bootstrap awaits Dispatcher.UIThread.InvokeAsync).
        Assert.Matches(
            @"_\s*=\s*BootstrapAutostartAsync\s*\(\s*\)",
            ctorRegion);
    }

    [Fact]
    public void Bootstrap_GeneratesSecret_WhenEmpty_MirroringManualPath()
    {
        var src = LoadSource(
            "VPNRouter.App", "ViewModels", "MainWindowViewModel.AutostartBootstrap.cs");
        if (src == null) return;

        var stripped = StripLineComments(src);

        // The manual ToggleTgProxyAsync at MainWindowViewModel.cs:4354-4358
        // generates a 16-byte secret when missing. The autostart bootstrap
        // must mirror this so a user with autostart_tgproxy=true but no
        // saved secret (e.g. first run after manually editing config.yaml)
        // still gets a working proxy instead of a silent skip.
        Assert.Contains("IsNullOrWhiteSpace(TgProxySecret)", stripped);
        Assert.Contains("RandomNumberGenerator.GetBytes(16)", stripped);
        Assert.Contains("Convert.ToHexString", stripped);
    }

    [Fact]
    public void Bootstrap_IsIdempotent_SkipsSpawnWhenAlreadyRunning()
    {
        var src = LoadSource(
            "VPNRouter.App", "ViewModels", "MainWindowViewModel.AutostartBootstrap.cs");
        if (src == null) return;

        var stripped = StripLineComments(src);

        // If a previous-session daemon is still running (LoadSettingsIntoUI
        // already detected this case at MainWindowViewModel.cs:2468), or a
        // second App instance launches via HKCU\Run during a slow login,
        // the bootstrap must NOT double-spawn. TgProxyManager.IsAnyRunning
        // and ZapretManager.IsWinwsRunning are the canonical idempotency
        // checks.
        Assert.Contains("TgProxyManager.IsAnyRunning(TgProxyPort)", stripped);
        Assert.Contains("ZapretManager.IsWinwsRunning()", stripped);
    }

    // ─── helpers ────────────────────────────────────────────────────────────

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

    private static string StripLineComments(string src)
    {
        return string.Join('\n',
            src.Split('\n').Select(l =>
                l.Contains("//") ? l[..l.IndexOf("//")] : l));
    }

    /// <summary>Pull a window around the <c>public MainWindowViewModel()</c>
    /// signature so we don't accidentally satisfy pins by matching unrelated
    /// callsites elsewhere in the 5700+ line file.</summary>
    private static string ExtractCtorRegion(string src)
    {
        var idx = src.IndexOf(
            "public MainWindowViewModel()",
            System.StringComparison.Ordinal);
        if (idx < 0) return src;
        var start = System.Math.Max(0, idx);
        // Q14 (2026-05-17): widened window 5000 → 9000 chars. The
        // MainWindowViewModel ctor has grown across v2.32.x releases —
        // recovery banner consume, branch protection notice, placeholder
        // prune, autostart bootstrap, conflict detector init — so
        // `_ = BootstrapAutostartAsync();` (currently at line ~2778
        // relative to ctor declaration at ~2669, ~110 lines / ~5500
        // chars deep) was falling outside the 5000-char window and the
        // pin failed. 9000 keeps comfortable margin for future ctor
        // growth without picking up unrelated callsites (the ctor +
        // its immediate epilogue still fit well under that bound).
        var end = System.Math.Min(src.Length, idx + 9000);
        return src.Substring(start, end - start);
    }
}
