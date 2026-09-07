using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VPNRouter.App.Localization;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.App.ViewModels;

/// <summary>
/// v2.32.2 (W-4) — Tools tab Emergency Channel (wgturn) card.
///
/// <para>Three visual states:</para>
/// <list type="bullet">
/// <item><b>Install</b> — when <see cref="WgturnUpdater.IsInstalled"/> is
/// false. Surfaces an «Install (~10 MB)» button + secondary «Download
/// full version (~120 MB)» for the Embedded variant + description text.</item>
/// <item><b>Idle</b> — wgturn-cli installed but not connected. Surfaces a
/// config picker (ComboBox over <see cref="WgturnConfigs"/>), a VK-link
/// input, and Connect / Remove / Update buttons.</item>
/// <item><b>Connected</b> — wgturn-cli is running. Shows status line with
/// the active label + PID + a Disconnect button.</item>
/// </list>
///
/// <para>Depends on the W-1 chip (<see cref="WgturnUpdater"/>) for the
/// real download pipeline. The Core stub in <c>VPNRouter.Core/Services/WgturnUpdater.cs</c>
/// throws on <c>DownloadLatestAsync</c> — once W-1 lands the stub will be
/// deleted and the real implementation will plug in transparently
/// because the public API is frozen.</para>
///
/// <para>Depends on the W-2 chip for <see cref="AppPaths.WgturnCliExePath"/>
/// + <see cref="AppPaths.WgturnCliLogPath"/>; those constants already exist
/// in the worktree (added in v2.31.10 era), so W-4 references them today.</para>
/// </summary>
public partial class MainWindowViewModel
{
    // ── Observable state ──

    /// <summary>True iff <see cref="WgturnUpdater.IsInstalled"/> returned
    /// true on the most recent poll. Polled at ctor time and after
    /// every download / remove command.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWgturnCardInstallVisible))]
    [NotifyPropertyChangedFor(nameof(IsWgturnCardIdleVisible))]
    [NotifyPropertyChangedFor(nameof(IsWgturnCardConnectedVisible))]
    [NotifyPropertyChangedFor(nameof(WgturnTitleText))]
    private bool _isWgturnInstalled;

    /// <summary>Installed version tag (e.g. <c>v0.1.0</c>) from
    /// <c>{DataDir}/wgturn/version.txt</c>. Empty when not installed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WgturnTitleText))]
    private string _wgturnVersion = string.Empty;

    /// <summary>Installed variant marker — <c>slim</c> or <c>embedded</c>.
    /// Empty when not installed. Named <c>WgturnVariantLabel</c> on
    /// purpose to avoid clashing with the
    /// <see cref="WgturnVariant"/> enum imported via
    /// <c>VPNRouter.Core.Services</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WgturnTitleText))]
    private string _wgturnVariantLabel = string.Empty;

    /// <summary>True while a download / install is in flight. Disables
    /// the install + update buttons so a double-click can't race with
    /// the in-flight download.</summary>
    [ObservableProperty] private bool _isWgturnDownloading;

    /// <summary>True while StartAsync is racing to bring the tunnel up.
    /// Coalesces with <see cref="IsWgturnConnected"/> to drive the
    /// status badge color (connecting → amber, connected → green).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WgturnStatusText))]
    private bool _isWgturnConnecting;

    /// <summary>True when the <see cref="EmergencyChannelEngine"/>
    /// reports <see cref="EmergencyChannelState.Connected"/>. Flipped by
    /// the engine's <see cref="EmergencyChannelEngine.StateChanged"/>
    /// event handler in the ctor wiring.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWgturnCardIdleVisible))]
    [NotifyPropertyChangedFor(nameof(IsWgturnCardConnectedVisible))]
    [NotifyPropertyChangedFor(nameof(WgturnStatusText))]
    private bool _isWgturnConnected;

    /// <summary>PID of the running wgturn-cli, or null when not running.
    /// Shown on the Connected-state card so a debugger can find it.</summary>
    [ObservableProperty] private int? _wgturnPid;

    /// <summary>Current VK Calls invite link. Two-way bound to the input
    /// TextBox; persisted to <c>EmergencyChannel.LastVkLink</c> on
    /// connect.</summary>
    [ObservableProperty] private string _wgturnVkLink = string.Empty;

    /// <summary>Currently-selected entry in <see cref="WgturnConfigs"/>.
    /// Drives the Connect command's payload (the URL portion).</summary>
    [ObservableProperty] private WgturnEntry? _selectedWgturnConfig;

