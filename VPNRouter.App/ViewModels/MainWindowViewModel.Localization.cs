using System.Reflection;
using VPNRouter.App.Localization;

namespace VPNRouter.App.ViewModels;

/// <summary>
/// v2.25.12 — PropertyChanged-aware proxies for every Strings.cs key used
/// in XAML. Each <c>L_Foo</c> returns <c>Strings.Foo</c>. When the user
/// toggles language, <see cref="RefreshL10nProxies"/> fires PropertyChanged
/// for every L_* member so bindings re-read the value. This replaces the
/// v2.17.10 workaround of rebuilding the entire MainWindow (which blocked
/// the UI thread for the whole XAML re-parse — ~200-500 ms of visible
/// freeze even after the v2.25.11 Dispatcher-defer fix).
///
/// Generated from <c>/tmp/gen-l10n.sh</c>; regenerate if Strings.cs adds
/// new keys that XAML starts referencing.
/// </summary>
public partial class MainWindowViewModel
{
    public string L_AboutBrandName => Strings.AboutBrandName;
    public string L_AboutCloseBtn => Strings.AboutCloseBtn;
    public string L_AboutCreatorLabel => Strings.AboutCreatorLabel;
    public string L_TrueSplitBadge => Strings.TrueSplitBadge;      // W1.3 status-zone badge
    public string L_TrueSplitTooltip => Strings.TrueSplitTooltip;  // W1.3 caveats tooltip
    public string L_TrueSplitRetry => Strings.TrueSplitRetry;
    public string L_AboutRepoLabel => Strings.AboutRepoLabel;
    public string L_AboutSingBoxLabel => Strings.AboutSingBoxLabel;
    public string L_AboutTagline => Strings.AboutTagline;
    public string L_AboutTitle => Strings.AboutTitle;
    public string L_AboutVersionLabel => Strings.AboutVersionLabel;
    public string L_AddAppHint => Strings.AddAppHint;
    public string L_AddCategory => Strings.AddCategory;
    public string L_AddCustomAppBtn => Strings.AddCustomAppBtn;
    public string L_ImportSteamGames => IsRussian ? "Импорт Steam" : "Import Steam";
    public string L_AddSubscription => Strings.AddSubscription;
    public string L_ApplyChanges => Strings.ApplyChanges;
    public string L_ApplyNowHint => Strings.ApplyNowHint;
    public string L_ApplyNowReloadVpn => Strings.ApplyNowReloadVpn;
    public string L_AppsGroupEmpty => Strings.AppsGroupEmpty;
    // v2.29.0 — Apps page full-tunnel banner (replaces silent IsEnabled
    // disable, see ApplicationsPage.axaml).
    public string L_AppsFullTunnelBanner => Strings.AppsFullTunnelBanner;
    public string L_AppsFullTunnelBannerAction => Strings.AppsFullTunnelBannerAction;
    // v2.32 — Apps Include/Exclude 2-mode segmented toggle.
    public string L_AppsModeSectionTitle => Strings.AppsModeSectionTitle;
    public string L_AppsModeInclude => Strings.AppsModeInclude;
    public string L_AppsModeExclude => Strings.AppsModeExclude;
    public string L_AppsPendingApplyHint => Strings.AppsPendingApplyHint;
    public string L_AppsModeIncludeHint => Strings.AppsModeIncludeHint;
    public string L_AppsModeExcludeHint => Strings.AppsModeExcludeHint;
    public string L_AppsListSectionTitle => Strings.AppsListSectionTitle;
    public string L_AppsListInclude => Strings.AppsListInclude;
    public string L_AppsListExclude => Strings.AppsListExclude;
    /// <summary>Selects which hint to show beneath the segmented toggle
    /// based on current <see cref="RoutingAppsMode"/>.</summary>
    public string L_CurrentAppsModeHint =>
        IsRoutingAppsModeExclude ? L_AppsModeExcludeHint : L_AppsModeIncludeHint;
    // v2.32 — ServersPage orphan-entry marker (F-C).
    public string L_ServersOrphanBadge => Strings.ServersOrphanBadge;
    public string L_ServersOrphanTooltip => Strings.ServersOrphanTooltip;
    // v2.32 — Auto-failover (F-E) UI status surfacing.
    // v2.30.0 — Custom rules engine (direct/proxy/block).
    public string L_CustomRulesPlaceholder => Strings.CustomRulesPlaceholder;
    public string L_CustomRulesConflictHeader => Strings.CustomRulesConflictHeader;
    // v2.30.0-r2 — Network → Rules section strings.
    public string L_CustomRulesEmpty => Strings.CustomRulesEmpty;
    public string L_CustomRulesAddTitle => Strings.CustomRulesAddTitle;
    public string L_CustomRulesAddBtn => Strings.CustomRulesAddBtn;
    public string L_CustomRulesCommentLabel => Strings.CustomRulesCommentLabel;
    public string L_CustomRulesDelete => Strings.CustomRulesDelete;
    public string L_CustomRulesImport => Strings.CustomRulesImport;
    public string L_CustomRulesExport => Strings.CustomRulesExport;
    public string L_CustomRulesImportTooltip => Strings.CustomRulesImportTooltip;
    public string L_CustomRulesExportTooltip => Strings.CustomRulesExportTooltip;
    public string L_CustomRulesSearchPlaceholder => Strings.CustomRulesSearchPlaceholder;
    public string L_CustomRulesClearAll => Strings.CustomRulesClearAll;
    public string L_CustomRulesEnableAll => Strings.CustomRulesEnableAll;
    public string L_CustomRulesDisableAll => Strings.CustomRulesDisableAll;
    public string L_CustomRulesExistingHeader => Strings.CustomRulesExistingHeader;
    // v2.30.0-r7 — Cards/Edit view-mode toggle (RulesExplorations.html design).
    public string L_RulesViewCards => Strings.RulesViewCards;
    public string L_RulesViewRead => Strings.RulesViewRead;
    public string L_RulesViewEdit => Strings.RulesViewEdit;
    public string L_RulesViewCardsTooltip => Strings.RulesViewCardsTooltip;
    public string L_RulesViewReadTooltip => Strings.RulesViewReadTooltip;
    public string L_RulesViewEditTooltip => Strings.RulesViewEditTooltip;
    public string L_RulesEditorRevert => Strings.RulesEditorRevert;
    public string L_RulesEditorDirty => Strings.RulesEditorDirty;
    public string L_RulesEditorFormatHint => Strings.RulesEditorFormatHint;
    public string L_RulesFilterAll => Strings.RulesFilterAll;
    public string L_RulesBulkActions => Strings.RulesBulkActions;
    // v2.30.0-r14 — bulk-actions popover localizations.
    public string L_RulesSortByType => Strings.RulesSortByType;
    // v2.30.0-r17 — Custom-rules-priority CheckBox.
    public string L_RulesCustomAboveToggles => Strings.RulesCustomAboveToggles;
    public string L_RulesCustomAboveTogglesHint => Strings.RulesCustomAboveTogglesHint;
    // v2.30.0-r18 — Clear All inline confirm bar.
    public string L_RulesClearAllHint => Strings.RulesClearAllHint;
    public string L_RulesClearAllConfirm => Strings.RulesClearAllConfirm;
    public string L_Cancel => Strings.CommonCancel;
    public string L_RulesAddLabelAction  => Strings.RulesAddLabelAction;
    public string L_RulesAddLabelType    => Strings.RulesAddLabelType;
    public string L_RulesAddLabelValue   => Strings.RulesAddLabelValue;
    public string L_RulesAddLabelComment => Strings.RulesAddLabelComment;
    public string L_RulesAddLabelOpt     => Strings.RulesAddLabelOpt;
    // v2.30.0-r12 — structured help-banner Runs (per-piece localization).
    public string L_RulesHelpHeader => Strings.RulesHelpHeader;
    public string L_RulesHelpB1Pre  => Strings.RulesHelpB1Pre;
    public string L_RulesHelpB1T1   => Strings.RulesHelpB1T1;
    public string L_RulesHelpB1Mid  => Strings.RulesHelpB1Mid;
    public string L_RulesHelpB1T2   => Strings.RulesHelpB1T2;
    public string L_RulesHelpB1Suf  => Strings.RulesHelpB1Suf;
    public string L_RulesHelpB2Pre  => Strings.RulesHelpB2Pre;
    public string L_RulesHelpB2Mid  => Strings.RulesHelpB2Mid;
    public string L_RulesHelpB2Suf  => Strings.RulesHelpB2Suf;
    public string L_RulesHelpB3Pre  => Strings.RulesHelpB3Pre;
    public string L_RulesHelpB3Bold => Strings.RulesHelpB3Bold;
    public string L_RulesHelpB3Suf  => Strings.RulesHelpB3Suf;
    public string LblSettingsRules => Strings.SectionRules;
    // Legacy v2.29.0 aliases (kept for cached XAML).
    public string L_AutoUpdateCheckLabel => Strings.AutoUpdateCheckLabel;
    // v2.27 Bug C — two-section layout headers + hints
    public string L_AutostartBootSectionTitle => Strings.AutostartBootSectionTitle;
    public string L_AutostartBootSectionSub => Strings.AutostartBootSectionSub;
    public string L_AutostartComponentsInfoHint => Strings.AutostartComponentsInfoHint;
    public string L_BtnInstallServiceInlineCta => Strings.BtnInstallServiceInlineCta;
    public string L_TipInstallServiceInlineCta => Strings.TipInstallServiceInlineCta;
    public string L_TipSubscriptionMetadata => Strings.TipSubscriptionMetadata;
    public string L_AutostartLoginSectionTitle => Strings.AutostartLoginSectionTitle;
    public string L_AutostartLoginAppDescription => Strings.AutostartLoginAppDescription;
    // v2.27 §4.5 — prominent PID line replacing the small pill
    public string L_ServiceStoppedLine => Strings.ServiceStoppedLine;
    public string L_ServiceRunningLine =>
        ServiceVm.ServicePid.HasValue ? Strings.ServiceRunningLine(ServiceVm.ServicePid.Value) : Strings.ServiceRunningText;
    // v2.27.0-r2 — Simple autostart link-card replacing the old checkbox
    public string L_SmpAutostartCardTitle => Strings.SmpAutostartCardTitle;
    public string L_SmpAutostartCardStatus =>
        ServiceVm.IsInstalled && ServiceVm.IsRunning
            ? Strings.SmpAutostartCardOn
            : Strings.SmpAutostartCardOff;
    public string L_CategoryNamePrompt => Strings.CategoryNamePrompt;
    public string L_ClearDiscordCache => Strings.ClearDiscordCache;
    public string L_ColIp => Strings.ColIp;
    public string L_ColPing => Strings.ColPing;
    public string L_ColPingTooltip => Strings.ColPingTooltip;
    public string L_ColPort => Strings.ColPort;
    public string L_ColServer => Strings.ColServer;
    public string L_EnableWholeGroup => Strings.EnableWholeGroup;
    public string L_SelectAllApps => Strings.AppsSelectAll;
    public string L_ClearAllApps => Strings.AppsClearAll;
    public string L_FcAdvancedSettings => Strings.FcAdvancedSettings;
    public string L_FcApplySelected => Strings.FcApplySelected;
    public string L_FcColBandwidth => Strings.FcColBandwidth;
    public string L_FcSpeedColumnTooltip => Strings.FcSpeedColumnTooltip;
    public string L_FcColCountry => Strings.FcColCountry;
    public string L_FcColEndpoint => Strings.FcColEndpoint;
    public string L_FcColLatency => Strings.FcColLatency;
    public string L_FcColTransport => Strings.FcColTransport;
    public string L_FcConfigsWord => Strings.FcConfigsWord;
    public string L_FcConnectHint => Strings.FcConnectHint;
    public string L_FcDeepExcludeRu => Strings.FcDeepExcludeRu;
    public string L_FcDeepHint => Strings.FcDeepHint;
    public string L_FcDeepStop => Strings.FcDeepStop;
    public string L_FcDeepStopTooltip => Strings.FcDeepStopTooltip;
    public string L_FcDeepVerify => Strings.FcDeepVerify;
    public string L_FcDeepVerifyTooltip => Strings.FcDeepVerifyTooltip;
    public string L_FcFilteredEmpty => Strings.FcFilteredEmpty;
    public string L_FcListHeader => Strings.FcListHeader;
    public string L_FcListShown => Strings.FcListShown;
    public string L_FcMsUnit => Strings.FcMsUnit;

