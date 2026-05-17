#nullable enable
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Phase 2B characterization snapshot for <c>MainWindowViewModel</c>.
/// The class is currently a 6,753-LOC god-class. Phase 2B extracts 4 new
/// partials (Profiles / Subscriptions / FreeConfigs / Settings) without
/// touching the public surface. This test pins the public-surface hash;
/// it must match pre- and post-split. Any drift = forbidden refactor side
/// effect (renamed, removed, or signature-changed member). See
/// <see cref="PublicSurfaceHashHelper"/> for inclusion rules.
///
/// <para>If this test FAILS with "Assert.Equal() Failure", it means the
/// public surface of MainWindowViewModel has drifted from the pinned
/// 2026-05-18 baseline. Either:</para>
/// <list type="number">
///   <item>You intentionally added/removed/changed a public member, in
///   which case re-capture the hash via
///   <c>PublicSurfaceHashHelper.Compute(typeof(MainWindowViewModel))</c>
///   and update the pin below.</item>
///   <item>You accidentally renamed/removed a member during a refactor.
///   Revert that change.</item>
/// </list>
///
/// <para>To see which member drifted, run
/// <see cref="PublicSurfaceHashHelper.DumpMembers"/> and diff against the
/// baseline dump (stored as a test asset is overkill — diff the failing
/// commit's branch against main for the offending member move).</para>
/// </summary>
public class MainWindowViewModelCharacterizationTests
{
    /// <summary>
    /// Pinned public-surface hash, captured 2026-05-18 against the
    /// pre-Phase-2B 6,753-LOC monolith state. Wave 8 (2B split) must
    /// preserve this hash across all 4 partial-class extractions.
    /// </summary>
    private const string PinnedHash =
        "5f190a6078303a3c6a8759d9ebaf70917faa804af18c505eec8789f9a0924e66";

    [Fact]
    public void MainWindowViewModel_PublicSurface_MatchesPinnedHash()
    {
        var t = typeof(VPNRouter.App.ViewModels.MainWindowViewModel);
        var hash = PublicSurfaceHashHelper.Compute(t);

        if (hash != PinnedHash)
        {
            throw new Xunit.Sdk.XunitException(
                $"MainWindowViewModel public-surface hash drifted.\n" +
                $"  Expected (pinned): {PinnedHash}\n" +
                $"  Actual:            {hash}\n" +
                $"If this drift is intentional (Phase 2B split or you " +
                $"genuinely changed the public API), update PinnedHash to " +
                $"the Actual value above. Otherwise, a refactor accidentally " +
                $"renamed/removed/changed a member — revert it.");
        }
    }
}
