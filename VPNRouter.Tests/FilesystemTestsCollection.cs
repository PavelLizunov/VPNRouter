#nullable enable
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Serializes test classes that share the real <c>%ProgramData%\VPNRouter\</c>
/// directory (config.yaml, *.invalid-*, backups). xUnit parallelizes test
/// classes by default; multiple classes touching the same scratch location
/// race on temp-file rename + create operations, producing flaky failures
/// that only repro on Linux CI (ubuntu-latest runners have aggressive
/// filesystem timing).
///
/// <para><strong>Quarantined classes</strong> (use <c>[Collection("FilesystemTests")]</c>):</para>
/// <list type="bullet">
///   <item><see cref="SettingsLoaderRobustnessTests"/></item>
///   <item><see cref="SettingsValidatorTests"/></item>
/// </list>
///
/// <para><strong>Long-term fix</strong>: Phase 3G-1 (Wave 13) replaces
/// <c>SettingsLoader.Load/Save</c> static call sites with
/// <c>ISettingsStore</c> injection; the test refactor switches both classes
/// to <c>InMemorySettingsStore</c> and removes the need for this collection.
/// Once that lands, this file (and the attributes on the two test classes)
/// can be deleted. Tracked in <c>plans/phase3-3G-service-polish-2026-05-18.md</c>.
///
/// <para>The <c>DisableParallelization=true</c> flag means xUnit will run
/// all tests in this collection on a single thread, in the order they're
/// discovered. Slows the full suite by ~2 seconds but eliminates the flake.</para>
/// </summary>
[CollectionDefinition("FilesystemTests", DisableParallelization = true)]
public sealed class FilesystemTestsCollection
{
    // Marker class only — no test methods, no fixture state needed.
    // Just anchors the collection name + parallelization policy.
}
