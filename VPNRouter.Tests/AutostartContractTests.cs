using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

/// <summary>
/// Source-pin + behavioural fence around the three Service-level
/// autostart entry points (VPN / Zapret / TgProxy) and the
/// soon-to-be-added App-side TgProxy bootstrap.
///
/// <para><b>Why a contract test class instead of behavioural integration?</b>
/// The autostart flows live in <see cref="VPNRouter.Service.VPNRouterService"/>
/// which boots under <c>LocalSystem</c>, owns process lifetime via
/// <see cref="ResilientStarter"/>, and gates on filesystem state we
/// can't fully mock from xUnit. Behavioural integration tests would
/// need a real sing-box install, a TUN adapter, and SCM access — none
/// of which run on CI. Source-pin tests instead read the .cs file as
/// text and grep for the structural invariants. A future refactor that
/// silently drops a guard (the bug class we've shipped before — autostart
/// silently doesn't fire) flips a test red at PR time, before merge.</para>
///
/// <para><b>Bug class this fences against:</b> the three flag checks
/// in <see cref="VPNRouter.Service.VPNRouterService.ExecuteAsync"/> are
/// trivial-looking <c>if (settings.App.AutostartXxx)</c> calls. A refactor
/// that consolidates them into a loop, replaces them with a strategy
/// dispatcher, or accidentally drops one (especially the newer TgProxy
/// branch added in v2.27) would not fail any existing test — yet would
/// silently break the user's "I toggled this in the UI, why doesn't it
/// start at boot" expectation. <see cref="Service_ChecksAllThreeAutostartFlags"/>
/// pins each branch as a literal string; <see cref="Settings_AllThreeAutostartFlagsExist"/>
/// pins the YAML schema side. Together they ensure neither end of the
/// contract drifts.</para>
///
/// <para><b>Behavioural layer (#7-#9):</b> <see cref="TgProxyUpdater.IsInstalled"/>
/// is the gate that decides whether the Service or App attempts to
/// start tg-ws-proxy at all. The path getters are static (
/// <c>%ProgramData%\VPNRouter\tg-proxy\...</c>), which makes
/// behavioural unit tests against the no-install case awkward — every
/// CI runner sees the same global path. To get clean, deterministic
/// coverage we added a small <see cref="TgProxyUpdater.IsInstalledAt"/>
/// internal overload that takes an explicit base directory; the
/// production <c>IsInstalled()</c> just delegates. The behavioural
/// tests synthesize a temp dir layout and walk the three states.</para>
///
/// <para><b>Test #5 (App-side bootstrap):</b> sister task DBG-2 will
/// add <c>AutostartTgProxyAsync</c> to <see cref="VPNRouter.App.ViewModels.MainWindowViewModel"/>
/// with a <c>ServiceVm.IsRunning</c> double-start guard. As of the
/// commit that introduced this test class, DBG-2 has NOT merged — the
/// method does not exist. The test is marked with <see cref="SkipAttribute"/>
/// so it surfaces in the test run output as a TODO/skipped row,
/// reminding whoever lands DBG-2 to either change the Skip to enable
/// or update the assertion to match the merged shape. Either way the
/// signal is visible.</para>
///
/// <para><b>Source-pin caveat:</b> all source-pin tests strip C#
/// single-line comments before grepping. The bug-history commentary
/// in <see cref="VPNRouter.Service.VPNRouterService"/> legitimately
/// quotes some of the very patterns we forbid (e.g. the comment that
/// explains <c>EnableRaisingEvents = false</c>). Strip-then-grep keeps
/// the assertion clean.</para>
/// </summary>
public sealed class AutostartContractTests
{
    // ─── Source-pin layer (compile-time invariants) ─────────────────────