    // ── v2.28.6 Phase 2 — Saved-tab strings ──
    public string L_FcTabSearch              => Strings.FcTabSearch;
    public string L_FcSavedTabHint           => Strings.FcSavedTabHint;
    public string L_FcSavedClearAllBtn       => Strings.FcSavedClearAllBtn;
    public string L_FcSavedColStatus         => Strings.FcSavedColStatus;
    public string L_FcSavedEmpty             => Strings.FcSavedEmpty;
    public string L_FcSavedRecheckOneTooltip => Strings.FcSavedRecheckOneTooltip;
    public string L_FcSavedRemoveOneTooltip  => Strings.FcSavedRemoveOneTooltip;
    public string L_FcSearchListEmptyHint    => Strings.FcSearchListEmptyHint;
    public string L_FcTargetNLabel => Strings.FcTargetNLabel;
    public string L_FcWithPingUnder => Strings.FcWithPingUnder;
    public string L_FullTunnelSubtitle => Strings.FullTunnelSubtitle;
    public string L_FullTunnelTitle => Strings.FullTunnelTitle;
    public string L_GameFilter => Strings.GameFilter;
    public string L_GameFilterAll => Strings.GameFilterAll;
    public string L_GameFilterOff => Strings.GameFilterOff;
    public string L_GameFilterTcp => Strings.GameFilterTcp;
    public string L_GameFilterUdp => Strings.GameFilterUdp;
    public string L_IpSetAny => Strings.IpSetAny;
    public string L_IpSetFilter => Strings.IpSetFilter;
    public string L_IpSetLoaded => Strings.IpSetLoaded;
    public string L_IpSetNone => Strings.IpSetNone;
    public string L_LblAddSubscriptionHint => Strings.LblAddSubscriptionHint;
    public string L_LblCustomBadge => Strings.LblCustomBadge;
    public string L_LblName => Strings.LblName;
    public string L_LblNoServers => Strings.LblNoServers;
    public string L_LblPort => Strings.LblPort;
    public string L_LblPublicKey => Strings.LblPublicKey;
    // v2.31.6-r9 — removed L_LblRoutingMode (no XAML reference).
    // The corresponding LblRoutingMode getter in MainWindowViewModel.cs
    // is still in use by NetworkPage.axaml routing-mode label binding,
    // so kept; only the L_-prefixed proxy was orphaned.
    public string L_LblServer => Strings.LblServer;
    public string L_LblShortId => Strings.LblShortId;
    public string L_LblUuid => Strings.LblUuid;
    public string L_OpenFolder => Strings.OpenFolder;
    public string L_OpenGitHub => Strings.OpenGitHub;
    public string L_OpenServiceMenu => Strings.OpenServiceMenu;
    public string L_TipOpenServiceMenu => Strings.TipOpenServiceMenu;
    public string L_RefreshAll => Strings.RefreshAll;
    public string L_ReinstallService => Strings.ReinstallService;
    public string L_RemoveServiceLabel => Strings.RemoveServiceLabel;
    public string L_RestartService => Strings.RestartService;
    public string L_RoutingDescription => Strings.RoutingDescription;
    public string L_RunDiagnostics => Strings.RunDiagnostics;
    public string L_RunTestsLabel => Strings.RunTestsLabel;
    public string L_SelectCategoryHint => Strings.SelectCategoryHint;
    public string L_ServiceComponentsHeader => Strings.ServiceComponentsHeader;
    public string L_ServiceMasterSubtitle => Strings.ServiceMasterSubtitle;
    public string L_ServiceMasterTitle => Strings.ServiceMasterTitle;
    public string L_SettingsAutosaved => Strings.SettingsAutosaved;
    public string L_SmpAdvCardSubtitle => Strings.SmpAdvCardSubtitle;
    public string L_SmpAdvCardTitle => Strings.SmpAdvCardTitle;
    public string L_SmpConfigRowLabel => Strings.SmpConfigRowLabel;
    public string L_SmpFullHint => Strings.SmpFullHint;
    public string L_SmpFullOption => Strings.SmpFullOption;
    public string L_SmpInputHint => Strings.SmpInputHint;
    public string L_SmpInputLabel => Strings.SmpInputLabel;
    public string L_SmpInputWatermark => Strings.SmpInputWatermark;
    public string L_SmpMenuAbout => Strings.SmpMenuAbout;
    public string L_SmpMenuCheckLeaks => Strings.SmpMenuCheckLeaks;
    public string L_SmpMenuCheckUpdates => Strings.SmpMenuCheckUpdates;
    public string L_CurrentVersion => Strings.CurrentVersion;
    public string L_CustomConfigsEmptyTitle => Strings.CustomConfigsEmptyTitle;
    public string L_CustomConfigsEmptyHint => Strings.CustomConfigsEmptyHint;
    public string L_SmpMenuDiagnosticsSection => Strings.SmpMenuDiagnosticsSection;
    public string L_SmpMenuHealthCheck => Strings.SmpMenuHealthCheck;
    public string L_SmpMenuSetupWizard => Strings.SmpMenuSetupWizard;
    public string L_SmpMenuOpenLogs => Strings.SmpMenuOpenLogs;
    public string L_DiagSupportHeader => Strings.DiagSupportHeader;
    public string L_DiagExportButton => Strings.DiagExportButton;
    public string L_DiagExporting => Strings.DiagExporting;
    public string L_DiagExportHint => Strings.DiagExportHint;
    public string L_SmpMenuSafeMode => Strings.SmpMenuSafeMode;
    public string L_SmpMenuTroubleshootingSection => Strings.SmpMenuTroubleshootingSection;
    public string L_SmpMenuViewSection => Strings.SmpMenuViewSection;
    public string L_SmpSegDark => Strings.SmpSegDark;
    public string L_SmpSegEn => Strings.SmpSegEn;
    public string L_SmpSegLight => Strings.SmpSegLight;
    public string L_SmpSegSystem => Strings.SmpSegSystem;
    public string L_SmpSegRu => Strings.SmpSegRu;
    public string L_SmpSplitHint => Strings.SmpSplitHint;
    public string L_SmpSplitOption => Strings.SmpSplitOption;
    public string L_SmpTipAutostart => Strings.SmpTipAutostart;
    public string L_SmpTunnelModeLabel => Strings.SmpTunnelModeLabel;
    public string L_SplitTunnelSubtitle => Strings.SplitTunnelSubtitle;
    public string L_SplitTunnelTitle => Strings.SplitTunnelTitle;
    public string L_SubscriptionNameHint => Strings.SubscriptionNameHint;
    public string L_SubscriptionsSection => Strings.SubscriptionsSection;
    public string L_TgProxyCopy => Strings.TgProxyCopy;
    public string L_TgProxyCopySecretA11y => Strings.TgProxyCopySecretA11y;
    public string L_TgProxyRegenerateSecretA11y => Strings.TgProxyRegenerateSecretA11y;
    public string L_TgProxyPort => Strings.TgProxyPort;
    public string L_TgProxyRegenerate => Strings.TgProxyRegenerate;
    public string L_TgProxySecret => Strings.TgProxySecret;
    public string L_TgProxySetupOnce => Strings.TgProxySetupOnce;
    public string L_TipCloseServerDetail => Strings.TipCloseServerDetail;
    public string L_TipDismiss => Strings.TipDismiss;
    public string L_TipDeleteServer => Strings.TipDeleteServer;
    public string L_TipDeepVerifyServers => Strings.TipDeepVerifyServers;
    public string L_TipFcSkipRu => Strings.TipFcSkipRu;
    public string L_TipIpLeak => Strings.TipIpLeak;
    public string L_TipLeakFlushDns => Strings.TipLeakFlushDns;
    public string L_TipLeakForceIpv4 => Strings.TipLeakForceIpv4;
    public string L_TipLeakStrictDns => Strings.TipLeakStrictDns;
    public string L_TipLeakStrictMode => Strings.TipLeakStrictMode;
    public string L_TipDnsLeakLockdown => Strings.TipDnsLeakLockdown;
    public string L_DnsLeakLockdownLabel => Strings.DnsLeakLockdownLabel;
    public string L_DnsLeakLockdownUnavailableNote => Strings.DnsLeakLockdownUnavailableNote;
    public string L_TipOpenLogs => Strings.TipOpenLogs;
    public string L_TipRefreshSubscription => Strings.TipRefreshSubscription;
    public string L_TipRemoveApp => Strings.TipRemoveApp;
    public string L_TipRemoveCategory => Strings.TipRemoveCategory;
    public string L_TipRemoveSubscription => Strings.TipRemoveSubscription;
    public string L_TipSmpMenuAbout => Strings.TipSmpMenuAbout;
    public string L_TipSmpMenuHealthCheck => Strings.TipSmpMenuHealthCheck;
    public string L_TipSmpMenuSetupWizard => Strings.TipSmpMenuSetupWizard;
    public string L_TipSmpMenuResetConfig => Strings.TipSmpMenuResetConfig;
    public string L_TipSmpMenuSafeMode => Strings.TipSmpMenuSafeMode;
    public string L_TipTestAllServers => Strings.TipTestAllServers;
    public string L_TipTestTcpTls => Strings.TipTestTcpTls;
    public string L_TipZapretAutoUpdate => Strings.TipZapretAutoUpdate;
    // R09 — update-banner button (was hardcoded "↓ Update" in MainWindow.axaml;
    // now runtime-refreshable so it follows the Ru/En toggle).
    public string L_UpdateButton => Strings.UpdateButton;
    public string L_UpdateIpSet => Strings.UpdateIpSet;
    public string L_WmTgProxyPort => Strings.WmTgProxyPort;
    public string L_WmTgProxySecret => Strings.WmTgProxySecret;
    public string L_WmVlessUri => Strings.WmVlessUri;
    public string L_WmZapretCustomArgs => Strings.WmZapretCustomArgs;
    public string L_ZapretHostsHint => Strings.ZapretHostsHint;
    public string L_ZapretSecAdvanced => Strings.ZapretSecAdvanced;
    public string L_ZapretSecAdvancedDesc => Strings.ZapretSecAdvancedDesc;
    public string L_ZapretSecFilters => Strings.ZapretSecFilters;
    public string L_ZapretSecFiltersDesc => Strings.ZapretSecFiltersDesc;
    public string L_ZapretSecHosts => Strings.ZapretSecHosts;
    public string L_ZapretSecHostsDesc => Strings.ZapretSecHostsDesc;
    public string L_ZapretSecStatus => Strings.ZapretSecStatus;
    public string L_ZapretSecStrategy => Strings.ZapretSecStrategy;
    public string L_ZapretSecStrategyDesc => Strings.ZapretSecStrategyDesc;

