#nullable enable
using System;
using System.IO;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Phase 2C characterization snapshot for <c>VPNRouter.Android.AndroidApp</c>.
/// Wave 9 (2026-05-18) extracts 4 new partials (Notifications / Permissions
/// / VpnLifecycle / UiBindings) out of the 7,177-LOC god-class without
/// touching the public/private surface. This test pins the source-derived
/// member-signature hash; it must match pre and post split. Any drift =
/// forbidden refactor side effect (renamed, removed, or signature-changed
/// member).
///
/// <para><strong>Why source-derived instead of reflection-derived?</strong>
/// Wave 8's <see cref="MainWindowViewModelCharacterizationTests"/> works
/// via reflection because <c>MainWindowViewModel</c> lives in
/// <c>VPNRouter.App</c> (net8.0) which the test project references.
/// <c>AndroidApp</c> however lives in <c>VPNRouter.Android</c>
/// (net8.0-android), which a net8.0 test project cannot reference at all
/// — incompatible target framework. So this test reads the AndroidApp
/// source files directly and reconstructs the member-signature set from
/// the C# declarations. Different mechanism, same invariant.</para>
///
/// <para><strong>If this test FAILS</strong>, it means the union of
/// member declarations across all <c>VPNRouter.Android/AndroidApp*.cs</c>
/// files drifted from the pinned 2026-05-18 baseline. Either:</para>
/// <list type="number">
///   <item>You intentionally added/removed/changed a member, in which
///   case re-capture the hash via
///   <c>AndroidAppSourceSurfaceHashHelper.Compute(androidProjectDir)</c>
///   and update the pin below.</item>
///   <item>You accidentally renamed/dropped a member during the partial
///   split. Revert that change and re-stage.</item>
/// </list>
///
/// <para>To see which member drifted, run
/// <c>AndroidAppSourceSurfaceHashHelper.DumpMembers(androidProjectDir)</c>
/// against the pre-split commit and diff against the post-split run.</para>
///
/// <para><strong>Why pin instead of compare against HEAD?</strong>
/// The pin lives in source so it survives across worktrees and reaches
/// every developer's machine through the same channel as the refactor.
/// Comparing against HEAD would tie tests to git state, which doesn't
/// work in CI sandboxes or detached-HEAD release branches.</para>
/// </summary>
public class AndroidAppCharacterizationTests
{
    /// <summary>
    /// Source-derived member-set hash. Re-pinned 2026-05-18 for Phase 4
    /// (Wave 18) <c>IUpdateSource</c> caller migration, which intentionally
    /// reshaped <c>AndroidApp.AutoUpdate.cs</c>:
    /// <list type="bullet">
    ///   <item><c>_pendingUpdate</c> field type changed from
    ///   <c>AndroidUpdateInfo?</c> to
    ///   <c>UpdateSourceInfo?</c> (platform-neutral record).</item>
    ///   <item>Added <c>_updateSource</c> + <c>_updateSourceChannel</c>
    ///   private fields caching the channel-keyed
    ///   <c>IUpdateSource</c>.</item>
    ///   <item>Added <c>GetOrBuildUpdateSource</c> + <c>LaunchInstallAsync</c>
    ///   private methods; <c>PromptUpdateAvailable</c> signature changed
    ///   to take <c>UpdateSourceInfo</c>.</item>
    /// </list>
    /// Pre-Phase 4 (Wave 9) hash was
    /// <c>98061071858cefdc384be4f69e109f0f4b3d31aaa4c0158d0386fd22a6bb219f</c>.
    /// </summary>
    private const string PinnedHash =
        "a9a2a0a88c577f6933276702657a8ce755c60285e5c8ac29acd0f9b3fea32e03";

    [Fact]
    public void AndroidApp_SourceSurface_MatchesPinnedHash()
    {
        var dir = AndroidAppSourceSurfaceHashHelper.FindAndroidProjectDir();
        if (dir is null || !Directory.Exists(dir))
        {
            // Running outside the worktree (CI sandbox without source
            // checkout, NuGet-only test execution, etc.) — skip rather
            // than fail. The pin is meaningful only when we can read
            // the AndroidApp partials.
            return;
        }

        var hash = AndroidAppSourceSurfaceHashHelper.Compute(dir);

        if (PinnedHash == "PENDING_INITIAL_CAPTURE")
        {
            throw new Xunit.Sdk.XunitException(
                $"AndroidApp characterization hash has not been pinned yet.\n" +
                $"  Computed hash: {hash}\n" +
                $"Update PinnedHash in AndroidAppCharacterizationTests.cs " +
                $"to this value once the pre-split baseline is confirmed.");
        }

        if (hash != PinnedHash)
        {
            throw new Xunit.Sdk.XunitException(
                $"AndroidApp source-surface hash drifted from the pinned baseline.\n" +
                $"  Expected (pinned): {PinnedHash}\n" +
                $"  Actual:            {hash}\n" +
                $"If this drift is intentional (Wave 9 partial split or you " +
                $"genuinely changed AndroidApp's surface), update the " +
                $"PinnedHash constant to the Actual value above. Otherwise, " +
                $"a refactor accidentally renamed/removed/changed a member " +
                $"— revert it. Use " +
                $"AndroidAppSourceSurfaceHashHelper.DumpMembers(androidProjectDir) " +
                $"on the pre- and post-split states to see which member " +
                $"changed.");
        }
    }
}
