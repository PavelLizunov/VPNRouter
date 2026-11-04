using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.App.Localization;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty] private ServerViewModel? _detailServer;
    [ObservableProperty] private CustomConfigViewModel? _detailCustomConfig;

    [RelayCommand]
    private void CloseServerDetail() => DetailServer = null;

    [RelayCommand]
    private void CloseCustomConfigDetail() => DetailCustomConfig = null;

    [RelayCommand]
    private void OpenServerDetail(ServerViewModel? server) => DetailServer = server;

    [RelayCommand]
    private void OpenCustomConfigDetail(CustomConfigViewModel? cfg) => DetailCustomConfig = cfg;

    private void MarkOrphanServers()
    {
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

    private void WireServersOrphanTracking()
    {
        Servers.CollectionChanged += (_, _) =>
        {
            if (_isLoadingUI) return;
            try { ServerViewModel.RefreshUdpSiblingFlags(Servers); }
            catch (Exception ex) { _logger.Warning(ex, "[VM] Auto RefreshUdpSiblingFlags on Servers change failed"); }
            try { ServerViewModel.RefreshProviderRiskFlags(Servers); }
            catch (Exception ex) { _logger.Warning(ex, "[VM] Auto RefreshProviderRiskFlags on Servers change failed"); }
            try { MarkOrphanServers(); }
            catch (Exception ex) { _logger.Warning(ex, "[VM] Auto MarkOrphanServers on Servers change failed"); }
        };
    }

    [RelayCommand]
    private void AddServer()
    {
        var rawInput = (VlessUri ?? string.Empty).Trim();
        var addedAny = false;

        if (ServerUriParser.IsWireGuardConf(rawInput))
        {
            try
            {
                var entry = ServerUriParser.Parse(rawInput);
                if (!Servers.Any(s => s.Name == entry.Name && s.Server == entry.Server && s.Port == entry.Port))
                {
                    Servers.Add(new ServerViewModel(entry));
                    addedAny = true;
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to parse WireGuard / AmneziaWG config: {Error}", ex.Message);
            }
        }
        else
        {
            var lines = rawInput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var line in lines)
            {
                if (!ServerUriParser.IsSupportedScheme(line))
                    continue;

                try
                {
                    var entry = ServerUriParser.Parse(line);
                    if (Servers.Any(s => s.Name == entry.Name && s.Server == entry.Server && s.Port == entry.Port))
                        continue;
                    Servers.Add(new ServerViewModel(entry));
                    addedAny = true;
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to parse server URI: {Line}", CrashReporter.ScrubSecrets(line));
                }
            }
        }

        if (addedAny)
        {
            if (SelectedServer == null)
                SelectedServer = Servers.FirstOrDefault();
            SaveSettings();
        }

        VlessUri = string.Empty;
    }

    [RelayCommand]
    private void RemoveServer()
    {
        if (SelectedServer != null)
            RemoveServerByEntry(SelectedServer);
    }

    [RelayCommand]
    private void RemoveServerByEntry(ServerViewModel? entry)
    {
        if (entry == null) return;
        var wasSelected = ReferenceEquals(SelectedServer, entry);
        Servers.Remove(entry);
        if (wasSelected)
            SelectedServer = Servers.FirstOrDefault();

        SaveSettings();
        _logger.Information(
            "[VM] RemoveServerByEntry: persisted deletion of '{Name}' ({Server}:{Port}) — {Remaining} servers remain",
            entry.Name, entry.Server, entry.Port, Servers.Count);

        MarkOrphanServers();
        RefreshActiveIndicator();
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

            if (CustomConfigs.Any(c => c.Name.Equals(configName, StringComparison.OrdinalIgnoreCase)))
            {
                StatusText = Strings.ConfigExists(configName);
                return;
            }

            var json = await File.ReadAllTextAsync(sourcePath);
            var (isValid, errors) = CustomConfigInjector.Validate(json);
            if (!isValid)
            {
                StatusText = $"{Strings.InvalidConfig} {string.Join("; ", errors)}";
                return;
            }

            var destPath = CustomConfigInjector.CopyToProgramData(sourcePath, configName);
            var entry = new CustomConfigEntry { Name = configName, Path = destPath };

            var isFirst = CustomConfigs.Count == 0;
            var vm = new CustomConfigViewModel(entry, isFirst);
            CustomConfigs.Add(vm);

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

    partial void OnSelectedSubscriptionServerChanged(ServerViewModel? value)
    {
        if (_isLoadingUI || value == null || _isReconnecting) return;
        if (value.IsActive) return;
        _logger.Information(
            "[VM] OnSelectedSubscriptionServerChanged name={N} ip={Ip} IsConnected={C} IsSubscribeMode={S} IsConnecting={IC}",
            value.DisplayName, value.Server, IsConnected, IsSubscribeMode, IsConnecting);
        if (IsConnected && IsSubscribeMode && !IsConnecting)
        {
            if (IsServiceManagedVpn) { WarnServiceManagedReconnect(value.DisplayName); return; }
            _ = ReconnectAsync(value.DisplayName, ReconnectIntent.Subscription);
        }
    }

    partial void OnSelectedServerChanged(ServerViewModel? value)
    {
        if (_isLoadingUI || value == null || _isReconnecting) return;

        _logger.Information(
            "[VM] OnSelectedServerChanged name={N} ip={Ip} IsConnected={C} IsVlessMode={V} IsSubscribeMode={S} IsConnecting={IC}",
            value.DisplayName, value.Server, IsConnected, IsVlessMode, IsSubscribeMode, IsConnecting);
        if (IsConnected && IsVlessMode && !IsConnecting)
        {
            if (IsServiceManagedVpn) { WarnServiceManagedReconnect(value.DisplayName); return; }
            _ = ReconnectAsync(value.DisplayName, ReconnectIntent.ManualVless);
        }
    }

    internal IReadOnlyList<ServerViewModel> GetBusinessServerChoices()
    {
        var names = BusinessDefaults.GetServerChoices(Servers.Select(s => s.ToEntry()))
            .Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Servers.Where(s => names.Contains(s.Name)).ToArray();
    }

    internal void SelectBusinessServer(ServerViewModel? server)
    {
        if (_isLoadingUI || _isReconnecting || IsConnected || IsConnecting ||
            server == null || !GetBusinessServerChoices().Contains(server)) return;

        SelectedServer = server;
        try { SaveSettings(); }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[Business] Failed to save server selection");
            StatusText = VPNRouter.Core.Localization.Strings.BusinessServerSaveFailed;
        }
    }

    internal bool ReplaceBusinessServers(string connection, string sourceHash)
    {
        if (_isLoadingUI || _isReconnecting || IsConnected || IsConnecting || _engine.IsRunning) return false;
        var entries = BusinessDefaults.ParseConnection(connection);
        var next = BusinessDefaults.CreateServerPool(entries, _settings.Vless);
        var rows = entries.Select(s => new ServerViewModel(s)).ToArray();
        ServerViewModel.RefreshUdpSiblingFlags(rows);
        ServerViewModel.RefreshProviderRiskFlags(rows);
        var old = _settings.Vless;
        var oldSource = _settings.App.BusinessSubscriptionSource;
        var oldCache = _settings.App.BusinessSubscriptionCache;
        _settings.Vless = next;
        _settings.App.BusinessSubscriptionSource = sourceHash;
        _settings.App.BusinessSubscriptionCache = connection;
        try { _settingsStore.Save(_settings, AppPaths.ConfigYamlPath); }
        catch
        {
            _settings.Vless = old;
            _settings.App.BusinessSubscriptionSource = oldSource;
            _settings.App.BusinessSubscriptionCache = oldCache;
            throw;
        }
        _isLoadingUI = true;
        try
        {
            Servers.Clear();
            foreach (var row in rows) Servers.Add(row);
            SelectedServer = rows.First(s => s.Name == next.ActiveServer);
        }
        finally { _isLoadingUI = false; }
        return true;
    }

    partial void OnSelectedCustomConfigChanged(CustomConfigViewModel? value)
    {
        if (_isLoadingUI || value == null) return;
        if (value.IsActive) return;
        if (_isReconnecting) return;

        SetActiveCustomConfig(value);

        if (IsConnected && !IsVlessMode && !IsConnecting)
        {
            if (IsServiceManagedVpn) { WarnServiceManagedReconnect(value.Name); return; }
            _ = ReconnectAsync(value.Name, ReconnectIntent.CustomConfig);
        }
    }
}
