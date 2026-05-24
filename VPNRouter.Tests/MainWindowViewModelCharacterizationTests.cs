#nullable enable
using System;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Phase 2B characterization snapshot for <c>MainWindowViewModel</c>.
/// Wave 8 extracted 4 new partials (FreeConfigs / Subscriptions / Settings /
/// Profiles) out of the 6,753-LOC god-class without touching the public
/// surface. This test pins the public-surface hash; it must match pre and
/// post split. Any drift = forbidden refactor side effect (renamed, removed,
/// or signature-changed member). See <see cref="PublicSurfaceHashHelper"/>
/// for inclusion rules.
///
/// <para><strong>Why platform-specific hashes?</strong> MainWindowViewModel
/// has 26 <c>#if PLATFORM_WINDOWS</c> blocks for Win-only services
/// (PowerEventListener, ETW, Mutex). The Linux build strips these blocks,
/// yielding a different reflection-visible public surface than the Windows
/// build. Pinning per-platform hashes lets us catch drift on EITHER platform
/// — a Linux-only refactor that accidentally renames a cross-platform member
/// will trip the Linux pin, and likewise for Windows.</para>
///
/// <para>If this test FAILS, it means MainWindowViewModel's public surface
/// drifted from the pinned 2026-05-18 baseline on this platform. Either:</para>
/// <list type="number">
///   <item>You intentionally added/removed/changed a public member, in
///   which case re-capture the hash via
///   <c>PublicSurfaceHashHelper.Compute(typeof(MainWindowViewModel))</c>
///   on the failing platform and update the corresponding pin below.</item>
///   <item>You accidentally renamed/removed a member during a refactor.
///   Revert that change.</item>
/// </list>
///
/// <para>To see which member drifted, run
/// <see cref="PublicSurfaceHashHelper.DumpMembers"/> and diff against the
/// baseline dump on the same platform.</para>
/// </summary>
public class MainWindowViewModelCharacterizationTests
{
    /// <summary>
    /// Windows hash, captured 2026-05-18 against the pre-Phase-2B 6,753-LOC
    /// monolith state. Wave 8 preserved this hash across all 4 partial-class
    /// extractions (verified locally on Windows).
    ///
    /// <para><b>Phase 4 Wave 19 (2026-05-18 evening):</b> hash bumped to
    /// account for the new <c>public MainWindowViewModel(ISettingsStore?)</c>
    /// ctor overload added by the v3.0 refactor (Phase 3G-1 ISettingsStore
    /// rollout). The parameterless ctor is unchanged and still chains to
    /// the overload with <c>RealSettingsStore.Instance</c> default —
    /// production callers see zero behaviour change.</para>
    /// </summary>
    /// <summary>
    /// Wave 39 (2026-05-19) — re-pinned after Agent B added the
    /// `IsDnsLeakLockdownEnabled` ObservableProperty pair (auto-generated
    /// public partial method `OnIsDnsLeakLockdownEnabledChanged(bool)`
    /// + property getter/setter). New surface, intentional drift.
    /// </summary>
    /// <summary>
    /// v2.36 (2026-05-24) — MVP one-button TgProxy UX surface added:
    /// new ObservableProperties (<c>IsTelegramSchemeWarningVisible</c>,
    /// <c>TgProxyDownloadStep</c>), new getter
    /// (<c>HasTgProxyDownloadStep</c>), three new label getters
    /// (<c>L_TgProxySchemeMissingWarning</c>, <c>L_TgProxyDismiss</c>,
    /// <c>L_TgProxyCopyLink</c>), one new RelayCommand
    /// (<c>DismissTelegramSchemeWarning</c>), one new partial method
    /// (<c>OnTgProxyDownloadStepChanged</c>). Intentional drift.
    /// </summary>
    // v2.36.0-r7 (2026-05-24 night): TgProxyOneTap design surface added.
    // New getters: LblTgProxyHeroTitle, LblTgProxyHeroLede,
    // L_TgProxyOneTapStep1/2/3, L_TgProxyOneTapTune, LblTgProxyAirPill.
    // Plus NotifyPropertyChangedFor wiring on _tgProxyEnabled and
    // _tgProxyPort for the hero re-narration. Surface drift is
    // additive (no removals).
    private const string PinnedHashWindows =
        "486cf13f1bd5ae25af11e99c1277e94fa7b7427b21bbe3676a72ec837a55aef7";

    /// <summary>
    /// Linux hash, captured 2026-05-18 from ubuntu-latest CI run on the
    /// pre-Phase-2B monolith. Wave 8 should preserve this too (the extracted
    /// partials don't move any <c>#if PLATFORM_WINDOWS</c>-gated members
    /// across partials, so the conditional-stripped surface stays identical).
    /// If Wave 8 ever DOES touch a #if-gated member, this pin will go red
    /// on the next CI run — update it then with the actual Linux hash.
    ///
    /// <para><b>Phase 4 Wave 19 (2026-05-18 evening):</b> the new
    /// <c>MainWindowViewModel(ISettingsStore?)</c> ctor is non-
    /// <c>#if PLATFORM_WINDOWS</c>-gated, so the Linux surface drifts too.
    /// The actual Linux hash will surface on the next ubuntu-latest CI run
    /// as the test failure's "Actual:" line — update this constant then.</para>
    ///
    /// <para><b>Phase 6 Wave 32 (2026-05-19 night):</b> Linux hash now
    /// captured from CI run 26087428554 and pinned below. Windows-side hash
    /// from Wave 4-19 was applied locally but Linux was left as the pre-
    /// Wave-19 value pending an actual CI surface; this commit closes that
    /// loop. Same `MainWindowViewModel(ISettingsStore?)` ctor surface on
    /// both platforms.</para>
    ///
    /// <para><b>r13 (audit 2026-05-20):</b> CI dotnet-test has been failing
    /// since r5 (Wave 39) because that release added the
    /// `IsDnsLeakLockdownEnabled` ObservableProperty pair — the Windows pin
    /// got bumped at the time but the Linux pin was missed. r12 user-
    /// reported CI failure email caught this. Updated Linux hash to the
    /// post-Wave-39 value captured from CI run 26150686106. Going forward,
    /// every release that touches MVM surface MUST update both pins, and
    /// CI status must be checked after every ship (not just locally).</para>
    ///
    /// <para><b>v2.36 (2026-05-24):</b> MVP one-button TgProxy UX surface
    /// added (see PinnedHashWindows summary). Linux hash captured from
    /// ubuntu-latest CI run 26363598512 on the initial push, then bumped
    /// here. The added members are NOT inside #if PLATFORM_WINDOWS blocks
    /// (they're cross-platform observable properties + getters), so the
    /// Linux surface drifts in lock-step with Windows.</para>
    ///
    /// <para><b>v2.36.0-r7 (2026-05-24 night):</b> TgProxyOneTap design
    /// surface (see PinnedHashWindows note). Same cross-platform getters,
    /// Linux hash bumped here from CI run 26368644430 actual.</para>
    /// </summary>
    private const string PinnedHashLinux =
        "5bd459a01e48f245b89661ad570a21d23b4d060c74d10e00e413dc75979ae6ac";

    [Fact]
    public void MainWindowViewModel_PublicSurface_MatchesPinnedHash()
    {
        var t = typeof(VPNRouter.App.ViewModels.MainWindowViewModel);
        var hash = PublicSurfaceHashHelper.Compute(t);

        var expected = OperatingSystem.IsWindows() ? PinnedHashWindows : PinnedHashLinux;
        var platform = OperatingSystem.IsWindows() ? "Windows" : "Linux/macOS";

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
}