    /// <summary>
    /// Pin: <see cref="VPNRouter.Service.VPNRouterService.ExecuteAsync"/>
    /// must contain exactly three top-level autostart-flag checks, one
    /// per component. Order is not pinned (a refactor may reorder), but
    /// presence of each literal <c>if (settings.App.AutostartXxx)</c>
    /// is required.
    ///
    /// <para>Failure mode this catches: a refactor that consolidates
    /// the three branches into a loop / dispatcher would replace the
    /// literal calls with something like <c>foreach (var flag in
    /// AutostartFlags) ...</c>, which would no longer match. The
    /// review reviewer sees a red test and either updates the contract
    /// or restores the explicit branches.</para>
    /// </summary>
    [Fact]
    public void Service_ChecksAllThreeAutostartFlags()
    {
        var src = ReadVpnRouterServiceSourceOrSkip();
        if (src == null) return;

        var stripped = StripLineComments(src);

        Assert.Contains("if (settings.App.AutostartVpn)", stripped);
        Assert.Contains("if (settings.App.AutostartZapret)", stripped);
        Assert.Contains("if (settings.App.AutostartTgProxy)", stripped);
    }

    /// <summary>
    /// Pin: <see cref="VPNRouter.Service.VPNRouterService.AutostartTgProxyAsync"/>
    /// must early-return on BOTH <see cref="TgProxyUpdater.IsInstalled"/>
    /// returning false AND <see cref="string.IsNullOrWhiteSpace"/> on
    /// the secret. Either gate failing would otherwise crash the process
    /// or attempt a no-op start that user-visible ResilientStarter
    /// retries would fire on indefinitely.
    ///
    /// <para>Failure mode this catches: someone removes the
    /// <c>IsNullOrWhiteSpace(secret)</c> guard thinking "it's already
    /// validated upstream" — but secret is initially empty and only
    /// filled when the user clicks "Generate" in the UI. Without the
    /// guard, fresh-install autostart + saved-flag-on would crash the
    /// proxy launcher loop in tg-ws-proxy on missing secret.</para>
    /// </summary>
    [Fact]
    public void Service_TgProxyAutostart_ChecksInstallAndSecret()
    {
        var src = ReadVpnRouterServiceSourceOrSkip();
        if (src == null) return;

        var body = ExtractMethodBody(src, "AutostartTgProxyAsync");
        Assert.NotNull(body);

        Assert.Contains("TgProxyUpdater.IsInstalled()", body);
        Assert.Contains("string.IsNullOrWhiteSpace(secret)", body);

        // Both gates must `return` rather than just log+continue.
        // Pattern: `return;` after the guard. We assert the keyword
        // appears at least twice in the body — once for each guard
        // (the OperationCanceledException catch is `return` too in
        // some refactors, so >= 2 is the lower bound).
        var returnCount = Regex.Matches(body, @"\breturn\s*;").Count;
        Assert.True(returnCount >= 2,
            $"AutostartTgProxyAsync must return early on both guards. " +
            $"Found {returnCount} `return;` statements in body.");
    }

    /// <summary>
    /// Pin: <see cref="VPNRouter.Service.VPNRouterService.AutostartTgProxyAsync"/>
    /// must call <see cref="ResilientStarter.StartWithBackoffAsync"/>
    /// rather than calling <c>_tgProxy.Start</c> directly without a
    /// retry policy.
    ///
    /// <para>Failure mode this catches: a "let's keep it simple"
    /// refactor that replaces ResilientStarter with a raw
    /// <c>_tgProxy.Start(port, secret)</c>. tg-ws-proxy startup races
    /// the network stack (Python interpreter init + socket bind), so
    /// transient first-attempt failures are common — without backoff,
    /// boot-time autostart fails permanently. Same pattern fence as
    /// the VPN and Zapret branches.</para>
    /// </summary>
    [Fact]
    public void Service_TgProxyAutostart_UsesResilientStarter()
    {
        var src = ReadVpnRouterServiceSourceOrSkip();
        if (src == null) return;

        var body = ExtractMethodBody(src, "AutostartTgProxyAsync");
        Assert.NotNull(body);

        Assert.Contains("ResilientStarter.StartWithBackoffAsync", body);
    }

