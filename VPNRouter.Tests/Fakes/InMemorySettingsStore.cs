#nullable enable

using System;
using System.Collections.Generic;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests.Fakes;

public sealed class InMemorySettingsStore : ISettingsStore
{
    private const string DefaultKey = "<default>";

    private readonly object _lock = new();
    private readonly Dictionary<string, AppSettings> _snapshots = new(StringComparer.OrdinalIgnoreCase);

    private string? _recoveryNotice;
    private Action<AppSettings>? _watcherCallback;

    public int SaveCount { get; private set; }

    public (AppSettings Settings, string Path)? LastSave { get; private set; }

    public void SeedRecoveryNotice(string? notice)
    {
        lock (_lock) { _recoveryNotice = notice; }
    }

    public void TriggerWatcher(AppSettings newSettings)
    {
        Action<AppSettings>? cb;
        lock (_lock) { cb = _watcherCallback; }
        cb?.Invoke(newSettings);
    }

    public AppSettings Load(string? path = null)
    {
        lock (_lock)
        {
            var key = path ?? DefaultKey;
            if (_snapshots.TryGetValue(key, out var snapshot))
                return snapshot.EnsureSane();
            return new AppSettings().EnsureSane();
        }
    }

    public void Save(AppSettings settings, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (SafeMode.Enabled) return;

        lock (_lock)
        {
            var key = path ?? DefaultKey;
            _snapshots[key] = settings;
            SaveCount++;
            LastSave = (settings, key);
        }
    }

    public string? ResetToDefaults(string? path = null)
    {
        lock (_lock)
        {
            var key = path ?? DefaultKey;
            string? backup = null;
            if (_snapshots.ContainsKey(key))
            {
                backup = $"{key}.backup-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}";
                _snapshots.Remove(key);
            }
            _snapshots[key] = new AppSettings().EnsureSane();
            return backup;
        }
    }

    public string? ConsumeRecoveryNotice()
    {
        lock (_lock)
        {
            var notice = _recoveryNotice;
            _recoveryNotice = null;
            return notice;
        }
    }

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

    public void StartWatching(string? path = null, Action<AppSettings>? onReload = null)
    {
        lock (_lock) { _watcherCallback = onReload; }
    }

    public void StopWatching()
    {
        lock (_lock) { _watcherCallback = null; }
    }
}