    // ── Bug-r9-E + Bug-r9-G (2026-05-11) — pre-flight UX bindings ──
    public string L_ConflictOtherVpnDetectedTitle => Strings.ConflictOtherVpnDetectedTitle;
    public string L_ConflictRefreshButton => Strings.ConflictRefreshButton;
    // v2.32.1-r4 (Bug-r10-A) — Kill conflicting VPN button.
    public string L_ConflictKillButton => Strings.ConflictKillButton;
    public string L_ConflictKillTooltip => Strings.ConflictKillTooltip;
    // v2.32.1-r5 (Bug-r10-B) — Ignore conflict button.
    public string L_ConflictIgnoreButton => Strings.ConflictIgnoreButton;
    public string L_ConflictIgnoreTooltip => Strings.ConflictIgnoreTooltip;
    public string L_ZapretAvBlockCopyPath => Strings.ZapretAvBlockCopyPath;

    // ── v2.32.2 (W-4) — Emergency Channel (wgturn) card proxies ──
    // Static strings only — the dynamic ones (status with name, version
    // template, PID line) read directly from Strings.* in the VM so each
    // composition picks up live values without a manual refresh.
    public string L_EmergencyChannelDescription => Strings.EmergencyChannelDescription;
    public string L_EmergencyChannelInstall => Strings.EmergencyChannelInstall;
    public string L_EmergencyChannelInstallEmbedded => Strings.EmergencyChannelInstallEmbedded;
    public string L_EmergencyChannelConfigsLabel => Strings.EmergencyChannelConfigsLabel;
    public string L_EmergencyChannelAddConfig => Strings.EmergencyChannelAddConfig;
    // r10 r9+ (Bug-r10-I): add-config form L_-getters
    public string L_EmergencyChannelAddConfigNameWatermark => Strings.EmergencyChannelAddConfigNameWatermark;
    public string L_EmergencyChannelAddConfigUrlWatermark => Strings.EmergencyChannelAddConfigUrlWatermark;
    public string L_EmergencyChannelAddConfigBtn => Strings.EmergencyChannelAddConfigBtn;
    public string L_EmergencyChannelVkLinkLabel => Strings.EmergencyChannelVkLinkLabel;
    public string L_EmergencyChannelVkLinkHint => Strings.EmergencyChannelVkLinkHint;
    public string L_EmergencyChannelVkLinkWatermark => Strings.EmergencyChannelVkLinkWatermark;
    public string L_EmergencyChannelConnect => Strings.EmergencyChannelConnect;
    public string L_EmergencyChannelDisconnect => Strings.EmergencyChannelDisconnect;
    public string L_EmergencyChannelRemove => Strings.EmergencyChannelRemove;
    public string L_EmergencyChannelUpdate => Strings.EmergencyChannelUpdate;
    public string L_EmergencyChannelOpenLog => Strings.EmergencyChannelOpenLog;
    public string L_EmergencyChannelDetails => Strings.EmergencyChannelDetails;
    public string L_EmergencyChannelStatusNotInstalled => Strings.EmergencyChannelStatusNotInstalled;
    public string L_EmergencyChannelStatusLabel => Strings.EmergencyChannelStatusLabel;
    public string L_LblToolEmergencyChannel => Strings.EmergencyChannelCardTitle;

    /// <summary>
    /// Broadcasts PropertyChanged for every L_* proxy via reflection.
    /// Runs in ~1ms for 200 properties (faster than allocating a single
    /// MainWindow clone). Also fires for existing Lbl* properties so the
    /// same path refreshes hand-written localized properties.
    /// </summary>
    private void RefreshL10nProxies()
    {
        var t = typeof(MainWindowViewModel);
        foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.Name.StartsWith("L_") || prop.Name.StartsWith("Lbl"))
                OnPropertyChanged(prop.Name);
        }
    }
}