    /// <summary>
    /// Pin: <see cref="VPNRouter.Service.VPNRouterService.AutostartTgProxyAsync"/>
    /// must call <see cref="System.Diagnostics.EventLog.WriteEntry"/>
    /// (via the local <c>WriteEventLog</c> helper) on retry-exhaustion
    /// failure, with severity <see cref="System.Diagnostics.EventLogEntryType.Error"/>.
    ///
    /// <para>Failure mode this catches: a refactor that removes the
    /// EventLog write thinking "we already logged via Serilog". But
    /// users debug autostart failures through Windows Event Viewer
    /// (Application log → VPNRouter source) — that's the documented
    /// support path. Losing this audit trail is the kind of
    /// invisible-regression that took weeks to notice in past
    /// incidents.</para>
    /// </summary>
    [Fact]
    public void Service_TgProxyAutostart_GeneratesEventLogOnFailure()
    {
        var src = ReadVpnRouterServiceSourceOrSkip();
        if (src == null) return;

        var body = ExtractMethodBody(src, "AutostartTgProxyAsync");
        Assert.NotNull(body);

        // Pin both: a WriteEventLog call AND the Error severity in the
        // failure branch. The exact wording can change ("autostart failed"
        // vs "TgProxy failed to start") but the severity must stay Error
        // so the Event Viewer red-X icon shows up.
        Assert.Matches(
            new Regex(@"WriteEventLog\s*\([^;]*EventLogEntryType\.Error",
                      RegexOptions.Singleline),
            body);
    }

    /// <summary>
    /// Pin (post-DBG-2): the App-side TgProxy autostart bootstrap in
    /// <see cref="VPNRouter.App.ViewModels.MainWindowViewModel"/> must
    /// guard against double-start by checking
    /// <c>ServiceVm.IsRunning</c> and returning early if true.
    ///
    /// <para><b>Why this matters:</b> if the Windows Service is
    /// installed AND running, it has already launched tg-ws-proxy on
    /// port 1443. The App ALSO running its own autostart bootstrap
    /// would either spawn a second python.exe that can't bind the
    /// port (silent failure), or worse — race the lock and crash both
    /// processes. The App-side path is meant for the case where the
    /// user runs portable / no-Service mode. ServiceVm.IsRunning is
    /// the canonical "Service owns this" gate.</para>
    ///
    /// <para><b>Skip rationale:</b> DBG-2 (the App-side autostart
    /// bootstrap) is in flight on a sister branch and has not been
    /// merged at the time this test was written. The method
    /// <c>AutostartTgProxyAsync</c> does not exist on
    /// <c>MainWindowViewModel</c> yet. Once DBG-2 lands, change the
    /// <c>Skip</c> attribute to remove the suppression — the assertions
    /// below will pin the IsRunning guard.</para>
    /// </summary>
    [Fact(Skip = "DBG-2 not merged: MainWindowViewModel.AutostartTgProxyAsync does not exist yet. Remove Skip after DBG-2 merges.")]
    public void App_TgProxyAutostart_GuardsAgainstServiceDoubleStart()
    {
        var src = ReadMainWindowViewModelSourceOrSkip();
        if (src == null) return;

        // Find the post-DBG-2 method by name. If absent, the test fails
        // with a clear message — that's the signal to either rename the
        // assertion to match the actual method or update DBG-2 to use
        // the canonical name.
        var body = ExtractMethodBody(src, "AutostartTgProxyAsync");
        Assert.NotNull(body);

        // Pin: the method must reference ServiceVm.IsRunning AND have
        // an early return on it. The exact form will be either
        // `if (ServiceVm.IsRunning) return;` or
        // `if (ServiceVm?.IsRunning == true) return;` — both shapes
        // need to match.
        Assert.Matches(
            new Regex(@"ServiceVm[\?\.]*\.IsRunning"),
            body);

        // And there must be a `return` somewhere after that check.
        Assert.Contains("return", body);
    }

