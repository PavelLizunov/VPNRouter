// Phase 3 — 3G-1 (v3.0 refactor): single settings-persistence seam.
//
// Audit D §1: 17 call sites use `SettingsLoader.Load()` / `SettingsLoader.Save(...)`
// statically. The static path is fine for the desktop UI (a settings tree is a
// process-wide singleton anyway), but it makes unit testing painful — the
// SettingsLoaderRobustnessTests suite writes real files to `%TEMP%\VPNRouter.SR4.*`
// per case, and parallel xUnit runs have raced on the rename-to-`.unloadable-*`
// step (see VPNRouter.Tests/CLAUDE.md "Headless tests — known issues").
//
// Solution: thin `ISettingsStore` interface + `RealSettingsStore` (delegates
// to the existing static `SettingsLoader.Load/Save/ResetToDefaults`) +
// `InMemorySettingsStore` (xUnit fake, no filesystem). The static facade on
// `SettingsLoader` stays — keeps the 17 call sites compiling without churn —
// but classes that want testability can opt in by taking an `ISettingsStore`
// via ctor with `RealSettingsStore.Instance` as the back-compat default.
//
// Brief: plans/phase3-3G-service-polish-2026-05-18.md §3G-1.

#nullable enable

using System;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// Abstraction over the persistent <see cref="AppSettings"/> store. Wraps
/// the YAML load/save/reset operations and the live-reload file watcher.
///
/// <para>Two production paths:
/// <list type="bullet">
///   <item><see cref="RealSettingsStore.Instance"/> — back-compat singleton
///   that delegates to the existing static <see cref="SettingsLoader"/> API.
///   Use this everywhere outside tests.</item>
///   <item><c>InMemorySettingsStore</c> (in <c>VPNRouter.Tests/Fakes</c>) —
///   thread-safe dictionary-backed fake; no filesystem, no parallelism flake.</item>
/// </list></para>
///
/// <para>Lifecycle notes:
/// <list type="bullet">
///   <item>Implementations MUST swallow all transient I/O failures and
///   return defaults rather than throwing — matches the Load() / Save()
///   pre-3G contract documented on <see cref="SettingsLoader"/>.</item>
///   <item><see cref="StartWatching"/> / <see cref="StopWatching"/> are
///   optional no-ops on Android / in-memory implementations (no real
///   file-watcher available).</item>
/// </list></para>
/// </summary>
public interface ISettingsStore
{
    /// <summary>
    /// Load <c>config.yaml</c> (or platform equivalent). Never throws.
    /// </summary>
    /// <param name="path">Optional override path; <c>null</c> = the
    /// platform default (<c>AppPaths.ConfigYamlPath</c>).</param>
    AppSettings Load(string? path = null);

    /// <summary>
    /// Persist <paramref name="settings"/> to <paramref name="path"/>.
    /// <see cref="SafeMode"/> bypasses the actual write (matches the
    /// pre-3G contract).
    /// </summary>
    void Save(AppSettings settings, string? path = null);

    /// <summary>
    /// Reset the persisted config to factory defaults. The previous file
    /// is backed up as <c>{path}.backup-{timestamp}</c>.
    /// </summary>
    /// <returns>Path of the backup file that was created, or null if no
    /// prior config existed to back up.</returns>
    string? ResetToDefaults(string? path = null);

    /// <summary>
    /// Read-once accessor for the recovery notice populated by the most
    /// recent <see cref="Load"/> when SR-1 / SR-4 had to back-up a bad
    /// file and write defaults. Returns <c>null</c> once consumed.
    /// </summary>
    string? ConsumeRecoveryNotice();

    /// <summary>
    /// Read-once accessor for the v2.32.3 placeholder-prune notice
    /// surfaced by the Load pipeline when it stripped known-bad legacy
    /// credentials. Pair with <see cref="Save"/> to suppress the banner
    /// on subsequent launches.
    /// </summary>
    (int Count, string AtUtc) ConsumePlaceholderPruneNotice(AppSettings settings);

    /// <summary>
    /// Begin watching the config file for external writes. Each detected
    /// change re-parses the file and invokes <paramref name="onReload"/>.
    /// No-op on platforms that don't support file watching.
    /// </summary>
    void StartWatching(string? path = null, Action<AppSettings>? onReload = null);

    /// <summary>Stop the live-reload watcher started by <see cref="StartWatching"/>.</summary>
    void StopWatching();
}

/// <summary>
/// Production <see cref="ISettingsStore"/> backed by the existing static
/// <see cref="SettingsLoader"/> facade. Singleton — the underlying loader's
/// recovery-notice state and file-watcher are themselves singletons so
/// instantiating multiple <see cref="RealSettingsStore"/> instances would
/// just hand out aliases to the same underlying state.
///
/// <para><b>Phase 6 (v3.0 refactor):</b> <see cref="SettingsLoader.Load"/>
/// + <see cref="SettingsLoader.Save"/> are now <c>internal static</c>
/// rather than <c>public [Obsolete]</c>, so the previous CS0618
/// suppression block here is no longer needed — same-assembly callers
/// in <c>VPNRouter.Core</c> see the internal API directly. The
/// <see cref="Instance"/> singleton remains as the back-compat default
/// for ctor-injected <see cref="ISettingsStore"/> consumers (CLI commands,
/// the desktop ViewModel, Service, AutoFailoverEngine, StartupPipeline,
/// FreeConfigs VM, plus the contract test suite).</para>
/// </summary>
public sealed class RealSettingsStore : ISettingsStore
{
    /// <summary>Process-wide default instance.</summary>
    public static RealSettingsStore Instance { get; } = new();

    private RealSettingsStore() { }

    /// <inheritdoc />
    public AppSettings Load(string? path = null) => SettingsLoader.Load(path);

    /// <inheritdoc />
    public void Save(AppSettings settings, string? path = null) =>
        SettingsLoader.Save(settings, path);

    /// <inheritdoc />
    public string? ResetToDefaults(string? path = null) =>
        SettingsLoader.ResetToDefaults(path);

    /// <inheritdoc />
    public string? ConsumeRecoveryNotice() =>
        SettingsLoader.ConsumeRecoveryNotice();

    /// <inheritdoc />
    public (int Count, string AtUtc) ConsumePlaceholderPruneNotice(AppSettings settings) =>
        SettingsLoader.ConsumePlaceholderPruneNotice(settings);

    /// <inheritdoc />
    public void StartWatching(string? path = null, Action<AppSettings>? onReload = null) =>
        SettingsLoader.StartWatching(path, onReload);

    /// <inheritdoc />
    public void StopWatching() => SettingsLoader.StopWatching();
}