    /// <summary>
    /// r10 r9+ (Bug-r10-I, brat 2026-05-12) — input field for adding
    /// a new wgturn config to <see cref="WgturnConfigs"/>. Two-way bound
    /// to a TextBox in the add-form. Empty until user pastes the
    /// <c>wgturn://...</c> URL their operator gave them. Pre-r9+ there
    /// was no input — the AddConfig command read from
    /// <see cref="WgturnVkLink"/> as both URL AND VK link, which was
    /// confusing UX (user couldn't add a config without misusing the
    /// VK-link field).
    /// </summary>
    [ObservableProperty] private string _newWgturnConfigUrl = string.Empty;

    /// <summary>Optional name for the new config. Auto-generated as
    /// <c>Operator-NN</c> if left empty at Add time.</summary>
    [ObservableProperty] private string _newWgturnConfigName = string.Empty;

    /// <summary>True iff <see cref="NewWgturnConfigUrl"/> looks like a
    /// valid wgturn:// URL — drives the Add button's IsEnabled binding
    /// so users get immediate visual feedback before pressing.</summary>
    public bool IsNewWgturnConfigUrlValid =>
        !string.IsNullOrWhiteSpace(NewWgturnConfigUrl)
        && NewWgturnConfigUrl.Trim().StartsWith("wgturn://", StringComparison.OrdinalIgnoreCase);

    partial void OnNewWgturnConfigUrlChanged(string value)
        => OnPropertyChanged(nameof(IsNewWgturnConfigUrlValid));

    /// <summary>
    /// r10 r9+ — hoisted engine field so <see cref="DisconnectWgturnAsync"/>
    /// can actually call Stop on the same instance Connect started. Pre-r9+
    /// the engine was local to ConnectWgturnAsync's using block — Disconnect
    /// could only flip UI flags but the process was already alive in the
    /// background (or just freshly killed by using-block dispose). Now
    /// the field is non-null between Connect and Disconnect; Stop is
    /// called explicitly on Disconnect.
    /// </summary>
    private EmergencyChannelEngine? _wgturnEngine;

    /// <summary>Status text displayed on the Connected-state card. Built
    /// from <see cref="IsWgturnConnecting"/> + <see cref="IsWgturnConnected"/>
    /// + <see cref="SelectedWgturnConfig"/>'s label.</summary>
    public string WgturnStatusText
    {
        get
        {
            if (IsWgturnConnecting) return Strings.EmergencyChannelStatusConnecting;
            if (IsWgturnConnected)
            {
                var label = SelectedWgturnConfig?.Name ?? "—";
                return Strings.EmergencyChannelStatusConnectedTo(label);
            }
            return Strings.EmergencyChannelStatusDisconnected;
        }
    }

    /// <summary>Card title that includes the installed version + variant
    /// when those are populated, otherwise the bare title.</summary>
    public string WgturnTitleText =>
        IsWgturnInstalled && !string.IsNullOrEmpty(WgturnVersion)
            ? Strings.EmergencyChannelCardTitleWithVersion(WgturnVersion, WgturnVariantLabel)
            : Strings.EmergencyChannelCardTitle;

    /// <summary>Pretty-printed PID line for the Connected-state card.</summary>
    public string WgturnPidLineText =>
        WgturnPid is int p ? Strings.EmergencyChannelPidLine(p) : string.Empty;

    /// <summary>Saved wgturn entries (operator profiles). Backed by
    /// <see cref="EmergencyChannelSettings.Configs"/>; mutated via the
    /// AddWgturnConfig / RemoveWgturnConfig commands.</summary>
    public ObservableCollection<WgturnEntry> WgturnConfigs { get; } = new();

    // ── Visual-state gates (XAML IsVisible bindings) ──

    /// <summary>True ⇒ render the install-state card (button + description).</summary>
    public bool IsWgturnCardInstallVisible => !IsWgturnInstalled;

    /// <summary>True ⇒ render the idle-state card (config picker + VK link
    /// input + Connect/Remove/Update).</summary>
    public bool IsWgturnCardIdleVisible => IsWgturnInstalled && !IsWgturnConnected;

    /// <summary>True ⇒ render the connected-state card (status + PID +
    /// Disconnect).</summary>
    public bool IsWgturnCardConnectedVisible => IsWgturnInstalled && IsWgturnConnected;

    // ── Initialization ──