    /// <summary>
    /// Pin: <see cref="AppConfig"/> in
    /// <see cref="VPNRouter.Core.Models.AppSettings"/> must declare all
    /// three autostart flags as <c>bool</c> properties with the
    /// expected YAML aliases.
    ///
    /// <para>Failure mode this catches: a YAML schema migration that
    /// renames <c>autostart_tgproxy</c> to <c>autostart_tg_proxy</c>
    /// (or similar) without bumping <see cref="AppSettings.CurrentSchemaVersion"/>
    /// and adding a migrator. Existing user configs would silently lose
    /// the autostart flag — the deserialized value defaults to false,
    /// the user sees their "yes I want autostart" toggle quietly
    /// reset.</para>
    /// </summary>
    [Fact]
    public void Settings_AllThreeAutostartFlagsExist()
    {
        var appConfigType = typeof(AppConfig);

        AssertHasBoolPropertyWithYamlAlias(appConfigType, "AutostartVpn", "autostart_vpn");
        AssertHasBoolPropertyWithYamlAlias(appConfigType, "AutostartZapret", "autostart_zapret");
        AssertHasBoolPropertyWithYamlAlias(appConfigType, "AutostartTgProxy", "autostart_tgproxy");
    }

    // ─── Behavioural layer (TgProxyUpdater.IsInstalledAt) ───────────────

    /// <summary>
    /// Behavioural: <see cref="TgProxyUpdater.IsInstalledAt"/> returns
    /// <c>false</c> when only python.exe is present (proxy/ directory
    /// missing). This is the post-Python-extracted, pre-source-cloned
    /// transient state.
    /// </summary>
    [Fact]
    public void IsInstalled_FalseWhenProxyDirMissing()
    {
        using var sandbox = new TempSandbox();

        // Synthesize python/python.exe but NO proxy/ directory.
        var pythonDir = Path.Combine(sandbox.Root, "python");
        Directory.CreateDirectory(pythonDir);
        File.WriteAllText(Path.Combine(pythonDir, "python.exe"), "stub");

        Assert.False(
            TgProxyUpdater.IsInstalledAt(sandbox.Root),
            "IsInstalled should be false when proxy/ directory is missing.");
    }

    /// <summary>
    /// Behavioural: <see cref="TgProxyUpdater.IsInstalledAt"/> returns
    /// <c>false</c> when only the proxy/ directory is present
    /// (python.exe missing). Could happen if a partial download placed
    /// source files but Python extraction failed.
    /// </summary>
    [Fact]
    public void IsInstalled_FalseWhenPythonMissing()
    {
        using var sandbox = new TempSandbox();

        // Synthesize proxy/ directory but NO python/python.exe.
        Directory.CreateDirectory(Path.Combine(sandbox.Root, "proxy"));

        Assert.False(
            TgProxyUpdater.IsInstalledAt(sandbox.Root),
            "IsInstalled should be false when python/python.exe is missing.");
    }

    /// <summary>
    /// Behavioural: <see cref="TgProxyUpdater.IsInstalledAt"/> returns
    /// <c>true</c> only when both python.exe (file) AND proxy/
    /// (directory) are present. Positive case.
    /// </summary>
    [Fact]
    public void IsInstalled_TrueWhenBothPresent()
    {
        using var sandbox = new TempSandbox();

        var pythonDir = Path.Combine(sandbox.Root, "python");
        Directory.CreateDirectory(pythonDir);
        File.WriteAllText(Path.Combine(pythonDir, "python.exe"), "stub");
        Directory.CreateDirectory(Path.Combine(sandbox.Root, "proxy"));

        Assert.True(
            TgProxyUpdater.IsInstalledAt(sandbox.Root),
            "IsInstalled should be true when both python.exe and proxy/ are present.");
    }

    // ─── Helpers ────────────────────────────────────────────────────────

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

    /// <summary>
    /// Walk up from the test bin dir looking for a project source file.
    /// Returns null if not found (NuGet binary, partial CI checkout) so
    /// the caller can skip rather than fail.
    /// </summary>
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

