// Phase 3 — 3G-1 (v3.0 refactor): xUnit collection to serialize tests that
// mutate the global SafeMode.Enabled flag.
//
// Pre-3G, AutoFailoverEngineTests + StartupPipelineTests both flipped
// SafeMode.Enabled=true in their ctors to suppress SettingsLoader.Save's
// write to %ProgramData%\VPNRouter\config.yaml. The flag is a process-
// global static, so when xUnit ran these classes in parallel with
// SettingsLoaderRobustnessTests / SettingsValidatorTests / the new
// ISettingsStoreContractTests, those readers saw the flipped flag and
// Load() short-circuited to defaults — masking real fixture values and
// flaking ~14 cases.
//
// 3G-1 migrated AutoFailoverEngineTests to InMemorySettingsStore so it no
// longer flips SafeMode. StartupPipelineTests still needs SafeMode for its
// FullTunnel-bypass behaviour (see comment on that class) — this
// [Collection] keeps it serialized against the readers so it can't leak
// the global flip mid-parse.

#nullable enable

using Xunit;

namespace VPNRouter.Tests.Fakes;

/// <summary>
/// Marker class used by xUnit's <see cref="CollectionDefinitionAttribute"/>
/// to group tests that mutate process-global state (specifically
/// <c>SafeMode.Enabled</c>). All members of this collection run
/// sequentially relative to each other and relative to <see cref="Xunit.CollectionAttribute"/>-
/// decorated readers.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SafeModeStateCollection
{
    public const string Name = "SafeMode-global-state";
}