    /// <summary>
    /// Called once from the main ctor (see
    /// <c>MainWindowViewModel.cs</c> ~line 2562 after
    /// <c>WireServersOrphanTracking()</c>). Polls the local install
    /// state, loads saved configs from settings, and pre-populates the
    /// VK link from <see cref="EmergencyChannelSettings.LastVkLink"/>.
    /// </summary>
    private void InitializeWgturnState()
    {
        IsWgturnInstalled = WgturnUpdater.IsInstalled();
        if (IsWgturnInstalled)
        {
            WgturnVersion = WgturnUpdater.GetLocalVersion() ?? string.Empty;
            // r10 r9 merge fix: W-1's GetLocalVariant returns non-nullable
            // WgturnVariant (defaults to Slim on missing/unknown). The W-4
            // stub returned nullable — adjust for real W-1 contract.
            WgturnVariantLabel = WgturnUpdater.GetLocalVariant().ToString().ToLowerInvariant();
        }

        // Load persisted configs.
        WgturnConfigs.Clear();
        foreach (var entry in _settings.EmergencyChannel.Configs)
            WgturnConfigs.Add(entry);

        // Restore selection: prefer the explicit ActiveConfig setting,
        // else fall back to the first entry (so the picker isn't empty
        // when configs exist but no active was persisted).
        var activeName = _settings.EmergencyChannel.ActiveConfig;
        if (!string.IsNullOrEmpty(activeName))
            SelectedWgturnConfig = WgturnConfigs
                .FirstOrDefault(e => string.Equals(e.Name, activeName, StringComparison.Ordinal));
        if (SelectedWgturnConfig == null && WgturnConfigs.Count > 0)
            SelectedWgturnConfig = WgturnConfigs[0];

        // Restore the last VK link the user pasted (each VK call still
        // typically needs a fresh one but pre-filling saves a paste).
        WgturnVkLink = _settings.EmergencyChannel.LastVkLink ?? string.Empty;
    }

    // ── Commands ──

    /// <summary>Download the slim variant from the public W-1 release
    /// pipeline. Surfaces categorized errors via the
    /// <see cref="WgturnDownloadException"/> machinery W-1 throws.</summary>
    [RelayCommand]
    private async Task DownloadWgturnAsync() => await DownloadWgturnVariantAsync(WgturnVariant.Slim);

    /// <summary>Download the embedded variant (full ~120 MB build with
    /// the wgturn-server binary baked in). Same pipeline as
    /// <see cref="DownloadWgturnAsync"/> but a different asset.</summary>
    [RelayCommand]
    private async Task DownloadWgturnEmbeddedAsync()
        => await DownloadWgturnVariantAsync(WgturnVariant.Embedded);

    /// <summary>Re-download latest of the currently installed variant
    /// (default slim if no variant marker is present).</summary>
    [RelayCommand]
    private async Task UpdateWgturnAsync()
    {
        var variant = WgturnUpdater.GetLocalVariant();
        await DownloadWgturnVariantAsync(variant);
    }

    /// <summary>Shared body of the three download paths. Wires status
    /// updates back to <see cref="IsWgturnDownloading"/> + re-polls
    /// <see cref="IsWgturnInstalled"/> at the end.</summary>
    private async Task DownloadWgturnVariantAsync(WgturnVariant variant)
    {
        if (IsWgturnDownloading) return;
        IsWgturnDownloading = true;
        try
        {
            var updater = new WgturnUpdater(_logger);
            updater.StatusChanged += s =>
                Dispatcher.UIThread.Post(() => _logger.Information("[Wgturn] {Status}", s));

            await updater.DownloadLatestAsync(variant);

            // Refresh local state after install completes.
            IsWgturnInstalled = WgturnUpdater.IsInstalled();
            if (IsWgturnInstalled)
            {
                WgturnVersion = WgturnUpdater.GetLocalVersion() ?? string.Empty;
                WgturnVariantLabel = WgturnUpdater.GetLocalVariant().ToString().ToLowerInvariant();
            }
        }
        catch (NotImplementedException nie)
        {
            // W-1 stub branch — surface a clear log so QA understands
            // why the install didn't actually fetch anything.
            _logger.Warning(
                "[Wgturn] DownloadLatestAsync hit the W-4 stub (W-1 chip not yet merged): {Msg}",
                nie.Message);
        }
        catch (WgturnDownloadException wex)
        {
            _logger.Warning(
                "[Wgturn] Download failed: {Category} {Msg}", wex.Category, wex.Message);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[Wgturn] Download failed (uncategorized)");
        }
        finally
        {
            IsWgturnDownloading = false;
        }
    }

