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
    public string L_AboutRepoLabel => Strings.AboutRepoLabel;
    public string L_AboutSingBoxLabel => Strings.AboutSingBoxLabel;
    public string L_AboutTagline => Strings.AboutTagline;
    public string L_AboutTitle => Strings.AboutTitle;
    public string L_AboutVersionLabel => Strings.AboutVersionLabel;
    public string L_AddAppHint => Strings.AddAppHint;
    public string L_AddCategory => Strings.AddCategory;
    public string L_AddSubscription => Strings.AddSubscription;
    public string L_ApplyChanges => Strings.ApplyChanges;
    public string L_ApplyNowHint => Strings.ApplyNowHint;
    public string L_ApplyNowReloadVpn => Strings.ApplyNowReloadVpn;
    public string L_AppsGroupEmpty => Strings.AppsGroupEmpty;
    public string L_AutoUpdateCheckLabel => Strings.AutoUpdateCheckLabel;
    public string L_AutostartPlatformNotice => Strings.AutostartPlatformNotice;
    public string L_CategoryNamePrompt => Strings.CategoryNamePrompt;
    public string L_ClearDiscordCache => Strings.ClearDiscordCache;
    public string L_ColIp => Strings.ColIp;
    public string L_ColPing => Strings.ColPing;
    public string L_ColPort => Strings.ColPort;
    public string L_ColServer => Strings.ColServer;
    public string L_EnableWholeGroup => Strings.EnableWholeGroup;
    public string L_FcApplySelected => Strings.FcApplySelected;
    public string L_FcBandwidthHint => Strings.FcBandwidthHint;
    public string L_FcCancel => Strings.FcCancel;
    public string L_FcClearAll => Strings.FcClearAll;
    public string L_FcClearFailed => Strings.FcClearFailed;
    public string L_FcColCountry => Strings.FcColCountry;
    public string L_FcColEndpoint => Strings.FcColEndpoint;
    public string L_FcColLatency => Strings.FcColLatency;
    public string L_FcColSni => Strings.FcColSni;
    public string L_FcColTransport => Strings.FcColTransport;
    public string L_FcConfigsWord => Strings.FcConfigsWord;
    public string L_FcConnectHint => Strings.FcConnectHint;
    public string L_FcCountryFilter => Strings.FcCountryFilter;
    public string L_FcCustomBw => Strings.FcCustomBw;
    public string L_FcCustomPing => Strings.FcCustomPing;
    public string L_FcDashboardFake => Strings.FcDashboardFake;
    public string L_FcDashboardTlsFail => Strings.FcDashboardTlsFail;
    public string L_FcDashboardTotal => Strings.FcDashboardTotal;
    public string L_FcDashboardUnreach => Strings.FcDashboardUnreach;
    public string L_FcDashboardVerified => Strings.FcDashboardVerified;
    public string L_FcDashboardWorking => Strings.FcDashboardWorking;
    public string L_FcDeepExcludeRu => Strings.FcDeepExcludeRu;
    public string L_FcDeepHint => Strings.FcDeepHint;
    public string L_FcDeepStop => Strings.FcDeepStop;
    public string L_FcDeepStopTooltip => Strings.FcDeepStopTooltip;
    public string L_FcDeepTargetLabel => Strings.FcDeepTargetLabel;
    public string L_FcDeepVerify => Strings.FcDeepVerify;
    public string L_FcDeepVerifyTooltip => Strings.FcDeepVerifyTooltip;
    public string L_FcEmptyCtaButton => Strings.FcEmptyCtaButton;
    public string L_FcEmptyCtaSubtitle => Strings.FcEmptyCtaSubtitle;
    public string L_FcEmptyCtaTitle => Strings.FcEmptyCtaTitle;
    public string L_FcFastScanHint => Strings.FcFastScanHint;
    public string L_FcFastScanLabel => Strings.FcFastScanLabel;
    public string L_FcFilteredEmpty => Strings.FcFilteredEmpty;
    public string L_FcKeepVerified => Strings.FcKeepVerified;
    public string L_FcListHeader => Strings.FcListHeader;
    public string L_FcListShown => Strings.FcListShown;
    public string L_FcMbpsUnit => Strings.FcMbpsUnit;
    public string L_FcMsUnit => Strings.FcMsUnit;
    public string L_FcOnlyWorking => Strings.FcOnlyWorking;
    public string L_FcOpenLogs => Strings.FcOpenLogs;
    public string L_FcPageDescription => Strings.FcPageDescription;
    public string L_FcPresetBest => Strings.FcPresetBest;
    public string L_FcPresetChat => Strings.FcPresetChat;
    public string L_FcPresetCustom => Strings.FcPresetCustom;
    public string L_FcPresetGaming => Strings.FcPresetGaming;
    public string L_FcPresetLabel => Strings.FcPresetLabel;
    public string L_FcPresetStream => Strings.FcPresetStream;
    public string L_FcQuickstartDismiss => Strings.FcQuickstartDismiss;
    public string L_FcQuickstartStep1 => Strings.FcQuickstartStep1;
    public string L_FcQuickstartStep2 => Strings.FcQuickstartStep2;
    public string L_FcQuickstartStep3 => Strings.FcQuickstartStep3;
    public string L_FcQuickstartTitle => Strings.FcQuickstartTitle;
    public string L_FcRefreshSources => Strings.FcRefreshSources;
    public string L_FcRefreshTooltip => Strings.FcRefreshTooltip;
    public string L_FcRetestAll => Strings.FcRetestAll;
    public string L_FcRetestTooltip => Strings.FcRetestTooltip;
    public string L_FcSecCleanup => Strings.FcSecCleanup;
    public string L_FcSecDeep => Strings.FcSecDeep;
    public string L_FcSecFilters => Strings.FcSecFilters;
    public string L_FcSecMySources => Strings.FcSecMySources;
    public string L_FcSecOverview => Strings.FcSecOverview;
    public string L_FcSecScan => Strings.FcSecScan;
    public string L_FcSmartRefreshHint => Strings.FcSmartRefreshHint;
    public string L_FcSmartRefreshLabel => Strings.FcSmartRefreshLabel;
    public string L_FcTargetNLabel => Strings.FcTargetNLabel;
    public string L_FcUserSrcAdd => Strings.FcUserSrcAdd;
    public string L_FcUserSrcEmpty => Strings.FcUserSrcEmpty;
    public string L_FcUserSrcHint => Strings.FcUserSrcHint;
    public string L_FcUserSrcNamePlaceholder => Strings.FcUserSrcNamePlaceholder;
    public string L_FcUserSrcUrlPlaceholder => Strings.FcUserSrcUrlPlaceholder;
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
    public string L_LblServer => Strings.LblServer;
    public string L_LblShortId => Strings.LblShortId;
    public string L_LblUuid => Strings.LblUuid;
    public string L_OpenFolder => Strings.OpenFolder;
    public string L_OpenGitHub => Strings.OpenGitHub;
    public string L_OpenServiceMenu => Strings.OpenServiceMenu;
    public string L_RefreshAll => Strings.RefreshAll;
    public string L_ReinstallService => Strings.ReinstallService;
    public string L_Remove => Strings.Remove;
    public string L_RemoveServiceLabel => Strings.RemoveServiceLabel;
    public string L_RestartService => Strings.RestartService;
    public string L_RoutingDescription => Strings.RoutingDescription;
    public string L_RunDiagnostics => Strings.RunDiagnostics;
    public string L_RunTestsLabel => Strings.RunTestsLabel;
    public string L_SelectCategoryHint => Strings.SelectCategoryHint;
    public string L_ServiceRunningText => Strings.ServiceRunningText;
    public string L_ServiceStatusLabel => Strings.ServiceStatusLabel;
    public string L_ServiceStoppedText => Strings.ServiceStoppedText;
    public string L_SettingsAutosaved => Strings.SettingsAutosaved;
    public string L_SmpAdvCardSubtitle => Strings.SmpAdvCardSubtitle;
    public string L_SmpAdvCardTitle => Strings.SmpAdvCardTitle;
    public string L_SmpAutostartLabel => Strings.SmpAutostartLabel;
    public string L_SmpConfigRowLabel => Strings.SmpConfigRowLabel;
    public string L_SmpFullHint => Strings.SmpFullHint;
    public string L_SmpFullOption => Strings.SmpFullOption;
    public string L_SmpInputHint => Strings.SmpInputHint;
    public string L_SmpInputLabel => Strings.SmpInputLabel;
    public string L_SmpInputWatermark => Strings.SmpInputWatermark;
    public string L_SmpMenuAbout => Strings.SmpMenuAbout;
    public string L_SmpMenuCheckLeaks => Strings.SmpMenuCheckLeaks;
    public string L_SmpMenuCheckUpdates => Strings.SmpMenuCheckUpdates;
    public string L_SmpMenuDiagnosticsSection => Strings.SmpMenuDiagnosticsSection;
    public string L_SmpMenuHealthCheck => Strings.SmpMenuHealthCheck;
    public string L_SmpMenuOpenLogs => Strings.SmpMenuOpenLogs;
    public string L_SmpMenuSafeMode => Strings.SmpMenuSafeMode;
    public string L_SmpMenuTroubleshootingSection => Strings.SmpMenuTroubleshootingSection;
    public string L_SmpMenuViewSection => Strings.SmpMenuViewSection;
    public string L_SmpSegDark => Strings.SmpSegDark;
    public string L_SmpSegEn => Strings.SmpSegEn;
    public string L_SmpSegLight => Strings.SmpSegLight;
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
    public string L_TgProxyOpenInTelegram => Strings.TgProxyOpenInTelegram;
    public string L_TgProxyPort => Strings.TgProxyPort;
    public string L_TgProxyRegenerate => Strings.TgProxyRegenerate;
    public string L_TgProxySecret => Strings.TgProxySecret;
    public string L_TgProxySetupOnce => Strings.TgProxySetupOnce;
    public string L_TipClearAllCache => Strings.TipClearAllCache;
    public string L_TipClearFailed => Strings.TipClearFailed;
    public string L_TipCloseServerDetail => Strings.TipCloseServerDetail;
    public string L_TipDeepVerifyServers => Strings.TipDeepVerifyServers;
    public string L_TipFcFastScan => Strings.TipFcFastScan;
    public string L_TipFcSkipRu => Strings.TipFcSkipRu;
    public string L_TipFcSmartRefresh => Strings.TipFcSmartRefresh;
    public string L_TipIpLeak => Strings.TipIpLeak;
    public string L_TipKeepVerifiedOnly => Strings.TipKeepVerifiedOnly;
    public string L_TipLeakFlushDns => Strings.TipLeakFlushDns;
    public string L_TipLeakForceIpv4 => Strings.TipLeakForceIpv4;
    public string L_TipLeakStrictDns => Strings.TipLeakStrictDns;
    public string L_TipLeakStrictMode => Strings.TipLeakStrictMode;
    public string L_TipOpenFreeConfigLogs => Strings.TipOpenFreeConfigLogs;
    public string L_TipOpenLogs => Strings.TipOpenLogs;
    public string L_TipRefreshSubscription => Strings.TipRefreshSubscription;
    public string L_TipRemoveApp => Strings.TipRemoveApp;
    public string L_TipRemoveCategory => Strings.TipRemoveCategory;
    public string L_TipRemoveSubscription => Strings.TipRemoveSubscription;
    public string L_TipSmpMenuAbout => Strings.TipSmpMenuAbout;
    public string L_TipSmpMenuHealthCheck => Strings.TipSmpMenuHealthCheck;
    public string L_TipSmpMenuResetConfig => Strings.TipSmpMenuResetConfig;
    public string L_TipSmpMenuSafeMode => Strings.TipSmpMenuSafeMode;
    public string L_TipTestAllServers => Strings.TipTestAllServers;
    public string L_TipTestTcpTls => Strings.TipTestTcpTls;
    public string L_TipZapretAutoUpdate => Strings.TipZapretAutoUpdate;
    public string L_UpdateIpSet => Strings.UpdateIpSet;
    public string L_WmTgProxyPort => Strings.WmTgProxyPort;
    public string L_WmTgProxySecret => Strings.WmTgProxySecret;
    public string L_WmVlessUri => Strings.WmVlessUri;
    public string L_WmZapretCustomArgs => Strings.WmZapretCustomArgs;
    public string L_ZapretHostsHint => Strings.ZapretHostsHint;
    public string L_ZapretSecAdvanced => Strings.ZapretSecAdvanced;
    public string L_ZapretSecDiagnostics => Strings.ZapretSecDiagnostics;
    public string L_ZapretSecFilters => Strings.ZapretSecFilters;
    public string L_ZapretSecHosts => Strings.ZapretSecHosts;
    public string L_ZapretSecStatus => Strings.ZapretSecStatus;
    public string L_ZapretSecStrategy => Strings.ZapretSecStrategy;
    public string L_ZapretSecUpdates => Strings.ZapretSecUpdates;

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