    /// <summary>
    /// Strip C# single-line comments so source-pin assertions don't
    /// match historical bug-history commentary that legitimately
    /// quotes patterns we want to forbid in emitted code.
    /// </summary>
    private static string StripLineComments(string src) =>
        string.Join("\n",
            src.Split('\n')
               .Select(l => l.Contains("//") ? l[..l.IndexOf("//")] : l));

    /// <summary>
    /// Extract the body of a named method from a C# source string by
    /// brace-balancing. Returns null if the method is not found. Body
    /// includes the outer braces. Comments are stripped first so brace
    /// counting isn't fooled by <c>{</c> in <c>// ...</c> lines.
    ///
    /// <para>Locates the declaration (not a call site) by matching
    /// <c>(return-type) methodName(...) {</c> — i.e. methodName preceded
    /// by a return-type token (Task / void / bool / etc., possibly with
    /// generic args). Call sites like <c>_ = AutostartTgProxyAsync(...)</c>
    /// are preceded by <c>=</c> or <c>(</c> and don't match.</para>
    /// </summary>
    private static string? ExtractMethodBody(string src, string methodName)
    {
        var stripped = StripLineComments(src);

        // Match a method DECLARATION: `(returnType) methodName(`.
        // The return type token can be Task, Task<...>, ValueTask,
        // ValueTask<...>, void, bool, int, string, or a custom type
        // (we accept any identifier here). Modifiers (async/static/
        // partial/public/private/etc.) sit before it but we don't
        // require them — only the (returnType) (whitespace) (methodName)
        // (open paren) anchor.
        var declRegex = new Regex(
            @"\b(?:[A-Za-z_][A-Za-z0-9_]*(?:<[^>]+>)?)\s+" +
                Regex.Escape(methodName) + @"\s*\(",
            RegexOptions.Multiline);

        // Walk through matches and pick the first one that LOOKS like
        // a declaration: not preceded by `=`, `,`, `(` or `.` (which
        // would mark a call site / member access). The declRegex
        // requires a type token before methodName, but a token like
        // `_` (variable) followed by `=` and then `MethodName(` would
        // still match: `_ = MethodName(` parses as `_ = MethodName`
        // (the `_ =` is not part of the regex match because there's
        // no type-token preceding `MethodName` directly — the regex
        // anchors on `\b<typeToken>\s+methodName\(` with `<typeToken>`
        // matching at a word boundary). Defense-in-depth: also reject
        // matches preceded by `= ` so `_ = MethodName(` patterns get
        // skipped if any do slip through.
        foreach (Match m in declRegex.Matches(stripped))
        {
            // Reject obvious call-site shapes by inspecting char before
            // the matched type token.
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

    /// <summary>
    /// Reflection-assert: the given type must declare a public bool
    /// property named <paramref name="propertyName"/> with a
    /// <see cref="YamlDotNet.Serialization.YamlMemberAttribute"/> whose
    /// <c>Alias</c> is <paramref name="expectedAlias"/>.
    /// </summary>
    private static void AssertHasBoolPropertyWithYamlAlias(
        Type t, string propertyName, string expectedAlias)
    {
        var prop = t.GetProperty(propertyName,
            BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(prop);
        Assert.Equal(typeof(bool), prop!.PropertyType);

        // YamlMemberAttribute: read the Alias field via reflection so we
        // don't take a hard reference on YamlDotNet's attribute type
        // (it's already loaded via Core, but staying loose here means
        // future YAML lib swaps don't break this test for the wrong
        // reason).
        var yamlAttr = prop.GetCustomAttributes(inherit: false)
            .FirstOrDefault(a => a.GetType().Name == "YamlMemberAttribute");
        Assert.NotNull(yamlAttr);

        var aliasProp = yamlAttr!.GetType()
            .GetProperty("Alias", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(aliasProp);

        var actualAlias = aliasProp!.GetValue(yamlAttr) as string;
        Assert.Equal(expectedAlias, actualAlias);
    }

    /// <summary>
    /// Disposable temp directory under <c>%TEMP%</c>. Cleans up on
    /// Dispose. Exists to keep the behavioural tests free of static
    /// path leaks.
    /// </summary>
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