    /// <summary>Delete the local wgturn-cli binary. Used when the user
    /// wants to reset the install (e.g. after a corrupt download or to
    /// switch variants).</summary>
    [RelayCommand]
    private void RemoveWgturn()
    {
        try
        {
            if (File.Exists(AppPaths.WgturnCliExePath))
                File.Delete(AppPaths.WgturnCliExePath);

            // Also clear version + variant markers so the UI doesn't
            // claim a version is installed when the binary is gone.
            try { if (File.Exists(WgturnUpdater.VersionFilePath)) File.Delete(WgturnUpdater.VersionFilePath); } catch { }
            try { if (File.Exists(WgturnUpdater.VariantFilePath)) File.Delete(WgturnUpdater.VariantFilePath); } catch { }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[Wgturn] Failed to delete wgturn-cli at {Path}", AppPaths.WgturnCliExePath);
        }
        finally
        {
            IsWgturnInstalled = WgturnUpdater.IsInstalled();
            WgturnVersion = string.Empty;
            WgturnVariantLabel = string.Empty;
        }
    }

    /// <summary>
    /// Start the emergency channel using the selected config + the
    /// pasted VK link. Persists the VK link to settings so reopening
    /// the app pre-fills it. Surfaces failures via the engine's
    /// <see cref="EmergencyChannelEngine.ErrorOccurred"/> event (the
    /// ctor wiring routes that to the log).
    /// </summary>
    [RelayCommand]
    private async Task ConnectWgturnAsync()
    {
        if (SelectedWgturnConfig == null)
        {
            _logger.Information("[Wgturn] Connect skipped — no config selected");
            return;
        }
        if (string.IsNullOrWhiteSpace(WgturnVkLink))
        {
            _logger.Information("[Wgturn] Connect skipped — VK link empty");
            return;
        }

        if (!EmergencyChannelConfig.TryParse(SelectedWgturnConfig.Url, WgturnVkLink, out var cfg))
        {
            _logger.Warning("[Wgturn] Selected URL failed structural parse: {Name}", SelectedWgturnConfig.Name);
            return;
        }
        cfg.Label = SelectedWgturnConfig.Name;

        // Persist VK link + active config so reload pre-fills them.
        _settings.EmergencyChannel.LastVkLink = WgturnVkLink;
        _settings.EmergencyChannel.ActiveConfig = SelectedWgturnConfig.Name;
        SaveSettings();

        IsWgturnConnecting = true;
        try
        {
            // r10 r9+ (Bug-r10-I): hoist engine to field so Disconnect can
            // actually Stop it. Dispose any leftover instance first
            // (e.g. previous failed Connect, app force-close before
            // clean Stop).
            try { _wgturnEngine?.Dispose(); } catch { }
            _wgturnEngine = new EmergencyChannelEngine(_logger);
            _wgturnEngine.StateChanged += OnWgturnEngineStateChanged;
            _wgturnEngine.ErrorOccurred += msg => _logger.Warning("[Wgturn] Engine error: {Msg}", msg);

            await _wgturnEngine.StartAsync(cfg).ConfigureAwait(true);
            WgturnPid = _wgturnEngine.Pid;
            IsWgturnConnected = _wgturnEngine.State == EmergencyChannelState.Connected;
        }
        catch (FileNotFoundException fnf)
        {
            _logger.Warning("[Wgturn] Connect failed — wgturn-cli missing: {Msg}", fnf.Message);
            IsWgturnConnected = false;
            try { _wgturnEngine?.Dispose(); } catch { }
            _wgturnEngine = null;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[Wgturn] Connect failed");
            IsWgturnConnected = false;
            try { _wgturnEngine?.Dispose(); } catch { }
            _wgturnEngine = null;
        }
        finally
        {
            IsWgturnConnecting = false;
        }
    }

