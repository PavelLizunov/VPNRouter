#nullable enable

using System;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public interface ISettingsStore
{
    AppSettings Load(string? path = null);

    void Save(AppSettings settings, string? path = null);

    string? ResetToDefaults(string? path = null);

    string? ConsumeRecoveryNotice();

    (int Count, string AtUtc) ConsumePlaceholderPruneNotice(AppSettings settings);

    void StartWatching(string? path = null, Action<AppSettings>? onReload = null);

    void StopWatching();
}

public sealed class RealSettingsStore : ISettingsStore
{
    public static RealSettingsStore Instance { get; } = new();

    private RealSettingsStore() { }

    public AppSettings Load(string? path = null) => SettingsLoader.Load(path);

    public void Save(AppSettings settings, string? path = null) =>
        SettingsLoader.Save(settings, path);

    public string? ResetToDefaults(string? path = null) =>
        SettingsLoader.ResetToDefaults(path);

    public string? ConsumeRecoveryNotice() =>
        SettingsLoader.ConsumeRecoveryNotice();

    public (int Count, string AtUtc) ConsumePlaceholderPruneNotice(AppSettings settings) =>
        SettingsLoader.ConsumePlaceholderPruneNotice(settings);

    public void StartWatching(string? path = null, Action<AppSettings>? onReload = null) =>
        SettingsLoader.StartWatching(path, onReload);

    public void StopWatching() => SettingsLoader.StopWatching();
}
