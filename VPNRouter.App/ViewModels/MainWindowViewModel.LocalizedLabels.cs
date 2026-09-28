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
    public string L_AutoSelectBest => global::VPNRouter.Core.Localization.Strings.AutoSelectBestServer;
    public string L_AutoSelectBestTip => global::VPNRouter.Core.Localization.Strings.AutoSelectBestServerTip
        + (IsRussian ? " Применяется при следующем подключении." : " Applies on next connect.");

    public string LblTabTools => IsRussian ? "Инструменты" : "Tools";
    public string LblTabFreeConfigs => Strings.TabFreeConfigs;
    public string LblSettingsRouting => Strings.SectionRouting;
    public string LblSettingsLeak => Strings.SectionLeakProtection;
    public string LblSettingsContent => Strings.SectionContent;
    public string LblSettingsUpdates => Strings.SectionUpdates;
    public string LblAutostartSection => Strings.AutostartSection;
    public string LblAutostartVpn => Strings.AutostartVpn;
    public string LblAutostartZapret => Strings.AutostartZapret;
    public string LblAutostartTgProxy => Strings.AutostartTgProxy;
    public string LblAutostartUi => Strings.AutostartUi;

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

    public string LblTabTelegram => Strings.TabTgWsProxy;
    public string LblTgProxyDescription => Strings.TgProxyDescription;
    public string LblTgProxySetupHint => Strings.TgProxySetupHint;
    public string LblTgProxyToggle => TgProxyEnabled ? Strings.TgProxyStop : Strings.TgProxyStart;

    public string LblTgProxyMainAction => TgProxyEnabled
        ? Strings.TgProxyStop
        : Strings.TgProxyStartAndOpen;

    public string L_TgProxyReopenInTelegram => Strings.TgProxyReopenInTelegram;
    public string LblUpdateTgProxy => IsRussian
        ? (TgProxyUpdater.IsInstalled() ? "Обновить TgProxy" : "Скачать TgProxy")
        : (TgProxyUpdater.IsInstalled() ? "Update TgProxy" : "Download TgProxy");

    public string L_TgProxySchemeMissingWarning => Strings.TgProxySchemeMissingWarning;
    public string L_TgProxyDismiss => IsRussian ? "Скрыть" : "Dismiss";
    public string L_TgProxyCopyLink => IsRussian ? "Копировать ссылку" : "Copy link";

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