    /// <summary>Stop the emergency channel. r10 r9+ (Bug-r10-I) actually
    /// calls Stop on the hoisted <see cref="_wgturnEngine"/> field set
    /// during ConnectWgturnAsync. Pre-r9+ this just flipped UI flags
    /// while the wgturn-cli process kept running (or was already dead
    /// from the using-block in Connect). Idempotent — safe to call
    /// when not connected.</summary>
    [RelayCommand]
    private async Task DisconnectWgturnAsync()
    {
        try
        {
            var engine = _wgturnEngine;
            if (engine != null)
            {
                _logger.Information("[Wgturn] Disconnect: stopping engine PID {Pid}", engine.Pid);
                await Task.Run(() => engine.Stop()).ConfigureAwait(true);
                try { engine.Dispose(); } catch { }
                _wgturnEngine = null;
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[Wgturn] Disconnect: error while stopping");
        }
        finally
        {
            IsWgturnConnected = false;
            IsWgturnConnecting = false;
            WgturnPid = null;
        }
    }

    /// <summary>Open the wgturn-cli log file with the OS default handler.
    /// Pre-W-2 the log lives at <see cref="AppPaths.WgturnCliLogPath"/>.</summary>
    [RelayCommand]
    private void OpenWgturnLog()
    {
        try
        {
            var path = AppPaths.WgturnCliLogPath;
            if (!File.Exists(path))
            {
                _logger.Information("[Wgturn] OpenWgturnLog — log file not yet created: {Path}", path);
                return;
            }
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[Wgturn] OpenWgturnLog failed");
        }
    }

    /// <summary>Open the project's plan document for the user to read
    /// the details of the emergency channel feature.</summary>
    [RelayCommand]
    private void OpenWgturnDetails()
    {
        // For now we just open the GitHub README anchor — future Phase
        // 3 might add an in-app EmergencyChannelPage with full setup
        // walkthrough. The exact URL is centralised so a later move
        // doesn't have to touch the VM.
        const string detailsUrl = "https://github.com/PavelLizunov/VPNRouter#emergency-channel";
        OpenUrl(detailsUrl);
    }

    /// <summary>
    /// r10 r9+ (Bug-r10-I) — real "Add config" flow.
    /// Reads <see cref="NewWgturnConfigUrl"/> + <see cref="NewWgturnConfigName"/>
    /// (the two TextBox inputs in the add-form). Validates the URL
    /// (must start with <c>wgturn://</c> + structurally parseable),
    /// generates a name if the user left it empty, adds a new
    /// <see cref="WgturnEntry"/>, persists settings, clears inputs.
    /// Skips silently if URL is empty/invalid — UI's <see cref="IsNewWgturnConfigUrlValid"/>
    /// gate already disables the button in that case.
    /// </summary>
    [RelayCommand]
    private void AddWgturnConfig()
    {
        var rawUrl = (NewWgturnConfigUrl ?? "").Trim();
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            _logger.Information("[Wgturn] AddWgturnConfig skipped — URL empty");
            return;
        }
        if (!EmergencyChannelConfig.TryParse(rawUrl, out _))
        {
            _logger.Warning("[Wgturn] AddWgturnConfig: URL failed structural parse: {Url}", CanaryPolicy.RedactUrl(rawUrl));
            return;
        }

        var name = (NewWgturnConfigName ?? "").Trim();
        if (string.IsNullOrEmpty(name))
            name = $"Operator-{WgturnConfigs.Count + 1:00}";

        var entry = new WgturnEntry
        {
            Name = name,
            Url = rawUrl,
            AddedAt = DateTimeOffset.UtcNow,
        };
        WgturnConfigs.Add(entry);
        _settings.EmergencyChannel.Configs.Add(entry);
        SelectedWgturnConfig = entry;
        SaveSettings();

        // Clear inputs so subsequent Add ops start fresh.
        NewWgturnConfigUrl = string.Empty;
        NewWgturnConfigName = string.Empty;

        _logger.Information("[Wgturn] Added config '{Name}' ({Count} total)", name, WgturnConfigs.Count);
    }

    /// <summary>r10 r9+ (Bug-r10-I) — remove the currently selected
    /// config from <see cref="WgturnConfigs"/> and persist.
    /// SelectedWgturnConfig auto-falls back to the next entry (or null).</summary>
    [RelayCommand]
    private void RemoveSelectedWgturnConfig()
    {
        var sel = SelectedWgturnConfig;
        if (sel == null) return;

        WgturnConfigs.Remove(sel);
        _settings.EmergencyChannel.Configs.RemoveAll(e =>
            string.Equals(e.Name, sel.Name, StringComparison.Ordinal)
            && string.Equals(e.Url, sel.Url, StringComparison.Ordinal));

        SelectedWgturnConfig = WgturnConfigs.FirstOrDefault();
        if (SelectedWgturnConfig != null)
            _settings.EmergencyChannel.ActiveConfig = SelectedWgturnConfig.Name;
        else
            _settings.EmergencyChannel.ActiveConfig = string.Empty;

        SaveSettings();
        _logger.Information("[Wgturn] Removed config '{Name}' ({Count} remain)", sel.Name, WgturnConfigs.Count);
    }

    private void OnWgturnEngineStateChanged(EmergencyChannelState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            switch (state)
            {
                case EmergencyChannelState.Connecting:
                    IsWgturnConnecting = true;
                    IsWgturnConnected = false;
                    break;
                case EmergencyChannelState.Connected:
                    IsWgturnConnecting = false;
                    IsWgturnConnected = true;
                    break;
                case EmergencyChannelState.Disconnected:
                case EmergencyChannelState.Failed:
                default:
                    IsWgturnConnecting = false;
                    IsWgturnConnected = false;
                    break;
            }
        });
    }
}
