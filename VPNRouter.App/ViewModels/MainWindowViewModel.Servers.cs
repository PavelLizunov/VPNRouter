using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Platform;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.FreeConfigs;
using VPNRouter.App.Localization;
using VPNRouter.App.ViewModels.FreeConfigs;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel
{
    /// <summary>
    /// v2.32 (r10, F-C) — mark each entry in <see cref="Servers"/> as orphan
    /// if it doesn't belong to any enabled subscription. The badge in
    /// ServersPage row template binds to <c>IsOrphanFromSubscription</c>.
    ///
    /// <para>Match by composite key <c>{server|port|uuid}</c> (case-insensitive)
    /// so the same physical server can be identified across name renames.</para>
    ///
    /// <para>Called from <c>LoadSettingsIntoUI</c> after Servers is rebuilt,
    /// and re-runs on subscription refresh via <c>RefreshSubscriptionAsync</c>
    /// (added in callsite there).</para>
    /// </summary>
    private void MarkOrphanServers()
    {
        // r10 r9 (Bug-r10-H, 2026-05-12 brat screenshot) — null-safe guard
        // for early calls during ctor wire-up before _settings lands.
        if (_settings == null) return;

        var hasEnabledSubs = _settings.App?.Subscriptions?
            .Any(s => s.Enabled && (s.Servers?.Count ?? 0) > 0) == true;
        if (!hasEnabledSubs)
        {
            foreach (var vm in Servers)
                vm.IsOrphanFromSubscription = false;
            return;
        }

        var subKeys = _settings.App!.Subscriptions!
            .Where(s => s.Enabled)
            .SelectMany(s => s.Servers ?? new System.Collections.Generic.List<VlessServerEntry>())
            .Select(s => $"{s.Server}|{s.Port}|{s.Uuid}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var vm in Servers)
        {
            var key = $"{vm.Server}|{vm.Port}|{vm.Uuid}";
            vm.IsOrphanFromSubscription = !subKeys.Contains(key);
        }
    }

    /// <summary>
    /// r10 r9 (Bug-r10-H, 2026-05-12 brat screenshot) — listener wired
    /// after <c>_settings</c> is loaded to keep <c>IsOrphanFromSubscription</c>
    /// in sync on ANY mutation of <see cref="Servers"/>. Pre-r9 the badge
    /// re-evaluation happened only in <c>LoadSettingsIntoUI</c> (initial
    /// load) and <c>RemoveServerByEntry</c> (× click). Other paths —
    /// Free Configs «Использовать» (<see cref="ApplyFreeConfigAsync"/>),
    /// VLESS URI paste, subscription refresh-into-list — added entries
    /// directly via <c>Servers.Add</c> and the badge state stayed at
    /// the default <c>false</c>, so freshly-added orphans showed without
    /// the «Не из подписки» badge while older orphans had it. User saw
    /// is-01-hy2-test marked but ⚡ [EE] not, even though both are
    /// non-subscription manual entries — inconsistent.
    ///
    /// <para>Guarded by <see cref="_isLoadingUI"/> so bulk reload's
    /// per-Add CollectionChanged events don't trigger N redundant
    /// MarkOrphanServers calls; the explicit single call at the end
    /// of <c>LoadSettingsIntoUI</c> covers that path.</para>
    /// </summary>
    private void WireServersOrphanTracking()
    {
        Servers.CollectionChanged += (_, _) =>
        {
            if (_isLoadingUI) return;
            // r9 follow-up #1: keep the "naive + hy2" subtitle in sync on manual
            // Add/Remove/row-delete/free-config-apply — not just load + sub rebuild.
            try { ServerViewModel.RefreshUdpSiblingFlags(Servers); }
            catch (Exception ex) { _logger?.Warning(ex, "[VM] Auto RefreshUdpSiblingFlags on Servers change failed"); }
            try { ServerViewModel.RefreshProviderRiskFlags(Servers); } // R3
            catch (Exception ex) { _logger?.Warning(ex, "[VM] Auto RefreshProviderRiskFlags on Servers change failed"); }
            try { MarkOrphanServers(); }
            catch (Exception ex) { _logger?.Warning(ex, "[VM] Auto MarkOrphanServers on Servers change failed"); }
        };
    }

    [RelayCommand]
    private void AddServer()
    {
        var lines = VlessUri?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

        foreach (var line in lines)
        {
            // v2.30.1-r3: dispatch by scheme via ServerUriParser instead
            // of hard-coded vless:// prefix check. Pasting Hysteria2 /
            // TUIC / Shadowsocks links lands in the same Servers list.
            if (!ServerUriParser.IsSupportedScheme(line))
                continue;

            try
            {
                var entry = ServerUriParser.Parse(line);
                // Check duplicate by name+IP+port (same IP+port with different
                // name/uuid is OK). Port is part of the comparison — without it,
                // two different transports on the same host (e.g. an AmneziaWG
                // endpoint and an xhttp VLESS server, both named "main-brat" on
                // the same IP but different ports) collide and the second paste
                // silently does nothing.
                if (Servers.Any(s => s.Name == entry.Name && s.Server == entry.Server && s.Port == entry.Port))
                    continue;
                Servers.Add(new ServerViewModel(entry));
                SaveSettings();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to parse server URI: {Line}", line);
            }
        }

        VlessUri = string.Empty;
    }

    [RelayCommand]
    private void RemoveServer()
    {
        if (SelectedServer != null)
            Servers.Remove(SelectedServer);
    }

    /// <summary>
    /// Per-row delete (the × button on each VLESS server row). Removes
    /// the specific entry passed as the parameter without changing
    /// selection — clicking the × on row N must NOT trigger
    /// OnSelectedServerChanged (which would auto-reconnect to row N
    /// when VPN is running). v2.30.1-r3 fix: user reported "при каждом
    /// клике на другие конфиги для удаления, оно запускались, так как
    /// я на них кликал, только потом я их удалял".
    /// </summary>
    [RelayCommand]
    private void RemoveServerByEntry(ServerViewModel? entry)
    {
        if (entry == null) return;
        // Don't change SelectedServer — the row's × button removes the
        // row directly. If the entry being removed is the active one,
        // clear SelectedServer too so the now-empty radio doesn't
        // dangle on a freed row.
        var wasSelected = ReferenceEquals(SelectedServer, entry);
        Servers.Remove(entry);
        if (wasSelected)
            SelectedServer = Servers.FirstOrDefault();

        // v2.32.1-r6 (Bug-r10-D): user-reported pain — user deleted a
        // VLESS server entry that the F-C orphan badge suggested
        // removing, but after app restart the entry reappeared because
        // the row removal only mutated the in-memory ObservableCollection
        // and never wrote back to YAML. SaveSettings (line ~3686) does
        // rebuild _settings.Vless.Servers from this collection, but the
        // function wasn't called for row-level mutations — only on
        // Apply / connect transitions. Now we persist immediately on
        // any × click so the deletion sticks through restart.
        SaveSettings();
        _logger?.Information(
            "[VM] RemoveServerByEntry: persisted deletion of '{Name}' ({Server}:{Port}) — {Remaining} servers remain",
            entry.Name, entry.Server, entry.Port, Servers.Count);

        // F-C marker on remaining entries needs refresh — the deleted
        // entry might have been the only orphan; or the previously
        // active server may have been the deleted one and we need to
        // re-mark the new selection.
        MarkOrphanServers();
    }

    [RelayCommand]
    private async Task AddCustomConfigAsync()
    {
        try
        {
            var window = GetMainWindow();
            if (window == null)
            {
                _logger.Warning("[VM] AddCustomConfig: MainWindow not found");
                StatusText = IsRussian ? "Не удалось открыть диалог выбора файла" : "Failed to open file picker";
                return;
            }

            var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Strings.SelectSingBoxConfig,
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } }
                }
            });

            if (files.Count == 0) return;

            var file = files[0];
            var sourcePath = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(sourcePath)) return;

            var configName = Path.GetFileNameWithoutExtension(sourcePath);

            // Check duplicate
            if (CustomConfigs.Any(c => c.Name.Equals(configName, StringComparison.OrdinalIgnoreCase)))
            {
                StatusText = Strings.ConfigExists(configName);
                return;
            }

            // Validate
            var json = await File.ReadAllTextAsync(sourcePath);
            var (isValid, errors) = CustomConfigInjector.Validate(json);
            if (!isValid)
            {
                StatusText = $"{Strings.InvalidConfig} {string.Join("; ", errors)}";
                return;
            }

            // Copy to app support
            var destPath = CustomConfigInjector.CopyToProgramData(sourcePath, configName);
            var entry = new CustomConfigEntry { Name = configName, Path = destPath };

            var isFirst = CustomConfigs.Count == 0;
            var vm = new CustomConfigViewModel(entry, isFirst);
            CustomConfigs.Add(vm);

            // Auto-select and save
            SelectedCustomConfig = vm;
            SaveSettings();
            StatusText = IsRussian
                ? $"Конфиг \"{configName}\" добавлен" + (isFirst ? " и активирован" : "")
                : $"Config \"{configName}\" added" + (isFirst ? " and activated" : "");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VM] AddCustomConfig failed");
            StatusText = IsRussian
                ? $"Ошибка добавления конфига: {ex.Message}"
                : $"Failed to add config: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RemoveCustomConfig()
    {
        if (SelectedCustomConfig == null) return;
        var name = SelectedCustomConfig.Name;
        var wasActive = SelectedCustomConfig.IsActive;
        CustomConfigs.Remove(SelectedCustomConfig);

        // If removed the active one, activate the first remaining
        if (wasActive && CustomConfigs.Count > 0)
        {
            CustomConfigs[0].IsActive = true;
            SelectedCustomConfig = CustomConfigs[0];
        }

        SaveSettings();
        StatusText = IsRussian ? $"Конфиг \"{name}\" удалён" : $"Config \"{name}\" removed";
    }

    [RelayCommand]
    private void SetActiveCustomConfig(CustomConfigViewModel? config)
    {
        if (config == null) return;
        foreach (var c in CustomConfigs)
            c.IsActive = false;
        config.IsActive = true;
        SaveSettings();
    }

}
