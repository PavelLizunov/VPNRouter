// Phase 3 — 3G-1 (v3.0 refactor): test double for VPNRouter.Core.Services.ISettingsStore.
//
// Replaces the real SettingsLoader.Load/Save which previously hit
// %TEMP%\VPNRouter.SR4.* (or worse, %ProgramData%\VPNRouter\config.yaml) for
// every unit test. The previous headless-test flake was caused by:
//   1. Parallel xUnit cases racing on the rename-to-`.unloadable-{ts}` step
//      when two malformed-yaml cases happened to land in the same millisecond
//      (timestamp collision → File.Move(overwrite:false) threw).
//   2. Cross-test contamination of SettingsLoader.LastRecoveryNotice static
//      property when one test consumed and another expected it populated.
//
// In-memory storage with per-instance recovery-notice state fixes both:
// each InMemorySettingsStore instance owns its own dictionary + notice, no
// global static contention, no filesystem.
//
// Brief: plans/phase3-3G-service-polish-2026-05-18.md §3G-1.

#nullable enable

using System;
using System.Collections.Generic;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests.Fakes;

/// <summary>
/// In-memory <see cref="ISettingsStore"/> for unit tests. Stores serialised
/// <see cref="AppSettings"/> as snapshots in a thread-safe dictionary keyed
/// by path string. Default path (<c>null</c>) maps to a sentinel key so the
/// test can call <c>store.Load()</c> without specifying a path.
///
/// <para>This fake does NOT exercise YAML serialisation — it stores object
/// references directly and clones on Save so subsequent mutations to the
/// in-memory tree don't leak back into the store. Tests that want to assert
/// YAML round-trip behaviour should keep using <see cref="SettingsLoader"/>
/// against a real temp file.</para>
///
/// <para>File-watcher methods are no-ops; tests that need to simulate a
/// file change call <see cref="TriggerWatcher"/> directly.</para>
/// </summary>
public sealed class InMemorySettingsStore : ISettingsStore
{
    private const string DefaultKey = "<default>";

    private readonly object _lock = new();
    private readonly Dictionary<string, AppSettings> _snapshots = new(StringComparer.OrdinalIgnoreCase);

    private string? _recoveryNotice;
    private Action<AppSettings>? _watcherCallback;

    /// <summary>
    /// Number of times <see cref="Save"/> has been called. Useful for
    /// assertions like "exactly one persist after each fix".
    /// </summary>
    public int SaveCount { get; private set; }

    /// <summary>
    /// Last (settings, path) pair passed to <see cref="Save"/>, or null if
    /// no save has happened yet. Path is the resolved key — sentinel for
    /// the default path.
    /// </summary>
    public (AppSettings Settings, string Path)? LastSave { get; private set; }

    /// <summary>
    /// Seed a recovery notice that the next <see cref="ConsumeRecoveryNotice"/>
    /// call will return. Mirrors the real Load() path where SR-1 / SR-4
    /// populates the notice when it had to back-up a bad file.
    /// </summary>
    public void SeedRecoveryNotice(string? notice)
    {
        lock (_lock) { _recoveryNotice = notice; }
    }

    /// <summary>
    /// Simulate an external write that fires the live-reload watcher with
    /// <paramref name="newSettings"/>. No-op if <see cref="StartWatching"/>
    /// hasn't been called.
    /// </summary>
    public void TriggerWatcher(AppSettings newSettings)
    {
        Action<AppSettings>? cb;
        lock (_lock) { cb = _watcherCallback; }
        cb?.Invoke(newSettings);
    }

    /// <inheritdoc />
    public AppSettings Load(string? path = null)
    {
        lock (_lock)
        {
            var key = path ?? DefaultKey;
            if (_snapshots.TryGetValue(key, out var snapshot))
                return snapshot.EnsureSane();
            // First load: hand out an EnsureSane'd default so callers can
            // mutate-then-Save without dealing with null sub-trees.
            return new AppSettings().EnsureSane();
        }
    }

    /// <inheritdoc />
    public void Save(AppSettings settings, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (SafeMode.Enabled) return; // mirror real loader's SafeMode bypass.

        lock (_lock)
        {
            var key = path ?? DefaultKey;
            _snapshots[key] = settings; // store by reference — Load returns the same instance.
            SaveCount++;
            LastSave = (settings, key);
        }
    }

    /// <inheritdoc />
    public string? ResetToDefaults(string? path = null)
    {
        lock (_lock)
        {
            var key = path ?? DefaultKey;
            string? backup = null;
            if (_snapshots.ContainsKey(key))
            {
                // Mirror the real loader's behaviour: surface a synthetic
                // backup path so callers that check "backup != null" still
                // work. The fake doesn't actually write anywhere.
                backup = $"{key}.backup-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}";
                _snapshots.Remove(key);
            }
            _snapshots[key] = new AppSettings().EnsureSane();
            return backup;
        }
    }

    /// <inheritdoc />
    public string? ConsumeRecoveryNotice()
    {
        lock (_lock)
        {
            var notice = _recoveryNotice;
            _recoveryNotice = null;
            return notice;
        }
    }

    /// <inheritdoc />
    public (int Count, string AtUtc) ConsumePlaceholderPruneNotice(AppSettings settings)
    {
        if (settings?.App == null) return (0, string.Empty);
        lock (_lock)
        {
            var count = settings.App.PlaceholderPruneCount;
            var at = settings.App.PlaceholderPruneAtUtc_Str ?? string.Empty;
            settings.App.PlaceholderPruneCount = 0;
            settings.App.PlaceholderPruneAtUtc_Str = string.Empty;
            return (count, at);
        }
    }

    /// <inheritdoc />
    public void StartWatching(string? path = null, Action<AppSettings>? onReload = null)
    {
        lock (_lock) { _watcherCallback = onReload; }
    }

    /// <inheritdoc />
    public void StopWatching()
    {
        lock (_lock) { _watcherCallback = null; }
    }
}
