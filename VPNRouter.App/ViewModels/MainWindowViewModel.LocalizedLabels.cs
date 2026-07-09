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
    // ── Localized labels (proxies to Strings.cs, refreshed on language toggle) ──
    public string LblTabServers => Strings.TabServers;
    public string LblTabManual => Strings.TabServers;
    public string LblTabSubscribe => Strings.ModeSubscribe;
    public string LblTabApps => Strings.TabApps;
    public string LblTabNetwork => Strings.TabSettings;
    public string LblVlessServers => Strings.VlessServers;
    public string LblCustomConfigJson => Strings.CustomConfigJson;
    public string LblAddServers => Strings.AddServers;
    public string LblRemove => Strings.Remove;
    public string LblAddConfig => Strings.AddConfig;
    public string LblBtnAdd => Strings.BtnAdd;
    public string LblSplitTunnel => Strings.SplitTunnel;
    public string LblFullTunnel => Strings.FullTunnel;
    public string LblAppsHint => Strings.AppsHint;
    public string LblFieldName => Strings.FieldName;
    public string LblFieldServer => Strings.FieldServer;
    public string LblFieldPort => Strings.FieldPort;
    public string LblFieldUuid => Strings.FieldUuid;
    public string LblFieldPublicKey => Strings.FieldPublicKey;
    public string LblFieldShortId => Strings.FieldShortId;
    public string LblDoubleClickEditServer => Strings.DoubleClickEditServer;
    public string LblDoubleClickActiveConfig => Strings.DoubleClickActiveConfig;
    public string LblClickToActivateConfig => Strings.ClickToActivateConfig;
    public string LblSubscribeMode => Strings.SubscribeMode;
    public string LblSubscriptionUrlHint => Strings.SubscriptionUrlHint;
    public string LblSyncButton => Strings.SyncButton;
    public string LblAddCustomAppHint => Strings.AddCustomAppHint;
    public string LblTcpUdpHint => Strings.TcpUdpHint;
    public string BypassRuLabel => Strings.BypassRussianTrafficLabel;
    public string BypassRuHint => Strings.BypassRussianTrafficHint;
    public string CheckLeaksLabel => Strings.CheckLeaks;
    public string ShowLogsLabel => Strings.ShowLogs;
    public string StrictModeLabel => Strings.StrictModeLabel;
    public string StrictModeHint => Strings.StrictModeHint;
    public string MtuLabel => Strings.MtuLabel;
    public string MtuHint => Strings.MtuHint;
    public string MtuAutoTuneButton => Strings.MtuAutoTuneButton;
    public string ForceIpv4Label => Strings.ForceIpv4Label;
    public string FlushDnsLabel => Strings.FlushDnsLabel;
    public string StrictDnsLabel => Strings.StrictDnsLabel;
    public string DnsLeakLockdownLabel => Strings.DnsLeakLockdownLabel;
    public string BlockAdsLabel => IsRussian ? "Блокировать рекламу и трекеры" : "Block ads & trackers";
    public string BlockAdsHint => IsRussian
        ? "AdGuard DNS + adblock rule_set (~300K доменов)"
        : "AdGuard DNS + adblock rule_set (~300K domains)";
    // Backlog A (2026-06-20): opt-in urltest auto-select toggle (Subscribe page).
    // urltest R5 (2026-07-09): forward to the shared Core strings so desktop and
    // Android carry the SAME honest wording ("quick web test", not "best server") —
    // the R5 brat live gate caught these stale duplicated literals.
    public string L_AutoSelectBest => global::VPNRouter.Core.Localization.Strings.AutoSelectBestServer;
    public string L_AutoSelectBestTip => global::VPNRouter.Core.Localization.Strings.AutoSelectBestServerTip
        + (IsRussian ? " Применяется при следующем подключении." : " Applies on next connect.");

    // DPI Bypass labels
    public string LblTabTools => IsRussian ? "Инструменты" : "Tools";
    public string LblTabFreeConfigs => Strings.TabFreeConfigs;
    public string LblSettingsRouting => Strings.SectionRouting;
    // LblSettingsRules lives in MainWindowViewModel.Localization.cs (v2.30.0-r2).
    public string LblSettingsLeak => Strings.SectionLeakProtection;
    public string LblSettingsContent => Strings.SectionContent;
    public string LblSettingsUpdates => Strings.SectionUpdates;
    public string LblAutostartSection => Strings.AutostartSection;
    public string LblAutostartVpn => Strings.AutostartVpn;
    public string LblAutostartZapret => Strings.AutostartZapret;
    public string LblAutostartTgProxy => Strings.AutostartTgProxy;
    public string LblAutostartUi => Strings.AutostartUi;

    // v2.31.10 (autostart UX clarity): per-component status badge. Each
    // CheckBox in the Section A "Components to auto-start with the service"
    // block now shows a small label that names the actual delivery channel
    // (Windows Service at boot vs App-side login bootstrap vs nothing) so a
    // user can't tick a toggle that doesn't fire. Status is computed from
    // (ServiceVm.IsInstalled, HasAppBootstrap{Vpn,Zapret,TgProxy}); the
    // ServiceVm.PropertyChanged subscription in the constructor already
    // re-fires PropertyChanged for these labels on every IsInstalled flip.
    //
    // Currently HasAppBootstrap* return false for all three components —
    // the App.axaml.cs OnFrameworkInitializationCompleted path doesn't run
    // any of VpnEngine/ZapretManager/TgProxyManager at user login. The
    // sister DBG-2 task adds App-side bootstrap; flipping the corresponding
    // flag to true at that point switches affected components from the red
    // ⛔ "won't fire" badge to the amber ⚠ "fires after App login" badge.
    internal const bool HasAppBootstrapVpn = false;
    internal const bool HasAppBootstrapZapret = false;
    internal const bool HasAppBootstrapTgProxy = false;

    public string LblAutostartVpnStatus =>
        ComputeAutostartStatus(ServiceVm.IsInstalled, HasAppBootstrapVpn);
    public string LblAutostartZapretStatus =>
        ComputeAutostartStatus(ServiceVm.IsInstalled, HasAppBootstrapZapret);
    public string LblAutostartTgProxyStatus =>
        ComputeAutostartStatus(ServiceVm.IsInstalled, HasAppBootstrapTgProxy);

    public bool IsAutostartVpnStatusGood => ServiceVm.IsInstalled;
    public bool IsAutostartVpnStatusWarn => !ServiceVm.IsInstalled && HasAppBootstrapVpn;
    public bool IsAutostartVpnStatusBad => !ServiceVm.IsInstalled && !HasAppBootstrapVpn;

    public bool IsAutostartZapretStatusGood => ServiceVm.IsInstalled;
    public bool IsAutostartZapretStatusWarn => !ServiceVm.IsInstalled && HasAppBootstrapZapret;
    public bool IsAutostartZapretStatusBad => !ServiceVm.IsInstalled && !HasAppBootstrapZapret;

    public bool IsAutostartTgProxyStatusGood => ServiceVm.IsInstalled;
    public bool IsAutostartTgProxyStatusWarn => !ServiceVm.IsInstalled && HasAppBootstrapTgProxy;
    public bool IsAutostartTgProxyStatusBad => !ServiceVm.IsInstalled && !HasAppBootstrapTgProxy;

    /// <summary>
    /// Pure-function status dispatch — extracted as <c>internal static</c>
    /// so it can be unit-tested without instantiating MainWindowViewModel
    /// (which spins up file I/O, logger, etc.). Three branches matching
    /// the three badge states surfaced in the Autostart sub-tab.
    /// </summary>
    internal static string ComputeAutostartStatus(bool isServiceInstalled, bool hasAppBootstrap)
    {
        if (isServiceInstalled) return Strings.AutostartStatusBoot;
        return hasAppBootstrap
            ? Strings.AutostartStatusLoginFallback
            : Strings.AutostartStatusNoBoot;
    }
    public string LblServerModeVless => Strings.VlessServers;
    public string LblServerModeCustom => Strings.CustomConfigJson;
    public string LblToolZapret => Strings.TabZapret;
    public string LblToolTgProxy => Strings.TabTgWsProxy;
    public string LblDpiBypassTab => Strings.TabZapret;
    // v2.30.7 — UX-44 followup: the v2.30.5 fix dropped the "(zapret от
    // Flowseal)" parenthetical from RU only. EN side kept "(zapret by
    // Flowseal)". Symmetric drop here — Flowseal credit lives in the
    // GitHub link in the Advanced section.
    public string LblDpiDescription => IsRussian
        ? "Обход блокировок провайдера. Работает с Discord, YouTube, и другими заблокированными сервисами. Если стратегия не работает — пробуйте другую."
        : "Bypass ISP blocking. Works with Discord, YouTube, and other blocked services. If a strategy doesn't work — try another.";
    public string LblDpiStrategy => IsRussian ? "Стратегия" : "Strategy";
    public string LblUpdateZapret => IsRussian
        ? (VPNRouter.Core.Services.ZapretUpdater.IsInstalled() ? "Обновить" : "Скачать")
        : (VPNRouter.Core.Services.ZapretUpdater.IsInstalled() ? "Update" : "Download");
    public string LblDpiWarning => IsRussian
        ? "⚠ Только Windows. Можно использовать без VPN и вместе с VPN."
        : "⚠ Windows only. Can be used without VPN and alongside VPN.";
    public string LblDpiToggle => IsRussian
        ? (ZapretEnabled ? "Остановить обход DPI" : "Запустить обход DPI")
        : (ZapretEnabled ? "Stop DPI Bypass" : "Start DPI Bypass");
    public string LblDiscordHosts => IsRussian
        ? (DiscordHostsInstalled ? "Удалить Discord hosts" : "Добавить Discord hosts")
        : (DiscordHostsInstalled ? "Remove Discord hosts" : "Add Discord hosts");
    public string LblDiscordHostsDesc => IsRussian
        ? "Перенаправляет Discord voice серверы (finland*.discord.media) на рабочий Cloudflare IP. Фиксит голосовые каналы."
        : "Redirects Discord voice servers (finland*.discord.media) to working Cloudflare IP. Fixes voice channels.";
    public string ReceivePrereleasesLabel => IsRussian ? "Получать prerelease обновления (experimental канал)" : "Receive prereleases (experimental channel)";
    public string UpdateChannelHeader => IsRussian ? "Канал обновлений" : "Update channel";

    // Telegram proxy labels
    public string LblTabTelegram => Strings.TabTgWsProxy;
    public string LblTgProxyDescription => Strings.TgProxyDescription;
    public string LblTgProxySetupHint => Strings.TgProxySetupHint;
    public string LblTgProxyToggle => TgProxyEnabled ? Strings.TgProxyStop : Strings.TgProxyStart;

    /// <summary>
    /// v2.31.6-r5 (TG-2): label for the unified footer action introduced
    /// per user feedback 2026-05-03 night. When stopped, footer fires the
    /// full SetupTgProxy chain (download → start → open Telegram), so
    /// label reads «Запустить и открыть Telegram» / «Start &amp; open
    /// Telegram». When running, footer reverts to the existing «Stop»
    /// semantics. Bound to <see cref="TgProxyMainActionCommand"/>.
    /// </summary>
    public string LblTgProxyMainAction => TgProxyEnabled
        ? Strings.TgProxyStop
        : Strings.TgProxyStartAndOpen;

    // v2.31.6-r9 — purged 5 unused L_TgProxySetup* / L_TgProxyClientAutoHint
    // / L_TgProxyAdvanced getters added in v2.31.6-r1's two-state cascade
    // but orphaned by r3's design-aligned redo. Iter#4 audit confirmed no
    // XAML bindings. Only L_TgProxyReopenInTelegram is still used (body
    // «Reopen in Telegram» button).
    public string L_TgProxyReopenInTelegram => Strings.TgProxyReopenInTelegram;
    // v2.30.7-r4 — F-17 fix: button label "Обновить" / "Update" alone
    // is ambiguous — the page has multiple things that can be updated
    // (binary version, secret, port). Prefix with "TgProxy" so the
    // action is unambiguous: "Обновить TgProxy" / "Update TgProxy".
    public string LblUpdateTgProxy => IsRussian
        ? (TgProxyUpdater.IsInstalled() ? "Обновить TgProxy" : "Скачать TgProxy")
        : (TgProxyUpdater.IsInstalled() ? "Update TgProxy" : "Download TgProxy");

    // v2.36 (MVP one-button task C): non-blocking scheme-missing
    // banner. Bound from TelegramPage.axaml; visibility controlled
    // by IsTelegramSchemeWarningVisible.
    public string L_TgProxySchemeMissingWarning => Strings.TgProxySchemeMissingWarning;
    public string L_TgProxyDismiss => IsRussian ? "Скрыть" : "Dismiss";
    public string L_TgProxyCopyLink => IsRussian ? "Копировать ссылку" : "Copy link";

    // v2.36.0-r7 — TgProxyOneTap design hero labels. Switch on running
    // state so the body re-narrates after Start: "Включить Telegram" →
    // "Telegram через MTProto", lede updates with live port. Bind these
    // and they re-fetch via NotifyPropertyChangedFor on TgProxyEnabled
    // (see _tgProxyEnabled / _tgProxyPort fields).
    public string LblTgProxyHeroTitle => TgProxyEnabled
        ? Strings.TgProxyOneTapTitleRunning
        : Strings.TgProxyOneTapTitleStopped;
    public string LblTgProxyHeroLede => TgProxyEnabled
        ? Strings.TgProxyOneTapLedeRunning(TgProxyPort)
        : Strings.TgProxyOneTapLedeStopped;
    public string L_TgProxyOneTapStep1 => Strings.TgProxyOneTapStep1;
    public string L_TgProxyOneTapStep2 => Strings.TgProxyOneTapStep2;
    public string L_TgProxyOneTapStep3 => Strings.TgProxyOneTapStep3;
    public string L_TgProxyOneTapTune  => Strings.TgProxyOneTapTune;
    public string LblTgProxyAirPill   => Strings.TgProxyOneTapAirPill(TgProxyPort);

}
