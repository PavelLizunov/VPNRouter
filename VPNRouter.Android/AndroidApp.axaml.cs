using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.UI.Controls;

namespace VPNRouter.Android;

/// <summary>
/// v3.0 Phase 8.2 (2026-05-07) — code-side equivalent of XAML's
/// <c>{DynamicResource KeyName}</c>. Used to wire token brushes to
/// Avalonia controls built in code-behind so they auto-repaint on
/// <c>Application.RequestedThemeVariant</c> change without manually
/// walking the visual tree.
///
/// <para><see cref="DynamicResourceExtension"/> implements
/// <see cref="IBinding"/>, so handing it to <c>AvaloniaObjectExtensions.Bind</c>
/// installs a live binding that resolves the resource through the
/// element's logical parent chain and re-resolves on theme change.</para>
/// </summary>
internal static class StyledElementResourceExtensions
{
    /// <summary>
    /// Bind <paramref name="prop"/> on <paramref name="element"/> to the
    /// dynamic resource at <paramref name="key"/>. Returns the element
    /// for fluent chaining. Replaces any prior binding at the same
    /// property+priority.
    /// </summary>
    public static T BindToken<T>(this T element, AvaloniaProperty prop, string key)
        where T : AvaloniaObject
    {
        element.Bind(prop, new DynamicResourceExtension(key));
        return element;
    }
}

/// <summary>
/// v3.0 Phase 3 (2026-05-04) — honest visual parity with desktop SimplePage.
///
/// <para>User feedback 2026-05-04: «Приложение не выглядит так как выглядит
/// на ПК, совершенно разный интерфейс и оформление». Phase 2's
/// "tokens applied = parity" was wrong. Desktop SimplePage has a
/// specific structure — status card with dot, config row button with
/// flag icon + chevron, collapsible form, three-variant CTA button,
/// "Расширенные настройки" card — and Phase 2's hand-rolled view
/// looked nothing like it.</para>
///
/// <para>This rewrite mirrors <c>VPNRouter.App/Views/Pages/SimplePage.axaml</c>
/// section-by-section:</para>
///
/// <list type="number">
///   <item>Status card: dot (Success/Warning/Muted) + bold title +
///   description (matches lines 42-72 of SimplePage)</item>
///   <item>Config row tappable button: flag icon, label + value
///   "вручную · полный", chevron (lines 74-120)</item>
///   <item>Collapsible inline form: input + radio buttons for tunnel
///   mode + autostart link card (lines 122-220)</item>
///   <item>CTA button — three mutually exclusive variants by state
///   (lines 222-266)</item>
///   <item>Расширенные настройки card → Android version: subscription
///   list page link (lines 268-304)</item>
/// </list>
///
/// <para>Light theme by default to match desktop's default appearance.
/// All colors/radii/spacing pulled from the linked Tokens.axaml.</para>
/// </summary>
public partial class AndroidApp : Avalonia.Application
{
    // Status card.
    // v3.0 Phase G step 1 (2026-05-09): replaced the Ellipse + 2× TextBlock
    // trio with the shared VPNRouter.UI.Controls.StatusCard so desktop +
    // Android render the same Border / dot / typography from one .axaml.
    // Title / Subtitle / IsOn / IsWarn / IsOff are StyledProperty setters
    // on the control; UpdateConnectionState mutates them directly.
    private StatusCard? _statusCard;

    // v2.32.0 (AND-DIAG, 2026-05-07) — runtime diagnostics on status card.
    // Mirrors desktop's MainWindowViewModel.RuntimeStatus surface: a
    // periodic 1-second timer drives uptime in the status title and a
    // 30-second log-delta probe that surfaces under the description. The
    // error one-liner appears under the probe row when a TUNNEL_ERROR
    // arrives and persists for 30 s.
    //
    // State machine:
    //   • idle       — _connectionStartedAt == null, timer stopped
    //   • connected  — _connectionStartedAt set, timer ticking,
    //                  _statusCard.Title shows "Connected · M:SS" /
    //                  "Connected · H:MM:SS"
    //   • error      — _lastError set, _lastErrorAt sealed; timer keeps
    //                  ticking (or starts briefly even if disconnected) so
    //                  the 30 s auto-clear runs
    private TextBlock? _statusHealthCheck;
    private TextBlock? _statusErrorOneLiner;
    private DispatcherTimer? _diagnosticsTimer;
    private DateTime? _connectionStartedAt;
    private string? _lastError;
    private DateTime _lastErrorAt;
    /// <summary>30 s — how long the error one-liner stays visible
    /// before auto-clearing (matches the prompt acceptance criteria).</summary>
    private static readonly TimeSpan ErrorDisplayWindow = TimeSpan.FromSeconds(30);
    /// <summary>30 s — health probe cadence. Keeps cost minimal (one
    /// stat call per probe) while staying responsive to a stalled tunnel.</summary>
    private static readonly TimeSpan HealthProbeInterval = TimeSpan.FromSeconds(30);
    /// <summary>60 s — tolerance window for log delta. If the file hasn't
    /// changed in this window AND we've been connected longer than this,
    /// the probe reports "stale". Idle Android phones with screen off can
    /// see legitimate quiet periods of 30+ s, so 60 s is the lower bound
    /// that still catches a wedged sing-box without false-positives during
    /// normal idle.</summary>
    private static readonly TimeSpan HealthStaleThreshold = TimeSpan.FromSeconds(60);
    private DateTime _lastHealthProbeAt;
    private long _lastHealthLogSize = -1;
    private DateTime _lastHealthLogMTime;
    private bool _lastHealthOk;
    private bool _firstProbePending;
    // Bug-AND-006 (2026-05-16) — cache last formatted uptime so the
    // 1 Hz diagnostics tick only mutates Avalonia text properties when
    // the second actually flipped (i.e. once per second the title
    // changes from "0:42" to "0:43"; in-between calls have nothing to
    // do). Without the guard each tick wrote both _statusCard.Title
    // and _advFooterStatusText.Text unconditionally, dirtying their
    // visual trees even when the value was identical to the previous
    // frame.
    private string? _lastFormattedUptimeTitle;

    // Config row button
    private TextBlock? _configRowLabel;
    private TextBlock? _configRowValue;
    private TextBlock? _configRowChevron;

    // Collapsible form
    private Border? _formCard;
    private TextBox? _serverInput;
    private TextBlock? _serverInputLabel;
    private TextBlock? _serverInputHint;
    private TextBlock? _serverInputError;
    private TextBlock? _tunnelModeLabel;
    private Avalonia.Controls.RadioButton? _splitRadio;
    private Avalonia.Controls.RadioButton? _fullRadio;
    private TextBlock? _splitLabel;
    private TextBlock? _splitHint;
    private TextBlock? _fullLabel;
    private TextBlock? _fullHint;

    // Server list (subscription)
    private TextBlock? _serverListHeader;
    private ListBox? _serverList;

    // v2.32.0 (AND-CC, 2026-05-07) — Custom sing-box JSON mode UI.
    // Sits below the existing input field inside the form card. Hidden
    // unless the segmented mode selector at the top of the form picks
    // "Custom JSON". Shows: paste TextBox (multi-line, monospace) +
    // status banner (validation OK / error from CustomConfigInjector) +
    // Validate / Save / Clear button row.
    private Avalonia.Controls.Button? _ccModeSubBtn;
    private Avalonia.Controls.Button? _ccModeManualBtn;
    private Avalonia.Controls.Button? _ccModeCustomBtn;
    private StackPanel? _ccModeRow;
    private StackPanel? _ccCustomSection;
    private StackPanel? _ccUriSection;
    private TextBlock? _ccCustomLabel;
    private TextBlock? _ccCustomHint;
    private TextBox? _ccCustomInput;
    private TextBlock? _ccCustomStatus;
    private Avalonia.Controls.Button? _ccValidateBtn;
    private Avalonia.Controls.Button? _ccSaveCustomBtn;
    private Avalonia.Controls.Button? _ccClearCustomBtn;
    /// <summary>"subscribe" | "manual" | "custom" — mirrors desktop ConfigMode.</summary>
    private string _ccMode = "manual";

    // CTA buttons (3 variants)
    private Avalonia.Controls.Button? _ctaConnect;
    private Avalonia.Controls.Button? _ctaConnecting;
    private Avalonia.Controls.Button? _ctaDisconnect;

    // Bottom card
    private TextBlock? _advCardTitle;
    private TextBlock? _advCardSubtitle;

    // Header (Phase 4: full sub-header matching desktop)
    private TextBlock? _brandTitle;
    private TextBlock? _vpnChip;
    private TextBlock? _zapretChip;
    private TextBlock? _tgChip;
    // v3.0 Phase 8.2 (2026-05-07) — Image is invariant under DynamicResource
    // because Bitmap source is bytes, not a brush. Theme switch must
    // re-call LoadMascot() to get the inverted Bgra8888 variant. Stored
    // in a field so ApplyTheme(string) can flip Source.
    private Image? _mascotImage;
    private Avalonia.Controls.Button? _kebabMenuButton;
    private Popup? _kebabPopup;
    // v3.0 Phase 7.3 — segmented control buttons (RU|EN, Light|Dark)
    // replacing the v3.0 Phase 4 single-toggle buttons. User flagged
    // 2026-05-04: "toogle на android отличаеться от pc версии".
    // Desktop (MainWindow.axaml:430-459) has 2-segment grids that
    // SET a specific value rather than toggle. Android now mirrors.
    private Avalonia.Controls.Button? _menuLangRu;
    private Avalonia.Controls.Button? _menuLangEn;
    private Avalonia.Controls.Button? _menuThemeLight;
    private Avalonia.Controls.Button? _menuThemeDark;
    // Phase 7.2 — additional menu items (Diagnostics + Troubleshooting + About)
    private Avalonia.Controls.Button? _menuOpenLogItem;
    private Avalonia.Controls.Button? _menuCopyLogPathItem;
    // v2.32.0 (AND-CRASH-HOOK, 2026-05-08) — Diagnostics → "View crash log".
    // Reuses the singbox-log overlay; the click handler swaps the title
    // and replaces the content with the most-recent file from
    // <filesDir>/crashes/ (either crash-*.txt from the C# CrashReporter
    // or java-crash-*.txt from VpnRouterService's uncaught-handler).
    private Avalonia.Controls.Button? _menuViewCrashLogItem;
    private Avalonia.Controls.Button? _menuUpdateCheckItem;
    private Avalonia.Controls.Button? _menuResetSettingsItem;
    // F-12 kebab visual parity (2026-05-09): About row is a Button whose
    // content is a 2-column Grid — left text "О приложении / About"
    // (TextPrimary), right text mono version pill (TextMuted). Click target
    // opens the GitHub repo (Android has no AboutWindow). Pre-fix Android
    // had two separate rows (version + repo link) under an "About" section
    // header; desktop has just one inline row with the version pill.
    private Avalonia.Controls.Button? _menuVersionItem;
    private TextBlock? _menuAboutLabel;
    private TextBlock? _menuVersionPill;
    // Bug-AND-009 follow-up (2026-05-16) — promote kebab "Advanced ▸"
    // button to a field so language toggle refreshes its label
    // (RU "Расширенный ▸" / EN "Advanced ▸").
    private Avalonia.Controls.Button? _menuAdvancedToggleBtn;
    // _menuRepoItem retained as field-level null-stub so existing
    // ToggleLanguageAndRefresh / null-check sites don't need refactoring.
    // Functionally retired — the About row above absorbs the repo-open click.
    private Avalonia.Controls.Button? _menuRepoItem;
    // AND-MIGRATE-OVERLAYS (2026-05-09): Free Configs / Tools / DPI bypass
    // dropped from the kebab — they now live as Advanced-shell tabs
    // (Public configs / DPI bypass / Telegram) reachable via the
    // "Advanced settings ▸" CTA on the Simple page. Field stubs kept null
    // so the language-refresh path's null-checks compile.
    private Avalonia.Controls.Button? _menuFreeConfigsItem;
    private Avalonia.Controls.Button? _menuToolsItem;
    private Avalonia.Controls.Button? _menuDpiBypassItem;
    private TextBlock? _menuSectionTools;
    // F-10 kebab parity (2026-05-09) — items added to Android Diagnostics
    // + Troubleshooting blocks so the kebab matches desktop sequence
    // 1:1. Pre-fix Check IP leak / Run Health Check / Restart in Safe
    // Mode were desktop-only; now both platforms expose the same set.
    private Avalonia.Controls.Button? _menuCheckLeaksItem;
    private Avalonia.Controls.Button? _menuHealthCheckItem;
    private Avalonia.Controls.Button? _menuRestartSafeModeItem;
    // Localized section header TextBlocks — kept so language toggle can refresh them.
    private TextBlock? _menuSectionView;
    private TextBlock? _menuSectionDiagnostics;
    private TextBlock? _menuSectionTroubleshooting;
    private TextBlock? _menuSectionAbout;
    private TextBlock? _menuSectionFreeConfigs;
    // Tracks Reset confirm flow: first tap → confirm prompt, second tap → wipe.
    private bool _resetConfirmPending = false;
    // Banner that surfaces transient kebab-menu feedback (Update toast,
    // log-path copied, settings reset done, etc.) without a real Snackbar.
    private TextBlock? _menuFeedback;

    // v2.32.0 (2026-05-07) — auto-update banner. Mirrors desktop's
    // UpdateNotificationViewModel-driven card, except in code-behind
    // because Android view tree is built imperatively. State machine:
    //   • Hidden            — _updateBanner.IsVisible = false
    //   • Available         — title shows version + size, action = Download
    //   • Downloading       — title shows "Downloading… N%", action disabled
    //   • DownloadDone      — title shows "Downloaded", action = Install
    //   • PermissionNeeded  — title shows "Allow install" deep-link copy,
    //                          action = Allow → opens Settings
    //   • Failed            — title shows error message, action = Retry
    private Border? _updateBanner;
    private TextBlock? _updateBannerTitle;
    private TextBlock? _updateBannerSubtitle;
    private Avalonia.Controls.Button? _updateBannerAction;
    private Avalonia.Controls.Button? _updateBannerDismiss;
    private AndroidUpdateInfo? _pendingUpdate;
    private string? _downloadedApkPath;
    private bool _updateInFlight; // guard against double-tap during async ops

    // v3.0 Phase 7.4 — in-app log viewer overlay. Shown when user taps
    // Diagnostics > "Открыть лог" / "Open log". Reads last 50 KB of
    // singbox.log into a monospace ScrollViewer. Closed via × button.
    private Border? _logOverlay;
    private TextBlock? _logViewerContent;
    private TextBlock? _logViewerEmptyState;
    private ScrollViewer? _logViewerScroller;
    private TextBlock? _logViewerTitle;
    private Avalonia.Controls.Button? _logViewerCloseBtn;
    private Avalonia.Controls.Button? _logViewerRefreshBtn;

    // AND-MIGRATE-OVERLAYS (2026-05-09): Settings is now the Network tab
    // inside the Advanced shell. Field set is the same minus the
    // overlay/title/close widgets the shell now owns; helpers live in
    // AndroidApp.AdvancedShell.cs (BuildNetworkTabContent +
    // ReseedNetworkTabState).
    private Avalonia.Controls.RadioButton? _settingsSplitRadio;
    private Avalonia.Controls.RadioButton? _settingsFullRadio;
    private Avalonia.Controls.CheckBox? _settingsBypassRu;
    private Avalonia.Controls.CheckBox? _settingsBlockOnVpnFail;
    // v2.32.0 (AND-ZAPRET) — DPI bypass picker. ComboBox with three values
    // (Off / Standard / Aggressive) wired to AndroidStorage.GetDpiBypassMode.
    // Lives inside Settings > Routing alongside split/full + bypass-RU.
    private Avalonia.Controls.ComboBox? _settingsDpiBypassMode;
    private Avalonia.Controls.ComboBox? _settingsDnsStrategy;
    // Content section parity with desktop NetworkPage — single checkbox-card
    // toggle for ad/tracker blocking. Persists today; the AndroidConfigBuilder
    // route wiring (geosite-ads → reject + AdGuard DoH) is a follow-up.
    private Avalonia.Controls.CheckBox? _settingsBlockAds;
    private Avalonia.Controls.CheckBox? _settingsReceivePrereleases;
    private TextBlock? _settingsCurrentVersion;
    private Avalonia.Controls.CheckBox? _settingsAutostartVpn;
    private Avalonia.Controls.CheckBox? _settingsAutostartZapret;
    private Avalonia.Controls.CheckBox? _settingsAutostartTgProxy;
    private Avalonia.Controls.Button? _menuSettingsItem;
    // v2.32.0 AND-NETRES — Reliability section controls. Always-on row
    // is text + button (no programmatic status read — the Android API
    // for "is VPNRouter the always-on VPN package" is system-only since
    // Android Q). Battery opt row reads PowerManager.IsIgnoringBatteryOptimizations
    // each time the overlay opens. Auto-reconnect is a simple CheckBox
    // bound to AndroidStorage.GetAutoReconnectOnNetworkChange.
    private TextBlock? _reliabilityBatteryStatusLabel;
    private Avalonia.Controls.Button? _reliabilityBatteryButton;
    private Avalonia.Controls.CheckBox? _reliabilityAutoReconnect;
    private bool _settingsLoading = false;

    // Phase C (2026-05-10): nested side-nav for the Settings tab. Mirrors
    // desktop NetworkPage's master-detail layout — left column is a list of
    // 6 sub-section buttons (Routing / Rules / Leak / Content / Updates /
    // Autostart), right column swaps content based on the selected index.
    // Index matches desktop's SelectedSettingsIndex so muscle-memory carries
    // over. _settingsSubSectionButtons + _settingsSubSectionPanels stay in
    // lockstep — same key set, mutated together in BuildSettingsTabContent.
    private readonly Avalonia.Controls.Button?[] _settingsSubSectionButtons = new Avalonia.Controls.Button?[6];
    private readonly Control?[] _settingsSubSectionPanels = new Control?[6];
    private int _settingsSelectedSubSection = 0;

    // Apply / Auto-saved footer slot. Settings persistence is auto-saved on
    // every CheckBox/Radio/ComboBox change (existing OnSettings*Changed
    // handlers call AndroidStorage.Set* directly). The Apply button surfaces
    // when there's a pending change that needs the running tunnel to reload
    // — flipping routing mode, DNS strategy, etc. while connected. The badge
    // ("✓ Auto-saved") is the resting state.
    private bool _settingsDirty = false;
    private Border? _settingsAutoSavedBadge;
    private Avalonia.Controls.Button? _settingsApplyButton;

    // v2.32.0 (AND-PROFILES, 2026-05-08) — routing-profile catalog overlay.
    // Tap kebab → "Routing profiles" → this overlay. List of profile cards
    // from BuiltInAndroidProfiles plus a "No profile" pseudo-card at the
    // top. Tap any card → ProfileApplication.Plan() → AndroidStorage writes
    // → close + toast. Active profile gets an accent border + ✓ badge.
    private Border? _profilesOverlay;
    private TextBlock? _profilesOverlayTitle;
    private TextBlock? _profilesOverlayIntro;
    private Avalonia.Controls.Button? _profilesCloseBtn;
    private StackPanel? _profilesList;
    private Avalonia.Controls.Button? _menuProfilesItem;
    private TextBlock? _menuSectionProfiles;

    // AND-MIGRATE-OVERLAYS (2026-05-09): per-app filter picker is now the
    // Applications tab inside the Advanced shell. The "Choose apps…" button
    // on the Simple form deeplinks via OpenAdvancedShell(AdvancedTab.Applications).
    // AND-ADV-CHROME (2026-05-10): tab renamed Apps → Applications.
    private TextBox? _appPickerSearch;
    private Avalonia.Controls.CheckBox? _appPickerSystemToggle;
    private TextBlock? _appPickerCount;
    // Bug #2 (2026-05-11) — mobile redesign: small "Showing N apps" hint
    // next to the system-apps toggle so the user can verify the device-
    // wide app enumeration count after the launcher-activities fallback
    // landed in AppListLoader.
    private TextBlock? _appPickerShowingCount;
    private ListBox? _appPickerList;
    private Avalonia.Controls.Button? _appPickerSaveBtn;
    private Avalonia.Controls.Button? _perAppPickButton;
    private TextBlock? _perAppCountLabel;
    // Bug-AND-014 (2026-05-16, full manual test pass iter 18) — promote
    // Simple-page inline-autostart card text to fields so the language
    // refresh updates them. Pre-fix the strings were captured into local
    // TextBlock vars inside BuildAutostartInlineCard, so a RU↔EN toggle
    // left "Autostart" / "Configure VPN autostart on device boot" in
    // English on a Russian-language device.
    private TextBlock? _autostartCardTitleText;
    private TextBlock? _autostartCardSubText;
    private List<AppListLoader.AppEntry> _appPickerCache = new();
    private HashSet<string> _appPickerSelected = new(System.StringComparer.OrdinalIgnoreCase);
    private bool _appPickerSystemAppsVisible = false;
    // v3.0 v2.32.0 (2026-05-07) — exclude-mode UI inside the picker
    // overlay. Storage already round-trips "include" / "exclude" through
    // VpnRouterService.java's addAllowedApplication / addDisallowedApplication
    // branches; this is the missing UI surface that lets a user pick.
    // The two segment buttons sit above the search box; selection drives
    // the hint TextBlock below them and the count label on the form.
    private Avalonia.Controls.Button? _appPickerModeIncludeBtn;
    private Avalonia.Controls.Button? _appPickerModeExcludeBtn;
    private TextBlock? _appPickerModeLabel;
    private TextBlock? _appPickerModeHint;
    private string _appPickerMode = "include";

    // Phase D (AND-ADV-APPS-CATEGORIES, 2026-05-10) — left category sidebar
    // + right per-category content pane. Mirrors desktop ApplicationsPage
    // (ColumnDefinitions="120,*"). Active category id persists via
    // KeyApplicationsActiveCategory; null/empty = "Select a category"
    // placeholder. Custom user categories live in
    // _advAppsCustomCategories (loaded from AndroidStorage on tab activation,
    // persisted via SetCustomCategories).
    private string? _advAppsActiveCategoryId;
    private StackPanel? _advAppsCategoryListPanel;
    // Bug-AND-008 (2026-05-16) — WrapPanel host that replaces the
    // horizontal-scrolling category strip. All chips are simultaneously
    // visible and tappable — no gesture conflict with a parent
    // ScrollViewer. The legacy _advAppsCategoryListPanel field is kept
    // declared so other call sites that null-check it still compile,
    // but rebuild now writes children straight to this WrapPanel.
    private WrapPanel? _advAppsCategoryWrapHost;
    private TextBox? _advAppsNewCategoryInput;
    private Avalonia.Controls.Button? _advAppsAddCategoryBtn;
    private TextBlock? _advAppsRightPanePlaceholder;
    private Border? _advAppsRightPaneScopeContainer;
    private List<VPNRouter.Core.Models.CustomCategory> _advAppsCustomCategories = new();
    private readonly Dictionary<string, Border> _advAppsCategoryRowMap = new(System.StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> _advAppsCategoryCountMap = new(System.StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> _advAppsCategoryNameMap = new(System.StringComparer.OrdinalIgnoreCase);

    // State
    // AND-RESTORE-SIMPLE (2026-05-09) — default true to mirror desktop
    // SimpleMode VM `_smpFormExpanded = true`. Pre-fix this defaulted to
    // false and was only flipped true on first-launch (no saved config).
    // Existing users with a saved subscription saw a collapsed form on
    // every launch — input + radios + autostart effectively "lost" until
    // they discovered the Config·Mode chevron tap. Desktop has always
    // shown them by default; Android now matches.
    private bool _formExpanded = true;
    private List<VlessServerEntry> _cachedServers = new();

    /// <summary>
    /// v3.0 Phase 7.1 (2026-05-04) — chip semantic state. Mirrors desktop's
    /// status-chip pattern (`On` = green, `Connecting` = yellow + pulse,
    /// `Off` = gray). Pre-7.1 chips were static decoration; user requested
    /// they reflect the real connection lifecycle:
    /// <list type="bullet">
    ///   <item><c>VPN</c>: Off → user taps Connect → Connecting → tunnel
    ///   broadcast UP → On. Reverts to Off on TUNNEL_DOWN / TUNNEL_ERROR.</item>
    ///   <item><c>Zapret</c>, <c>TG</c>: stay Off (Android port doesn't
    ///   support those features yet — chips reserved for parity with
    ///   desktop layout).</item>
    /// </list>
    /// </summary>
    private enum ChipState { Off, Connecting, On }
    private ChipState _vpnChipState = ChipState.Off;
    // v2.32.0 (AND-ZAPRET) — Zapret chip state. Mirrors VPN chip pattern.
    // Driven by UpdateZapretChipFromState() which composes the current
    // DPI bypass setting + VPN connection state into a single chip color.
    private ChipState _zapretChipState = ChipState.Off;
    private System.Threading.CancellationTokenSource? _zapretPulseCts;
    private System.Threading.CancellationTokenSource? _vpnPulseCts;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // v2.32.0 (AND-SR-1) — central self-repair pass before any
        // consumer reads. Mirrors desktop's
        // SettingsLoader.LoadCore → EnsureSane → SettingsValidator
        // pipeline so a corrupt enum value (KeyRoutingMode="garbage",
        // KeyTheme="neon", etc.) is normalised + announced via
        // recovery notice instead of reaching the routing engine.
        // Wrapped in try/catch on top of the Core helper's own
        // best-effort guards: a SharedPreferences failure here must
        // never block app launch (SR-4 contract).
        try { AndroidStorage.RepairAllOnLoad(); }
        catch (Exception ex)
        {
            try
            {
                global::Android.Util.Log.Warn("VpnRouter.SelfRepair",
                    $"RepairAllOnLoad in OnFrameworkInitialization failed: {ex.GetType().Name}: {ex.Message}");
            }
            catch { /* nothing more we can do */ }
        }

        Localization.LoadFromStorage();
        ApplyTheme();

        if (ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.ISingleViewApplicationLifetime singleView)
        {
            // AND-RESTORE-SIMPLE (2026-05-09) — pre-fix this branch read
            // hasManual + hasSubscription and collapsed the form for
            // returning users. Desktop's `_smpFormExpanded = true` is
            // unconditional, so Android matches now. The form-default
            // value at the field declaration above (true) is sufficient;
            // no per-launch override needed.

            var view = BuildSimplePageView();
            singleView.MainView = view;
            // Bug-AND-011 / High-4 (2026-05-16 code review): re-attach
            // via the helper so we can detach when the view is torn
            // down (config-change-driven AndroidApp reconstruction).
            // Pre-fix the static event held a strong reference to every
            // previously-created AndroidApp instance, indefinitely
            // retaining the visual tree + _appPickerCache + Bitmaps.
            // Idempotent: subscribes only once per AndroidApp instance.
            AttachLifecycleEvents();
            UpdateConnectionState(MainActivity.IntendedConnected);
            ReloadServerList();

            // v2.32.0 (AUTOUPDATE) — silent auto-update check on launch.
            // Fire-and-forget; banner surfaces if newer release found.
            _ = Task.Run(() => RunUpdateCheckAsync(manual: false));

            // v2.32.0 SR-2 — MarkStable on first attach + consume recovery
            // notice. Mirrors desktop MainWindow.Opened semantics.
            EventHandler<Avalonia.VisualTreeAttachmentEventArgs>? attachHandler = null;
            attachHandler = (sender, _) =>
            {
                if (attachHandler != null && sender is Control c)
                    c.AttachedToVisualTree -= attachHandler;
                try
                {
                    if (!string.IsNullOrEmpty(MainActivity.LaunchCounterPath))
                        VPNRouter.Core.Services.LaunchFailureCounter.MarkStable(MainActivity.LaunchCounterPath);
                }
                catch { /* counter is advisory */ }

                try { ConsumeAndSurfaceRecoveryNotice(); }
                catch (Exception ex)
                {
                    global::Android.Util.Log.Warn("VpnRouter.SelfRepair",
                        $"recovery notice surfacing failed: {ex.GetType().Name}: {ex.Message}");
                }
            };
            view.AttachedToVisualTree += attachHandler;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// v2.32.0 SR-1/2/3/4 — pull whatever recovery notices accumulated
    /// during this launch (bad SharedPrefs JSON deserialise, unknown
    /// enum reset, persistent safe-mode flag from a previous chronic
    /// crash run) and surface them via the existing menu-feedback
    /// banner. Kept tiny + try/catch'd: the banner is informational,
    /// not load-bearing.
    /// </summary>
    private void ConsumeAndSurfaceRecoveryNotice()
    {
        // Order: SettingsLoader notice (desktop-style YAML, currently
        // always null on Android — kept for forward compat), then
        // AndroidStorage notice (our actual per-key SR-1/3/4 stamps),
        // then safe-mode banner (SR-2 tier-3, persisted across crashes).
        var coreNotice = VPNRouter.Core.Services.SettingsLoader.ConsumeRecoveryNotice();
        var androidNotice = AndroidStorage.ConsumeRecoveryNotice();
        var safeMode = AndroidStorage.ConsumeSafeModeBanner();

        var parts = new System.Collections.Generic.List<string>(3);
        if (!string.IsNullOrWhiteSpace(coreNotice)) parts.Add(coreNotice);
        if (!string.IsNullOrWhiteSpace(androidNotice)) parts.Add(androidNotice);
        if (safeMode)
        {
            parts.Add(Localization.Ru
                ? "Если проблемы продолжаются: Настройки > Приложения > VPNRouter > Хранилище > Очистить данные."
                : "If problems persist: Settings > Apps > VPNRouter > Storage > Clear data.");
        }

        if (parts.Count == 0) return;
        var combined = string.Join(" — ", parts);
        ShowMenuFeedback(combined);
    }

    private void ApplyTheme()
    {
        var pref = AndroidStorage.GetTheme();
        // Default to Light to match desktop.
        RequestedThemeVariant = pref switch
        {
            "dark" => ThemeVariant.Dark,
            "system" => ThemeVariant.Default,
            _ => ThemeVariant.Light,
        };
    }

    // ── Token helpers ───────────────────────────────────────────────────

    // v3.0 Phase 8.2 (2026-05-07) — most brushes in BuildSimplePageView
    // now ride BindToken (DynamicResource) so theme switches auto-repaint.
    // The helper below remains for the AndroidApp.SubscribePage /
    // AndroidApp.FreeConfigs partials (AND-1 / AND-3 ports) — they were
    // merged before Phase 8.2 landed and snapshot the brushes at build
    // time. Migrating those call sites to BindToken is a follow-up; for
    // now this keeps them building. GetRadius stays unchanged because
    // radii are theme-invariant.
    private IBrush GetBrush(string key)
    {
        if (Resources.TryGetResource(key, ActualThemeVariant, out var v) && v is IBrush b)
            return b;
        return Brushes.Transparent;
    }

    private double GetRadius(string key)
    {
        if (Resources.TryGetResource(key, ActualThemeVariant, out var v))
        {
            return v switch
            {
                double d => d,
                int i => i,
                _ => 8.0
            };
        }
        return 8.0;
    }

    /// <summary>
    /// SimplePage-equivalent view, code-behind. Mirrors
    /// VPNRouter.App/Views/Pages/SimplePage.axaml section-by-section.
    /// </summary>
    private Control BuildSimplePageView()
    {
        // v3.0 Phase 8.2 (2026-05-07) — every Background / Foreground /
        // BorderBrush / Fill below goes through BindToken (DynamicResource)
        // so theme switches auto-repaint the visual tree. Cached brush
        // locals from pre-8.2 are gone; only the radii (theme-invariant)
        // stay as locals.
        var radiusXs = GetRadius("RadiusXs");
        var radiusSm = GetRadius("RadiusSm");
        var radiusMd = GetRadius("RadiusMd");

        // ── Sub-header (mascot + brand + chips + kebab menu) ────────────
        // v3.0 Phase 4 (2026-05-04) — desktop parity. Pre-4 had a plain
        // "VPNRouter" title with a "RU" toggle pill at right. Desktop
        // shows: mascot 🐧 + "Virtual Penguin Network" bold + three
        // status chips (VPN / Zapret / TG) + ⋯ kebab menu. The kebab
        // hosts language + theme toggles (was inline RU pill).

        // v3.0 Phase 5 — real PNG mascot with theme-aware RGB inversion.
        // Mirrors desktop's MainWindowViewModel.LogoSource pattern:
        //   - Light theme: penguin_mascot.png as-is (black lineart on
        //     transparent bg)
        //   - Dark theme: RGB-inverted copy (white lineart on transparent)
        // Inversion preserves alpha so anti-aliased edges stay clean.
        // v3.0 Phase 8.2 — store on field so ApplyTheme(string) can call
        // _mascotImage.Source = LoadMascot() to switch between original
        // and RGB-inverted bitmap variants.
        _mascotImage = new Image
        {
            Source = LoadMascot(),
            Stretch = Stretch.Uniform,
            Width = 26,
            Height = 26,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapInterpolationMode(_mascotImage, BitmapInterpolationMode.HighQuality);
        var mascot = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = true,
            Child = _mascotImage,
        };
        mascot.BindToken(Border.BackgroundProperty, "AccentBgSubtleBrush");

        // v2.32.0 parity port (2026-05-09) — brand title FontSize 14 → 12 to
        // match desktop SimplePage.axaml line 60. Pre-port the heavier 14pt
        // size made the title visually compete with the status card title
        // (15 Bold), pushing the chip row off the same vertical rhythm as
        // desktop. 12 Bold matches desktop exactly.
        _brandTitle = new TextBlock
        {
            Text = Localization.BrandTitle,
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _brandTitle.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        // v3.0 Phase 7.1 — start all chips in Off state. VPN chip transitions
        // through Connecting → On as the tunnel comes up.
        // v3.0 Phase 8.2 — chips ride DynamicResource via MakeChip's key
        // parameters so they auto-repaint on theme variant change.
        //
        // 2026-05-15 (Bug-AND-002 brat live-test): hide Zapret + TG chips
        // entirely on Android. Pre-fix: chips were always rendered Off
        // because «those features aren't ported yet». User feedback:
        // «не нужно отображать zapret и tg прокси так как из нет, условно
        // ведь на мак мы их не отображет». Same rationale as Mac/Linux —
        // platform-not-applicable features should be hidden, not shown
        // as perpetually-Off. The _zapretChip / _tgChip fields are kept
        // (still touched by some legacy update paths) but excluded from
        // the visual chip row.
        _vpnChip = MakeChip("VPN", "SurfaceSunkenBrush", "TextMutedBrush");
        _zapretChip = MakeChip("Zapret", "SurfaceSunkenBrush", "TextMutedBrush");
        _tgChip = MakeChip("TG", "SurfaceSunkenBrush", "TextMutedBrush");

        var chipRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _vpnChip }
        };

        var brandStack = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _brandTitle, chipRow }
        };

        // ⋮ kebab menu trigger (vertical ellipsis — `⋯` horizontal
        // doesn't render correctly on Android default fonts)
        _kebabMenuButton = new Avalonia.Controls.Button
        {
            Content = "⋮",
            FontSize = 22,
            FontWeight = FontWeight.Bold,
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _kebabMenuButton.BindToken(Avalonia.Controls.Button.ForegroundProperty, "TextSecondaryBrush");
        _kebabMenuButton.Click += OnKebabMenuClicked;

        // v3.0 Phase 7.2 + 7.3 (2026-05-04) — full kebab menu with 4 sections
        // mirroring desktop's MainWindow.axaml ContextMenu (lines 414-512).
        //   • Вид           — Theme segmented (Light|Dark) + Language segmented (RU|EN)
        //   • Диагностика   — Open log / Copy log path / Update check
        //   • Устранение    — Reset settings (with confirm step)
        //   • О приложении  — Version + GitHub repo link
        // 7.3 swap (user-flagged "toogle на android отличаеться от pc"):
        // single-toggle buttons → 2-segment grids that SET a value
        // directly (idempotent — clicking the active segment is a no-op).

        // Theme segmented row: Light | Dark
        var isDark = AndroidStorage.GetTheme() == "dark";
        _menuThemeLight = MakeSegmentButton(Localization.MenuSegLight, !isDark, OnMenuThemeLightClicked);
        _menuThemeDark  = MakeSegmentButton(Localization.MenuSegDark,   isDark,  OnMenuThemeDarkClicked);
        var themeRow = MakeSegmentRow(_menuThemeLight, _menuThemeDark);

        // Language segmented row: RU | EN
        _menuLangRu = MakeSegmentButton(Localization.MenuSegRu, Localization.Ru, OnMenuLangRuClicked);
        _menuLangEn = MakeSegmentButton(Localization.MenuSegEn, !Localization.Ru, OnMenuLangEnClicked);
        var langRow = MakeSegmentRow(_menuLangRu, _menuLangEn);

        // Diagnostics + Troubleshooting + About items stay as full-width
        // labelled buttons. v2.32.0 desktop parity (2026-05-10): kebab is
        // 7 items split into View / Diagnostics(3) / Troubleshooting(3) /
        // About + Advanced toggle. Settings, Copy log path, View crash log,
        // Export/Import config, Profiles, Free Configs, Tools and DPI
        // bypass were post-v2.32.0 additions on Android — removed here so
        // Android matches desktop's compact kebab. Their content is still
        // reachable: Settings/network options live in Advanced > Network
        // tab, custom-config import in Advanced > Subscriptions, profiles
        // and DPI/Tools as Advanced shell tabs.
        _menuSettingsItem = null;
        _menuOpenLogItem  = MakeMenuItem(Localization.MenuItemOpenLogs,
                                         "TextPrimaryBrush", OnMenuOpenLogClicked);
        _menuCopyLogPathItem = null;
        _menuViewCrashLogItem = null;
        _menuUpdateCheckItem = MakeMenuItem(Localization.MenuItemUpdateCheck,
                                            "TextPrimaryBrush", OnMenuUpdateCheckClicked);
        _menuExportConfigItem = null;
        _menuImportConfigItem = null;
        _menuResetSettingsItem = MakeMenuItem(Localization.MenuItemResetSettings,
                                              "DangerSolidBrush", OnMenuResetSettingsClicked);
        _menuCheckLeaksItem = MakeMenuItem(Localization.MenuItemCheckLeaks,
                                           "TextPrimaryBrush", OnMenuCheckLeaksClicked);
        _menuHealthCheckItem = MakeMenuItem(Localization.MenuItemHealthCheck,
                                            "TextPrimaryBrush", OnMenuHealthCheckClicked);
        _menuRestartSafeModeItem = MakeMenuItem(Localization.MenuItemSafeMode,
                                                "TextPrimaryBrush", OnMenuRestartSafeModeClicked);
        // F-12 kebab visual parity (2026-05-09): About row mirrors desktop's
        // single-row "О приложении · v2.X.Y" with the version rendered as a
        // mono-font pill on the right (TextMutedBrush) — see MainWindow.axaml
        // line 623-635 (Button Classes="menu-item" wrapping Grid */Auto). On
        // desktop the row opens AboutWindow; on Android there is no AboutWindow,
        // so the row's tap target opens the GitHub repo (the same destination
        // the standalone "GitHub repository" row used to point at). _menuRepoItem
        // is gone — its function folds into the new About row.
        _menuAboutLabel = new TextBlock
        {
            Text = Localization.SmpMenuAbout,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _menuAboutLabel.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        _menuVersionPill = new TextBlock
        {
            Text = VPNRouter.Core.AppVersion.Version,
            FontSize = 9,
            FontFamily = new FontFamily("Consolas, 'SF Mono', 'Cascadia Code', 'Ubuntu Mono', monospace"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _menuVersionPill.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");
        var aboutGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 16,
        };
        Grid.SetColumn(_menuAboutLabel, 0);
        Grid.SetColumn(_menuVersionPill, 1);
        aboutGrid.Children.Add(_menuAboutLabel);
        aboutGrid.Children.Add(_menuVersionPill);
        _menuVersionItem = new Avalonia.Controls.Button
        {
            Content = aboutGrid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(10, 7),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
        };
        _menuVersionItem.Click += OnMenuRepoClicked;
        // _menuRepoItem retired: combined into the About row above.
        _menuRepoItem = null;

        var menuStack = new StackPanel
        {
            Spacing = 1,
        };

        // v2.32.0 desktop parity (2026-05-10): Free Configs / Profiles /
        // Tools / DPI bypass kebab entries removed — none of these exist
        // in v2.32.0 desktop's kebab. They're reachable as Advanced shell
        // tabs (Public configs / DPI bypass / Telegram). Stubs stay null
        // so RefreshKebabLocalizedStrings's null-checks compile.
        _menuFreeConfigsItem = null;
        _menuProfilesItem = null;
        _menuToolsItem     = null;
        _menuDpiBypassItem = null;

        AppendMenuSectionWithControls(menuStack, Localization.MenuSectionView,
                                      new Control[] { themeRow, langRow });
        // v2.32.0 desktop parity (2026-05-10): Diagnostics = Open log +
        // Check IP leak + Check for updates (3 items, matches MainWindow.axaml
        // line 506-523). Other items previously here (Settings, Copy log
        // path, View crash log, Export/Import config) were post-v2.32.0
        // additions and are removed.
        AppendMenuSection(menuStack, Localization.MenuSectionDiagnostics,
                          new[] { _menuOpenLogItem, _menuCheckLeaksItem,
                                  _menuUpdateCheckItem });
        // v2.32.0 desktop parity (2026-05-10): Troubleshooting = Run Health
        // Check + Restart in Safe Mode + Reset settings (3 items, matches
        // MainWindow.axaml line 531-548). Run Health Check moves back here
        // from Diagnostics to match desktop ordering.
        AppendMenuSection(menuStack, Localization.MenuSectionTroubleshooting,
                          new[] { _menuHealthCheckItem, _menuRestartSafeModeItem,
                                  _menuResetSettingsItem });
        // F-12 kebab visual parity (2026-05-09): About row sits inline at
        // the very bottom — no section header (matches desktop, which has
        // a divider then a single "О приложении · v2.X.Y" Button row, no
        // section label). The trailing divider appended by the previous
        // AppendMenuSection call serves as the visual separator above.
        menuStack.Children.Add(_menuVersionItem);

        // v2.32.0 desktop parity (2026-05-10): bottom-row "Advanced ▸"
        // primary CTA mirrors MainWindow.axaml line 573-575 — accent-solid
        // pill that toggles into the Advanced shell. The Simple page also
        // has an "Advanced settings ▸" card; both routes are intentional
        // (kebab shortcut + dedicated card).
        var advancedToggleBtn = new Avalonia.Controls.Button
        {
            Content = Localization.SmpToggleToAdvanced,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0, 8),
            Margin = new Thickness(0, 6, 0, 0),
            FontSize = 11,
            // Bug-AND-009 follow-up — store on field so the language
            // toggle refresh can re-stamp Content with the localized
            // string (otherwise the EN/RU label drifts after toggle).
            FontWeight = FontWeight.SemiBold,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
        };
        advancedToggleBtn.BindToken(Avalonia.Controls.Button.BackgroundProperty, "AccentSolidBrush");
        advancedToggleBtn.BindToken(Avalonia.Controls.Button.ForegroundProperty, "AccentOnSolidBrush");
        advancedToggleBtn.Click += (_, _) =>
        {
            if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
            OpenAdvancedShell(AdvancedTab.Servers);
        };
        _menuAdvancedToggleBtn = advancedToggleBtn;
        menuStack.Children.Add(advancedToggleBtn);

        // F-12 kebab visual parity (2026-05-09): container now matches desktop
        // MainWindow.axaml line 465 verbatim — Width=232, BorderDefault 1px,
        // CornerRadius=RadiusMd, Padding=6. BoxShadow drives off the ShadowMd
        // theme token (BindToken so light/dark switches re-resolve). Pre-fix
        // values (radiusSm=6, padding="0,4", hand-rolled shadow Color.FromArgb)
        // produced a flatter, less-elevated card that read as a sunken inset
        // rather than a popover.
        var menuPanel = new Border
        {
            Width = 232,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(GetRadius("RadiusMd")),
            Padding = new Thickness(6),
            Child = menuStack,
        };
        menuPanel.BindToken(Border.BackgroundProperty, "SurfaceBaseBrush");
        menuPanel.BindToken(Border.BorderBrushProperty, "BorderDefaultBrush");
        menuPanel.BindToken(Border.BoxShadowProperty, "ShadowMd");

        _kebabPopup = new Popup
        {
            PlacementTarget = _kebabMenuButton,
            Placement = PlacementMode.BottomEdgeAlignedRight,
            Child = menuPanel,
            IsLightDismissEnabled = true,
        };

        // DEFCT-001 (2026-05-10) — Avalonia 11.3.12 ToggleNodeInfoProvider
        // crashes on PopulateNodeInfo (s_checkedProperty.SetValue(this, ...)
        // bug, target type mismatch — should pass `nodeInfo`, not `this`).
        // Tapping the kebab triggers Android's a11y traversal of the popup
        // subtree even with no a11y service enabled, which trips the bug
        // and aborts the app with System.Reflection.TargetException. Marking
        // the entire popup subtree AccessibilityView=Raw drops every
        // descendant out of the Control/Content automation views so the
        // toggle peer never gets enumerated. Trade-off: TalkBack cannot
        // navigate the kebab — acceptable as Phase 1 because the Simple
        // page already has an "Advanced settings ▸" card as a parallel
        // entry point. Deeper fix (peer attribution audit + Avalonia
        // upstream PR) tracked separately.
        HideSubtreeFromAccessibility(menuPanel);

        // v2.32.0 parity port (2026-05-09) — drop the (16, 12, 16, 4)
        // Margin so the brand row composes cleanly inside the centered
        // 420 Grid (mirrors desktop SimplePage.axaml line 46 — the
        // mini-header has no outer margin; the ScrollViewer Padding +
        // outerGrid Margin handle horizontal + vertical insets). Column
        // 2 keeps the kebab — Android-only, since desktop's kebab lives
        // in MainWindow chrome and is hidden behind the IsSimpleMode
        // brand-row override.
        var headerRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("28,*,Auto"),
            ColumnSpacing = 10,
        };
        Grid.SetColumn(mascot, 0);
        Grid.SetColumn(brandStack, 1);
        Grid.SetColumn(_kebabMenuButton, 2);
        headerRow.Children.Add(mascot);
        headerRow.Children.Add(brandStack);
        headerRow.Children.Add(_kebabMenuButton);
        headerRow.Children.Add(_kebabPopup);

        // ── Status card (dot + title + description) ─────────────────────
        // v3.0 Phase G step 1 (2026-05-09): visual treatment moved into
        // VPNRouter.UI.Controls.StatusCard so desktop + Android render
        // the same Border / dot / typography from one .axaml. The card
        // itself only owns the dot + title + subtitle. Diagnostic chips
        // (_statusHealthCheck, _statusErrorOneLiner) live alongside the
        // card — wrapped in a small StackPanel so they appear visually
        // adjacent in the parent scroller. Pre-G they were nested inside
        // the bordered card; the new arrangement is slightly looser
        // visually but only manifests when AND-DIAG probes / errors
        // surface (chips default IsVisible=false).
        _statusCard = new StatusCard
        {
            IsOff = true,
            IsOn = false,
            IsWarn = false,
            Title = Localization.SimpleStatusTitleOff,
            Subtitle = Localization.SimpleStatusDescOff,
        };

        // v2.32.0 (AND-DIAG) — health-check chip beneath the card. Hidden
        // until first probe runs (which is ~30 s post-connect; the
        // pending text fills the gap). 20 px indent so the column lines
        // up with the StatusCard's title text rather than the dot.
        // (StatusCard UserControl from VPNRouter.UI shared lib renders the
        // title + subtitle internally — old inline _statusTitle / _statusDesc
        // builders removed in foundation merge.)
        _statusHealthCheck = new TextBlock
        {
            Text = string.Empty,
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(20, 0, 0, 0),
            LineHeight = 14,
            IsVisible = false,
        };
        _statusHealthCheck.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");

        // v2.32.0 (AND-DIAG) — error one-liner. Surfaced when an
        // ACTION_TUNNEL_ERROR arrives, persists 30 s. Red-ish foreground
        // via DangerFgBrush so the user sees it on a glance even before
        // reading the message.
        _statusErrorOneLiner = new TextBlock
        {
            Text = string.Empty,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(20, 0, 0, 0),
            LineHeight = 14,
            IsVisible = false,
        };
        _statusErrorOneLiner.BindToken(TextBlock.ForegroundProperty, "DangerFgBrush");

        // Group the card + diagnostic chips so they ride innerStack as
        // one unit. 6 px internal spacing matches the pre-G in-card stack.
        var statusCard = new StackPanel
        {
            Spacing = 6,
            Children = { _statusCard, _statusHealthCheck, _statusErrorOneLiner },
        };

// (statusCard already declared above as wrapper StackPanel containing
        // _statusCard UserControl + _statusHealthCheck + _statusErrorOneLiner.)
        // ── Config row button (tappable, expands form) ──────────────────
        var flagGlyph = new TextBlock
        {
            Text = "⚑",
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        flagGlyph.BindToken(TextBlock.ForegroundProperty, "AccentFgBrush");
        var flagIcon = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(radiusXs),
            VerticalAlignment = VerticalAlignment.Center,
            Child = flagGlyph,
        };
        flagIcon.BindToken(Border.BackgroundProperty, "AccentBgSubtleBrush");

        _configRowLabel = new TextBlock
        {
            Text = Localization.SmpConfigRowLabel,
            FontSize = 9,
            FontWeight = FontWeight.SemiBold,
        };
        _configRowLabel.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");

        _configRowValue = new TextBlock
        {
            Text = Localization.SimpleConfigSummary,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            FontFamily = new FontFamily("monospace"),
        };
        _configRowValue.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        _configRowChevron = new TextBlock
        {
            // v3.0 Phase 7.3 — initial glyph follows _formExpanded so the
            // chevron points down when the form is auto-expanded on
            // first launch (mirrors OnConfigRowClicked's flip logic).
            // v2.32.0 parity port (2026-05-09) — chevron FontSize 14 → 13 to
            // match desktop SimplePage.axaml line 218 (FontSize="13"). The
            // 14pt size made the chevron heavier than the surrounding 11pt
            // value text; 13pt sits flush with the value baseline.
            Text = _formExpanded ? "⌄" : "›",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _configRowChevron.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");

        var configRowGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 10,
            Margin = new Thickness(12, 8),
        };
        Grid.SetColumn(flagIcon, 0);
        configRowGrid.Children.Add(flagIcon);
        var configRowText = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _configRowLabel, _configRowValue }
        };
        Grid.SetColumn(configRowText, 1);
        configRowGrid.Children.Add(configRowText);
        Grid.SetColumn(_configRowChevron, 2);
        configRowGrid.Children.Add(_configRowChevron);

        var configRowButton = new Avalonia.Controls.Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(radiusSm),
            Content = configRowGrid,
        };
        configRowButton.BindToken(Avalonia.Controls.Button.BackgroundProperty, "SurfaceRaisedBrush");
        configRowButton.BindToken(Avalonia.Controls.Button.BorderBrushProperty, "BorderSubtleBrush");
        configRowButton.Click += OnConfigRowClicked;

        // ── Collapsible form (input + tunnel mode radios + autostart) ───

        // Slim to v2.32.0 desktop parity (2026-05-10): label · TextBox ·
        // hint · error. No Save / QR / Refresh action row, no auto-detect
        // "Detected: …" line, no Save/Refresh confirmation toast. Desktop
        // v2.32.0 SimplePage commits the typed input implicitly on Connect
        // (see SmpToggleConnectAsync). Save / Refresh on subscriptions live
        // in Advanced > Subscriptions tab; the QR camera flow stays in code
        // (MainActivity / QrCodeDecoder) but no longer has a button entry.
        //
        // _ccMode is still loaded from storage so the implicit-save flow on
        // Connect (OnSaveClicked) keeps its three-way switch
        // (subscribe / manual / custom). Custom JSON edit remains reachable
        // via Advanced > Subscriptions tab.
        _ccMode = AndroidStorage.GetConfigMode();
        if (_ccMode != "subscribe" && _ccMode != "manual" && _ccMode != "custom")
            _ccMode = "manual";

        _serverInputLabel = new TextBlock
        {
            Text = Localization.SmpInputLabel,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
        };
        _serverInputLabel.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        _serverInput = new TextBox
        {
            FontSize = 11,
            Padding = new Thickness(10, 7),
            AcceptsReturn = false,
            CornerRadius = new CornerRadius(radiusXs),
            Watermark = Localization.SmpInputWatermark,
        };
        var existingSub = AndroidStorage.GetSubscriptionUrl();
        var existingUri = AndroidStorage.GetVlessUri();
        _serverInput.Text = existingSub ?? existingUri ?? string.Empty;

        _serverInputHint = new TextBlock
        {
            Text = Localization.SmpInputHint,
            FontSize = 9,
            TextWrapping = TextWrapping.Wrap,
        };
        _serverInputHint.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");

        _serverInputError = new TextBlock
        {
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };
        _serverInputError.BindToken(TextBlock.ForegroundProperty, "DangerFgBrush");

        var inputSection = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                _serverInputLabel,
                _serverInput,
                _serverInputHint,
                _serverInputError,
            },
        };

        // Tunnel mode (split / full)
        _tunnelModeLabel = new TextBlock
        {
            Text = Localization.SmpTunnelModeLabel,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
        };
        _tunnelModeLabel.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        _splitLabel = new TextBlock
        {
            Text = Localization.SmpSplitOption,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _splitLabel.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        _splitRadio = new Avalonia.Controls.RadioButton
        {
            GroupName = "TunnelMode",
            // v3.0 Phase 7.5 — radio state seeded from stored per-app
            // mode. v2.32.0 expanded: any non-"off" mode (include OR
            // exclude) keeps split selected; the picker overlay refines
            // include vs exclude inside the split branch.
            IsChecked = AndroidStorage.GetPerAppMode() != "off",
            Content = _splitLabel,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            MinHeight = 0,
            Padding = new Thickness(4, 0),
        };
        _splitRadio.IsCheckedChanged += OnTunnelModeRadioChanged;
        _splitHint = new TextBlock
        {
            Text = Localization.SmpSplitHint,
            FontSize = 9,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24, 0, 0, 0),
        };
        _splitHint.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");
        _fullLabel = new TextBlock
        {
            Text = Localization.SmpFullOption,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _fullLabel.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        _fullRadio = new Avalonia.Controls.RadioButton
        {
            GroupName = "TunnelMode",
            // v3.0 Phase 7.5 — full mode = mode == "off".
            // v2.32.0: was `!= "include"` which silently selected full when
            // mode was "exclude" — wrong, exclude is still split.
            IsChecked = AndroidStorage.GetPerAppMode() == "off",
            Content = _fullLabel,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            MinHeight = 0,
            Padding = new Thickness(4, 0),
        };
        _fullRadio.IsCheckedChanged += OnTunnelModeRadioChanged;
        _fullHint = new TextBlock
        {
            Text = Localization.SmpFullHint,
            FontSize = 9,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24, 0, 0, 0),
        };
        _fullHint.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");

        // v3.0 Phase 7.5 — "Choose apps…" button + selection counter
        // pair, only visible when "Selected apps" radio is checked.
        // Tap → opens the app picker overlay defined later in this file.
        _perAppPickButton = StyledSecondaryButton(Localization.PerAppPickButton);
        _perAppPickButton.Click += OnPerAppPickButtonClicked;
        _perAppPickButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        _perAppPickButton.Margin = new Thickness(24, 4, 0, 0);

        var initialPerAppCount = AndroidStorage.GetPerAppPackages().Count;
        var initialMode = AndroidStorage.GetPerAppMode();
        var initialCountFmt = initialMode == "exclude"
            ? Localization.PerAppCountExclude
            : Localization.PerAppCountInclude;
        _perAppCountLabel = new TextBlock
        {
            Text = string.Format(initialCountFmt, initialPerAppCount),
            FontSize = 9,
            Margin = new Thickness(24, 2, 0, 0),
        };
        _perAppCountLabel.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");

        var perAppStack = new StackPanel
        {
            Spacing = 0,
            // v2.32.0: visible whenever split is on (mode != off), not just
            // include. Exclude mode also needs the "Choose apps…" button.
            IsVisible = AndroidStorage.GetPerAppMode() != "off",
            Children = { _perAppPickButton, _perAppCountLabel },
        };
        // Tag the stack so OnTunnelModeRadioChanged can flip its
        // visibility — using Tag avoids storing yet another field.
        _splitRadio.Tag = perAppStack;

        var tunnelSection = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                _tunnelModeLabel,
                new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new StackPanel { Spacing = 1, Children = { _splitRadio, _splitHint, perAppStack } },
                        new StackPanel { Spacing = 1, Children = { _fullRadio, _fullHint } },
                    }
                }
            }
        };

        // Subscription server list (only visible when subscription has servers)
        _serverListHeader = new TextBlock
        {
            Text = Localization.AvailableServers,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            IsVisible = false,
        };
        _serverListHeader.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        _serverList = new ListBox
        {
            MaxHeight = 240,
            IsVisible = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };
        _serverList.SelectionChanged += OnServerSelectionChanged;

        var listSection = new StackPanel
        {
            Spacing = 4,
            Children = { _serverListHeader, _serverList }
        };

        // v2.32.0 parity port (2026-05-09) — autostart card moves INSIDE
        // the form Border, mirroring desktop SimplePage.axaml lines 338-362
        // (the autostart Button is the last child of the collapsible form
        // StackPanel, not a standalone row outside it). Pre-port Android
        // had it between the CTA and the Advanced settings card on the
        // outer scroll which made the form feel hollow and the autostart
        // entry feel orphaned. Inside the form it composes with input +
        // tunnel sections as one logical group.
        var autostartCard = BuildAutostartInlineCard(radiusSm);

        _formCard = new Border
        {
            IsVisible = _formExpanded,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(radiusSm),
            // Bug-AND-010 (2026-05-16) — 5" small-phone audit. Padding
            // 12→10 and Spacing 14→11 to bring the input + tunnel +
            // autostart trio closer together. Saves ~12 dp vertical so
            // Connect button stays visible above the system nav bar on
            // a 5" 720p phone without scrolling.
            Padding = new Thickness(10),
            Child = new StackPanel
            {
                Spacing = 11,
                // listSection is Android-only (subscription server picker);
                // sits after the autostart card so the form still ends
                // with a clean rhythm — input → tunnel → autostart on
                // desktop · plus → list on Android. Server list stays
                // hidden until a subscription has been refreshed, so the
                // visible default matches desktop exactly.
                Children = { inputSection, tunnelSection, autostartCard, listSection }
            }
        };
        _formCard.BindToken(Border.BackgroundProperty, "SurfaceBaseBrush");
        _formCard.BindToken(Border.BorderBrushProperty, "BorderSubtleBrush");

        // ── CTA — three mutually exclusive variants ─────────────────────
        // Disconnected (default visible): outlined accent
        _ctaConnect = new Avalonia.Controls.Button
        {
            Content = Localization.ButtonConnect,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            // Bug-AND-010 — Connect/Connecting/Disconnect CTA pad trim
            // (12→10 vertical) shaves 4 dp per CTA. Still meets the 44 dp
            // Material touch target (12px font + 10*2 padding = 32 dp,
            // plus the implicit MinHeight=44 inherited from style).
            Padding = new Thickness(0, 10),
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(radiusSm),
            IsVisible = true,
        };
        _ctaConnect.BindToken(Avalonia.Controls.Button.BackgroundProperty, "SurfaceBaseBrush");
        _ctaConnect.BindToken(Avalonia.Controls.Button.ForegroundProperty, "AccentFgBrush");
        _ctaConnect.BindToken(Avalonia.Controls.Button.BorderBrushProperty, "AccentBorderBrush");
        _ctaConnect.Click += OnConnectClicked;

        // Connecting: sunken disabled
        _ctaConnecting = new Avalonia.Controls.Button
        {
            Content = Localization.ButtonConnecting,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            // Bug-AND-010 — Connect/Connecting/Disconnect CTA pad trim
            // (12→10 vertical) shaves 4 dp per CTA. Still meets the 44 dp
            // Material touch target (12px font + 10*2 padding = 32 dp,
            // plus the implicit MinHeight=44 inherited from style).
            Padding = new Thickness(0, 10),
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(radiusSm),
            IsEnabled = false,
            IsVisible = false,
        };
        _ctaConnecting.BindToken(Avalonia.Controls.Button.BackgroundProperty, "SurfaceSunkenBrush");
        _ctaConnecting.BindToken(Avalonia.Controls.Button.ForegroundProperty, "TextSecondaryBrush");

        // Connected: accent solid (bg blue, text white) — per design NOT red
        _ctaDisconnect = new Avalonia.Controls.Button
        {
            Content = Localization.ButtonDisconnect,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            // Bug-AND-010 — Connect/Connecting/Disconnect CTA pad trim
            // (12→10 vertical) shaves 4 dp per CTA. Still meets the 44 dp
            // Material touch target (12px font + 10*2 padding = 32 dp,
            // plus the implicit MinHeight=44 inherited from style).
            Padding = new Thickness(0, 10),
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(radiusSm),
            IsVisible = false,
        };
        _ctaDisconnect.BindToken(Avalonia.Controls.Button.BackgroundProperty, "AccentSolidBrush");
        _ctaDisconnect.BindToken(Avalonia.Controls.Button.ForegroundProperty, "AccentOnSolidBrush");
        _ctaDisconnect.Click += OnConnectClicked;

        // ── Расширенные настройки card (placeholder navigation) ─────────
        _advCardTitle = new TextBlock
        {
            Text = Localization.SmpAdvCardTitle,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
        };
        _advCardTitle.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        _advCardSubtitle = new TextBlock
        {
            Text = Localization.SmpAdvCardSubtitle,
            FontSize = 9,
            TextWrapping = TextWrapping.Wrap,
        };
        _advCardSubtitle.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");
        var chevronGlyph = new TextBlock
        {
            Text = "›",
            FontSize = 15,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        chevronGlyph.BindToken(TextBlock.ForegroundProperty, "AccentFgBrush");
        var chevronCircle = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(radiusSm),
            VerticalAlignment = VerticalAlignment.Center,
            Child = chevronGlyph,
        };
        chevronCircle.BindToken(Border.BackgroundProperty, "AccentBgSubtleBrush");
        var advGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 10,
            Margin = new Thickness(14, 12),
        };
        var advText = new StackPanel
        {
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _advCardTitle, _advCardSubtitle }
        };
        Grid.SetColumn(advText, 0);
        advGrid.Children.Add(advText);
        Grid.SetColumn(chevronCircle, 1);
        advGrid.Children.Add(chevronCircle);
        var advCardButton = new Avalonia.Controls.Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(radiusMd),
            Content = advGrid,
        };
        advCardButton.BindToken(Avalonia.Controls.Button.BackgroundProperty, "SurfaceBaseBrush");
        advCardButton.BindToken(Avalonia.Controls.Button.BorderBrushProperty, "BorderDefaultBrush");
        advCardButton.Click += OnAdvCardClicked;

        // v3.0 Phase 7.2 — transient feedback banner that surfaces the
        // result of kebab-menu actions (log path copied, settings reset,
        // update placeholder). Hidden by default; ShowMenuFeedback shows
        // for ~3 s then hides.
        _menuFeedback = new TextBlock
        {
            Text = string.Empty,
            FontSize = 11,
            Padding = new Thickness(12, 8),
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };
        _menuFeedback.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");
        _menuFeedback.BindToken(TextBlock.BackgroundProperty, "SurfaceSunkenBrush");

        // v2.32.0 (2026-05-07) — auto-update banner card.
        // Style mirrors desktop's UpdateNotification card layout:
        //   AccentBgSubtle background + AccentBorder + RadiusMd, title
        //   (semibold) + subtitle (muted) + 2-button row (action +
        //   dismiss). Hidden by default; surfaced by
        //   PromptUpdateAvailable(info) once CheckAsync returns a hit.
        BuildUpdateBanner(radiusMd);

        // ── Inner stack with all sections, max 420 wide on tablets ──────
        // v2.32.0 parity (2026-05-10): composition mirrors desktop
        // SimplePage.axaml line 33 (StackPanel Spacing="14" inside the
        // 420-wide centered Grid). Header row moves INSIDE the centered
        // grid so the brand row scrolls with the rest of the content.
        // No Save/Refresh toast — desktop v2.32.0 has none.
        var innerStack = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                headerRow,
                statusCard,
                _menuFeedback,
                _updateBanner!,
                configRowButton,
                _formCard,
                _ctaConnect,
                _ctaConnecting,
                _ctaDisconnect,
                advCardButton,
            }
        };

        var innerWrapper = new Grid
        {
            MaxWidth = 420,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { innerStack }
        };

        // DEFCT-002 (2026-05-10) — Background=Transparent on the wrapping
        // grid so bare gutters between the centered 420 dp content column
        // and the ScrollViewer edges are hit-test visible. Without this,
        // hits on the gutters land on whichever opaque element ends up
        // beneath, instead of the ScrollViewer; the direct pointer-event
        // handlers attached on mainScroller below need consistent gutter
        // hit-testing to start a swipe from anywhere in the row.
        var outerGrid = new Grid
        {
            Margin = new Thickness(16, 0, 16, 0),
            Background = Brushes.Transparent,
            Children = { innerWrapper }
        };

        // v2.32.0 parity port (2026-05-09) — ScrollViewer Padding 0,12,0,16
        // mirrors desktop SimplePage.axaml line 30 (Padding="0,12,0,16").
        // Pre-port the top padding lived on a separate headerRow Margin
        // (16,12,16,4) outside the scroll content; moving it onto the
        // ScrollViewer means the brand row now starts 12 px below the
        // status bar instead of stacking two top margins (16+12=28).
        // Removing the contentStack wrapper drops one redundant container
        // — outerGrid is now the direct ScrollViewer child.
        var mainScroller = new ScrollViewer
        {
            Content = outerGrid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 12, 0, 16),
            // Bug-AND-007b (2026-05-16) — explicitly Focusable so the
            // scroll-gesture handler can shift focus from a focused
            // TextBox to the scroller itself when a drag starts. By
            // default ScrollViewer's Focusable is false on Avalonia 11,
            // and calling .Focus() on a non-focusable control is a
            // no-op — which left the TextBox still focused after a
            // swipe, so the next tap couldn't re-pop the IME (Android
            // only shows the keyboard when focus *enters* the TextBox,
            // not when an already-focused one is re-tapped).
            Focusable = true,
        };
        mainScroller.BindToken(ScrollViewer.BackgroundProperty, "SurfaceAppBrush");

        // DEFCT-002 (2026-05-10) — direct pointer-event scroll tracking on
        // the outer ScrollViewer. The default ScrollViewer template's inner
        // ScrollGestureRecognizer is unreliable on Android when the user
        // starts a swipe on a child Button: the Button captures the pointer
        // on PointerPressed and the inner recognizer never wins the
        // threshold race (mirrors Avalonia issue #3146 "ListBox inside
        // ScrollViewer prevent touch scrolling"). Avalonia 11.3 on Android
        // does NOT route subsequent moves to ancestors via the Tunnel route
        // once a descendant captures (verified empirically: 1 move event
        // reaches the ancestor handler vs ~20 expected for an 800 ms swipe).
        //
        // Strategy: hook PointerPressed/Moved/Released on the ScrollViewer
        // with Tunnel routing + handledEventsToo so we observe pointer
        // motion before children handle it. Once the drag exceeds the
        // 8 dp threshold we call Pointer.Capture(mainScroller) to steal
        // pointer ownership from whatever child Button grabbed it. With
        // ownership transferred, every subsequent PointerMoved is delivered
        // directly to mainScroller and our handler fires on each frame,
        // producing 1:1 swipe-to-scroll tracking. Each move applies an
        // INCREMENTAL delta (current - last) to Offset, so partial event
        // delivery still produces proportional scroll. e.Handled=true on
        // each Move stops the inner gesture recognizer from clobbering us.
        const double scrollStartDistance = 8.0;
        bool isDragging = false;
        double dragStartY = 0;
        double lastY = 0;
        IPointer? activePointer = null;
        mainScroller.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            var p = e.GetCurrentPoint(mainScroller);
            if (p.Pointer.Type != PointerType.Touch && p.Pointer.Type != PointerType.Pen)
                return;
            isDragging = false;
            dragStartY = p.Position.Y;
            lastY = p.Position.Y;
            activePointer = e.Pointer;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        mainScroller.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
        {
            var p = e.GetCurrentPoint(mainScroller);
            if (p.Pointer.Type != PointerType.Touch && p.Pointer.Type != PointerType.Pen)
                return;
            if (activePointer is null) return;
            var totalDeltaY = p.Position.Y - dragStartY;
            if (!isDragging)
            {
                if (System.Math.Abs(totalDeltaY) < scrollStartDistance) return;
                isDragging = true;
                // Steal pointer capture from the child (Button) so
                // subsequent moves route directly to mainScroller.
                e.Pointer.Capture(mainScroller);
                // Bug-AND-007b (2026-05-16) — when the press landed on a
                // TextBox (e.g. _serverInput at the top of the Simple
                // page), Avalonia's TextBox focuses on PointerPressed
                // *before* the drag threshold trips. Result: the soft
                // keyboard popped up every time a swipe happened to
                // start inside the URL TextBox area, ruining the
                // scroll. Once we've decided this is a drag, blur the
                // currently focused TextBox AND ask Android's
                // InputMethodManager to hide the keyboard. Two-step
                // because Avalonia's Focus() alone doesn't tear down
                // the IME — Mono.Android's mainline `TextBox` peer
                // pre-emptively shows the keyboard the moment the
                // pointer presses, before our focus-shuffle runs.
                try
                {
                    var topLevel = global::Avalonia.Controls.TopLevel.GetTopLevel(mainScroller);
                    var focused = topLevel?.FocusManager?.GetFocusedElement();
                    if (focused is Avalonia.Controls.TextBox)
                    {
                        // Move focus away from the TextBox (Avalonia
                        // bookkeeping side).
                        mainScroller.Focus();
                        // And tell Android directly to hide the soft
                        // keyboard (IME side). Without this the IMM
                        // keeps the keyboard up because the show was
                        // already queued before we shuffled focus.
                        HideAndroidSoftKeyboard();
                    }
                }
                catch
                {
                    // Focus shuffle is best-effort — don't let a focus
                    // glitch break the swipe.
                }
            }
            var dy = p.Position.Y - lastY;
            lastY = p.Position.Y;
            var maxOffset = System.Math.Max(0,
                mainScroller.Extent.Height - mainScroller.Viewport.Height);
            var newY = System.Math.Max(0,
                System.Math.Min(mainScroller.Offset.Y - dy, maxOffset));
            mainScroller.Offset = new Vector(mainScroller.Offset.X, newY);
            e.Handled = true;
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        mainScroller.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            if (isDragging)
            {
                e.Pointer.Capture(null);
                e.Handled = true;
            }
            isDragging = false;
            activePointer = null;
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        mainScroller.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) =>
        {
            isDragging = false;
            activePointer = null;
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        // v3.0 Phase 7.4 (2026-05-04) — fullscreen log-viewer overlay
        // sits on top of the main content stack. Hidden by default; the
        // Diagnostics > "Open log" menu action reads singbox.log into
        // _logViewerContent and flips IsVisible=true.
        _logOverlay = BuildLogOverlay();

        // v2.32.0 (Android-led, 2026-05-07) — config share overlays
        // (export / import). Defined in AndroidApp.ConfigShare.cs.
        // Both are hidden by default, surfaced via kebab menu items.
        _cfgExportOverlay = BuildExportOverlay();
        _cfgImportOverlay = BuildImportOverlay();

        // v2.32.0 (AND-PROFILES, 2026-05-08) — fullscreen routing-profile
        // catalog overlay. Triggered from the Profiles section in the
        // kebab menu. Routing profiles stay reachable via kebab — they're
        // a quick switcher, not a feature page.
        _profilesOverlay = BuildProfilesOverlay();

        // AND-MIGRATE-OVERLAYS (2026-05-09) — Advanced shell. Single
        // overlay that hosts Servers / Subscribe / Settings / Applications /
        // Tools / Public as a tab strip (AND-ADV-CHROME 2026-05-10 renamed
        // tabs + merged DPI bypass+Telegram into Tools). Replaces the
        // kebab → feature-page pattern that diverged from desktop. Tab
        // content is built lazily on first activation; see
        // AndroidApp.AdvancedShell.cs.
        _advShellOverlay = BuildAdvancedShellOverlay();

        return new Grid
        {
            // Per-feature overlays folded into Advanced shell tabs (AND-ADV-MIGRATE).
            // Kebab QR-share also removed (chip naughty-darwin) — _cfgQrOverlay gone.
            Children = { mainScroller, _logOverlay,
                         _cfgExportOverlay, _cfgImportOverlay, _profilesOverlay,
                         _advShellOverlay }
        };
    }

    /// <summary>
    /// v3.0 Phase 7.4 (2026-05-04) — build the in-app log viewer overlay.
    /// Layout: top title bar (× close, refresh, "singbox.log" title) +
    /// a horizontally + vertically scrollable monospace TextBlock that
    /// renders the last ~50 KB of the log file. Closes the handbook §5.6
    /// gap (in-app logs viewer) so users can debug without adb.
    /// </summary>
    private Border BuildLogOverlay()
    {
        _logViewerTitle = new TextBlock
        {
            Text = "singbox.log",
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _logViewerTitle.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        _logViewerCloseBtn = new Avalonia.Controls.Button
        {
            Content = "✕",
            FontSize = 16,
            Width = 36,
            Height = 36,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };
        _logViewerCloseBtn.BindToken(Avalonia.Controls.Button.ForegroundProperty, "TextSecondaryBrush");
        _logViewerCloseBtn.Click += OnLogViewerCloseClicked;

        _logViewerRefreshBtn = new Avalonia.Controls.Button
        {
            Content = "⟳",
            FontSize = 16,
            Width = 36,
            Height = 36,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };
        _logViewerRefreshBtn.BindToken(Avalonia.Controls.Button.ForegroundProperty, "TextSecondaryBrush");
        _logViewerRefreshBtn.Click += OnLogViewerRefreshClicked;

        var titleBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(8, 4, 4, 4),
        };
        Grid.SetColumn(_logViewerTitle, 0);
        Grid.SetColumn(_logViewerRefreshBtn, 1);
        Grid.SetColumn(_logViewerCloseBtn, 2);
        _logViewerRefreshBtn.HorizontalAlignment = HorizontalAlignment.Right;
        titleBar.Children.Add(_logViewerTitle);
        titleBar.Children.Add(_logViewerRefreshBtn);
        titleBar.Children.Add(_logViewerCloseBtn);

        var titleBarBorder = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 4),
            Child = titleBar,
        };
        titleBarBorder.BindToken(Border.BackgroundProperty, "SurfaceRaisedBrush");
        titleBarBorder.BindToken(Border.BorderBrushProperty, "BorderSubtleBrush");

        _logViewerContent = new TextBlock
        {
            FontFamily = new FontFamily("monospace"),
            FontSize = 9,
            TextWrapping = TextWrapping.NoWrap,
            Padding = new Thickness(8),
        };
        _logViewerContent.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        _logViewerEmptyState = new TextBlock
        {
            FontSize = 12,
            Text = string.Empty,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(24),
            IsVisible = false,
        };
        _logViewerEmptyState.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");

        _logViewerScroller = new ScrollViewer
        {
            Content = _logViewerContent,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _logViewerScroller.BindToken(ScrollViewer.BackgroundProperty, "SurfaceAppBrush");

        var contentArea = new Grid
        {
            Children = { _logViewerScroller, _logViewerEmptyState }
        };

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(titleBarBorder, Dock.Top);
        dock.Children.Add(titleBarBorder);
        dock.Children.Add(contentArea);

        var overlay = new Border
        {
            IsVisible = false,
            Child = dock,
        };
        overlay.BindToken(Border.BackgroundProperty, "SurfaceAppBrush");
        return overlay;
    }

    // ── Settings tab body (Network tab inside the Advanced shell) ──────
    //
    // Phase C (2026-05-10) restructures this tab from a flat scrollable
    // stack to a master-detail layout with side-nav + per-sub-section
    // content pane + footer Apply bar — matching desktop NetworkPage's
    // shape. Pre-Phase-C the four sub-sections (Routing / Leak / Updates /
    // Autostart) were stacked in one ScrollViewer; AND-MIGRATE-OVERLAYS
    // (2026-05-09) had brought them into the Advanced shell as the
    // "Network" tab. The flat layout shipped fine functionally but
    // structurally diverged from desktop, so Phase C restores parity.

    /// <summary>
    /// Phase C (2026-05-10) — Settings tab body. Mirrors desktop
    /// NetworkPage.axaml's master-detail layout: a left side-nav listing
    /// the six sub-sections (Routing / Rules / Leak / Content / Updates /
    /// Autostart) + a right scrollable content pane swapped by the active
    /// sub-section, with a footer Apply bar carrying the "✓ Auto-saved"
    /// badge or the [Apply] button depending on whether there are pending
    /// changes that need a tunnel reload to take effect.
    ///
    /// <para>Index order matches desktop's <c>SelectedSettingsIndex</c>
    /// (NetworkPage.axaml:202-211 + MainWindowViewModel.IsSettings*Selected
    /// at line 1710-1715) so user muscle-memory carries between platforms.
    /// On Android the desktop's standalone Reliability section (Always-on
    /// VPN + battery + auto-reconnect) is folded into the Autostart sub-
    /// section per the parity plan's platform-impossible item table —
    /// Always-on VPN IS the Android replacement for Windows-Service-on-boot,
    /// so it naturally belongs there.</para>
    /// </summary>
    private Control BuildNetworkTabContent()
    {
        var sideNav = BuildSettingsSideNav();
        var contentPane = BuildSettingsContentPane();
        var footerBar = BuildSettingsFooterBar();

        // Master-detail Grid: two-column body row + full-width footer row.
        // Side-nav width matches desktop's 140 dp (NetworkPage.axaml:190
        // ColumnDefinitions="140,*"). On Android dp ≈ logical pixel for
        // Avalonia layout, so we use the same value.
        var body = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            ColumnDefinitions = new ColumnDefinitions("140,*"),
            Background = GetBrush("SurfaceAppBrush"),
        };
        Grid.SetRow(sideNav, 0);
        Grid.SetColumn(sideNav, 0);
        Grid.SetRow(contentPane, 0);
        Grid.SetColumn(contentPane, 1);
        Grid.SetRow(footerBar, 1);
        Grid.SetColumn(footerBar, 0);
        Grid.SetColumnSpan(footerBar, 2);
        body.Children.Add(sideNav);
        body.Children.Add(contentPane);
        body.Children.Add(footerBar);

        return body;
    }

    /// <summary>
    /// Left-column side-nav. Six button rows (Routing / Rules / Leak /
    /// Content / Updates / Autostart). Active row paints with
    /// <c>AccentBgSubtleBrush</c> + <c>AccentFgBrush</c> + a 2 dp left
    /// underline (vertical bar) to match desktop NetworkPage's ListBoxItem
    /// active style. Inactive rows use <c>TextMutedBrush</c>.
    /// </summary>
    private Border BuildSettingsSideNav()
    {
        var stack = new StackPanel
        {
            Spacing = 0,
            Margin = new Thickness(0, 6, 0, 6),
        };
        for (int i = 0; i < 6; i++)
        {
            var button = MakeSettingsSubSectionButton(i);
            _settingsSubSectionButtons[i] = button;
            stack.Children.Add(button);
        }

        var scroller = new ScrollViewer
        {
            Content = stack,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.Transparent,
        };

        return new Border
        {
            BorderBrush = GetBrush("BorderDefaultBrush"),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Background = GetBrush("SurfaceSunkenBrush"),
            Child = scroller,
        };
    }

    /// <summary>
    /// One side-nav row. Tap selects the sub-section + flips the content
    /// pane. Persists the choice via <see cref="AndroidStorage.SetSettingsActiveSubSection"/>
    /// so reopening Advanced > Settings restores the same pane.
    /// <para>POL-1: dropped the 2 dp left BorderThickness marker — desktop
    /// NetworkPage uses Avalonia's default ListBoxItem:selected styling
    /// (AccentBgSubtle bg + AccentFg fg, no left bar). The marker was an
    /// Android-only invention and made the side-nav read inconsistently
    /// vs Apps category list / Public sub-tabs which already match
    /// desktop's flat styling.</para>
    /// </summary>
    private Avalonia.Controls.Button MakeSettingsSubSectionButton(int index)
    {
        var btn = new Avalonia.Controls.Button
        {
            Content = SettingsSubSectionLabel(index),
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(10, 7),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
        };
        btn.Click += (_, _) => SelectSettingsSubSection(index);
        StyleSettingsSubSectionButton(btn, index == _settingsSelectedSubSection);
        return btn;
    }

    private static string SettingsSubSectionLabel(int index) => index switch
    {
        0 => Localization.SettingsSectionRouting,
        1 => Localization.SettingsSectionRules,
        2 => Localization.SettingsSectionLeak,
        3 => Localization.SettingsSectionContent,
        4 => Localization.SettingsSectionUpdates,
        5 => Localization.SettingsSectionAutostart,
        _ => string.Empty,
    };

    /// <summary>Active = AccentBgSubtle bg + AccentFg fg (matches desktop
    /// ListBoxItem:selected default); inactive = muted text, transparent bg.
    /// POL-1: BorderBrush no longer assigned — left bar dropped.</summary>
    private void StyleSettingsSubSectionButton(Avalonia.Controls.Button btn, bool active)
    {
        if (active)
        {
            btn.Background = GetBrush("AccentBgSubtleBrush");
            btn.Foreground = GetBrush("AccentFgBrush");
        }
        else
        {
            btn.Background = Brushes.Transparent;
            btn.Foreground = GetBrush("TextMutedBrush");
        }
    }

    /// <summary>
    /// Right-column content pane. One scroller per sub-section, all built
    /// up-front + held in <see cref="_settingsSubSectionPanels"/>. Selection
    /// flips IsVisible on each child rather than rebuilding the tree, so
    /// scroll position survives sub-section switches.
    /// </summary>
    private Control BuildSettingsContentPane()
    {
        // Initial selected sub-section comes from persisted state; default
        // to Routing (index 0) on first open.
        _settingsSelectedSubSection = AndroidStorage.GetSettingsActiveSubSection();

        var host = new Grid
        {
            Background = GetBrush("SurfaceAppBrush"),
        };

        _settingsSubSectionPanels[0] = WrapSubSectionScroller(BuildSettingsRoutingSection());
        _settingsSubSectionPanels[1] = WrapSubSectionScroller(BuildSettingsRulesSection());
        _settingsSubSectionPanels[2] = WrapSubSectionScroller(BuildSettingsLeakSection());
        _settingsSubSectionPanels[3] = WrapSubSectionScroller(BuildSettingsContentSection());
        _settingsSubSectionPanels[4] = WrapSubSectionScroller(BuildSettingsUpdatesSection());

        // Autostart pane on Android merges desktop's Autostart + Reliability —
        // see BuildSettingsAutostartSection comment for rationale.
        _settingsSubSectionPanels[5] = WrapSubSectionScroller(BuildSettingsAutostartSection());

        for (int i = 0; i < _settingsSubSectionPanels.Length; i++)
        {
            var panel = _settingsSubSectionPanels[i];
            if (panel is null) continue;
            panel.IsVisible = i == _settingsSelectedSubSection;
            host.Children.Add(panel);
        }

        return host;
    }

    /// <summary>
    /// Wrap a sub-section's content stack in a ScrollViewer + outer padding
    /// matching desktop's NetworkPage right-pane chrome (Padding="0,10,0,12"
    /// + inner Margin="14,0,14,0"). The sunken background stays on the
    /// outer host; this scroller is transparent.
    /// </summary>
    private ScrollViewer WrapSubSectionScroller(Control content)
    {
        var inner = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(14, 10, 14, 12),
            Children = { content },
        };

        return new ScrollViewer
        {
            Content = inner,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.Transparent,
        };
    }

    /// <summary>
    /// Switch the active sub-section. Persists the index, flips IsVisible
    /// on the panel set, repaints the side-nav buttons. Idempotent —
    /// re-selecting the active section is a no-op.
    /// </summary>
    private void SelectSettingsSubSection(int index)
    {
        if (index < 0 || index >= 6) return;
        _settingsSelectedSubSection = index;
        AndroidStorage.SetSettingsActiveSubSection(index);

        for (int i = 0; i < _settingsSubSectionPanels.Length; i++)
        {
            var panel = _settingsSubSectionPanels[i];
            if (panel is not null) panel.IsVisible = i == index;
        }
        for (int i = 0; i < _settingsSubSectionButtons.Length; i++)
        {
            var btn = _settingsSubSectionButtons[i];
            if (btn is not null) StyleSettingsSubSectionButton(btn, i == index);
        }
    }

    /// <summary>
    /// Footer Apply bar. Mirrors desktop NetworkPage.axaml:2213-2243 — left
    /// side hosts the "✓ Auto-saved" badge (resting state), right side
    /// hosts the "Apply now (reload VPN)" button. Per the Phase C spec the
    /// two swap based on <see cref="_settingsDirty"/>: the badge shows when
    /// no pending changes exist, the button takes its place when there are.
    /// </summary>
    private Border BuildSettingsFooterBar()
    {
        // ✓ Auto-saved badge — small SuccessFg pill stating the obvious so
        // the user doesn't go hunting for a Save button. Mirrors desktop's
        // L_SettingsAutosaved row.
        var checkGlyph = new TextBlock
        {
            Text = "✓",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = GetBrush("SuccessSolidBrush"),
        };
        var badgeText = new TextBlock
        {
            Text = Localization.SettingsAutosaved,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = GetBrush("SuccessFgBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _settingsAutoSavedBadge = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 5,
                Children = { checkGlyph, badgeText },
            },
        };

        // [Apply] button — swaps in when _settingsDirty is true. Click
        // clears the dirty flag and, if currently connected, kicks a
        // disconnect/reconnect cycle so the running tunnel picks up the
        // new config. When not connected the click just clears the badge
        // (next Connect will rebuild from fresh storage anyway).
        _settingsApplyButton = new Avalonia.Controls.Button
        {
            Content = Localization.ApplyNowReloadVpn,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(10, 5),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Background = GetBrush("AccentSolidBrush"),
            Foreground = GetBrush("AccentOnSolidBrush"),
            HorizontalAlignment = HorizontalAlignment.Right,
            IsVisible = false,
        };
        _settingsApplyButton.Click += OnSettingsApplyClicked;

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(_settingsAutoSavedBadge, 0);
        Grid.SetColumn(_settingsApplyButton, 1);
        grid.Children.Add(_settingsAutoSavedBadge);
        grid.Children.Add(_settingsApplyButton);

        return new Border
        {
            Padding = new Thickness(14, 7, 14, 8),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = GetBrush("BorderDefaultBrush"),
            Background = GetBrush("SurfaceSunkenBrush"),
            Child = grid,
        };
    }

    /// <summary>
    /// Routing sub-section: split/full radio cards + Russian-traffic bypass.
    /// Mirrors desktop NetworkPage.axaml lines 237-309 (Routing block).
    /// </summary>
    private Control BuildSettingsRoutingSection()
    {
        var sectionTitle = MakeSectionTitle(Localization.SettingsSectionRouting);
        var description = new TextBlock
        {
            Text = Localization.RoutingDescription,
            FontSize = 11,
            Foreground = GetBrush("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        };

        var routingMode = AndroidStorage.GetRoutingMode();

        _settingsSplitRadio = new Avalonia.Controls.RadioButton
        {
            GroupName = "SettingsRouting",
            IsChecked = routingMode == "split",
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0),
        };
        _settingsSplitRadio.IsCheckedChanged += OnSettingsRoutingChanged;
        var splitCard = MakeRadioCard(_settingsSplitRadio,
            Localization.SplitTunnelTitle, Localization.SplitTunnelSubtitle);

        _settingsFullRadio = new Avalonia.Controls.RadioButton
        {
            GroupName = "SettingsRouting",
            IsChecked = routingMode == "full",
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0),
        };
        _settingsFullRadio.IsCheckedChanged += OnSettingsRoutingChanged;
        var fullCard = MakeRadioCard(_settingsFullRadio,
            Localization.FullTunnelTitle, Localization.FullTunnelSubtitle);

        _settingsBypassRu = new Avalonia.Controls.CheckBox
        {
            IsChecked = AndroidStorage.GetBypassRussianTraffic(),
            MinHeight = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0),
        };
        _settingsBypassRu.IsCheckedChanged += OnSettingsBypassRuChanged;
        var bypassCard = MakeCheckboxCard(_settingsBypassRu,
            Localization.BypassRussianTrafficLabel, Localization.BypassRussianTrafficHint);

        // 2026-05-15 (Bug-AND-004, brat live-test): DPI bypass (Zapret)
        // card removed from Routing tab on Android. Zapret is Windows-
        // only — the card was showing a non-functional picker with a
        // confusing «...в отличие от Windows-версии Zapret» footnote.
        // Same rationale as Bug-AND-002/003: platform-not-applicable
        // features hidden, not shown as stubs. BuildDpiBypassCard()
        // method retained in case a future Android-native DPI bypass
        // implementation lands.

        var stack = new StackPanel
        {
            Spacing = 10,
            Children = { sectionTitle, description, splitCard, fullCard, bypassCard }
        };
        return WrapSection(stack);
    }

    /// <summary>
    /// v2.32.0 (AND-ZAPRET, 2026-05-07) — Routing-section card for the DPI
    /// bypass strategy picker. Three-value ComboBox (Off / Standard /
    /// Aggressive) + descriptive hint + warning blurb. Uses the same
    /// SurfaceSunkenBrush card chrome as the bypass-RU checkbox card so
    /// the section reads as one consistent block.
    /// </summary>
    private Border BuildDpiBypassCard()
    {
        var titleText = new TextBlock
        {
            Text = Localization.SettingsDpiBypassLabel,
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            Foreground = GetBrush("TextPrimaryBrush"),
        };
        var hintText = new TextBlock
        {
            Text = Localization.SettingsDpiBypassHint,
            FontSize = 10,
            Foreground = GetBrush("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        };

        _settingsDpiBypassMode = new Avalonia.Controls.ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = 11,
            ItemsSource = new[]
            {
                Localization.SettingsDpiBypassOff,
                Localization.SettingsDpiBypassStandard,
                Localization.SettingsDpiBypassAggressive,
            },
            SelectedIndex = AndroidStorage.GetDpiBypassMode() switch
            {
                "standard" => 1,
                "aggressive" => 2,
                _ => 0,
            },
        };
        _settingsDpiBypassMode.SelectionChanged += OnSettingsDpiBypassModeChanged;

        var warning = new TextBlock
        {
            Text = Localization.SettingsDpiBypassWarning,
            FontSize = 9,
            Foreground = GetBrush("WarningFgBrush"),
            TextWrapping = TextWrapping.Wrap,
        };

        return new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Background = GetBrush("SurfaceSunkenBrush"),
            BorderBrush = GetBrush("BorderSubtleBrush"),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 6,
                Children = { titleText, hintText, _settingsDpiBypassMode, warning }
            }
        };
    }

    /// <summary>
    /// Phase C (2026-05-10) — Rules sub-section. Mirrors desktop
    /// NetworkPage.axaml's Rules block (around line 322) but on Android the
    /// CustomRulesParser pipeline isn't wired into AndroidConfigBuilder yet
    /// (custom routing rules are a desktop-only knob today). Rather than
    /// shipping a no-op text editor that pretends to take effect, we surface
    /// a placeholder explainer that points the user to the Apps tab as the
    /// current way to choose what goes through VPN. The side-nav slot exists
    /// so visual parity with desktop is preserved + a future port can fill
    /// in the editor without re-doing the chrome.
    /// </summary>
    private Control BuildSettingsRulesSection()
    {
        var sectionTitle = MakeSectionTitle(Localization.SettingsSectionRules);

        var note = new TextBlock
        {
            Text = Localization.AdvSettingsRulesAndroidNote,
            FontSize = 11,
            Foreground = GetBrush("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        };

        // Sunken Border mirrors the per-section card chrome the other
        // sub-sections use — without it the placeholder reads as a stray
        // paragraph instead of a deliberate empty state.
        var card = new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Background = GetBrush("SurfaceSunkenBrush"),
            BorderBrush = GetBrush("BorderSubtleBrush"),
            BorderThickness = new Thickness(1),
            Child = note,
        };

        var stack = new StackPanel
        {
            Spacing = 10,
            Children = { sectionTitle, card }
        };
        return WrapSection(stack);
    }

    /// <summary>
    /// Leak protection sub-section. Desktop NetworkPage:1779-1859 packs four
    /// inline 24,* checkbox rows inside a single SurfaceSunken Border. We
    /// mirror that chrome but surface only the controls that map cleanly to
    /// the Android stack — block_on_vpn_fail (VpnService.setBlocking) and
    /// the DNS strategy combo. StrictMode / ForceIpv4 / FlushDns / StrictDns
    /// are desktop-only (Windows firewall + DNS cache flush) and intentionally
    /// not exposed; they would be no-ops on Android. The Block-on-VPN-fail
    /// checkbox is the Android equivalent of desktop's firewall-netsh-based
    /// kill switch — same UI, different mechanism (VpnService.setBlocking
    /// instead of netsh AdvFirewall) — so we keep it visible and document
    /// the platform difference in the hint copy.
    /// </summary>
    private Control BuildSettingsLeakSection()
    {
        var sectionTitle = MakeSectionTitle(Localization.SettingsSectionLeak);

        _settingsBlockOnVpnFail = new Avalonia.Controls.CheckBox
        {
            IsChecked = AndroidStorage.GetBlockOnVpnFail(),
            MinHeight = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _settingsBlockOnVpnFail.IsCheckedChanged += OnSettingsBlockOnVpnFailChanged;

        // Inline 24,* checkbox row inside a SurfaceSunken Border, matching
        // desktop NetworkPage:1804-1857. Label TextBlock sits in the * col
        // with TextWrapping=Wrap so long localised labels reflow inside the
        // card width instead of pushing the parent past the ScrollViewer.
        var blockLabel = new TextBlock
        {
            Text = Localization.BlockOnVpnFailLabel,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
        };
        var blockGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("24,*"),
            ColumnSpacing = 6,
        };
        Grid.SetColumn(_settingsBlockOnVpnFail, 0);
        Grid.SetColumn(blockLabel, 1);
        blockGrid.Children.Add(_settingsBlockOnVpnFail);
        blockGrid.Children.Add(blockLabel);

        var blockHint = new TextBlock
        {
            Text = Localization.BlockOnVpnFailHint,
            FontSize = 10,
            Foreground = GetBrush("TextMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(30, 0, 0, 0),
        };

        var leakInner = new Border
        {
            Padding = new Thickness(8, 6),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Background = GetBrush("SurfaceSunkenBrush"),
            BorderBrush = GetBrush("BorderDefaultBrush"),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 4,
                Children = { blockGrid, blockHint }
            }
        };

        // DNS strategy combo lives in a sibling SurfaceSunken Border so the
        // visual grouping reads "two leak-protection cards", same as the
        // desktop pattern of stacking SurfaceSunken Borders inside a section.
        _settingsDnsStrategy = new Avalonia.Controls.ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = 12,
            ItemsSource = new[]
            {
                Localization.DnsStrategyIpv4Only,
                Localization.DnsStrategyPreferIpv4,
                Localization.DnsStrategyPreferIpv6,
            },
            SelectedIndex = AndroidStorage.GetDnsStrategy() switch
            {
                "prefer_ipv4" => 1,
                "prefer_ipv6" => 2,
                _ => 0,
            },
        };
        _settingsDnsStrategy.SelectionChanged += OnSettingsDnsStrategyChanged;

        var dnsHeader = new TextBlock
        {
            Text = Localization.DnsStrategyHeader,
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
        };
        var dnsHint = new TextBlock
        {
            Text = Localization.DnsStrategyHint,
            FontSize = 10,
            Foreground = GetBrush("TextMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
        };

        var dnsInner = new Border
        {
            Padding = new Thickness(8, 6),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Background = GetBrush("SurfaceSunkenBrush"),
            BorderBrush = GetBrush("BorderDefaultBrush"),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 4,
                Children = { dnsHeader, _settingsDnsStrategy, dnsHint }
            }
        };

        var stack = new StackPanel
        {
            Spacing = 10,
            Children = { sectionTitle, leakInner, dnsInner }
        };
        return WrapSection(stack);
    }

    /// <summary>
    /// Content sub-section. Mirrors desktop NetworkPage:1861-1879 — a single
    /// checkbox-card for AdGuard DNS / ad blocking. Persists the toggle today
    /// so future overlays read consistent state; the AndroidConfigBuilder
    /// integration (geosite-ads route → reject + AdGuard DoH override) is a
    /// follow-up. Visually identical to desktop's "checkbox-card" pattern.
    /// </summary>
    private Control BuildSettingsContentSection()
    {
        var sectionTitle = MakeSectionTitle(Localization.SettingsSectionContent);

        _settingsBlockAds = new Avalonia.Controls.CheckBox
        {
            IsChecked = AndroidStorage.GetBlockAds(),
            MinHeight = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0),
        };
        _settingsBlockAds.IsCheckedChanged += OnSettingsBlockAdsChanged;
        var card = MakeCheckboxCard(_settingsBlockAds,
            Localization.SettingsBlockAdsLabel,
            Localization.SettingsBlockAdsHint);

        var stack = new StackPanel
        {
            Spacing = 10,
            Children = { sectionTitle, card }
        };
        return WrapSection(stack);
    }

    // Phase C (2026-05-10): BuildSettingsReliabilitySection was removed —
    // its three rows (Always-on VPN, battery optimization, auto-reconnect)
    // moved into BuildSettingsAutostartSection above so the side-nav has
    // exactly six entries matching desktop. UpdateBatteryOptimizationStatus
    // + the OnReliability* event handlers are still wired (see below);
    // they're now invoked from inside the Autostart pane instead.

    /// <summary>
    /// Updates sub-section: prerelease channel toggle + current version
    /// label + manual check button. Mirrors desktop NetworkPage 1881-1928.
    /// On Android the Check button reuses the same placeholder behaviour
    /// as the kebab > Diagnostics > "Check for updates" entry — Android
    /// auto-update is out of v3.0 alpha scope (handbook §6).
    /// </summary>
    private Control BuildSettingsUpdatesSection()
    {
        var sectionTitle = MakeSectionTitle(Localization.SettingsSectionUpdates);

        // Channel sub-card. Desktop NetworkPage:1885-1899 wraps the channel
        // header + prerelease checkbox in a SurfaceSunken Border. Mirroring
        // the chrome here keeps Android's stacked-section layout matching
        // desktop's master-detail pane visually.
        var channelHeader = new TextBlock
        {
            Text = Localization.UpdateChannelHeader,
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
        };

        _settingsReceivePrereleases = new Avalonia.Controls.CheckBox
        {
            IsChecked = AndroidStorage.GetUpdateChannel() == "experimental",
            MinHeight = 0,
            Padding = new Thickness(4, 0),
            Content = new TextBlock
            {
                Text = Localization.ReceivePrereleasesLabel,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
            }
        };
        _settingsReceivePrereleases.IsCheckedChanged += OnSettingsChannelChanged;

        var channelInner = new Border
        {
            Padding = new Thickness(8, 6),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Background = GetBrush("SurfaceSunkenBrush"),
            BorderBrush = GetBrush("BorderDefaultBrush"),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 3,
                Children = { channelHeader, _settingsReceivePrereleases }
            }
        };

        // Current version + Check button row in its own SurfaceSunken Border,
        // mirroring desktop NetworkPage:1904-1927 (the SUGGEST-22 panel).
        _settingsCurrentVersion = new TextBlock
        {
            Text = VPNRouter.Core.AppVersion.Version,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = GetBrush("TextPrimaryBrush"),
        };
        var versionLabel = new TextBlock
        {
            Text = Localization.CurrentVersionLabel,
            FontSize = 10,
            Opacity = 0.7,
            // Bug-AND-018 (2026-05-16, polish iter 32) — paired with
            // shortened RU "Версия" label so the row fits without wrap.
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var versionStack = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { versionLabel, _settingsCurrentVersion }
        };

        var checkBtn = new Avalonia.Controls.Button
        {
            Content = Localization.CheckForUpdatesButton,
            FontSize = 10,
            Padding = new Thickness(10, 5),
            MinHeight = 0,
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            VerticalAlignment = VerticalAlignment.Center,
        };
        checkBtn.Click += OnSettingsCheckUpdatesClicked;

        var versionRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8,
        };
        Grid.SetColumn(versionStack, 0);
        Grid.SetColumn(checkBtn, 1);
        versionRow.Children.Add(versionStack);
        versionRow.Children.Add(checkBtn);

        var versionInner = new Border
        {
            Padding = new Thickness(8, 6),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Background = GetBrush("SurfaceSunkenBrush"),
            BorderBrush = GetBrush("BorderDefaultBrush"),
            BorderThickness = new Thickness(1),
            Child = versionRow,
        };

        var stack = new StackPanel
        {
            Spacing = 10,
            Children = { sectionTitle, channelInner, versionInner }
        };
        return WrapSection(stack);
    }

    /// <summary>
    /// Phase C (2026-05-10) — Autostart sub-section. Combines desktop's
    /// Autostart (Service-install + boot toggles) and Reliability (Always-on
    /// VPN + battery opt + auto-reconnect) sections per the parity plan's
    /// platform-impossible item table — Always-on VPN IS Android's
    /// replacement for Windows-Service-on-boot, so it naturally belongs
    /// here. The 3 boot toggles (VPN/Zapret/TgProxy) keep persisting their
    /// flags so a future BootCompletedReceiver can read them without a
    /// migration, but they're permanently in the ⛔ tier on Android until
    /// that receiver lands.
    /// </summary>
    private Control BuildSettingsAutostartSection()
    {
        var sectionTitle = MakeSectionTitle(Localization.SettingsSectionAutostart);

        // Android-equivalence intro — explains that Always-on VPN is the
        // way to get boot-time + network-change-time tunnel restoration
        // without a Windows-style service. Sets expectations before the
        // user sees the (non-firing) boot-toggle group below.
        var androidIntro = new TextBlock
        {
            Text = Localization.AdvSettingsAutostartAndroidIntro,
            FontSize = 11,
            Foreground = GetBrush("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        };

        // ── Always-on VPN row (formerly desktop Reliability section) ──
        var alwaysOnTitle = new TextBlock
        {
            Text = Localization.ReliabilityAlwaysOnTitle,
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            Foreground = GetBrush("TextPrimaryBrush"),
        };
        var alwaysOnHint = new TextBlock
        {
            Text = Localization.ReliabilityAlwaysOnHint,
            FontSize = 10,
            Foreground = GetBrush("TextMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
        };
        var alwaysOnBtn = new Avalonia.Controls.Button
        {
            Content = Localization.ReliabilityAlwaysOnButton,
            FontSize = 10,
            Padding = new Thickness(10, 5),
            MinHeight = 0,
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        alwaysOnBtn.Click += OnReliabilityAlwaysOnClicked;
        var alwaysOnRow = new StackPanel
        {
            Spacing = 4,
            Children = { alwaysOnTitle, alwaysOnHint, alwaysOnBtn },
        };

        // ── Battery optimization row (formerly desktop Reliability) ──
        var batteryTitle = new TextBlock
        {
            Text = Localization.ReliabilityBatteryOptTitle,
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            Foreground = GetBrush("TextPrimaryBrush"),
        };
        _reliabilityBatteryStatusLabel = new TextBlock
        {
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
        };
        var batteryHint = new TextBlock
        {
            Text = Localization.ReliabilityBatteryOptHint,
            FontSize = 10,
            Foreground = GetBrush("TextMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
        };
        _reliabilityBatteryButton = new Avalonia.Controls.Button
        {
            FontSize = 10,
            Padding = new Thickness(10, 5),
            MinHeight = 0,
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _reliabilityBatteryButton.Click += OnReliabilityBatteryClicked;
        UpdateBatteryOptimizationStatus();
        var batteryRow = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                batteryTitle,
                _reliabilityBatteryStatusLabel,
                batteryHint,
                _reliabilityBatteryButton,
            },
        };

        // ── Auto-reconnect on network change toggle ──
        _reliabilityAutoReconnect = new Avalonia.Controls.CheckBox
        {
            IsChecked = AndroidStorage.GetAutoReconnectOnNetworkChange(),
            MinHeight = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0),
        };
        _reliabilityAutoReconnect.IsCheckedChanged += OnReliabilityAutoReconnectChanged;
        var autoReconnectCard = MakeCheckboxCard(_reliabilityAutoReconnect,
            Localization.ReliabilityAutoReconnectTitle,
            Localization.ReliabilityAutoReconnectHint);

        // ── Boot toggles (Windows-Service parity scaffolding) ──
        // Pre-Phase-C these were the ENTIRE Autostart section. After Phase C
        // they're a separate sub-block under the Always-on / battery /
        // auto-reconnect rows because those are the controls that actually
        // matter on Android. The boot flags keep persisting so a future
        // BootCompletedReceiver port has its data ready.
        var bootHeader = new TextBlock
        {
            Text = Localization.AutostartBootSectionTitle,
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            Foreground = GetBrush("TextMutedBrush"),
        };
        var bootSub = new TextBlock
        {
            Text = Localization.AutostartBootSectionSub,
            FontSize = 10,
            Foreground = GetBrush("TextMutedBrush"),
            Opacity = 0.85,
            TextWrapping = TextWrapping.Wrap,
        };

        _settingsAutostartVpn = new Avalonia.Controls.CheckBox
        {
            IsChecked = AndroidStorage.GetAutostartVpn(),
            MinHeight = 0,
            Padding = new Thickness(4, 0),
            Content = new TextBlock
            {
                Text = Localization.AutostartLabelVpn,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
            }
        };
        _settingsAutostartVpn.IsCheckedChanged += OnSettingsAutostartVpnChanged;
        var vpnStack = MakeAutostartRow(_settingsAutostartVpn,
            Localization.AutostartStatusNoBoot, "DangerFgBrush");

        // Bug-AND-020 (2026-05-16, user-reported "в настройках автозапуска
        // осталось про tgproxy и про zapret"): Zapret + TgProxy aren't
        // ported to Android — surfacing their autostart toggles with a
        // "not ported" warning was confusing UX. Removed entirely.
        // The fields _settingsAutostartZapret / _settingsAutostartTgProxy
        // stay declared so other call-sites that null-check them still
        // compile, but they're never instantiated on Android now. Same
        // pattern as Bug-AND-002/004 (Zapret + Tools chip hiding).
        //
        // Bug-AND-020 follow-up: the "At Windows startup (before sign-in)"
        // section + "Start VPN on system boot" checkbox + ⛔ warning
        // text are Windows-service-specific. On Android the right
        // autostart path is Always-on VPN (already explained above in
        // androidIntro + alwaysOnRow). Hide the whole boot section.
        // _settingsAutostartVpn stays declared but isn't rendered;
        // bootHeader / bootSub / vpnStack are unused locals now (kept
        // to minimise diff churn — compiler dead-code-eliminates them).
        _ = bootHeader; _ = bootSub; _ = vpnStack; // silence unused-warnings

        var stack = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                sectionTitle,
                androidIntro,
                alwaysOnRow,
                batteryRow,
                autoReconnectCard,
            }
        };
        return WrapSection(stack);
    }

    // ── Settings overlay layout helpers ─────────────────────────────────

    private TextBlock MakeSectionTitle(string text) => new TextBlock
    {
        Text = text,
        FontWeight = FontWeight.Bold,
        FontSize = 13,
        Foreground = GetBrush("TextPrimaryBrush"),
    };

    private Border WrapSection(Control content) => new Border
    {
        Padding = new Thickness(12),
        CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
        Background = GetBrush("SurfaceBaseBrush"),
        BorderBrush = GetBrush("BorderSubtleBrush"),
        BorderThickness = new Thickness(1),
        Child = content,
    };

    /// <summary>
    /// "Radio-card" pattern from desktop NetworkPage — Border with a 24,*
    /// Grid (radio left, title+subtitle stack right). Whole card click
    /// flips the radio.
    /// </summary>
    private Border MakeRadioCard(Avalonia.Controls.RadioButton radio, string title, string subtitle)
    {
        var titleText = new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            Foreground = GetBrush("TextPrimaryBrush"),
        };
        var subText = new TextBlock
        {
            Text = subtitle,
            FontSize = 10,
            Foreground = GetBrush("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        };
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("24,*"),
            ColumnSpacing = 8,
        };
        Grid.SetColumn(radio, 0);
        var rightStack = new StackPanel { Spacing = 2, Children = { titleText, subText } };
        Grid.SetColumn(rightStack, 1);
        grid.Children.Add(radio);
        grid.Children.Add(rightStack);

        var card = new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Background = GetBrush("SurfaceSunkenBrush"),
            BorderBrush = GetBrush("BorderSubtleBrush"),
            BorderThickness = new Thickness(1),
            Child = grid,
        };
        card.PointerPressed += (_, __) =>
        {
            // Tap anywhere on the card flips the radio (desktop card click
            // semantics). Idempotent: clicking an already-active card
            // is a no-op since IsChecked → true is no change.
            radio.IsChecked = true;
        };
        return card;
    }

    /// <summary>"Checkbox-card" — same shape as MakeRadioCard but for a CheckBox.</summary>
    private Border MakeCheckboxCard(Avalonia.Controls.CheckBox cb, string title, string subtitle)
    {
        var titleText = new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            Foreground = GetBrush("TextPrimaryBrush"),
        };
        var subText = new TextBlock
        {
            Text = subtitle,
            FontSize = 10,
            Foreground = GetBrush("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        };
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("24,*"),
            ColumnSpacing = 8,
        };
        Grid.SetColumn(cb, 0);
        var rightStack = new StackPanel { Spacing = 2, Children = { titleText, subText } };
        Grid.SetColumn(rightStack, 1);
        grid.Children.Add(cb);
        grid.Children.Add(rightStack);

        var card = new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Background = GetBrush("SurfaceSunkenBrush"),
            BorderBrush = GetBrush("BorderSubtleBrush"),
            BorderThickness = new Thickness(1),
            Child = grid,
        };
        card.PointerPressed += (_, __) =>
        {
            cb.IsChecked = !(cb.IsChecked == true);
        };
        return card;
    }

    /// <summary>
    /// 24,* grid with checkbox + bold label + wrap-text hint underneath.
    /// Used in Leak section where labels are short and don't deserve a
    /// full radio-card look.
    /// </summary>
    private StackPanel MakeLabeledCheckboxRow(Avalonia.Controls.CheckBox cb, string label, string hint)
    {
        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        var hintText = new TextBlock
        {
            Text = hint,
            FontSize = 10,
            Foreground = GetBrush("TextMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(28, 0, 0, 0),
        };
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("24,*"),
            ColumnSpacing = 6,
        };
        Grid.SetColumn(cb, 0);
        Grid.SetColumn(labelText, 1);
        grid.Children.Add(cb);
        grid.Children.Add(labelText);

        return new StackPanel
        {
            Spacing = 2,
            Children = { grid, hintText }
        };
    }

    /// <summary>
    /// Autostart row: checkbox on top, status badge below indented to align
    /// under the label text. Mirrors desktop NetworkPage 2071-2150 — the
    /// status TextBlock is colored per its tier (Success / Warning / Danger).
    /// </summary>
    private StackPanel MakeAutostartRow(Avalonia.Controls.CheckBox cb, string statusText, string statusBrushKey)
    {
        var status = new TextBlock
        {
            Text = statusText,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 9,
            Margin = new Thickness(22, 0, 0, 0),
            Foreground = GetBrush(statusBrushKey),
        };
        return new StackPanel
        {
            Spacing = 2,
            Children = { cb, status }
        };
    }

    // ── Settings (Network) tab event handlers ───────────────────────────
    // Settings now lives inside the Advanced shell as the Network tab. The
    // standalone fullscreen overlay is gone — the kebab "Settings" entry
    // and the Simple-page autostart inline card both deeplink here.

    private void OnMenuSettingsClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        ShowSettings();
    }

    /// <summary>
    /// Deeplink: open the Advanced shell on the Settings tab. Re-seeds
    /// control state if the tab body has already been built. Replaces the
    /// old fullscreen Settings overlay path (gone in AND-MIGRATE-OVERLAYS).
    /// AND-ADV-CHROME (2026-05-10): tab renamed Network → Settings to
    /// match desktop v2.32.0.
    /// </summary>
    private void ShowSettings()
    {
        OpenAdvancedShell(AdvancedTab.Settings);
    }

    /// <summary>
    /// Re-seed Network-tab controls from <see cref="AndroidStorage"/> when
    /// the tab is selected. Called by the Advanced shell on tab switch +
    /// shell open. Mirrors the old ShowSettings re-seed body.
    /// </summary>
    private void ReseedNetworkTabState()
    {
        _settingsLoading = true;
        try
        {
            var routing = AndroidStorage.GetRoutingMode();
            if (_settingsSplitRadio is not null) _settingsSplitRadio.IsChecked = routing == "split";
            if (_settingsFullRadio is not null) _settingsFullRadio.IsChecked = routing == "full";
            if (_settingsBypassRu is not null) _settingsBypassRu.IsChecked = AndroidStorage.GetBypassRussianTraffic();
            if (_settingsBlockOnVpnFail is not null) _settingsBlockOnVpnFail.IsChecked = AndroidStorage.GetBlockOnVpnFail();
            if (_settingsBlockAds is not null) _settingsBlockAds.IsChecked = AndroidStorage.GetBlockAds();
            if (_settingsDnsStrategy is not null)
            {
                _settingsDnsStrategy.SelectedIndex = AndroidStorage.GetDnsStrategy() switch
                {
                    "prefer_ipv4" => 1,
                    "prefer_ipv6" => 2,
                    _ => 0,
                };
            }
            if (_settingsReceivePrereleases is not null)
                _settingsReceivePrereleases.IsChecked = AndroidStorage.GetUpdateChannel() == "experimental";
            if (_settingsCurrentVersion is not null) _settingsCurrentVersion.Text = VPNRouter.Core.AppVersion.Version;
            if (_settingsAutostartVpn is not null) _settingsAutostartVpn.IsChecked = AndroidStorage.GetAutostartVpn();
            if (_settingsAutostartZapret is not null) _settingsAutostartZapret.IsChecked = AndroidStorage.GetAutostartZapret();
            if (_settingsAutostartTgProxy is not null) _settingsAutostartTgProxy.IsChecked = AndroidStorage.GetAutostartTgProxy();
            if (_settingsDpiBypassMode is not null)
            {
                _settingsDpiBypassMode.SelectedIndex = AndroidStorage.GetDpiBypassMode() switch
                {
                    "standard" => 1,
                    "aggressive" => 2,
                    _ => 0,
                };
            }
            if (_reliabilityAutoReconnect is not null)
                _reliabilityAutoReconnect.IsChecked = AndroidStorage.GetAutoReconnectOnNetworkChange();
            UpdateBatteryOptimizationStatus();

            // Phase C (2026-05-10): re-applying stored values via the
            // setters above doesn't go through the OnSettings*Changed
            // path (we're inside _settingsLoading), so we don't pick up
            // a spurious dirty mark. Restore the persisted sub-section
            // selection so the user lands on the same pane they last
            // visited, and refresh the footer in case this is the first
            // open of the tab (badges weren't built before now).
            var persisted = AndroidStorage.GetSettingsActiveSubSection();
            if (persisted != _settingsSelectedSubSection)
                SelectSettingsSubSection(persisted);
            UpdateSettingsFooterVisibility();
        }
        finally
        {
            _settingsLoading = false;
        }
    }

    private void OnSettingsRoutingChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_settingsLoading) return;
        // RadioButton group fires IsCheckedChanged on both the now-off and
        // now-on members; we react only to the new "on" state to avoid
        // double-write. Falls back to "split" if neither radio is checked
        // (initial transient state during construction).
        var splitOn = _settingsSplitRadio?.IsChecked == true;
        var fullOn = _settingsFullRadio?.IsChecked == true;
        if (!splitOn && !fullOn) return;
        var newMode = splitOn ? "split" : "full";
        if (AndroidStorage.GetRoutingMode() == newMode) return;
        AndroidStorage.SetRoutingMode(newMode);
        MarkSettingsDirty();
    }

    private void OnSettingsBypassRuChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_settingsLoading || _settingsBypassRu is null) return;
        AndroidStorage.SetBypassRussianTraffic(_settingsBypassRu.IsChecked == true);
        MarkSettingsDirty();
    }

    private void OnSettingsBlockOnVpnFailChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_settingsLoading || _settingsBlockOnVpnFail is null) return;
        AndroidStorage.SetBlockOnVpnFail(_settingsBlockOnVpnFail.IsChecked == true);
        MarkSettingsDirty();
    }

    private void OnSettingsBlockAdsChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_settingsLoading || _settingsBlockAds is null) return;
        AndroidStorage.SetBlockAds(_settingsBlockAds.IsChecked == true);
        MarkSettingsDirty();
    }

    private void OnSettingsDnsStrategyChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
    {
        if (_settingsLoading || _settingsDnsStrategy is null) return;
        var value = _settingsDnsStrategy.SelectedIndex switch
        {
            1 => "prefer_ipv4",
            2 => "prefer_ipv6",
            _ => "ipv4_only",
        };
        AndroidStorage.SetDnsStrategy(value);
        MarkSettingsDirty();
    }

    private void OnSettingsChannelChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_settingsLoading || _settingsReceivePrereleases is null) return;
        AndroidStorage.SetUpdateChannel(_settingsReceivePrereleases.IsChecked == true ? "experimental" : "stable");
        // Update channel doesn't affect the running tunnel — no need to
        // mark dirty. Auto-saved badge stays.
    }

    private void OnSettingsCheckUpdatesClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // v2.32.0 (2026-05-07) — wires the Settings > Updates button to
        // the real flow. The Settings overlay stays open so the user
        // sees the result inline; banner appears under the status card
        // (it's behind the overlay, but visible if user dismisses).
        _ = RunUpdateCheckAsync(manual: true);
    }

    private void OnSettingsAutostartVpnChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_settingsLoading || _settingsAutostartVpn is null) return;
        AndroidStorage.SetAutostartVpn(_settingsAutostartVpn.IsChecked == true);
        // Boot-time autostart flag — affects only the future BootCompletedReceiver
        // path, not the currently-running tunnel. No dirty mark.
    }

    private void OnSettingsAutostartZapretChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_settingsLoading || _settingsAutostartZapret is null) return;
        AndroidStorage.SetAutostartZapret(_settingsAutostartZapret.IsChecked == true);
    }

    private void OnSettingsAutostartTgProxyChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_settingsLoading || _settingsAutostartTgProxy is null) return;
        AndroidStorage.SetAutostartTgProxy(_settingsAutostartTgProxy.IsChecked == true);
    }

    /// <summary>
    /// v2.32.0 (AND-ZAPRET) — DPI bypass mode picker. Persists the new
    /// value + refreshes the Zapret chip in the sub-header so the
    /// state visualisation stays in sync without waiting for the next
    /// VPN connect cycle.
    /// </summary>
    private void OnSettingsDpiBypassModeChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
    {
        if (_settingsLoading || _settingsDpiBypassMode is null) return;
        var value = _settingsDpiBypassMode.SelectedIndex switch
        {
            1 => "standard",
            2 => "aggressive",
            _ => "off",
        };
        AndroidStorage.SetDpiBypassMode(value);
        UpdateZapretChipFromState();
        MarkSettingsDirty();
    }

    // ── v2.32.0 AND-NETRES Reliability handlers ─────────────────────────

    /// <summary>
    /// Deep-link to the Android Settings → VPN page so the user can find
    /// the gear next to "VPNRouter" and toggle Always-on. We use
    /// <c>Settings.ACTION_VPN_SETTINGS</c> (works on Android 4.0+).
    /// On API 24+ the same intent surfaces the new VPN list UI; older
    /// platforms get the per-app VPN settings dialog.
    /// </summary>
    private void OnReliabilityAlwaysOnClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var activity = MainActivity.Instance;
            if (activity is null) return;
            var intent = new global::Android.Content.Intent(
                global::Android.Provider.Settings.ActionVpnSettings);
            // Settings activities run in their own task — FLAG_ACTIVITY_NEW_TASK
            // is required when launching from a non-Activity Context, but
            // even from the Activity it's good practice for cross-task
            // settings deep-links.
            intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
            activity.StartActivity(intent);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnRouter",
                $"AND-NETRES: open VPN settings failed — {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Battery-optimization deep-link. If we're already excluded
    /// (<c>PowerManager.IsIgnoringBatteryOptimizations</c> = true), open
    /// the system's "Battery optimization" list view so the user can
    /// inspect / revoke the exclusion. Otherwise fire
    /// <c>ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS</c> with our
    /// package URI to trigger the system's grant dialog. The user must
    /// confirm the prompt — we never auto-grant ourselves anything.
    /// </summary>
    private void OnReliabilityBatteryClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var activity = MainActivity.Instance;
            if (activity is null) return;
            bool isExempt = IsIgnoringBatteryOptimizations(activity);
            global::Android.Content.Intent intent;
            if (isExempt)
            {
                // Open the system list view so the user can revoke the
                // exclusion if they want. ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS
                // is API 23+; we already gate the whole feature at
                // SDK 23 minimum (csproj SupportedOSPlatformVersion=23).
                intent = new global::Android.Content.Intent(
                    global::Android.Provider.Settings.ActionIgnoreBatteryOptimizationSettings);
            }
            else
            {
                intent = new global::Android.Content.Intent(
                    global::Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations);
                intent.SetData(global::Android.Net.Uri.Parse(
                    $"package:{activity.PackageName}"));
            }
            intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
            activity.StartActivity(intent);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnRouter",
                $"AND-NETRES: battery opt deep-link failed — {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnReliabilityAutoReconnectChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_settingsLoading || _reliabilityAutoReconnect is null) return;
        AndroidStorage.SetAutoReconnectOnNetworkChange(
            _reliabilityAutoReconnect.IsChecked == true);
        // VpnRouterService.fireUpdate reads this flag every default-interface
        // change; the running tunnel picks up new behaviour on the next
        // network event without a reconnect, so no dirty mark.
    }

    /// <summary>
    /// Phase C (2026-05-10) — Mark Settings as having pending changes that
    /// need a tunnel reload to take effect (routing mode flip, DNS strategy,
    /// bypass-RU, ad-block, DPI bypass mode, block-on-VPN-fail). Swaps the
    /// "✓ Auto-saved" footer badge for an [Apply] button. Called from each
    /// affected OnSettings*Changed handler. Idempotent — re-marking already-
    /// dirty state is a no-op. The flag is kept tab-local; switching to
    /// another Advanced tab and back keeps the dirty state, which is the
    /// expected UX (a half-applied change shouldn't quietly clear because
    /// the user navigated elsewhere).
    /// </summary>
    private void MarkSettingsDirty()
    {
        if (_settingsDirty) return;
        _settingsDirty = true;
        UpdateSettingsFooterVisibility();
    }

    /// <summary>
    /// Apply button click. Clears the dirty flag and, if the tunnel is
    /// currently running, kicks a reconnect cycle so the running config
    /// picks up the new settings (Android has no sing-box hot-reload path
    /// — disconnect + reconnect is the only way to apply mid-flight). When
    /// disconnected, the click just clears the badge — the next user-
    /// initiated Connect will rebuild the config from fresh storage anyway.
    /// </summary>
    private void OnSettingsApplyClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _settingsDirty = false;
        UpdateSettingsFooterVisibility();

        // If the tunnel is running, restart it so the new config takes
        // effect. RequestDisconnect → IntentChanged(false) → user can tap
        // Connect again. We deliberately don't auto-reconnect because that
        // would be surprising — desktop's Apply button reloads in place,
        // but the equivalent on Android is a hard cycle that kills + rebuilds
        // the VpnService. A one-tap surprise reconnect would feel jarring.
        var activity = MainActivity.Instance;
        if (activity is null) return;
        if (MainActivity.IntendedConnected)
        {
            activity.RequestDisconnect();
        }
    }

    /// <summary>
    /// Show the Auto-saved badge when there are no pending changes; show
    /// the [Apply] button when there are. The two never co-exist in the
    /// footer — they swap so the user's eye is drawn to the actionable
    /// state (apply needed vs. nothing to do).
    /// </summary>
    private void UpdateSettingsFooterVisibility()
    {
        if (_settingsAutoSavedBadge is not null)
            _settingsAutoSavedBadge.IsVisible = !_settingsDirty;
        if (_settingsApplyButton is not null)
            _settingsApplyButton.IsVisible = _settingsDirty;
    }

    /// <summary>
    /// Re-read the live battery-optimization state and refresh the label
    /// + button. Called from <c>BuildSettingsAutostartSection</c> at build
    /// time (Phase C folded the old Reliability section into Autostart) AND
    /// from <c>ReseedNetworkTabState</c> each time the Settings tab is
    /// re-activated, so a user who just granted/revoked the exclusion in
    /// system settings sees the new state when they come back.
    /// </summary>
    private void UpdateBatteryOptimizationStatus()
    {
        var activity = MainActivity.Instance;
        if (activity is null) return;
        bool isExempt = IsIgnoringBatteryOptimizations(activity);

        if (_reliabilityBatteryStatusLabel is not null)
        {
            _reliabilityBatteryStatusLabel.Text = isExempt
                ? Localization.ReliabilityBatteryOptStatusExempt
                : Localization.ReliabilityBatteryOptStatusOptimized;
            _reliabilityBatteryStatusLabel.Foreground = GetBrush(
                isExempt ? "SuccessFgBrush" : "WarningFgBrush");
        }
        if (_reliabilityBatteryButton is not null)
        {
            _reliabilityBatteryButton.Content = isExempt
                ? Localization.ReliabilityBatteryOptButtonOpen
                : Localization.ReliabilityBatteryOptButtonGrant;
        }
    }

    private static bool IsIgnoringBatteryOptimizations(global::Android.App.Activity activity)
    {
        try
        {
            var pm = (global::Android.OS.PowerManager?)activity.GetSystemService(
                global::Android.Content.Context.PowerService);
            // PowerManager.IsIgnoringBatteryOptimizations is API 23+. Our
            // minSdk is 23 (csproj SupportedOSPlatformVersion=23.0) so the
            // call always resolves; older Androids don't reach this code
            // path because the feature is hidden at the Reliability section
            // gate. Returns false on null PowerManager (fallback to "warn").
            if (pm is null) return false;
            return pm.IsIgnoringBatteryOptimizations(activity.PackageName ?? "");
        }
        catch
        {
            return false;
        }
    }

    // ── v2.32.0 (AND-PROFILES, 2026-05-08) Profiles overlay ─────────────
    //
    // Fullscreen Border layered over the main ScrollViewer (same pattern as
    // Settings / Free Configs / Server list overlays). Top: title bar with
    // close ✕. Body: scrolling StackPanel of profile cards rebuilt on each
    // open so the active-profile indicator reflects the latest persisted
    // state.
    //
    // Tap-to-apply semantics: tapping any card calls ApplyProfile() which
    // routes through ProfileApplication.Plan() (Core, unit-tested) → writes
    // to AndroidStorage → refreshes per-app form count + form radios →
    // closes overlay → surfaces feedback toast. Per the prompt scope
    // (view + apply only; edit deferred), there's no "edit profile" /
    // "duplicate" / "delete" surface — those become a follow-up chip.

    private Border BuildProfilesOverlay()
    {
        _profilesOverlayTitle = new TextBlock
        {
            Text = Localization.ProfilesOverlayTitle,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _profilesOverlayTitle.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        _profilesCloseBtn = new Avalonia.Controls.Button
        {
            Content = "✕",
            FontSize = 16,
            Width = 36,
            Height = 36,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };
        _profilesCloseBtn.BindToken(Avalonia.Controls.Button.ForegroundProperty, "TextSecondaryBrush");
        _profilesCloseBtn.Click += OnProfilesCloseClicked;

        var titleBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(8, 4, 4, 4),
        };
        Grid.SetColumn(_profilesOverlayTitle, 0);
        Grid.SetColumn(_profilesCloseBtn, 1);
        titleBar.Children.Add(_profilesOverlayTitle);
        titleBar.Children.Add(_profilesCloseBtn);

        var titleBarBorder = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 4),
            Child = titleBar,
        };
        titleBarBorder.BindToken(Border.BackgroundProperty, "SurfaceRaisedBrush");
        titleBarBorder.BindToken(Border.BorderBrushProperty, "BorderSubtleBrush");

        _profilesOverlayIntro = new TextBlock
        {
            Text = Localization.ProfilesIntro,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        };
        _profilesOverlayIntro.BindToken(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        // Body StackPanel — populated on each ShowProfilesOverlay() call so
        // the active-state highlight reflects the current AndroidStorage
        // value without an event-bus subscription. Idempotent: rebuilding
        // 8 cards is essentially free.
        _profilesList = new StackPanel
        {
            Spacing = 10,
        };

        var inner = new StackPanel
        {
            Spacing = 0,
            Margin = new Thickness(16, 12, 16, 16),
            Children = { _profilesOverlayIntro, _profilesList },
        };

        var scroller = new ScrollViewer
        {
            Content = inner,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        scroller.BindToken(ScrollViewer.BackgroundProperty, "SurfaceAppBrush");

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(titleBarBorder, Dock.Top);
        dock.Children.Add(titleBarBorder);
        dock.Children.Add(scroller);

        var overlay = new Border
        {
            IsVisible = false,
            Child = dock,
        };
        overlay.BindToken(Border.BackgroundProperty, "SurfaceAppBrush");
        return overlay;
    }

    /// <summary>
    /// Build a single profile card. <paramref name="profile"/> = null →
    /// the "No profile" pseudo-card that clears the active selection
    /// and switches back to full-tunnel.
    /// </summary>
    private Border BuildProfileCard(VPNRouter.Core.Models.Profile? profile, string? activeName)
    {
        // Determine active state — null active ↔ null profile is the
        // "No profile" highlight; otherwise compare names case-insensitively
        // (storage uses the original casing but a stale lower-cased entry
        // shouldn't break the highlight).
        bool isActive;
        if (profile is null)
        {
            isActive = string.IsNullOrEmpty(activeName);
        }
        else
        {
            isActive = !string.IsNullOrEmpty(activeName)
                       && string.Equals(activeName, profile.Name, StringComparison.OrdinalIgnoreCase);
        }

        var titleText = profile?.Name ?? Localization.ProfilesNoneTitle;
        var descText = profile?.Description ?? Localization.ProfilesNoneDescription;

        var titleBlock = new TextBlock
        {
            Text = titleText,
            FontWeight = FontWeight.SemiBold,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        };
        titleBlock.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        var descBlock = new TextBlock
        {
            Text = descText,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        };
        descBlock.BindToken(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        // Active-state header pill (rendered when this card is the
        // currently-applied profile). Uses Success accent so the user can
        // spot the active card at a glance even when scrolled.
        TextBlock? activeBadge = null;
        if (isActive)
        {
            activeBadge = new TextBlock
            {
                Text = Localization.ProfilesActiveBadge,
                FontWeight = FontWeight.SemiBold,
                FontSize = 10,
                Margin = new Thickness(0, 0, 0, 4),
            };
            activeBadge.BindToken(TextBlock.ForegroundProperty, "SuccessFgBrush");
        }

        // Metadata chips — apps count + DNS mode + (optional) block-on-fail.
        // Hidden for the "No profile" pseudo-card (no metadata to show).
        var chipRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 6, 0, 0),
        };
        if (profile is not null)
        {
            var pkgCount = profile.AndroidPackages?.Count ?? 0;
            var pkgLabel = pkgCount == 1
                ? Localization.ProfilesAppsCountOne
                : string.Format(Localization.ProfilesAppsCount, pkgCount);
            chipRow.Children.Add(BuildProfileChip(pkgLabel, "AccentBgSubtleBrush", "AccentFgBrush"));

            if (!string.IsNullOrWhiteSpace(profile.DnsMode))
            {
                chipRow.Children.Add(BuildProfileChip(
                    string.Format(Localization.ProfilesDnsModeChip, profile.DnsMode),
                    "SurfaceSunkenBrush", "TextSecondaryBrush"));
            }

            if (profile.BlockOnVpnFail)
            {
                chipRow.Children.Add(BuildProfileChip(
                    Localization.ProfilesBlockOnFailChip, "WarningBgBrush", "WarningFgBrush"));
            }
        }

        var stack = new StackPanel { Spacing = 0 };
        if (activeBadge is not null) stack.Children.Add(activeBadge);
        stack.Children.Add(titleBlock);
        stack.Children.Add(descBlock);
        if (profile is not null) stack.Children.Add(chipRow);

        var card = new Border
        {
            Padding = new Thickness(14, 12),
            CornerRadius = new CornerRadius(GetRadius("RadiusMd")),
            BorderThickness = new Thickness(isActive ? 2 : 1),
            Cursor = new global::Avalonia.Input.Cursor(global::Avalonia.Input.StandardCursorType.Hand),
            Child = stack,
        };
        card.BindToken(Border.BackgroundProperty, isActive ? "AccentBgSubtleBrush" : "SurfaceBaseBrush");
        card.BindToken(Border.BorderBrushProperty, isActive ? "BorderAccentBrush" : "BorderSubtleBrush");

        // Tap anywhere on the card → apply. PointerPressed fires before
        // PointerReleased on Avalonia's mobile pointer pipeline; using
        // Pressed feels snappier and matches the radio-card / checkbox-
        // card pattern in the Settings overlay.
        card.PointerPressed += (_, __) => ApplyProfile(profile);

        return card;
    }

    private Border BuildProfileChip(string text, string bgKey, string fgKey)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
        };
        label.BindToken(TextBlock.ForegroundProperty, fgKey);

        var chip = new Border
        {
            Padding = new Thickness(8, 3),
            CornerRadius = new CornerRadius(GetRadius("RadiusPill")),
            Child = label,
        };
        chip.BindToken(Border.BackgroundProperty, bgKey);
        return chip;
    }

    private void OnMenuProfilesClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        ShowProfilesOverlay();
    }

    private void ShowProfilesOverlay()
    {
        if (_profilesOverlay is null || _profilesList is null) return;

        // Rebuild the card list each open so the active-profile highlight
        // reflects whatever's currently in storage. Cheap (8 entries) and
        // avoids a manual invalidate-on-storage-change wiring.
        _profilesList.Children.Clear();
        var active = AndroidStorage.GetActiveProfile();

        // "No profile" pseudo-card first — provides a clear escape hatch
        // back to full-tunnel without forcing the user to find the form's
        // tunnel-mode radio.
        _profilesList.Children.Add(BuildProfileCard(null, active));

        var catalog = VPNRouter.Core.Services.BuiltInAndroidProfiles.Get();
        foreach (var profile in catalog.Profiles)
        {
            _profilesList.Children.Add(BuildProfileCard(profile, active));
        }

        _profilesOverlay.IsVisible = true;
    }

    private void OnProfilesCloseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_profilesOverlay is not null) _profilesOverlay.IsVisible = false;
    }

    /// <summary>
    /// Apply the user's profile pick. Routes through
    /// <see cref="VPNRouter.Core.Services.ProfileApplication.Plan"/> (pure
    /// function, unit-tested) so the storage writes here are the only
    /// Android-side concern. Refreshes the form's per-app count + tunnel-
    /// mode radios so the user sees the new state on close, and surfaces
    /// a feedback banner so the apply isn't invisible.
    /// </summary>
    private void ApplyProfile(VPNRouter.Core.Models.Profile? profile)
    {
        var plan = VPNRouter.Core.Services.ProfileApplication.Plan(profile);

        AndroidStorage.SetActiveProfile(plan.ActiveProfileName);
        if (plan.RoutingMode is not null)
            AndroidStorage.SetRoutingMode(plan.RoutingMode);
        if (plan.AndroidPackages is not null)
            AndroidStorage.SetPerAppPackages(plan.AndroidPackages);
        if (plan.PerAppMode is not null)
            AndroidStorage.SetPerAppMode(plan.PerAppMode);
        if (plan.PerAppLastMode is not null)
            AndroidStorage.SetPerAppLastMode(plan.PerAppLastMode);
        if (plan.BlockOnVpnFail is not null)
            AndroidStorage.SetBlockOnVpnFail(plan.BlockOnVpnFail.Value);

        // Form radios may be visible behind the overlay — re-seed so
        // dismissing reveals the right state. Settings overlay re-seeds
        // its own controls in ShowSettings, so no work needed there.
        var routing = AndroidStorage.GetRoutingMode();
        if (_splitRadio is not null) _splitRadio.IsChecked = routing == "split";
        if (_fullRadio is not null) _fullRadio.IsChecked = routing == "full";
        UpdatePerAppFormCountLabel();

        // Toast feedback. Profile name embedded verbatim — catalog names
        // are ASCII underscore-separated (Discord_Privacy / Work_Suite)
        // so the localized format string still reads cleanly in RU/EN.
        var msg = profile is null
            ? Localization.ProfilesClearedToast
            : string.Format(Localization.ProfilesAppliedToast, profile.Name);
        ShowMenuFeedback(msg);

        if (_profilesOverlay is not null) _profilesOverlay.IsVisible = false;
    }

    // ── Mascot loading + theme-aware inversion ──────────────────────────

    private static Bitmap? _mascotLight;
    private static Bitmap? _mascotDark;

    /// <summary>
    /// v3.0 Phase 5 — load + cache mascot bitmap, RGB-inverted on dark
    /// theme. Lifted from desktop's MainWindowViewModel.TryBuildInvertedLogo:
    /// Bgra8888/Unpremul preserves alpha so edges stay anti-aliased
    /// after the channel flip.
    /// </summary>
    private Bitmap LoadMascot()
    {
        if (_mascotLight is null)
        {
            try
            {
                // Bug-AND-011 / Medium-4 (2026-05-16 code review) —
                // dispose the asset stream after Bitmap copies the
                // bytes internally. Pre-fix leaked one stream per
                // process; minor on its own but a copy-paste hazard
                // for the icon-loader pattern reused across this file.
                using var stream = AssetLoader.Open(new Uri("avares://VPNRouter.Android/Assets/penguin_mascot.png"));
                _mascotLight = new Bitmap(stream);
            }
            catch
            {
                // Fallback transparent 1x1 — won't be visible but keeps
                // the layout from crashing.
                var wb = new WriteableBitmap(new PixelSize(1, 1), new Vector(96, 96),
                    PixelFormat.Bgra8888, AlphaFormat.Unpremul);
                _mascotLight = wb;
            }
        }
        if (ActualThemeVariant == ThemeVariant.Dark)
        {
            _mascotDark ??= TryBuildInverted(_mascotLight) ?? _mascotLight;
            return _mascotDark;
        }
        return _mascotLight;
    }

    private static Bitmap? TryBuildInverted(Bitmap source)
    {
        try
        {
            var size = source.PixelSize;
            var wb = new WriteableBitmap(size, source.Dpi, PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using var fb = wb.Lock();
            int byteCount = fb.RowBytes * size.Height;
            source.CopyPixels(new PixelRect(size), fb.Address, byteCount, fb.RowBytes);
            var bytes = new byte[byteCount];
            System.Runtime.InteropServices.Marshal.Copy(fb.Address, bytes, 0, byteCount);
            // BGRA pixels — invert B, G, R; leave A alone
            for (int i = 0; i + 3 < bytes.Length; i += 4)
            {
                bytes[i + 0] = (byte)(255 - bytes[i + 0]); // B
                bytes[i + 1] = (byte)(255 - bytes[i + 1]); // G
                bytes[i + 2] = (byte)(255 - bytes[i + 2]); // R
            }
            System.Runtime.InteropServices.Marshal.Copy(bytes, 0, fb.Address, byteCount);
            return wb;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Phase 4 — pill-style status chip (rounded background + colored
    /// label) for the sub-header VPN/Zapret/TG indicators. Mirrors
    /// desktop's chip pattern from MainWindow.axaml header.
    ///
    /// <para>v3.0 Phase 8.2 — takes brush KEYS (not brushes) so the
    /// foreground + background ride <see cref="DynamicResourceExtension"/>
    /// and auto-repaint on theme variant change.</para>
    /// </summary>
    private TextBlock MakeChip(string label, string bgKey, string fgKey)
    {
        // Wrapped Border preferred for rounded corners, but Avalonia
        // TextBlock + StackPanel layout is simpler for now. Return a
        // TextBlock styled as a tag — uses parent StackPanel's width.
        // Note: chips render as boxes, not pills, on this font size;
        // looks similar enough on phone screen at 9pt.
        var tb = new TextBlock
        {
            Text = label,
            FontSize = 9,
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(7, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        tb.BindToken(TextBlock.ForegroundProperty, fgKey);
        tb.BindToken(TextBlock.BackgroundProperty, bgKey);
        return tb;
    }

    private Avalonia.Controls.Button StyledSecondaryButton(string label)
    {
        var btn = new Avalonia.Controls.Button
        {
            Content = label,
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Padding = new Thickness(14, 7),
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
            BorderThickness = new Thickness(1),
        };
        btn.BindToken(Avalonia.Controls.Button.BackgroundProperty, "SurfaceRaisedBrush");
        btn.BindToken(Avalonia.Controls.Button.ForegroundProperty, "TextPrimaryBrush");
        btn.BindToken(Avalonia.Controls.Button.BorderBrushProperty, "BorderDefaultBrush");
        return btn;
    }

    // ── Event handlers ─────────────────────────────────────────────────

    private void OnConfigRowClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _formExpanded = !_formExpanded;
        if (_formCard is not null) _formCard.IsVisible = _formExpanded;
        if (_configRowChevron is not null) _configRowChevron.Text = _formExpanded ? "⌄" : "›";
    }

    private void OnConnectClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var activity = MainActivity.Instance;
        if (activity is null) return;
        if (MainActivity.IntendedConnected)
        {
            activity.RequestDisconnect();
        }
        else
        {
            // v2.32.0 desktop parity (2026-05-10): Connect implicitly persists
            // whatever is in the input field before requesting the tunnel —
            // mirrors SmpToggleConnectAsync. The Save button is gone from the
            // Simple page (subscriptions/servers managed in Advanced >
            // Subscriptions tab); typing a vless:// or subscription URL and
            // tapping Connect must still work. OnSaveClicked is a no-op when
            // the input matches what's already saved, so this is idempotent.
            // Skipped when the input is empty so an existing saved config
            // isn't wiped on a "just connect with what I had" tap.
            if (!string.IsNullOrWhiteSpace(_serverInput?.Text))
            {
                OnSaveClicked(sender, e);
                if (_serverInputError is not null && _serverInputError.IsVisible)
                {
                    return;
                }
            }
            // v3.0 Phase 7.1 — flip VPN chip to Connecting immediately so
            // the user gets feedback while the system VPN consent dialog
            // is on screen (most visible during first-launch consent
            // flow). IntentChanged(true) will follow and transition Off →
            // skipped → On in the normal happy path; on consent decline
            // or TUNNEL_ERROR it bounces back to Off.
            SetVpnChipState(ChipState.Connecting);
            UpdateZapretChipFromState();
            activity.RequestConnect();
        }
    }

    private void OnIntentChanged(bool connected)
    {
        Dispatcher.UIThread.Post(() => UpdateConnectionState(connected));
    }

    /// <summary>
    /// Bug-AND-011 / High-4 (2026-05-16 code review) — central
    /// subscribe / unsubscribe helper for static lifecycle events
    /// (<see cref="MainActivity.IntentChanged"/>,
    /// <see cref="MainActivity.TunnelErrorReported"/>). Pre-fix only
    /// the subscribe side was wired
    /// (OnFrameworkInitializationCompleted), so every reconstructed
    /// AndroidApp instance accumulated a subscriber on the static
    /// event, indefinitely retaining the previous visual tree +
    /// Bitmap cache. The static tracker below ensures only ONE
    /// AndroidApp has subscriptions live at a time — calling
    /// Attach on a new instance detaches the previous one
    /// automatically.
    /// </summary>
    private static AndroidApp? s_currentLifecycleSubscriber;
    private bool _lifecycleEventsAttached;
    private void AttachLifecycleEvents()
    {
        var prev = System.Threading.Interlocked.Exchange(ref s_currentLifecycleSubscriber, this);
        if (prev is not null && !ReferenceEquals(prev, this))
            prev.DetachLifecycleEvents();
        if (_lifecycleEventsAttached) return;
        _lifecycleEventsAttached = true;
        MainActivity.IntentChanged += OnIntentChanged;
        MainActivity.TunnelErrorReported += OnTunnelErrorReported;
    }
    private void DetachLifecycleEvents()
    {
        if (!_lifecycleEventsAttached) return;
        _lifecycleEventsAttached = false;
        try { MainActivity.IntentChanged -= OnIntentChanged; } catch { }
        try { MainActivity.TunnelErrorReported -= OnTunnelErrorReported; } catch { }
        // Bug-AND-011 / Low-1 — release the diagnostics timer alongside
        // event subscriptions so the retired AndroidApp drops its only
        // remaining strong reference path into Avalonia's dispatcher.
        DisposeDiagnosticsTimer();
    }

    private void UpdateConnectionState(bool connected)
    {
        if (_statusCard is null) return;

        if (connected)
        {
            // v3.0 Phase G step 1 (2026-05-09) — flip the shared StatusCard
            // into its On state. The internal Ellipse Fill resolves through
            // DynamicResource on SuccessSolidBrush, so a theme switch while
            // connected re-renders automatically (no manual rebind needed).
            _statusCard.IsOn = true;
            _statusCard.IsWarn = false;
            _statusCard.IsOff = false;
            _statusCard.Title = Localization.SimpleStatusTitleOn;
            _statusCard.Subtitle = Localization.SimpleStatusDescOn;
            if (_ctaConnect is not null) _ctaConnect.IsVisible = false;
            if (_ctaConnecting is not null) _ctaConnecting.IsVisible = false;
            if (_ctaDisconnect is not null) _ctaDisconnect.IsVisible = true;
            SetVpnChipState(ChipState.On);

            // v2.32.0 (AND-DIAG) — start uptime tracking + diagnostics
            // pump. Set _connectionStartedAt FIRST so the immediate first
            // tick (which renders uptime) sees a non-null value.
            _connectionStartedAt = DateTime.UtcNow;
            _lastHealthLogSize = -1;
            _lastHealthLogMTime = DateTime.MinValue;
            _firstProbePending = true;
            _lastHealthOk = false;
            // Clear any stale error from a previous attempt — a successful
            // reconnect supersedes whatever went wrong before.
            _lastError = null;
            if (_statusErrorOneLiner is not null) _statusErrorOneLiner.IsVisible = false;
            StartDiagnosticsTimer();
            // Surface the pending message immediately so the user sees the
            // status card respond to their tap, instead of waiting up to
            // 30 s for the first probe.
            ApplyHealthCheckDisplay();
        }
        else
        {
            _statusCard.IsOn = false;
            _statusCard.IsWarn = false;
            _statusCard.IsOff = true;
            _statusCard.Title = Localization.SimpleStatusTitleOff;
            _statusCard.Subtitle = Localization.SimpleStatusDescOff;
            if (_ctaConnect is not null) _ctaConnect.IsVisible = true;
            if (_ctaConnecting is not null) _ctaConnecting.IsVisible = false;
            if (_ctaDisconnect is not null) _ctaDisconnect.IsVisible = false;
            SetVpnChipState(ChipState.Off);

            // v2.32.0 (AND-DIAG) — reset uptime + hide health check.
            // Keep _lastError around if it was set in the same frame as
            // this disconnect (TUNNEL_ERROR fires before SetIntent(false))
            // so the error one-liner stays visible during the 30 s
            // window. The diagnostics timer keeps ticking briefly so the
            // 30 s auto-clear still runs even after disconnect.
            _connectionStartedAt = null;
            if (_statusHealthCheck is not null) _statusHealthCheck.IsVisible = false;
            if (_lastError is null)
            {
                StopDiagnosticsTimer();
            }
            else
            {
                // Make sure it stays running until the error window expires.
                StartDiagnosticsTimer();
            }
        }
        // v2.32.0 (AND-ZAPRET) — Zapret chip mirrors VPN phase when DPI
        // bypass is enabled, since the bypass is implemented inside the
        // sing-box outbound (no separate process). Recompute on every
        // VPN state transition.
        UpdateZapretChipFromState();
        UpdateConfigSummary();

        // AND-ADV-CHROME (2026-05-10) — flip the Advanced shell's
        // persistent footer (status dot + text + Start/Stop VPN button)
        // alongside the Simple page CTA, so the two surfaces stay in
        // lock-step even while the user is inside Advanced. Helper is
        // null-safe before BuildAdvancedShellOverlay has run.
        ApplyAdvancedFooterConnectionState(connected);
    }

    /// <summary>
    /// v3.0 Phase 7.1 (2026-05-04) — flip VPN chip background + foreground
    /// (and start/stop the Connecting pulse animation) to reflect the
    /// current tunnel lifecycle phase. Idempotent: calling with the same
    /// state is a no-op.
    ///
    /// <para>v3.0 Phase 8.2 (2026-05-07) — chip brushes go through
    /// <see cref="StyledElementResourceExtensions.BindToken"/> so they
    /// auto-repaint on theme variant change. The <paramref name="force"/>
    /// flag lets <see cref="ApplyTheme(string)"/> re-issue the bindings
    /// even when state hasn't changed (a theme flip needs to retain the
    /// active state but re-pick the new variant's color).</para>
    /// </summary>
    private void SetVpnChipState(ChipState state, bool force = false)
    {
        if (_vpnChip is null) return;
        if (_vpnChipState == state && !force) return;
        _vpnChipState = state;

        // Stop any in-flight pulse first — Connecting → On, Connecting → Off,
        // Off → On all need to clear the animation that was driving Opacity.
        // On a forced re-bind we still want to restart the pulse if state
        // is Connecting so the breathing animation stays in sync.
        // Bug-AND-011 / High-6 (2026-05-16 code review) — capture +
        // null + Cancel + Dispose. Pre-fix the CTS was Cancelled but
        // never Disposed, leaking the Timer + ManualResetEvent on
        // every state transition (and chips toggle on every Connect /
        // Disconnect / DPI bypass mode change).
        var prevVpnCts = _vpnPulseCts;
        _vpnPulseCts = null;
        try { prevVpnCts?.Cancel(); } catch { }
        prevVpnCts?.Dispose();
        _vpnChip.Opacity = 1.0;

        string bgKey, fgKey;
        switch (state)
        {
            case ChipState.On:
                bgKey = "SuccessBgBrush";
                fgKey = "SuccessFgBrush";
                break;
            case ChipState.Connecting:
                bgKey = "WarningBgBrush";
                fgKey = "WarningFgBrush";
                _vpnPulseCts = StartChipPulse(_vpnChip);
                break;
            default: // Off
                bgKey = "SurfaceSunkenBrush";
                fgKey = "TextMutedBrush";
                break;
        }
        _vpnChip.BindToken(TextBlock.BackgroundProperty, bgKey);
        _vpnChip.BindToken(TextBlock.ForegroundProperty, fgKey);
        // Bug #3 fix (2026-05-11) — mirror brushes onto the Advanced
        // shell header chip so both surfaces share live state. The chip
        // is null until the Advanced overlay has been built once; first
        // open's force-rebind path covers that case.
        if (_advVpnChip is not null)
        {
            _advVpnChip.Opacity = 1.0;
            _advVpnChip.BindToken(TextBlock.BackgroundProperty, bgKey);
            _advVpnChip.BindToken(TextBlock.ForegroundProperty, fgKey);
        }
    }

    /// <summary>
    /// v2.32.0 (AND-ZAPRET, 2026-05-07) — same shape as
    /// <see cref="SetVpnChipState"/> but for the Zapret chip. Driven by
    /// <see cref="UpdateZapretChipFromState"/>, which composes the
    /// stored <c>dpi_bypass_mode</c> with the live VPN connection state
    /// into a chip color:
    /// <list type="bullet">
    ///   <item>Off: DPI bypass disabled OR VPN not connected (the
    ///   bypass mechanism is in-tunnel, so it can't be active when
    ///   the tunnel is down even if the user enabled it).</item>
    ///   <item>Connecting: DPI bypass enabled AND VPN currently in
    ///   the Connecting phase (pulse warning).</item>
    ///   <item>On: DPI bypass enabled AND VPN connected (success
    ///   green) — the tls_fragment block is now in libbox's outbound
    ///   dialer settings and packets are being fragmented.</item>
    /// </list>
    /// </summary>
    private void SetZapretChipState(ChipState state, bool force = false)
    {
        if (_zapretChip is null) return;
        if (_zapretChipState == state && !force) return;
        _zapretChipState = state;

        // Bug-AND-011 / High-6 (2026-05-16) — same CTS dispose pattern
        // as SetVpnChipState.
        var prevZapretCts = _zapretPulseCts;
        _zapretPulseCts = null;
        try { prevZapretCts?.Cancel(); } catch { }
        prevZapretCts?.Dispose();
        _zapretChip.Opacity = 1.0;

        string bgKey, fgKey;
        switch (state)
        {
            case ChipState.On:
                bgKey = "SuccessBgBrush";
                fgKey = "SuccessFgBrush";
                break;
            case ChipState.Connecting:
                bgKey = "WarningBgBrush";
                fgKey = "WarningFgBrush";
                _zapretPulseCts = StartChipPulse(_zapretChip);
                break;
            default: // Off
                bgKey = "SurfaceSunkenBrush";
                fgKey = "TextMutedBrush";
                break;
        }
        _zapretChip.BindToken(TextBlock.BackgroundProperty, bgKey);
        _zapretChip.BindToken(TextBlock.ForegroundProperty, fgKey);
        // Bug #3 fix (2026-05-11) — mirror brushes onto the Advanced
        // shell header chip (same pattern as SetVpnChipState above).
        if (_advZapretChip is not null)
        {
            _advZapretChip.Opacity = 1.0;
            _advZapretChip.BindToken(TextBlock.BackgroundProperty, bgKey);
            _advZapretChip.BindToken(TextBlock.ForegroundProperty, fgKey);
        }
    }

    /// <summary>
    /// v2.32.0 (AND-ZAPRET) — recompute the Zapret chip color from
    /// (DPI bypass mode, VPN intent state, VPN chip phase). Called
    /// whenever any of the three inputs changes.
    /// </summary>
    private void UpdateZapretChipFromState()
    {
        // DPI bypass off → chip always off, regardless of VPN state.
        var mode = AndroidStorage.GetDpiBypassMode();
        if (string.IsNullOrEmpty(mode) || string.Equals(mode, "off",
            System.StringComparison.OrdinalIgnoreCase))
        {
            SetZapretChipState(ChipState.Off);
            return;
        }

        // DPI bypass enabled — chip mirrors the VPN chip's phase.
        // _vpnChipState is the most accurate signal because it goes
        // through Connecting on click before IntendedConnected flips.
        switch (_vpnChipState)
        {
            case ChipState.Connecting:
                SetZapretChipState(ChipState.Connecting);
                break;
            case ChipState.On:
                SetZapretChipState(ChipState.On);
                break;
            default:
                SetZapretChipState(ChipState.Off);
                break;
        }
    }

    /// <summary>
    /// v3.0 Phase 7.1 — drive a soft "breathing" Opacity animation
    /// (1.0 ↔ 0.55 over 1.2 s, cycling indefinitely). Returns the CTS so
    /// callers can store + cancel it (one CTS per chip — VPN and Zapret
    /// chips each have their own field).
    ///
    /// <para>v2.32.0 (AND-ZAPRET) — refactored from a hard-coded
    /// <c>_vpnPulseCts</c> assignment so both chips can reuse the same
    /// animation. Old call site assigned the cts inside the method;
    /// new contract is "call site owns the CTS field, helper returns
    /// what to store".</para>
    /// </summary>
    private System.Threading.CancellationTokenSource StartChipPulse(Visual target)
    {
        var cts = new System.Threading.CancellationTokenSource();
        var anim = new Avalonia.Animation.Animation
        {
            Duration = System.TimeSpan.FromMilliseconds(1200),
            IterationCount = Avalonia.Animation.IterationCount.Infinite,
            PlaybackDirection = Avalonia.Animation.PlaybackDirection.Alternate,
            Easing = new Avalonia.Animation.Easings.QuadraticEaseInOut(),
            Children =
            {
                new Avalonia.Animation.KeyFrame
                {
                    Cue = new Avalonia.Animation.Cue(0d),
                    Setters = { new Avalonia.Styling.Setter(Visual.OpacityProperty, 1.0) },
                },
                new Avalonia.Animation.KeyFrame
                {
                    Cue = new Avalonia.Animation.Cue(1d),
                    Setters = { new Avalonia.Styling.Setter(Visual.OpacityProperty, 0.55) },
                },
            },
        };
        // Fire-and-forget — the animation drives the visual and gets
        // cancelled when cts.Cancel() is called from SetVpnChipState
        // / SetZapretChipState. The cts itself is owned by the caller.
        _ = anim.RunAsync(target, cts.Token);
        return cts;
    }

    // ── v2.32.0 (AND-DIAG, 2026-05-07) — runtime diagnostics pump ──────

    /// <summary>
    /// Receives ACTION_TUNNEL_ERROR broadcasts via the static
    /// <see cref="MainActivity.TunnelErrorReported"/> event. The receiver
    /// fires from a binder dispatch thread, so we marshal to the UI
    /// thread before mutating Avalonia state. The actual rendering lives
    /// in <see cref="ApplyErrorOneLinerDisplay"/> + the diagnostics timer
    /// loop, which clears the message after
    /// <see cref="ErrorDisplayWindow"/> elapses.
    /// </summary>
    private void OnTunnelErrorReported(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            _lastError = message.Trim();
            _lastErrorAt = DateTime.UtcNow;
            ApplyErrorOneLinerDisplay();
            // Keep the timer alive so the 30 s auto-clear runs even when
            // we're disconnected (UpdateConnectionState(false) preserves
            // the timer when _lastError is set).
            StartDiagnosticsTimer();
        });
    }

    private void StartDiagnosticsTimer()
    {
        if (_diagnosticsTimer is not null && _diagnosticsTimer.IsEnabled) return;
        _diagnosticsTimer ??= new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            (_, _) => OnDiagnosticsTick());
        _diagnosticsTimer.Start();
        // Render an immediate first frame so the title shows "0:00" /
        // "0:01" the second the user taps Connect, instead of waiting a
        // full second for the first DispatcherTimer tick.
        OnDiagnosticsTick();
    }

    private void StopDiagnosticsTimer()
    {
        if (_diagnosticsTimer is null) return;
        _diagnosticsTimer.Stop();
        if (_statusHealthCheck is not null) _statusHealthCheck.IsVisible = false;
        // Title resets to plain "Not connected" inside UpdateConnectionState,
        // so we don't touch _statusCard.Title here.
    }

    /// <summary>
    /// Bug-AND-011 / Low-1 (2026-05-16) — explicitly tear down the
    /// diagnostics DispatcherTimer when the AndroidApp instance is
    /// being abandoned (lifecycle event swap or harness rebuild). The
    /// timer was never released pre-fix; under recreation paths every
    /// retired AndroidApp kept its own timer rooted in the static
    /// MainActivity.* events. Idempotent + null-safe.
    /// </summary>
    private void DisposeDiagnosticsTimer()
    {
        if (_diagnosticsTimer is null) return;
        try { _diagnosticsTimer.Stop(); } catch { /* best-effort */ }
        _diagnosticsTimer = null;
    }

    private void OnDiagnosticsTick()
    {
        try
        {
            // 1. Uptime — refresh title every tick while connected.
            //
            // Bug-AND-006 (2026-05-16) — only mutate Avalonia text
            // properties when the formatted string actually changed.
            // The 1 Hz tick fires twice within the same second under
            // dispatcher contention, and even Avalonia's equality
            // guards on TextBlock.Text/StyledProperty still walk the
            // setter path to compare strings. On budget Android
            // devices that path was a measurable contributor to the
            // user-reported overheating.
            if (_connectionStartedAt is DateTime startUtc)
            {
                var elapsed = DateTime.UtcNow - startUtc;
                var uptimeTitle = string.Format(
                    Localization.SimpleStatusTitleOnWithUptime,
                    FormatUptime(elapsed));
                if (!string.Equals(uptimeTitle, _lastFormattedUptimeTitle, System.StringComparison.Ordinal))
                {
                    _lastFormattedUptimeTitle = uptimeTitle;
                    if (_statusCard is not null)
                        _statusCard.Title = uptimeTitle;
                    // AND-ADV-CHROME (2026-05-10) — mirror the uptime suffix
                    // into the Advanced shell's footer status text so the
                    // "Connected · M:SS" copy matches between Simple +
                    // Advanced surfaces. Bug-AND-006 — skip the write when
                    // the Advanced shell is collapsed (the TextBlock is
                    // off-screen + culled, but the property setter still
                    // raises an InvalidateMeasure walk through its parent
                    // tree which we can avoid entirely).
                    if (_advFooterStatusText is not null
                        && _advShellOverlay is not null
                        && _advShellOverlay.IsVisible)
                    {
                        _advFooterStatusText.Text = uptimeTitle;
                    }
                }
            }
            else
            {
                _lastFormattedUptimeTitle = null;
            }

            // 2. Health probe — every 30 s, only while connected. The first
            // probe fires HealthProbeInterval after Connect; before that
            // we show "awaiting first check" so the surface is not blank.
            if (_connectionStartedAt is not null)
            {
                var sinceLastProbe = DateTime.UtcNow - _lastHealthProbeAt;
                var connectedFor = DateTime.UtcNow - _connectionStartedAt.Value;
                var dueForProbe = _lastHealthProbeAt == DateTime.MinValue
                    ? connectedFor >= HealthProbeInterval
                    : sinceLastProbe >= HealthProbeInterval;
                if (dueForProbe) RunHealthProbe();
                ApplyHealthCheckDisplay();
            }

            // 3. Error one-liner — auto-clear after 30 s.
            if (_lastError is not null)
            {
                if (DateTime.UtcNow - _lastErrorAt >= ErrorDisplayWindow)
                {
                    _lastError = null;
                    if (_statusErrorOneLiner is not null) _statusErrorOneLiner.IsVisible = false;
                    // If we're disconnected and the error has cleared,
                    // there's nothing left to drive — stop the timer.
                    if (_connectionStartedAt is null) StopDiagnosticsTimer();
                }
            }
        }
        catch
        {
            // Diagnostics rendering must never crash the app — swallow
            // and let the next tick try again.
        }
    }

    /// <summary>
    /// Read sing-box log file's size + last-write time and decide whether
    /// the tunnel is still actively writing. We pick log delta (rather
    /// than a TCP probe to the proxy) because:
    /// <list type="bullet">
    ///   <item>It's purely local file I/O — no network round-trip needed,
    ///   so the probe itself can't time out under poor connectivity.</item>
    ///   <item>sing-box writes regular DNS-resolution + TCP-connect lines
    ///   while routing real traffic, so a healthy tunnel = a growing log.</item>
    ///   <item>It works for every protocol (VLESS / Hysteria2 / TUIC / SS)
    ///   without needing per-protocol probe machinery.</item>
    /// </list>
    /// </summary>
    private void RunHealthProbe()
    {
        _lastHealthProbeAt = DateTime.UtcNow;
        try
        {
            var ctx = global::Android.App.Application.Context;
            var extDir = ctx.GetExternalFilesDir(null);
            if (extDir is null)
            {
                _lastHealthOk = false;
                return;
            }
            var logPath = System.IO.Path.Combine(extDir.AbsolutePath, "singbox.log");
            if (!System.IO.File.Exists(logPath))
            {
                _lastHealthOk = false;
                return;
            }

            var info = new System.IO.FileInfo(logPath);
            var size = info.Length;
            var mtime = info.LastWriteTimeUtc;
            var grew = _lastHealthLogSize >= 0 && size > _lastHealthLogSize;
            // mtime is also a healthy signal — covers the case where a
            // log rotation truncates the file (size shrinks) but writing
            // has resumed normally.
            var recent = (DateTime.UtcNow - mtime) < HealthStaleThreshold;

            _lastHealthOk = grew || recent;
            _lastHealthLogSize = size;
            _lastHealthLogMTime = mtime;
            _firstProbePending = false;
        }
        catch
        {
            _lastHealthOk = false;
            _firstProbePending = false;
        }
    }

    private void ApplyHealthCheckDisplay()
    {
        if (_statusHealthCheck is null) return;
        if (_connectionStartedAt is null)
        {
            _statusHealthCheck.IsVisible = false;
            return;
        }
        _statusHealthCheck.IsVisible = true;

        if (_firstProbePending && _lastHealthProbeAt == DateTime.MinValue)
        {
            _statusHealthCheck.Text = Localization.DiagHealthCheckPending;
            _statusHealthCheck.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");
            return;
        }

        if (_lastHealthOk)
        {
            var ago = (int)Math.Max(0, (DateTime.UtcNow - _lastHealthProbeAt).TotalSeconds);
            _statusHealthCheck.Text = string.Format(Localization.DiagHealthCheckOk, ago);
            _statusHealthCheck.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");
        }
        else
        {
            _statusHealthCheck.Text = Localization.DiagHealthCheckStale;
            _statusHealthCheck.BindToken(TextBlock.ForegroundProperty, "WarningFgBrush");
        }
    }

    private void ApplyErrorOneLinerDisplay()
    {
        if (_statusErrorOneLiner is null) return;
        if (string.IsNullOrEmpty(_lastError))
        {
            _statusErrorOneLiner.IsVisible = false;
            return;
        }
        _statusErrorOneLiner.Text = string.Format(Localization.DiagErrorOneLiner, _lastError);
        _statusErrorOneLiner.IsVisible = true;
    }

    /// <summary>
    /// Auto-switch uptime format. Under 1 hour: "M:SS" (e.g. "0:42",
    /// "12:05"). At/over 1 hour: "H:MM:SS" (e.g. "1:23:45"). Mirrors the
    /// pattern users see on stock Android in the lock-screen / system
    /// VPN-key tile (and Slack / WhatsApp call timers).
    /// </summary>
    private static string FormatUptime(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        if (elapsed.TotalHours >= 1)
        {
            return string.Format("{0}:{1:D2}:{2:D2}",
                (int)elapsed.TotalHours, elapsed.Minutes, elapsed.Seconds);
        }
        return string.Format("{0}:{1:D2}", elapsed.Minutes, elapsed.Seconds);
    }

    /// <summary>
    /// Bug-AND-007b (2026-05-16) — hide the Android soft keyboard
    /// directly through Android's <c>InputMethodManager</c>. Used by
    /// the scroll-gesture handler in the main page builder: when a
    /// swipe starts inside a focused TextBox, Avalonia's own
    /// <c>Focus()</c> shuffle isn't enough to dismiss the IME on
    /// Mono.Android — the keyboard show was already queued by the
    /// TextBox peer on PointerPressed. Calling
    /// <c>hideSoftInputFromWindow</c> on the activity's current window
    /// token is the deterministic way to tear it down.
    /// <para>Best-effort: any exception is swallowed (e.g. if the
    /// activity is mid-finishing or the service has no window token).
    /// The worst case is the keyboard stays up, which is the
    /// pre-fix behaviour — no regression.</para>
    /// </summary>
    private static void HideAndroidSoftKeyboard()
    {
        try
        {
            var ctx = global::Android.App.Application.Context;
            if (ctx is null) return;
            var imm = (global::Android.Views.InputMethods.InputMethodManager?)
                ctx.GetSystemService(global::Android.Content.Context.InputMethodService);
            if (imm is null) return;
            var activity = global::VPNRouter.Android.MainActivity.Instance;
            var token = activity?.Window?.DecorView?.WindowToken;
            if (token is null) return;
            imm.HideSoftInputFromWindow(token, global::Android.Views.InputMethods.HideSoftInputFlags.None);
        }
        catch
        {
            // Best-effort — don't let a stale window token surface a
            // crash inside the scroll-gesture critical path.
        }
    }

    private void UpdateConfigSummary()
    {
        if (_configRowValue is null) return;
        var mode = _fullRadio?.IsChecked == true ? Localization.SmpFullOption : Localization.SmpSplitOption;
        // v2.32.0 (AND-CC) — three-way source label: subscription /
        // manual / custom JSON. Pre-CC was binary based on whether
        // subscription_url was non-null.
        string src;
        switch (AndroidStorage.GetConfigMode())
        {
            case "subscribe":
                src = Localization.SmpSourceSubscription;
                break;
            case "custom":
                src = Localization.CcSourceCustom;
                break;
            default:
                src = Localization.SmpSourceManual;
                break;
        }
        _configRowValue.Text = $"{src} · {mode.ToLower()}";
    }

    private void OnSaveClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_serverInput is null || _serverInputError is null) return;
        var raw = (_serverInput.Text ?? string.Empty).Trim();
        _serverInputError.IsVisible = false;

        if (string.IsNullOrWhiteSpace(raw))
        {
            AndroidStorage.SetVlessUri(null);
            AndroidStorage.SetSubscriptionUrl(null);
            AndroidStorage.SetServers(null);
            AndroidStorage.SetSelectedServerName(null);
            // Keep ConfigMode at whatever the user picked (don't auto-flip
            // to "custom") — empty input is just a clear, not a switch.
            _cachedServers = new List<VlessServerEntry>();
            UpdateServerListView();
            UpdateConfigSummary();
            return;
        }

        // v3.0 Phase 6.4 (2026-05-04) — accept all supported share-link
        // schemes (vless, hysteria2, hy2, tuic, ss), not just vless. The
        // parser does the actual scheme-dispatch; we only need a coarse
        // is-this-a-share-link gate before deciding URI vs subscription.
        if (ServerUriParser.IsSupportedScheme(raw))
        {
            try
            {
                var parsed = ServerUriParser.Parse(raw);
                if (string.IsNullOrEmpty(parsed.Server) || parsed.Port <= 0)
                {
                    _serverInputError.Text = Localization.SaveStatusUriBadHost;
                    _serverInputError.IsVisible = true;
                    return;
                }
                AndroidStorage.SetVlessUri(raw);
                AndroidStorage.SetSubscriptionUrl(null);
                AndroidStorage.SetServers(null);
                AndroidStorage.SetSelectedServerName(null);
                AndroidStorage.SetConfigMode("manual");
                _ccMode = "manual";
                ApplyCcModeVisuals();
                _cachedServers = new List<VlessServerEntry>();
                UpdateServerListView();
                UpdateConfigSummary();
            }
            catch (Exception ex)
            {
                _serverInputError.Text = string.Format(Localization.SaveStatusUriInvalid, ex.Message);
                _serverInputError.IsVisible = true;
            }
            return;
        }

        if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            AndroidStorage.SetSubscriptionUrl(raw);
            AndroidStorage.SetVlessUri(null);
            AndroidStorage.SetConfigMode("subscribe");
            _ccMode = "subscribe";
            ApplyCcModeVisuals();
            UpdateConfigSummary();
            return;
        }

        _serverInputError.Text = Localization.SaveStatusUnknown;
        _serverInputError.IsVisible = true;
    }

    // ── v2.32.0 (AND-CC) — Custom sing-box JSON mode ───────────────────

    private void OnCcModeSubClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => SetCcMode("subscribe");
    private void OnCcModeManualClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => SetCcMode("manual");
    private void OnCcModeCustomClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => SetCcMode("custom");

    private void SetCcMode(string mode)
    {
        if (mode != "subscribe" && mode != "manual" && mode != "custom")
            return;
        if (_ccMode == mode) return;
        _ccMode = mode;
        AndroidStorage.SetConfigMode(mode);
        ApplyCcModeVisuals();
        UpdateConfigSummary();
    }

    /// <summary>
    /// Repaints the segmented mode selector + flips visibility between
    /// the URI input section and the custom-JSON section. Mirrors the
    /// per-app picker's <see cref="ApplyPickerModeVisuals"/> pattern.
    /// </summary>
    private void ApplyCcModeVisuals()
    {
        StyleSegment(_ccModeSubBtn, _ccMode == "subscribe");
        StyleSegment(_ccModeManualBtn, _ccMode == "manual");
        StyleSegment(_ccModeCustomBtn, _ccMode == "custom");
        if (_ccUriSection is not null) _ccUriSection.IsVisible = _ccMode != "custom";
        if (_ccCustomSection is not null) _ccCustomSection.IsVisible = _ccMode == "custom";
    }

    private void OnCcValidateClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_ccCustomInput is null || _ccCustomStatus is null) return;
        var raw = (_ccCustomInput.Text ?? string.Empty).Trim();
        _ccCustomStatus.IsVisible = true;

        if (string.IsNullOrEmpty(raw))
        {
            _ccCustomStatus.Text = Localization.CcSaveStatusEmpty;
            _ccCustomStatus.BindToken(TextBlock.ForegroundProperty, "DangerFgBrush");
            return;
        }

        try
        {
            var (isValid, errors) = VPNRouter.Core.Services.CustomConfigInjector.Validate(raw);
            if (!isValid)
            {
                _ccCustomStatus.Text = string.Format(
                    Localization.CcValidationFailed,
                    string.Join("; ", errors));
                _ccCustomStatus.BindToken(TextBlock.ForegroundProperty, "DangerFgBrush");
                return;
            }
            var (protocols, server) = VPNRouter.Core.Services.CustomConfigInjector.ParseConfigInfo(raw);
            _ccCustomStatus.Text = string.Format(Localization.CcValidationOk, protocols, server);
            _ccCustomStatus.BindToken(TextBlock.ForegroundProperty, "SuccessFgBrush");
        }
        catch (Exception ex)
        {
            _ccCustomStatus.Text = string.Format(Localization.CcValidationParseError, ex.Message);
            _ccCustomStatus.BindToken(TextBlock.ForegroundProperty, "DangerFgBrush");
        }
    }

    private void OnCcSaveCustomClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_ccCustomInput is null || _ccCustomStatus is null) return;
        var raw = (_ccCustomInput.Text ?? string.Empty).Trim();
        _ccCustomStatus.IsVisible = true;

        if (string.IsNullOrEmpty(raw))
        {
            _ccCustomStatus.Text = Localization.CcSaveStatusEmpty;
            _ccCustomStatus.BindToken(TextBlock.ForegroundProperty, "DangerFgBrush");
            return;
        }

        var (isValid, errors) = VPNRouter.Core.Services.CustomConfigInjector.Validate(raw);
        AndroidStorage.SetCustomConfigJson(raw);
        AndroidStorage.SetConfigMode("custom");
        _ccMode = "custom";
        ApplyCcModeVisuals();
        UpdateConfigSummary();

        if (!isValid)
        {
            // Save anyway so the user doesn't lose their paste; they can
            // fix-and-resave. sing-box itself surfaces the actual error
            // when Connect runs.
            _ccCustomStatus.Text = string.Format(
                Localization.CcSaveStatusInvalid + " ({0})",
                string.Join("; ", errors));
            _ccCustomStatus.BindToken(TextBlock.ForegroundProperty, "WarningFgBrush");
            return;
        }

        _ccCustomStatus.Text = Localization.CcSaveStatusOk;
        _ccCustomStatus.BindToken(TextBlock.ForegroundProperty, "SuccessFgBrush");
    }

    private void OnCcClearCustomClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_ccCustomInput is not null) _ccCustomInput.Text = string.Empty;
        if (_ccCustomStatus is not null) _ccCustomStatus.IsVisible = false;
        AndroidStorage.SetCustomConfigJson(null);
        // Don't flip mode away from "custom" — user might be about to
        // paste a different config. UpdateConfigSummary still shows
        // "custom JSON · split/full".
        UpdateConfigSummary();
    }

    private async void OnRefreshClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_serverInputError is null) return;
        var url = AndroidStorage.GetSubscriptionUrl();
        if (string.IsNullOrEmpty(url) && _serverInput is not null)
        {
            var raw = (_serverInput.Text ?? string.Empty).Trim();
            if (raw.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                AndroidStorage.SetSubscriptionUrl(raw);
                url = raw;
            }
        }
        if (string.IsNullOrWhiteSpace(url))
        {
            _serverInputError.Text = Localization.RefreshNeedsUrl;
            _serverInputError.IsVisible = true;
            return;
        }

        _serverInputError.IsVisible = false;
        try
        {
            var servers = await SubscriptionFetcher.FetchAsync(url, logger: null, ct: System.Threading.CancellationToken.None).ConfigureAwait(true);
            var list = new List<VlessServerEntry>(servers);
            AndroidStorage.SetServers(list);
            _cachedServers = list;
            UpdateServerListView();
            var prevSelected = AndroidStorage.GetSelectedServerName();
            var hasPrev = !string.IsNullOrEmpty(prevSelected) &&
                          list.Exists(s => string.Equals(s.Name, prevSelected, StringComparison.OrdinalIgnoreCase));
            if (!hasPrev && list.Count > 0)
            {
                AndroidStorage.SetSelectedServerName(list[0].Name);
                if (_serverList is not null) _serverList.SelectedIndex = 0;
            }
            UpdateConfigSummary();
        }
        catch (Exception ex)
        {
            _serverInputError.Text = string.Format(Localization.RefreshFailed, ex.Message);
            _serverInputError.IsVisible = true;
        }
    }

    private void OnServerSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_serverList?.SelectedItem is VlessServerEntry entry)
            AndroidStorage.SetSelectedServerName(entry.Name);
    }

    private async void ReloadServerList()
    {
        // v3.0 Phase 7.6 (2026-05-04) — disk + JSON deserialize off the
        // UI thread. SharedPreferences GetString is fast (cached), but
        // JsonConvert.DeserializeObject<List<VlessServerEntry>> on a
        // 100-entry subscription cache can stall the UI for 100-200 ms
        // on slower phones, contributing to the "app lags" complaint.
        // Move to Task.Run; UI updates on the captured context.
        try
        {
            _cachedServers = await System.Threading.Tasks.Task.Run(AndroidStorage.GetServers);
        }
        catch
        {
            _cachedServers = new List<VlessServerEntry>();
        }
        UpdateServerListView();
    }

    private void UpdateServerListView()
    {
        if (_serverList is null || _serverListHeader is null) return;
        var visible = _cachedServers.Count > 0;
        _serverList.IsVisible = visible;
        _serverListHeader.IsVisible = visible;
        _serverList.ItemsSource = _cachedServers;
        _serverList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<VlessServerEntry>(
            (item, _) =>
            {
                var name = new TextBlock
                {
                    Text = string.IsNullOrEmpty(item?.Name) ? (item?.Server ?? "?") : item.Name,
                    FontSize = 12,
                    FontWeight = FontWeight.Medium,
                };
                name.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                var sub = new TextBlock
                {
                    Text = $"{item?.Server}:{item?.Port}  ·  {item?.Protocol ?? "vless"}",
                    FontSize = 10,
                };
                sub.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");
                return new StackPanel
                {
                    Spacing = 2,
                    Margin = new Thickness(8, 6),
                    Children = { name, sub }
                };
            }, supportsRecycling: true);
        var sel = AndroidStorage.GetSelectedServerName();
        if (!string.IsNullOrEmpty(sel))
        {
            for (int i = 0; i < _cachedServers.Count; i++)
            {
                if (string.Equals(_cachedServers[i].Name, sel, StringComparison.OrdinalIgnoreCase))
                {
                    _serverList.SelectedIndex = i;
                    break;
                }
            }
        }
    }

    private void OnAdvCardClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // AND-MIGRATE-OVERLAYS (2026-05-09): the Simple-page «Расширенные
        // настройки ▸» CTA opens the Advanced shell on the Servers tab —
        // matches desktop MainWindow's left-nav default landing ordering.
        // From there the user can switch to Subscriptions / Apps /
        // Network / DPI bypass / Telegram / Public configs without
        // bouncing back to the kebab.
        OpenAdvancedShell(AdvancedTab.Servers);
    }

    /// <summary>
    /// v2.32.0 parity audit F-02 row 11 (2026-05-09) — build an inline
    /// "Start with system" link card for the main scroller. Style mirrors
    /// the autostart card on desktop SimplePage.axaml: title + subtitle
    /// + small chevron, full-width tappable button. Clicking opens the
    /// existing Settings overlay (already has the Autostart sub-section);
    /// pre-fix this surface was only reachable via kebab → Settings →
    /// scroll, which the parity audit flagged as a discoverability gap
    /// vs. the desktop inline card.
    /// </summary>
    private Control BuildAutostartInlineCard(double radiusSm)
    {
        // Bug-AND-014 (2026-05-16) — promote title + subtitle to
        // instance fields so ToggleLanguageAndRefresh can update them.
        _autostartCardTitleText = new TextBlock
        {
            Text = Localization.SmpAutostartCardTitle,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
        };
        _autostartCardTitleText.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        _autostartCardSubText = new TextBlock
        {
            Text = Localization.SmpAutostartCardSubtitle,
            FontSize = 9,
            TextWrapping = TextWrapping.Wrap,
        };
        _autostartCardSubText.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");

        var chevron = new TextBlock
        {
            Text = "›",
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        chevron.BindToken(TextBlock.ForegroundProperty, "AccentFgBrush");

        var inner = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 10,
            Margin = new Thickness(10, 8),
        };
        var stack = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _autostartCardTitleText, _autostartCardSubText },
        };
        Grid.SetColumn(stack, 0);
        Grid.SetColumn(chevron, 1);
        inner.Children.Add(stack);
        inner.Children.Add(chevron);

        var btn = new Avalonia.Controls.Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(radiusSm),
            Content = inner,
        };
        btn.BindToken(Avalonia.Controls.Button.BackgroundProperty, "SurfaceSunkenBrush");
        btn.BindToken(Avalonia.Controls.Button.BorderBrushProperty, "BorderDefaultBrush");
        btn.Click += (_, _) => ShowSettings();
        return btn;
    }

    private void OnMenuExportConfigClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        ShowExportOverlay();
    }

    private void OnMenuImportConfigClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        ShowImportOverlay();
    }

    // ── Header kebab menu ──────────────────────────────────────────────

    /// <summary>
    /// v3.0 Phase 7.2 — generic factory for a kebab-menu row. Stretches
    /// horizontally, left-aligns content, transparent background. The
    /// click handler is optional (e.g. version row is non-interactive).
    /// </summary>
    private Avalonia.Controls.Button MakeMenuItem(
        string label,
        string foregroundKey,
        EventHandler<Avalonia.Interactivity.RoutedEventArgs>? onClick)
    {
        // F-12 kebab visual parity (2026-05-09): mirrors desktop
        // Style Selector="Button.menu-item" — FontSize=11, Padding=10,7,
        // CornerRadius=RadiusXs (3). Pre-fix Android used 12px / 14,8 /
        // 0px which made rows visibly taller and wider than desktop.
        var btn = new Avalonia.Controls.Button
        {
            Content = label,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 7),
            FontSize = 11,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
            IsHitTestVisible = onClick is not null,
        };
        btn.BindToken(Avalonia.Controls.Button.ForegroundProperty, foregroundKey);
        if (onClick is not null) btn.Click += onClick;
        return btn;
    }

    /// <summary>
    /// v3.0 Phase 7.3 (2026-05-04) — segment button factory. Mirrors
    /// desktop's <c>Classes="segment" Classes.active="..."</c> CSS:
    /// active segment uses the accent surface + accent foreground;
    /// inactive uses the base surface + secondary foreground.
    /// </summary>
    private Avalonia.Controls.Button MakeSegmentButton(
        string label,
        bool active,
        EventHandler<Avalonia.Interactivity.RoutedEventArgs> onClick)
    {
        var btn = new Avalonia.Controls.Button
        {
            Content = label,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0, 6),
            FontSize = 12,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
        };
        // v3.0 Phase 8.2 — initial bindings; StyleSegmentButton replaces
        // them on selection change so the active+inactive split moves
        // (token keys differ between the two states).
        StyleSegmentButton(btn, active);
        btn.Click += onClick;
        return btn;
    }

    /// <summary>
    /// v3.0 Phase 7.3 — wrap two segment buttons in a 2-column grid with
    /// equal width and small gap, mirroring desktop's
    /// <c>Grid ColumnDefinitions="*,*" ColumnSpacing="2"</c>.
    /// </summary>
    private Grid MakeSegmentRow(Avalonia.Controls.Button left, Avalonia.Controls.Button right)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 4,
            Margin = new Thickness(14, 4, 14, 4),
        };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    /// <summary>
    /// v3.0 Phase 7.3 — overload of <see cref="AppendMenuSection"/> that
    /// accepts arbitrary <see cref="Control"/> items (not just Buttons),
    /// so segment-control rows fit the same flow.
    /// </summary>
    private void AppendMenuSectionWithControls(
        StackPanel stack,
        string headerText,
        Control[] items)
    {
        // F-12 kebab visual parity (2026-05-09): section label spec mirrors
        // desktop Style Selector="TextBlock.section-label" — FontSize=9,
        // SemiBold, TextMutedBrush, Margin="8,6,8,4". Divider moved from
        // immediately under the header (was acting as a header underline)
        // to AFTER the section's items (acts as inter-section separator,
        // matches desktop Border Classes="menu-divider"/ pattern).
        var header = new TextBlock
        {
            Text = headerText,
            FontSize = 9,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(8, 6, 8, 4),
        };
        header.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");
        if (headerText == Localization.MenuSectionView) _menuSectionView = header;
        else if (headerText == Localization.MenuSectionDiagnostics) _menuSectionDiagnostics = header;
        else if (headerText == Localization.MenuSectionTroubleshooting) _menuSectionTroubleshooting = header;
        else if (headerText == Localization.MenuSectionAbout) _menuSectionAbout = header;
        else if (headerText == Localization.MenuSectionFreeConfigs) _menuSectionFreeConfigs = header;
        else if (headerText == Localization.MenuSectionProfiles) _menuSectionProfiles = header;
        else if (headerText == Localization.MenuSectionTools) _menuSectionTools = header;

        stack.Children.Add(header);

        foreach (var item in items)
        {
            stack.Children.Add(item);
        }

        AppendMenuDivider(stack);
    }

    /// <summary>
    /// v3.0 Phase 7.2 — append a section to the kebab menu stack:
    /// header TextBlock + thin divider + the supplied items + bottom
    /// spacer. Section header TextBlocks are stored on the field
    /// (_menuSectionView etc.) so language toggle can refresh them.
    /// </summary>
    private void AppendMenuSection(
        StackPanel stack,
        string headerText,
        Avalonia.Controls.Button[] items)
    {
        // F-12 kebab visual parity (2026-05-09): mirrors desktop section-label
        // (FontSize=9 SemiBold TextMutedBrush, Margin=8,6,8,4) and moves the
        // divider to AFTER the section so it separates this section from the
        // next, instead of underlining the header. See AppendMenuSectionWithControls
        // above for the same structure for the View segments section.
        var header = new TextBlock
        {
            Text = headerText,
            FontSize = 9,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(8, 6, 8, 4),
        };
        header.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");
        // Cache by header text so ToggleLanguageAndRefresh can find it.
        if (headerText == Localization.MenuSectionView) _menuSectionView = header;
        else if (headerText == Localization.MenuSectionDiagnostics) _menuSectionDiagnostics = header;
        else if (headerText == Localization.MenuSectionTroubleshooting) _menuSectionTroubleshooting = header;
        else if (headerText == Localization.MenuSectionAbout) _menuSectionAbout = header;
        else if (headerText == Localization.MenuSectionFreeConfigs) _menuSectionFreeConfigs = header;
        else if (headerText == Localization.MenuSectionProfiles) _menuSectionProfiles = header;
        else if (headerText == Localization.MenuSectionTools) _menuSectionTools = header;

        stack.Children.Add(header);

        foreach (var item in items)
        {
            stack.Children.Add(item);
        }

        AppendMenuDivider(stack);
    }

    /// <summary>
    /// F-12 kebab visual parity (2026-05-09) — 1px <see cref="BorderSubtleBrush"/>
    /// separator between sections. Mirrors desktop's
    /// <c>Style Selector="Border.menu-divider"</c> (Height=1,
    /// Background=BorderSubtleBrush, Margin=4,4).
    /// </summary>
    private void AppendMenuDivider(StackPanel stack)
    {
        var divider = new Border
        {
            Height = 1,
            Margin = new Thickness(4, 4, 4, 4),
        };
        divider.BindToken(Border.BackgroundProperty, "BorderSubtleBrush");
        stack.Children.Add(divider);
    }

    // DEFCT-001 (2026-05-10) — recursive AccessibilityView=Raw walk over
    // the popup subtree. See call site in the kebab construction block
    // (around the _kebabPopup = new Popup{} statement) for the rationale.
    // Implementation note: we walk the LOGICAL tree via ILogical so this
    // works on the freshly-constructed subtree before it's attached to a
    // visual root (Border.Child / Panel.Children / ContentControl.Content
    // are all logical children at construction time). Setting the property
    // on each StyledElement makes its eventual AutomationPeer surface as
    // Raw, which Avalonia's IsControlElement / IsContentElement honour.
    private static void HideSubtreeFromAccessibility(StyledElement element)
    {
        AutomationProperties.SetAccessibilityView(element, AccessibilityView.Raw);
        if (element is ILogical logical)
        {
            foreach (var child in logical.LogicalChildren)
            {
                if (child is StyledElement childElement)
                    HideSubtreeFromAccessibility(childElement);
            }
        }
    }

    private void OnKebabMenuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is null) return;
        _kebabPopup.IsOpen = !_kebabPopup.IsOpen;
        // Reset the Reset-confirm flow when the menu is reopened so a
        // stale "All settings will be cleared. Continue?" prompt doesn't
        // accidentally trigger on next tap.
        if (_kebabPopup.IsOpen)
        {
            _resetConfirmPending = false;
            if (_menuResetSettingsItem is not null)
                _menuResetSettingsItem.Content = Localization.MenuItemResetSettings;
        }
    }

    // v3.0 Phase 7.3 — segmented control click handlers. Each one SETS
    // a specific value (no-op if already active) instead of toggling.
    // Matches desktop's SetThemeLight / SetThemeDark / SetLanguageRussian
    // / SetLanguageEnglish commands. Popup stays open so the user can
    // see the segment switch visually.

    private void OnMenuLangRuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Localization.Ru) return; // already active — no-op
        ApplyLanguage(true);
    }

    private void OnMenuLangEnClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!Localization.Ru) return;
        ApplyLanguage(false);
    }

    private void OnMenuThemeLightClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ApplyTheme("light");
    }

    private void OnMenuThemeDarkClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ApplyTheme("dark");
    }

    /// <summary>
    /// v3.0 Phase 7.3 — set RU or EN explicitly + refresh all the
    /// labels through ToggleLanguageAndRefresh + repaint segment
    /// active state. Idempotent.
    /// DEFCT-004 (2026-05-10): pre-fix this called Localization.ToggleAndPersist()
    /// AND ToggleLanguageAndRefresh() — but ToggleLanguageAndRefresh internally
    /// also toggles, so the two calls cancelled out and tapping RU/EN never
    /// flipped state visibly. The early-return guard above already ensures
    /// we only proceed when state needs to change, so a single internal
    /// toggle (via ToggleLanguageAndRefresh) is sufficient.
    /// </summary>
    private void ApplyLanguage(bool ru)
    {
        if (Localization.Ru == ru) return;
        ToggleLanguageAndRefresh();
        RepaintLanguageSegment();
    }

    private void ApplyTheme(string mode)
    {
        var current = AndroidStorage.GetTheme();
        if (current == mode) return;
        AndroidStorage.SetTheme(mode);
        RequestedThemeVariant = mode == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;

        // Bug #4 fix (2026-05-11) — Phase 8.2 BindToken migration is
        // incomplete: ~247 GetBrush() snapshot call sites across the
        // partials don't repaint on theme switch. Until that migration
        // lands, rebuild the Avalonia MainView so every Build* helper
        // re-runs with the new theme tokens. Activity.Recreate was
        // tried first but crashes Mono runtime on Avalonia.Mobile
        // (xamarin::android::Helpers::abort_application). Reassigning
        // ISingleViewApplicationLifetime.MainView stays inside Avalonia
        // and is safe — class-field references get overwritten by the
        // re-run BuildSimplePageView so existing event subscriptions
        // (MainActivity.IntentChanged → OnIntentChanged → mutates
        // _statusCard etc.) still target the freshly-built controls.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try { RebuildSimplePageView(); }
            catch (Exception ex)
            {
                try
                {
                    global::Android.Util.Log.Warn("VpnRouter.Theme",
                        $"RebuildSimplePageView after theme switch failed: {ex.GetType().Name}: {ex.Message}");
                }
                catch { /* swallow logging failures */ }
            }
        }, Avalonia.Threading.DispatcherPriority.Background);

        // v3.0 Phase 8.2 (2026-05-07) — every property bound via
        // BindToken auto-resolves to the new theme's value through
        // Avalonia's DynamicResource pipeline. The two surfaces that
        // can't ride DynamicResource still need manual refresh:
        //   1) Mascot Bitmap — Bgra8888 byte buffer, must re-load to
        //      get the inverted dark variant (mirrors desktop's
        //      MainWindowViewModel.LogoSource pattern).
        //   2) Active-segment chrome — StyleSegmentButton/SetVpnChipState
        //      pick a different brush KEY for active vs inactive, so
        //      they need to re-bind to the right key (the theme
        //      variant change alone wouldn't move the active segment).
        if (_mascotImage is not null)
        {
            _mascotImage.Source = LoadMascot();
        }
        RepaintThemeSegment();
        RepaintLanguageSegment();
        SetVpnChipState(_vpnChipState, force: true);
        // v2.32.0 (AND-ZAPRET) — re-bind Zapret chip on theme flip too.
        // UpdateConnectionState below recomputes from current state, but
        // we force it explicitly so the BindToken call happens even when
        // state hasn't changed (mirrors the SetVpnChipState force path).
        SetZapretChipState(_zapretChipState, force: true);
        UpdateConnectionState(MainActivity.IntendedConnected);
    }

    /// <summary>
    /// Bug #4 fix (2026-05-11) — rebuild the Avalonia MainView so every
    /// GetBrush() call site re-snapshots brushes from the new theme.
    /// Called by ApplyTheme after RequestedThemeVariant changes. Safe
    /// to call multiple times — each invocation tears down the old
    /// view tree via the lifetime swap and constructs a fresh one
    /// from the current AndroidStorage / theme state.
    /// </summary>
    private void RebuildSimplePageView()
    {
        if (ApplicationLifetime is not
            Avalonia.Controls.ApplicationLifetimes.ISingleViewApplicationLifetime singleView)
            return;
        // Bug-AND-009 (2026-05-16) — capture navigation state BEFORE
        // the rebuild so we can restore the user's position afterwards.
        // brat reported "content disappears" when switching theme: the
        // pre-fix RebuildSimplePageView dropped them back to the Simple
        // page no matter what tab of the Advanced shell they had open
        // (because BuildSimplePageView creates a fresh Advanced shell
        // overlay whose IsVisible defaults to false).
        var advancedWasOpen = _advShellOverlay?.IsVisible == true;
        var advancedTab = _advShellSelectedTab;
        // Bug-AND-009 follow-up — clear the lazy tab-content cache
        // before the rebuild. Each tab's Control reference is owned
        // by the OLD overlay tree which is about to be replaced; if
        // EnsureTabContentBuilt finds the tab key already present
        // (true after first activation of any tab), it skips
        // construction and re-adds the stale Control to the NEW host,
        // producing the empty-body bug brat hit ("вкладка выбрана,
        // содержимое пропало"). Also clear button refs so the new
        // BuildAdvancedShellOverlay loop's tabPanel.Children.Add
        // doesn't end up wired to dead buttons.
        _advShellTabContent.Clear();
        _advShellTabButtons.Clear();
        // Build the new view BEFORE swapping so any construction
        // exception leaves the old one intact and visible.
        var fresh = BuildSimplePageView();
        singleView.MainView = fresh;
        // Re-seed transient UI state that BuildSimplePageView's
        // fresh instance doesn't know about — connection state +
        // server list cache. Mirrors OnFrameworkInitializationCompleted's
        // post-build calls so the rebuilt view immediately reflects
        // current reality instead of defaulting to disconnected /
        // empty.
        UpdateConnectionState(MainActivity.IntendedConnected);
        ReloadServerList();
        // Bug-AND-009 — restore Advanced-shell navigation if the user
        // had it open. The fresh BuildSimplePageView created a NEW
        // _advShellOverlay field reference (via BuildAdvancedShellOverlay),
        // so reopening uses the rebuilt-with-the-new-theme overlay tree.
        if (advancedWasOpen)
        {
            try { OpenAdvancedShell(advancedTab); }
            catch (Exception ex)
            {
                try
                {
                    global::Android.Util.Log.Warn("VpnRouter.Theme",
                        $"Restore Advanced shell after rebuild failed: {ex.GetType().Name}: {ex.Message}");
                }
                catch { /* swallow logging failures */ }
            }
        }
    }

    /// <summary>
    /// v3.0 Phase 7.3 — refresh segment colors after a theme change so
    /// the active segment moves to the new selection.
    /// </summary>
    private void RepaintThemeSegment()
    {
        var isDark = AndroidStorage.GetTheme() == "dark";
        StyleSegmentButton(_menuThemeLight, !isDark);
        StyleSegmentButton(_menuThemeDark, isDark);
    }

    private void RepaintLanguageSegment()
    {
        StyleSegmentButton(_menuLangRu, Localization.Ru);
        StyleSegmentButton(_menuLangEn, !Localization.Ru);
    }

    private void StyleSegmentButton(Avalonia.Controls.Button? btn, bool active)
    {
        if (btn is null) return;
        btn.FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal;
        // v3.0 Phase 8.2 — re-bind via DynamicResource so the button
        // tracks ThemeVariant changes between calls. New bindings
        // replace any prior binding at LocalValue priority on the same
        // property.
        btn.BindToken(Avalonia.Controls.Button.BackgroundProperty,
            active ? "AccentBgSubtleBrush" : "SurfaceSunkenBrush");
        btn.BindToken(Avalonia.Controls.Button.ForegroundProperty,
            active ? "AccentFgBrush" : "TextSecondaryBrush");
        btn.BindToken(Avalonia.Controls.Button.BorderBrushProperty,
            active ? "BorderAccentBrush" : "BorderSubtleBrush");
    }

    /// <summary>
    /// v3.0 Phase 7.4 (2026-05-04) — Diagnostics > Open log. Reads the
    /// last 50 KB of <c>getExternalFilesDir()/singbox.log</c> into the
    /// in-app overlay viewer. Pre-7.4 this only copied the path to the
    /// clipboard, which closed handbook §5.6 only formally — users on
    /// device couldn't actually read the log without `adb`.
    /// </summary>
    private void OnMenuOpenLogClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        ShowLogViewer();
    }

    private void ShowLogViewer()
    {
        if (_logOverlay is null) return;
        if (_logViewerTitle is not null) _logViewerTitle.Text = "singbox.log";
        LoadLogContent();
        _logOverlay.IsVisible = true;
    }

    /// <summary>
    /// v2.32.0 (AND-CRASH-HOOK, 2026-05-08) — Diagnostics → "View crash
    /// log". Opens the same overlay as the singbox.log viewer but loads
    /// the most recent file from <c>AppPaths.DataDir/crashes/</c>. Both
    /// the C# CrashReporter (<c>crash-*.txt</c>) and the VpnRouterService
    /// Java uncaught-handler (<c>java-crash-*.txt</c>) write here, so a
    /// single entry-point covers both origin paths.
    /// </summary>
    private void OnMenuViewCrashLogClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        if (_logOverlay is null) return;
        if (_logViewerTitle is not null)
            _logViewerTitle.Text = Localization.MenuItemViewCrashLog;
        LoadCrashLogContent();
        _logOverlay.IsVisible = true;
    }

    private void LoadCrashLogContent()
    {
        if (_logViewerContent is null) return;
        try
        {
            var crashesDir = System.IO.Path.Combine(
                VPNRouter.Core.AppPaths.DataDir, "crashes");
            if (!System.IO.Directory.Exists(crashesDir))
            {
                ShowLogEmptyState(Localization.CrashLogEmpty);
                return;
            }

            var files = System.IO.Directory.GetFiles(crashesDir, "*.txt");
            if (files.Length == 0)
            {
                ShowLogEmptyState(Localization.CrashLogEmpty);
                return;
            }

            var newest = files
                .Select(p => new System.IO.FileInfo(p))
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .First();

            // Cap at 50 KB to match singbox.log viewer — crash files are
            // typically <10 KB, but a malformed multi-MB report would
            // OOM the GC if we slurped it whole.
            const int MaxBytes = 50_000;
            string text;
            using (var fs = newest.OpenRead())
            {
                if (fs.Length <= MaxBytes)
                {
                    using var sr = new System.IO.StreamReader(fs);
                    text = sr.ReadToEnd();
                }
                else
                {
                    fs.Seek(-MaxBytes, System.IO.SeekOrigin.End);
                    using var sr = new System.IO.StreamReader(fs);
                    sr.ReadLine();
                    text = "(truncated to last 50 KB)\n\n" + sr.ReadToEnd();
                }
            }

            // Header line so the user/support sees which file they're
            // looking at when several crashes accumulate.
            text = $"# {newest.Name}\n# {newest.LastWriteTime:yyyy-MM-dd HH:mm:ss} " +
                   $"(of {files.Length} total)\n\n" + text;

            _logViewerContent.Text = text;
            if (_logViewerEmptyState is not null) _logViewerEmptyState.IsVisible = false;
            if (_logViewerScroller is not null)
            {
                _logViewerScroller.IsVisible = true;
                Dispatcher.UIThread.Post(() =>
                {
                    if (_logViewerScroller is null) return;
                    _logViewerScroller.Offset = new Vector(
                        _logViewerScroller.Offset.X, 0);
                }, DispatcherPriority.Background);
            }
        }
        catch (Exception ex)
        {
            ShowLogEmptyState(string.Format(Localization.LogViewerError,
                ex.GetType().Name, ex.Message));
        }
    }

    private void OnLogViewerCloseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_logOverlay is not null) _logOverlay.IsVisible = false;
    }

    private void OnLogViewerRefreshClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        LoadLogContent();
    }

    /// <summary>
    /// v3.0 Phase 7.4 — read the log file's tail (≤50 KB) into the
    /// viewer's TextBlock. Caps the read so a multi-megabyte log file
    /// doesn't OOM the GC. If the file doesn't exist or is empty,
    /// surface an empty-state hint instead of a blank pane.
    /// </summary>
    private void LoadLogContent()
    {
        if (_logViewerContent is null) return;
        try
        {
            var ctx = global::Android.App.Application.Context;
            var extDir = ctx.GetExternalFilesDir(null);
            var logPath = extDir is not null
                ? System.IO.Path.Combine(extDir.AbsolutePath, "singbox.log")
                : null;

            if (logPath is null || !System.IO.File.Exists(logPath))
            {
                ShowLogEmptyState(Localization.LogViewerEmpty);
                return;
            }

            const int MaxBytes = 50_000;
            string text;
            using (var fs = System.IO.File.Open(logPath, System.IO.FileMode.Open,
                                                System.IO.FileAccess.Read,
                                                System.IO.FileShare.ReadWrite))
            {
                if (fs.Length <= MaxBytes)
                {
                    using var sr = new System.IO.StreamReader(fs);
                    text = sr.ReadToEnd();
                }
                else
                {
                    fs.Seek(-MaxBytes, System.IO.SeekOrigin.End);
                    using var sr = new System.IO.StreamReader(fs);
                    // First line will be partial — drop it.
                    sr.ReadLine();
                    text = sr.ReadToEnd();
                }
            }

            if (string.IsNullOrEmpty(text))
            {
                ShowLogEmptyState(Localization.LogViewerEmpty);
                return;
            }

            _logViewerContent.Text = text;
            if (_logViewerEmptyState is not null) _logViewerEmptyState.IsVisible = false;
            if (_logViewerScroller is not null)
            {
                _logViewerScroller.IsVisible = true;
                // Scroll to bottom so the most-recent lines are visible
                // immediately. Defer to the next layout pass via
                // Dispatcher to give the TextBlock a chance to measure.
                Dispatcher.UIThread.Post(() =>
                {
                    if (_logViewerScroller is null) return;
                    _logViewerScroller.Offset = new Vector(
                        _logViewerScroller.Offset.X,
                        _logViewerScroller.Extent.Height);
                }, DispatcherPriority.Background);
            }
        }
        catch (Exception ex)
        {
            ShowLogEmptyState(string.Format(Localization.LogViewerError,
                ex.GetType().Name, ex.Message));
        }
    }

    private void ShowLogEmptyState(string message)
    {
        if (_logViewerEmptyState is not null)
        {
            _logViewerEmptyState.Text = message;
            _logViewerEmptyState.IsVisible = true;
        }
        if (_logViewerScroller is not null) _logViewerScroller.IsVisible = false;
    }

    // ── Phase 7.5 — Per-app filter UI (handbook §5.5) ───────────────────

    private void OnTunnelModeRadioChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // IsChecked changes fire on BOTH the previously-selected and the
        // newly-selected radio when group toggles, so dedupe by checking
        // the actual state.
        var splitOn = _splitRadio?.IsChecked == true;

        // v3.0 v2.32.0 — when the user toggles split ON, restore the last
        // active per-app mode ("include" or "exclude"); first-time users
        // get "include" via the GetPerAppLastMode default. Toggling split
        // OFF writes "off" to the active mode but preserves last-mode so
        // the next ON toggle is sticky.
        if (splitOn)
        {
            var current = AndroidStorage.GetPerAppMode();
            if (current == "off")
            {
                var restored = AndroidStorage.GetPerAppLastMode();
                AndroidStorage.SetPerAppMode(restored);
            }
        }
        else
        {
            if (AndroidStorage.GetPerAppMode() != "off")
            {
                AndroidStorage.SetPerAppMode("off");
            }
        }

        // Show/hide the "Choose apps…" sub-stack we tagged on the split
        // radio in BuildSimplePageView.
        if (_splitRadio?.Tag is StackPanel perAppStack)
        {
            perAppStack.IsVisible = splitOn;
        }

        UpdatePerAppFormCountLabel();
    }

    /// <summary>
    /// v3.0 v2.32.0 (2026-05-07) — keeps the form-side "Selected: N" label
    /// in sync with the saved package count + the active mode. The label
    /// suffix differs by mode so a user glancing at the form can tell
    /// whether "Selected: 3" means "3 apps go via VPN" (include) or
    /// "3 apps bypass VPN" (exclude). Called from
    /// <see cref="OnTunnelModeRadioChanged"/> + <see cref="OnAppPickerSaveClicked"/>.
    /// </summary>
    private void UpdatePerAppFormCountLabel()
    {
        if (_perAppCountLabel is null) return;
        var count = AndroidStorage.GetPerAppPackages().Count;
        var mode = AndroidStorage.GetPerAppMode();
        var fmt = mode == "exclude"
            ? Localization.PerAppCountExclude
            : Localization.PerAppCountInclude;
        _perAppCountLabel.Text = string.Format(fmt, count);
    }

    private void OnPerAppPickButtonClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ShowAppPicker();
    }

    private void ShowAppPicker()
    {
        // AND-MIGRATE-OVERLAYS (2026-05-09): "Choose apps" button now
        // deeplinks to the Advanced shell on the Applications tab. Re-seed
        // happens inside ReseedAppPickerTabState (called by the shell on
        // tab activation). AND-ADV-CHROME (2026-05-10): tab renamed
        // Apps → Applications to match desktop v2.32.0.
        OpenAdvancedShell(AdvancedTab.Applications);
    }

    /// <summary>
    /// Re-seed Apps tab state from persisted storage. Called by the
    /// Advanced shell on tab activation. Replaces the body of the old
    /// ShowAppPicker.
    /// <para>Phase D: also rebuilds the category sidebar (10 built-ins +
    /// any user-defined custom categories from
    /// <see cref="AndroidStorage.GetCustomCategories"/>) and restores the
    /// last-active category id from
    /// <see cref="AndroidStorage.GetApplicationsActiveCategory"/>. Empty / no
    /// active id keeps the right pane on the placeholder.</para>
    /// </summary>
    private async void ReseedAppPickerTabState()
    {
        // Seed the selection set from storage so check states match what
        // the user previously saved.
        _appPickerSelected = new HashSet<string>(AndroidStorage.GetPerAppPackages(),
                                                 System.StringComparer.OrdinalIgnoreCase);

        // v3.0 v2.32.0 — seed the picker mode. If storage is currently
        // "off" (user opened the picker after toggling split on but before
        // mode persisted), restore the last active mode; default to
        // "include" via GetPerAppLastMode for first-run.
        var storedMode = AndroidStorage.GetPerAppMode();
        _appPickerMode = storedMode switch
        {
            "include" => "include",
            "exclude" => "exclude",
            _ => AndroidStorage.GetPerAppLastMode(),
        };
        ApplyPickerModeVisuals();

        if (_appPickerSearch is not null) _appPickerSearch.Text = string.Empty;
        if (_appPickerSystemToggle is not null)
            _appPickerSystemToggle.IsChecked = _appPickerSystemAppsVisible;

        UpdateAppPickerCount();
        if (_appPickerList is not null)
        {
            _appPickerList.ItemsSource = new[] { Localization.PerAppLoading };
        }

        // Phase D — build the sidebar before the cache load so the row
        // styling (active highlight) is in place when the user lands on
        // the tab. Counts paint as zeros first; UpdateAllCategoryCounts
        // refreshes them after the cache returns.
        //
        // Bug #2 (2026-05-11) — mobile redesign: default to the
        // CustomCatchAll category on first open (no saved active id) so
        // the apps list is immediately populated. Pre-fix the right pane
        // showed a "← Select a category" placeholder, which on phone read
        // as "the tab is empty" — users had to discover the chip row to
        // get any apps to show. Defaulting to CustomCatchAll = "all apps"
        // matches user intent ("выбор приложений неудобен на телефоне").
        _advAppsCustomCategories = AndroidStorage.GetCustomCategories();
        var savedActiveId = AndroidStorage.GetApplicationsActiveCategory();
        _advAppsActiveCategoryId = ResolveActiveCategoryId(savedActiveId)
            ?? AndroidCategoryDefaults.CustomCatchAllId;
        RebuildAppCategorySidebar();

        try
        {
            _appPickerCache = await System.Threading.Tasks.Task.Run(() =>
                _appPickerSystemAppsVisible
                    ? AppListLoader.ListAllApps()
                    : AppListLoader.ListUserApps());
        }
        catch
        {
            _appPickerCache = new List<AppListLoader.AppEntry>();
        }
        UpdateAllCategoryCounts();
        ApplyAppPickerFilter();
    }

    /// <summary>Validate the persisted active-category id against the current
    /// built-in list + user-defined categories. Returns null if the id no
    /// longer maps (e.g. user removed a custom category between sessions),
    /// so the placeholder shows on next open instead of orphan styling.</summary>
    private string? ResolveActiveCategoryId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        if (AndroidCategoryDefaults.Find(id) is not null) return id;
        if (IsUserDefinedCategory(id)) return id;
        return null;
    }

    private void OnAppPickerSaveClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        AndroidStorage.SetPerAppPackages(_appPickerSelected);
        // v3.0 v2.32.0 — persist mode + sticky-restore key in one step so
        // the next split-radio toggle restores the same mode.
        AndroidStorage.SetPerAppMode(_appPickerMode);
        AndroidStorage.SetPerAppLastMode(_appPickerMode);
        UpdatePerAppFormCountLabel();
        // AND-MIGRATE-OVERLAYS (2026-05-09): Save no longer dismisses the
        // surface — Apps lives as a tab inside the Advanced shell. The
        // count label refresh + storage flush is enough; the user closes
        // the shell when they're done.
    }

    private void OnAppPickerModeIncludeClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_appPickerMode == "include") return;
        _appPickerMode = "include";
        ApplyPickerModeVisuals();
    }

    private void OnAppPickerModeExcludeClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_appPickerMode == "exclude") return;
        _appPickerMode = "exclude";
        ApplyPickerModeVisuals();
    }

    /// <summary>
    /// v3.0 v2.32.0 (2026-05-07) — repaints the include/exclude segment
    /// buttons + the hint TextBlock based on <see cref="_appPickerMode"/>.
    /// Mirrors how the kebab menu's theme/language segment row paints
    /// active/inactive (see <see cref="MakeSegmentButton"/>).
    /// </summary>
    private void ApplyPickerModeVisuals()
    {
        var includeActive = _appPickerMode == "include";
        var excludeActive = _appPickerMode == "exclude";
        StyleSegment(_appPickerModeIncludeBtn, includeActive);
        StyleSegment(_appPickerModeExcludeBtn, excludeActive);
        if (_appPickerModeHint is not null)
        {
            _appPickerModeHint.Text = excludeActive
                ? Localization.PerAppHintExclude
                : Localization.PerAppHintInclude;
        }
    }

    private void StyleSegment(Avalonia.Controls.Button? btn, bool active)
    {
        if (btn is null) return;
        btn.Background = active ? GetBrush("AccentBgSubtleBrush") : GetBrush("SurfaceSunkenBrush");
        btn.Foreground = active ? GetBrush("AccentFgBrush") : GetBrush("TextSecondaryBrush");
        btn.BorderBrush = active ? GetBrush("BorderAccentBrush") : GetBrush("BorderSubtleBrush");
        btn.FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal;
    }

    private void OnAppPickerSystemToggleChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var newValue = _appPickerSystemToggle?.IsChecked == true;
        if (newValue == _appPickerSystemAppsVisible) return;
        _appPickerSystemAppsVisible = newValue;
        // Reload list with the new include-system flag. This might take a
        // beat on slow devices; reuse the show flow for the loading state.
        _ = ReloadAppPickerCacheAsync();
    }

    private async System.Threading.Tasks.Task ReloadAppPickerCacheAsync()
    {
        if (_appPickerList is null) return;
        _appPickerList.ItemsSource = new[] { Localization.PerAppLoading };
        try
        {
            _appPickerCache = await System.Threading.Tasks.Task.Run(() =>
                _appPickerSystemAppsVisible
                    ? AppListLoader.ListAllApps()
                    : AppListLoader.ListUserApps());
        }
        catch
        {
            _appPickerCache = new List<AppListLoader.AppEntry>();
        }
        ApplyAppPickerFilter();
    }

    private void OnAppPickerSearchChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e)
    {
        ApplyAppPickerFilter();
    }

    /// <summary>
    /// Apply the current search term to <see cref="_appPickerCache"/> and
    /// refresh the ListBox with a row factory that builds CheckBox + label
    /// per visible app. Each row's CheckedChanged updates
    /// <see cref="_appPickerSelected"/> immediately so Save just persists
    /// the in-memory set.
    /// <para>Phase D: rows are first scoped to the active category. Built-in
    /// categories filter to their hint-package list; the catch-all "Custom"
    /// shows all apps; user-created categories show all apps too (the
    /// CustomCategory.Apps[] tag list grows as the user checks them while
    /// the category is active).</para>
    /// </summary>
    private void ApplyAppPickerFilter()
    {
        if (_appPickerList is null) return;
        var search = _appPickerSearch?.Text?.Trim() ?? string.Empty;

        // Phase D — category scope is the first filter. No active category =
        // empty pane (placeholder is shown by SetActiveAppCategory anyway,
        // but ItemsSource still needs to be empty so the ListBox doesn't
        // flash the previous category's rows).
        IEnumerable<AppListLoader.AppEntry> scoped = ScopeAppsToActiveCategory(_appPickerCache);

        var filtered = string.IsNullOrEmpty(search)
            ? scoped
            : scoped.Where(a =>
                a.Label.Contains(search, System.StringComparison.OrdinalIgnoreCase)
                || a.PackageName.Contains(search, System.StringComparison.OrdinalIgnoreCase));

        // v3.0 — Selected / Available split mirrors desktop ApplicationsPage
        // category structure. Sections are computed only at filter time
        // (search/system-toggle change); per-row checkbox toggles update
        // the selected count but leave rows in their current section so
        // the user doesn't lose scroll position mid-tap.
        var selectedRows = new List<AppListLoader.AppEntry>();
        var availableRows = new List<AppListLoader.AppEntry>();
        foreach (var app in filtered)
        {
            if (_appPickerSelected.Contains(app.PackageName))
                selectedRows.Add(app);
            else
                availableRows.Add(app);
        }

        var rows = new List<Control>(selectedRows.Count + availableRows.Count + 2);
        if (selectedRows.Count > 0)
        {
            rows.Add(BuildPickerSectionHeader(Localization.PerAppGroupSelected, selectedRows.Count));
            foreach (var app in selectedRows) rows.Add(BuildAppRow(app));
        }
        if (availableRows.Count > 0)
        {
            rows.Add(BuildPickerSectionHeader(Localization.PerAppGroupAvailable, availableRows.Count));
            foreach (var app in availableRows) rows.Add(BuildAppRow(app));
        }

        _appPickerList.ItemsSource = rows;
        UpdateAppPickerCount();
        // Bug #2 (2026-05-11) — surface the visible-app count so users
        // can verify the launcher-activities fallback in AppListLoader is
        // doing its job. Sum of selectedRows + availableRows == filtered
        // apps within active category scope (post-search). When the user
        // toggles "System apps" the reload reseeds _appPickerCache before
        // this runs.
        if (_appPickerShowingCount is not null)
        {
            _appPickerShowingCount.Text = string.Format(
                Localization.PerAppShowingCount,
                selectedRows.Count + availableRows.Count);
        }
    }

    /// <summary>Filter the installed-app cache down to the apps that belong
    /// to the active category. Built-ins use a static hint package set; the
    /// catch-all + user-defined custom categories surface all installed
    /// apps. Empty / unknown active id returns an empty sequence so the
    /// right pane shows nothing while the placeholder is visible.</summary>
    private IEnumerable<AppListLoader.AppEntry> ScopeAppsToActiveCategory(IEnumerable<AppListLoader.AppEntry> source)
    {
        if (string.IsNullOrEmpty(_advAppsActiveCategoryId))
            return System.Linq.Enumerable.Empty<AppListLoader.AppEntry>();

        // Custom catch-all + user-created custom categories: scope = all
        // installed apps. Same code path so the user can pick freely from
        // the full list either way.
        if (AndroidCategoryDefaults.IsCustomCatchAll(_advAppsActiveCategoryId)
            || IsUserDefinedCategory(_advAppsActiveCategoryId))
        {
            return source;
        }

        var def = AndroidCategoryDefaults.Find(_advAppsActiveCategoryId);
        if (def is null || def.PackageHints.Count == 0)
            return System.Linq.Enumerable.Empty<AppListLoader.AppEntry>();

        var hintSet = new HashSet<string>(def.PackageHints, System.StringComparer.OrdinalIgnoreCase);
        return source.Where(a => hintSet.Contains(a.PackageName));
    }

    private bool IsUserDefinedCategory(string id)
    {
        foreach (var cat in _advAppsCustomCategories)
            if (string.Equals(cat.Name, id, System.StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private VPNRouter.Core.Models.CustomCategory? FindUserDefinedCategory(string id)
    {
        foreach (var cat in _advAppsCustomCategories)
            if (string.Equals(cat.Name, id, System.StringComparison.OrdinalIgnoreCase))
                return cat;
        return null;
    }

    private Control BuildAppRow(AppListLoader.AppEntry app)
    {
        // v3.0 — visual parity with desktop ApplicationsPage Border.app-row:
        // sunken-bg rounded block, padding 10/7, 4-pt margin between rows.
        // Desktop has no per-app icon (Windows doesn't expose a uniform
        // per-process icon API) so the icon slot is Android-only polish;
        // typography (TextPrimary name + TextMuted secondary) and the
        // rounded-block surround mirror desktop one-to-one. CheckBox sits
        // trailing per Material list convention — desktop puts it leading,
        // but the touch ergonomics differ (large finger tapping a leading
        // checkbox occludes the icon/label readability mid-tap).
        var label = new TextBlock
        {
            Text = app.Label,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        var pkgLine = new TextBlock
        {
            Text = app.PackageName,
            FontSize = 9,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        pkgLine.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");
        var rowText = new StackPanel
        {
            Spacing = 1,
            Children = { label, pkgLine },
            VerticalAlignment = VerticalAlignment.Center,
        };

        var checkbox = new Avalonia.Controls.CheckBox
        {
            IsChecked = _appPickerSelected.Contains(app.PackageName),
            VerticalAlignment = VerticalAlignment.Center,
            MinHeight = 0,
            Padding = new Thickness(0),
        };
        checkbox.IsCheckedChanged += (_, __) =>
        {
            if (checkbox.IsChecked == true)
                _appPickerSelected.Add(app.PackageName);
            else
                _appPickerSelected.Remove(app.PackageName);

            // Bug-AND-013 (2026-05-16) — persist on every toggle so the
            // selection survives a tab rebuild (theme/lang switch goes
            // through ReseedAppPickerTabState which reads
            // AndroidStorage.GetPerAppPackages back into the in-memory
            // set). Pre-fix the Save button was the only persist path,
            // and a theme flip mid-edit silently dropped every unsaved
            // tap. Now the Done button is purely a visual "close"
            // affordance (storage is already up to date).
            AndroidStorage.SetPerAppPackages(_appPickerSelected);

            // Phase D — when active category is a user-defined custom one,
            // mirror the toggle into its Apps[] tag list so the sidebar
            // count + persisted membership reflect what the user just did.
            // Built-in hint lists are static; toggling there only affects
            // _appPickerSelected.
            if (!string.IsNullOrEmpty(_advAppsActiveCategoryId))
            {
                var custom = FindUserDefinedCategory(_advAppsActiveCategoryId);
                if (custom is not null)
                {
                    custom.Apps ??= new List<string>();
                    if (checkbox.IsChecked == true)
                    {
                        if (!custom.Apps.Any(p => string.Equals(p, app.PackageName, System.StringComparison.OrdinalIgnoreCase)))
                            custom.Apps.Add(app.PackageName);
                    }
                    else
                    {
                        custom.Apps.RemoveAll(p => string.Equals(p, app.PackageName, System.StringComparison.OrdinalIgnoreCase));
                    }
                    AndroidStorage.SetCustomCategories(_advAppsCustomCategories);
                }
            }

            UpdateAppPickerCount();
            UpdateAllCategoryCounts();
        };

        // 32dp icon — Material medium list-icon size, matches the touch
        // density of the rounded-block row. Cached Bitmap from
        // AppIconCache; null slot stays blank rather than placeholder.
        var iconImage = new Image
        {
            Width = 32,
            Height = 32,
            VerticalAlignment = VerticalAlignment.Center,
            Stretch = Stretch.Uniform,
            Source = app.IconBitmap,
        };
        RenderOptions.SetBitmapInterpolationMode(iconImage, BitmapInterpolationMode.HighQuality);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(iconImage, 0);
        Grid.SetColumn(rowText, 1);
        Grid.SetColumn(checkbox, 2);
        grid.Children.Add(iconImage);
        grid.Children.Add(rowText);
        grid.Children.Add(checkbox);

        var rowBorder = new Border
        {
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Padding = new Thickness(10, 7),
            Margin = new Thickness(0, 0, 0, 4),
            MinHeight = 44,
            Child = grid,
        };
        rowBorder.BindToken(Border.BackgroundProperty, "SurfaceSunkenBrush");
        // Bug-AND-008 / Bug-AND-013 (2026-05-16) — synthetic "tap row
        // to toggle" was the source of the scroll-toggle accidents in
        // the original brat report. Every implementation candidate had
        // an issue:
        //   - Bare PointerPressed: fired mid-scroll → mass toggles.
        //   - Manual time+distance: ListBoxItem captures the pointer
        //     for selection, swallowing PointerReleased on the inner
        //     Border so even genuine taps were ignored.
        //   - Tapped event: ScrollViewer's recognizer marks it Handled
        //     on scrolls before it bubbles past the inner Border.
        // Resolution: drop the row-Border tap handler. Users toggle via
        // the explicit CheckBox at the row's trailing edge — the
        // checkbox handler (above) is the single source of selection
        // truth and already auto-persists to AndroidStorage (Bug-AND-013).
        // CheckBox is large enough (32 dp visual + 44 dp implicit
        // Material touch target) to remain ergonomic with one hand.
        return rowBorder;
    }

    /// <summary>
    /// Section header for the per-app picker — mirrors desktop
    /// ApplicationsPage cat-name + cat-count style: SemiBold secondary
    /// label on the left, mono muted count on the right. Used to split
    /// the picker into "Selected" / "Available" subsections so users
    /// see at a glance what's currently routed via VPN vs the rest.
    /// </summary>
    private Control BuildPickerSectionHeader(string label, int count)
    {
        var nameTb = new TextBlock
        {
            Text = label,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        nameTb.BindToken(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        var countTb = new TextBlock
        {
            Text = count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            FontSize = 9,
            FontFamily = new FontFamily("Consolas, SF Mono, Cascadia Code, Ubuntu Mono, monospace"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };
        countTb.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        };
        Grid.SetColumn(nameTb, 0);
        Grid.SetColumn(countTb, 1);
        grid.Children.Add(nameTb);
        grid.Children.Add(countTb);

        return new Border
        {
            Padding = new Thickness(2, 8, 2, 4),
            Child = grid,
        };
    }

    private void UpdateAppPickerCount()
    {
        if (_appPickerCount is not null)
            _appPickerCount.Text = string.Format(Localization.PerAppCount, _appPickerSelected.Count);
    }

    /// <summary>
    /// Phase D (AND-ADV-APPS-CATEGORIES, 2026-05-10) — Applications tab body.
    /// Mirrors desktop <c>ApplicationsPage.axaml</c> two-column master/detail
    /// layout: ~140dp category sidebar on the left (with an inline
    /// "+ New category" form at the bottom) and the per-category app list on
    /// the right. The right pane shows the include/exclude mode picker, a
    /// search box, the system-apps toggle, and a scoped checkbox list of
    /// installed apps. The shell provides the title bar / close button.
    /// <para>The 10 built-in categories come from <see cref="AndroidCategoryDefaults"/>;
    /// user-created categories live in <see cref="AndroidStorage.GetCustomCategories"/>
    /// and are appended below the catch-all "Custom" row.</para>
    /// </summary>
    private Control BuildAppPickerTabContent()
    {
        // Bug #2 (2026-05-11) — single-column mobile-first layout. The
        // pre-fix design cloned desktop's 2-pane Grid (140dp sidebar +
        // right pane) which left ~330dp for the right pane on a 1080-px
        // phone — too cramped for icon + label + package + checkbox.
        // Replaced with a vertical DockPanel: search → horizontal
        // category chip row → +New row → mode picker → mode hint →
        // count/system-toggle row → app list (fills) → sticky Save.
        //
        // Reference points: Material Design app picker pattern
        // (filter chips on top), desktop divergence intentional per user
        // feedback ("Выбор приложений идентичный desktop неудобен на
        // телефоне"). The fields _advAppsRightPanePlaceholder /
        // _advAppsRightPaneScopeContainer stay declared because other
        // call sites null-check them, but they no longer participate in
        // the visual tree.

        // ── Category chip grid (categories) ──────────────────────────
        // Bug-AND-008 (2026-05-16) — replaced horizontal-scrollable strip
        // with a WrapPanel. The previous design (Horizontal StackPanel
        // inside a ScrollViewer) had three issues on Android:
        //   1. ScrollViewer's gesture recogniser preemptively captured
        //      every chip-press as a possible scroll-start, so chip
        //      activation needed unreliable tap-detection heuristics.
        //   2. "Custom" was offscreen-right and required a long swipe
        //      to reach (the catch-all is the most-used scope).
        //   3. Horizontal scrolling itself is awkward on a phone — users
        //      had to swipe with one hand while reading labels.
        // WrapPanel makes every chip simultaneously visible and tappable
        // — at typical font sizes 10 built-in categories fit in 2 rows
        // on a 1080dp screen. Custom now leads the first row
        // (RebuildAppCategorySidebar ordering).
        _advAppsCategoryListPanel = new StackPanel
        {
            // Keep the field type as StackPanel so the rest of the
            // codebase (.Children.Clear, .Children.Add) keeps working
            // without churn. We swap the panel into a WrapPanel host
            // via a wrapper Panel that lets us re-layout to wrap-flow
            // semantics without changing the field type.
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 6,
        };
        var chipWrapHost = new WrapPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            // Use an outer Border per row by laying chips directly in
            // a WrapPanel — children flow left-to-right and wrap to
            // the next line as needed.
            Margin = new Thickness(8, 4, 8, 4),
        };
        // Swap: WrapPanel hosts the rows directly. We replace the
        // StackPanel role by pointing _advAppsCategoryListPanel at a
        // panel that *is* the WrapPanel surface. Easiest pattern: keep
        // the StackPanel field but reassign children every rebuild via
        // a tiny adapter.
        // Concretely: the WrapPanel holds chips directly; rebuild adds
        // them straight to chipWrapHost. _advAppsCategoryListPanel is
        // kept as an alias bound to chipWrapHost.Children via
        // _advAppsCategoryWrapHost (private field set below).
        _advAppsCategoryWrapHost = chipWrapHost;

        // ── "+ New category" inline row (always visible, compact) ────
        _advAppsNewCategoryInput = new TextBox
        {
            Watermark = Localization.AdvAppsCategoryNamePlaceholder,
            FontSize = 11,
            Padding = new Thickness(8, 6),
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
            BorderThickness = new Thickness(1),
        };
        _advAppsNewCategoryInput.BindToken(TextBox.BackgroundProperty, "SurfaceSunkenBrush");
        _advAppsNewCategoryInput.BindToken(TextBox.BorderBrushProperty, "BorderSubtleBrush");

        _advAppsAddCategoryBtn = new Avalonia.Controls.Button
        {
            Content = Localization.AdvAppsAddCategoryButton,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(12, 6),
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
            BorderThickness = new Thickness(0),
        };
        _advAppsAddCategoryBtn.BindToken(Avalonia.Controls.Button.BackgroundProperty, "AccentSolidBrush");
        _advAppsAddCategoryBtn.BindToken(Avalonia.Controls.Button.ForegroundProperty, "AccentOnSolidBrush");
        _advAppsAddCategoryBtn.Click += OnAdvAppsAddCategoryClicked;

        var addCategoryRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 6,
            Margin = new Thickness(8, 0, 8, 6),
        };
        Grid.SetColumn(_advAppsNewCategoryInput, 0);
        Grid.SetColumn(_advAppsAddCategoryBtn, 1);
        addCategoryRow.Children.Add(_advAppsNewCategoryInput);
        addCategoryRow.Children.Add(_advAppsAddCategoryBtn);

        // ── Scope body (mode picker + filters + apps list + Save) ────
        var scopeBody = BuildAppPickerScopeBody();
        _advAppsRightPaneScopeContainer = new Border
        {
            Child = scopeBody,
            IsVisible = true,
        };

        // Field still declared but unused in mobile layout. Pre-fix the
        // right pane could swap to a "← Select a category" placeholder;
        // mobile design defaults the active category to CustomCatchAll
        // (all apps), so the apps list is always populated.
        _advAppsRightPanePlaceholder = null;

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(chipWrapHost, Dock.Top);
        DockPanel.SetDock(addCategoryRow, Dock.Top);
        dock.Children.Add(chipWrapHost);
        dock.Children.Add(addCategoryRow);
        dock.Children.Add(_advAppsRightPaneScopeContainer);

        var body = new Border { Child = dock };
        body.BindToken(Border.BackgroundProperty, "SurfaceAppBrush");
        return body;
    }

    /// <summary>
    /// Right-pane content used when a category is active: include/exclude
    /// segmented control + hint + search + system-apps toggle + apps ListBox
    /// + Save bar. Factored out of <see cref="BuildAppPickerTabContent"/> so
    /// the placeholder ("← Select a category") and the scoped body can swap
    /// via <see cref="_advAppsRightPaneScopeContainer"/> visibility without
    /// rebuilding the widget tree.
    /// </summary>
    private Control BuildAppPickerScopeBody()
    {
        _appPickerModeLabel = new TextBlock
        {
            Text = Localization.PerAppPickerModeLabel,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = GetBrush("TextMutedBrush"),
            Margin = new Thickness(8, 6, 8, 2),
        };
        _appPickerModeIncludeBtn = MakeSegmentButton(
            Localization.PerAppModeInclude,
            _appPickerMode == "include",
            OnAppPickerModeIncludeClicked);
        _appPickerModeExcludeBtn = MakeSegmentButton(
            Localization.PerAppModeExclude,
            _appPickerMode == "exclude",
            OnAppPickerModeExcludeClicked);
        var modeRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 4,
            Margin = new Thickness(8, 0, 8, 4),
        };
        Grid.SetColumn(_appPickerModeIncludeBtn, 0);
        Grid.SetColumn(_appPickerModeExcludeBtn, 1);
        modeRow.Children.Add(_appPickerModeIncludeBtn);
        modeRow.Children.Add(_appPickerModeExcludeBtn);
        _appPickerModeHint = new TextBlock
        {
            Text = _appPickerMode == "exclude"
                ? Localization.PerAppHintExclude
                : Localization.PerAppHintInclude,
            FontSize = 9,
            Foreground = GetBrush("TextMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8, 0, 8, 6),
        };

        _appPickerSearch = new TextBox
        {
            Watermark = Localization.PerAppSearchHint,
            FontSize = 12,
            Padding = new Thickness(10, 6),
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
            BorderThickness = new Thickness(1),
        };
        _appPickerSearch.BindToken(TextBox.BackgroundProperty, "SurfaceSunkenBrush");
        _appPickerSearch.BindToken(TextBox.BorderBrushProperty, "BorderSubtleBrush");
        _appPickerSearch.TextChanged += OnAppPickerSearchChanged;

        var systemToggleLabel = new TextBlock
        {
            Text = Localization.PerAppSystemAppsToggle,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        };
        systemToggleLabel.BindToken(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        _appPickerSystemToggle = new Avalonia.Controls.CheckBox
        {
            Content = systemToggleLabel,
            IsChecked = _appPickerSystemAppsVisible,
            MinHeight = 0,
            Padding = new Thickness(4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _appPickerSystemToggle.IsCheckedChanged += OnAppPickerSystemToggleChanged;

        _appPickerCount = new TextBlock
        {
            Text = string.Format(Localization.PerAppCount, 0),
            FontSize = 10,
            FontFamily = new FontFamily("Consolas, SF Mono, Cascadia Code, Ubuntu Mono, monospace"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _appPickerCount.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");

        // Bug #2 (2026-05-11) — "Showing N apps" hint sits next to the
        // system-toggle so users can verify the enumeration is producing
        // a sane count. Pre-fix the user reported apps missing on Xiaomi
        // MIUI; the launcher-activities fallback in AppListLoader plus
        // this visible count makes the regression detectable at a glance.
        _appPickerShowingCount = new TextBlock
        {
            Text = string.Format(Localization.PerAppShowingCount, 0),
            FontSize = 10,
            FontFamily = new FontFamily("Consolas, SF Mono, Cascadia Code, Ubuntu Mono, monospace"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _appPickerShowingCount.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");

        var filterRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8,
            Margin = new Thickness(8, 6, 8, 0),
        };
        Grid.SetColumn(_appPickerSearch, 0);
        Grid.SetColumn(_appPickerCount, 1);
        filterRow.Children.Add(_appPickerSearch);
        filterRow.Children.Add(_appPickerCount);

        // Compact row: [☐ System apps]   spacer   Showing: N
        var togglesRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 8,
            Margin = new Thickness(8, 4, 8, 4),
        };
        Grid.SetColumn(_appPickerSystemToggle, 0);
        Grid.SetColumn(_appPickerShowingCount, 2);
        togglesRow.Children.Add(_appPickerSystemToggle);
        togglesRow.Children.Add(_appPickerShowingCount);

        _appPickerList = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };

        _appPickerSaveBtn = new Avalonia.Controls.Button
        {
            Content = Localization.PerAppSaveButton,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0, 12),
            Margin = new Thickness(8, 6, 8, 8),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            BorderThickness = new Thickness(0),
        };
        _appPickerSaveBtn.BindToken(Avalonia.Controls.Button.BackgroundProperty, "AccentSolidBrush");
        _appPickerSaveBtn.BindToken(Avalonia.Controls.Button.ForegroundProperty, "AccentOnSolidBrush");
        _appPickerSaveBtn.Click += OnAppPickerSaveClicked;

        // Bug #2 (2026-05-11) — mobile-first dock order: search-first at
        // the top (most-used on phone, thumb-reach), then include/exclude
        // mode + hint, then the system-toggle / showing-count row, then
        // the apps list (fills), and a sticky Save button at the bottom.
        // Pre-fix put mode label + buttons above search; on phone that
        // wasted prime thumb-reach real estate on a setting users rarely
        // change after first run.
        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(filterRow, Dock.Top);
        DockPanel.SetDock(_appPickerModeLabel!, Dock.Top);
        DockPanel.SetDock(modeRow, Dock.Top);
        DockPanel.SetDock(_appPickerModeHint!, Dock.Top);
        DockPanel.SetDock(togglesRow, Dock.Top);
        DockPanel.SetDock(_appPickerSaveBtn!, Dock.Bottom);
        dock.Children.Add(filterRow);
        dock.Children.Add(_appPickerModeLabel!);
        dock.Children.Add(modeRow);
        dock.Children.Add(_appPickerModeHint!);
        dock.Children.Add(togglesRow);
        dock.Children.Add(_appPickerSaveBtn!);
        dock.Children.Add(_appPickerList!);
        return dock;
    }

    /// <summary>
    /// Rebuild the left category sidebar: 10 built-ins + any user-created
    /// custom categories. Each row is a clickable Border so the whole pill
    /// reacts to taps; the active row gets <c>AccentBgSubtleBrush</c> +
    /// <c>AccentFgBrush</c> styling. Counts come from
    /// <see cref="ComputeCategoryCount"/> against the cached app list.
    /// </summary>
    private void RebuildAppCategorySidebar()
    {
        // Bug-AND-008 (2026-05-16) — WrapPanel host replaces the
        // scroll-strip StackPanel. Write chips into the WrapPanel so
        // they wrap to multiple rows instead of overflowing into a
        // horizontal ScrollViewer.
        var host = (Avalonia.Controls.Panel?)_advAppsCategoryWrapHost
                   ?? _advAppsCategoryListPanel;
        if (host is null) return;
        host.Children.Clear();
        _advAppsCategoryRowMap.Clear();
        _advAppsCategoryCountMap.Clear();
        _advAppsCategoryNameMap.Clear();

        // Bug-AND-008c (2026-05-16) — render Custom (the catch-all
        // "all apps" scope) FIRST. brat reported having to scroll
        // a long way right to reach Custom; with the WrapPanel layout
        // the chip is now on the first row at the top-left.
        var customDef = AndroidCategoryDefaults.All
            .FirstOrDefault(d => AndroidCategoryDefaults.IsCustomCatchAll(d.Id));
        if (customDef is not null)
        {
            var customRow = MakeAppsCategoryRow(
                customDef.Id,
                Localization.AdvAppsCategoryCustom,
                isCustom: false);
            host.Children.Add(customRow);
        }

        // Built-ins next (skip Custom — already added).
        foreach (var def in AndroidCategoryDefaults.All)
        {
            if (AndroidCategoryDefaults.IsCustomCatchAll(def.Id)) continue;
            var displayName = Localization.GroupDisplayName(def.Id);
            var row = MakeAppsCategoryRow(def.Id, displayName, isCustom: false);
            host.Children.Add(row);
        }

        // User-created custom categories below the built-ins.
        foreach (var cat in _advAppsCustomCategories)
        {
            if (string.IsNullOrWhiteSpace(cat.Name)) continue;
            var row = MakeAppsCategoryRow(cat.Name, cat.Name, isCustom: true);
            host.Children.Add(row);
        }

        UpdateAllCategoryCounts();
        StyleActiveCategoryRow();
    }

    /// <summary>One chip in the horizontal category strip — name + optional
    /// count rendered inline as a compact pill. Whole chip is tappable; active
    /// chip repaints via <see cref="StyleActiveCategoryRow"/> with an accent
    /// border and tinted background.
    /// <para>Bug #2 (2026-05-11): replaced the vertical sidebar row layout
    /// with a compact horizontal pill (Material filter-chip pattern). The
    /// pre-fix row spanned the full sidebar width (~120dp); chip width is now
    /// driven by content + 10/6 padding so 6-8 chips fit in a single 1080px
    /// row with horizontal scroll for the rest.</para></summary>
    private Border MakeAppsCategoryRow(string id, string displayName, bool isCustom)
    {
        var nameTb = new TextBlock
        {
            Text = displayName,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        nameTb.BindToken(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        var countTb = new TextBlock
        {
            Text = string.Empty,
            FontSize = 9,
            FontFamily = new FontFamily("Consolas, SF Mono, Cascadia Code, Ubuntu Mono, monospace"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };
        countTb.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");

        var inner = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { nameTb, countTb },
        };

        var border = new Border
        {
            Padding = new Thickness(10, 6),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Child = inner,
        };
        border.BindToken(Border.BorderBrushProperty, "BorderSubtleBrush");
        // Bug-AND-008 (2026-05-16) — chip strip is now a WrapPanel (no
        // surrounding ScrollViewer), so plain PointerPressed activation
        // is safe: no horizontal scroll to mistakenly trigger. Every
        // category is simultaneously visible and tappable.
        border.PointerPressed += (_, _) => SetActiveAppCategory(id);

        // Bug-AND-019 (2026-05-16) — long-press on a user-defined custom
        // category brings up a delete confirmation. Built-in categories
        // (Discord, Browsers, Custom catch-all, etc.) are immutable and
        // ignore the gesture.
        if (isCustom)
        {
            border.AddHandler(Gestures.HoldingEvent, (_, e) =>
            {
                if (e.HoldingState == HoldingState.Started)
                    PromptDeleteCustomCategory(id);
            });
        }

        _advAppsCategoryRowMap[id] = border;
        _advAppsCategoryCountMap[id] = countTb;
        _advAppsCategoryNameMap[id] = nameTb;
        return border;
    }

    /// <summary>Recompute "selected ∩ scope" count for every sidebar row.</summary>
    private void UpdateAllCategoryCounts()
    {
        foreach (var def in AndroidCategoryDefaults.All)
        {
            if (!_advAppsCategoryCountMap.TryGetValue(def.Id, out var tb)) continue;
            var n = ComputeCategoryCount(def.Id);
            tb.Text = n > 0 ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
        }
        foreach (var cat in _advAppsCustomCategories)
        {
            if (string.IsNullOrWhiteSpace(cat.Name)) continue;
            if (!_advAppsCategoryCountMap.TryGetValue(cat.Name, out var tb)) continue;
            var n = ComputeCustomCategoryCount(cat);
            tb.Text = n > 0 ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
        }
    }

    /// <summary>Selected packages within this built-in category's hint scope.
    /// For the catch-all, that's "selected NOT in any built-in hint" so the
    /// counts across all sidebar rows partition the selected set.</summary>
    private int ComputeCategoryCount(string id)
    {
        if (AndroidCategoryDefaults.IsCustomCatchAll(id))
        {
            var allBuiltIn = AndroidCategoryDefaults.AllBuiltInPackages();
            int n = 0;
            foreach (var pkg in _appPickerSelected)
                if (!allBuiltIn.Contains(pkg)) n++;
            return n;
        }

        var def = AndroidCategoryDefaults.Find(id);
        if (def is null) return 0;
        int hits = 0;
        foreach (var hint in def.PackageHints)
            if (_appPickerSelected.Contains(hint)) hits++;
        return hits;
    }

    /// <summary>Selected packages within a user-defined custom category's
    /// tagged Apps[] list (mirrors desktop's Apps.Count display semantics for
    /// custom categories: shows the user's own membership view).</summary>
    private int ComputeCustomCategoryCount(VPNRouter.Core.Models.CustomCategory cat)
    {
        if (cat.Apps is null || cat.Apps.Count == 0) return 0;
        int hits = 0;
        foreach (var pkg in cat.Apps)
            if (_appPickerSelected.Contains(pkg)) hits++;
        return hits;
    }

    /// <summary>Repaint the active chip with an accent border + tinted
    /// background. Bug #2 (2026-05-11): pre-fix this used the desktop's
    /// "lifted-card" affordance (SurfaceBaseBrush) which was illegible on a
    /// horizontal chip strip — chips need a visible border state, not a
    /// background lift. Now uses BorderAccentBrush + AccentBgSubtleBrush.</summary>
    private void StyleActiveCategoryRow()
    {
        var activeBg = GetBrush("AccentBgSubtleBrush");
        var activeBorder = GetBrush("BorderAccentBrush");
        var inactiveBorder = GetBrush("BorderSubtleBrush");
        var accentFg = GetBrush("AccentFgBrush");
        var defaultName = GetBrush("TextSecondaryBrush");
        var defaultCount = GetBrush("TextMutedBrush");

        foreach (var kv in _advAppsCategoryRowMap)
        {
            var isActive = string.Equals(kv.Key, _advAppsActiveCategoryId, System.StringComparison.OrdinalIgnoreCase);
            kv.Value.Background = isActive ? activeBg : Brushes.Transparent;
            kv.Value.BorderBrush = isActive ? activeBorder : inactiveBorder;
            if (_advAppsCategoryNameMap.TryGetValue(kv.Key, out var nameTb))
            {
                nameTb.Foreground = isActive ? accentFg : defaultName;
                nameTb.FontWeight = isActive ? FontWeight.Bold : FontWeight.SemiBold;
            }
            if (_advAppsCategoryCountMap.TryGetValue(kv.Key, out var countTb))
                countTb.Foreground = isActive ? accentFg : defaultCount;
        }
    }

    /// <summary>Switch active category. Persists via
    /// <see cref="AndroidStorage.SetApplicationsActiveCategory"/> so the next
    /// open lands on the same category.
    /// <para>Bug #2 (2026-05-11) — mobile redesign dropped the placeholder
    /// surface (the pre-fix 2-pane layout swapped placeholder ↔ scope body
    /// when id was empty). The scope body is now the only content surface,
    /// always visible; chip strip drives the filter.</para></summary>
    private void SetActiveAppCategory(string? id)
    {
        // Bug-AND-019 — intercept the tap for pending-delete confirm.
        // If we committed a delete the sidebar was rebuilt; activation
        // for the deleted id should be skipped (Custom catch-all
        // already became active inside the consume helper).
        if (ConsumePendingDeleteIfMatches(id)) return;
        _advAppsActiveCategoryId = id;
        AndroidStorage.SetApplicationsActiveCategory(id);
        StyleActiveCategoryRow();
        ApplyAppPickerFilter();
    }

    /// <summary>
    /// Bug-AND-019 (2026-05-16) — track which user-defined custom
    /// category was just long-pressed. The chip enters an inline
    /// "tap-to-confirm-delete" state; a second tap on the same chip
    /// drops it from <see cref="_advAppsCustomCategories"/>, a tap on
    /// anything else (different chip, app row, etc.) cancels.
    /// </summary>
    private string? _pendingDeleteCategoryId;

    private void PromptDeleteCustomCategory(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (!IsUserDefinedCategory(id)) return;
        _pendingDeleteCategoryId = id;
        // Repaint the chip's text to surface the inline confirmation
        // ("✗ Tap again to delete"). Active state still applies so the
        // chip stays visually anchored.
        if (_advAppsCategoryNameMap.TryGetValue(id, out var tb) && tb is not null)
        {
            tb.Text = "✗ " + Localization.AndroidDeleteCategoryConfirm;
            tb.Foreground = GetBrush("DangerFgBrush");
        }
    }

    /// <summary>Called by SetActiveAppCategory when the user taps a chip.
    /// If a delete is pending and the user tapped the SAME chip, commit
    /// the delete. Otherwise (different chip or different surface),
    /// cancel and revert the inline state.</summary>
    private bool ConsumePendingDeleteIfMatches(string? tappedId)
    {
        var pending = _pendingDeleteCategoryId;
        if (string.IsNullOrEmpty(pending)) return false;
        _pendingDeleteCategoryId = null;
        if (!string.Equals(pending, tappedId, System.StringComparison.OrdinalIgnoreCase))
        {
            // Cancel — repaint the original label on the previously
            // pending chip via a sidebar rebuild (cheaper than tracking
            // original label).
            RebuildAppCategorySidebar();
            return false;
        }
        // Commit delete.
        try
        {
            _advAppsCustomCategories.RemoveAll(c =>
                string.Equals(c.Name, pending, System.StringComparison.OrdinalIgnoreCase));
            AndroidStorage.SetCustomCategories(_advAppsCustomCategories);
            if (string.Equals(_advAppsActiveCategoryId, pending, System.StringComparison.OrdinalIgnoreCase))
                _advAppsActiveCategoryId = AndroidCategoryDefaults.CustomCatchAllId;
            RebuildAppCategorySidebar();
            ApplyAppPickerFilter();
        }
        catch (System.Exception ex)
        {
            global::Android.Util.Log.Warn("VpnRouter.Categories",
                $"Bug-AND-019 delete failed: {ex.GetType().Name}: {ex.Message}");
        }
        return true;
    }

    /// <summary>Add a user-created custom category from the sidebar's
    /// "+ New category" form. Trims input, ignores duplicates, persists, and
    /// auto-activates the new row.</summary>
    private void OnAdvAppsAddCategoryClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var raw = _advAppsNewCategoryInput?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(raw)) return;

        // Skip dupes: built-in id collision OR existing custom name.
        if (AndroidCategoryDefaults.Find(raw) is not null) return;
        foreach (var existing in _advAppsCustomCategories)
            if (string.Equals(existing.Name, raw, System.StringComparison.OrdinalIgnoreCase))
                return;

        _advAppsCustomCategories.Add(new VPNRouter.Core.Models.CustomCategory
        {
            Name = raw,
            Apps = new List<string>(),
            Enabled = true,
        });
        AndroidStorage.SetCustomCategories(_advAppsCustomCategories);

        if (_advAppsNewCategoryInput is not null) _advAppsNewCategoryInput.Text = string.Empty;
        RebuildAppCategorySidebar();
        SetActiveAppCategory(raw);
    }

    private void OnMenuCopyLogPathClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        try
        {
            var ctx = global::Android.App.Application.Context;
            var extDir = ctx.GetExternalFilesDir(null);
            if (extDir is null)
            {
                ShowMenuFeedback(Localization.SaveStatusUnknown);
                return;
            }
            var logPath = System.IO.Path.Combine(extDir.AbsolutePath, "singbox.log");
            CopyToClipboard("singbox-log-path", logPath);
            ShowMenuFeedback(logPath);
        }
        catch (Exception ex)
        {
            ShowMenuFeedback($"Error: {ex.GetType().Name}");
        }
    }

    private void OnMenuUpdateCheckClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        // v2.32.0 (2026-05-07) — wires the kebab item to the real
        // Android auto-update flow (AndroidUpdater + REQUEST_INSTALL_PACKAGES).
        // Pre-2.32.0 this just showed "coming in next release" toast.
        _ = RunUpdateCheckAsync(manual: true);
    }

    private void OnMenuResetSettingsClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_resetConfirmPending)
        {
            // Second tap — actually wipe.
            if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
            _resetConfirmPending = false;
            if (_menuResetSettingsItem is not null)
                _menuResetSettingsItem.Content = Localization.MenuItemResetSettings;

            try
            {
                AndroidStorage.SetVlessUri(null);
                AndroidStorage.SetSubscriptionUrl(null);
                AndroidStorage.SetServers(null);
                AndroidStorage.SetSelectedServerName(null);
                // Theme + language preserved (those are UI prefs, not
                // routing config) — same behaviour as desktop "Reset
                // routing settings" not nuking theme.
                ShowMenuFeedback(Localization.MenuItemResetDone);
            }
            catch (Exception ex)
            {
                ShowMenuFeedback($"Error: {ex.GetType().Name}");
            }
            return;
        }

        // First tap — show confirm prompt inline. Don't dismiss the
        // popup so the user can read the warning + tap the row again.
        _resetConfirmPending = true;
        if (_menuResetSettingsItem is not null)
            _menuResetSettingsItem.Content = Localization.MenuItemResetConfirm;
    }

    private void OnMenuRepoClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        try
        {
            var intent = new global::Android.Content.Intent(global::Android.Content.Intent.ActionView,
                global::Android.Net.Uri.Parse("https://github.com/PavelLizunov/VPNRouter"));
            intent.SetFlags(global::Android.Content.ActivityFlags.NewTask);
            global::Android.App.Application.Context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            ShowMenuFeedback($"Error: {ex.GetType().Name}");
        }
    }

    // AND-MIGRATE-OVERLAYS (2026-05-09): OnMenuFreeConfigsClicked retired.
    // Free Configs is now the Public configs tab inside the Advanced shell;
    // the kebab no longer hosts that entry.

    // ── F-10 kebab parity (2026-05-09) ─────────────────────────────────
    //
    // Items added to Android's kebab so the menu matches desktop. Each
    // handler closes the popup first, then fires the cross-platform
    // action with Android-appropriate plumbing.

    /// <summary>
    /// Diagnostics → "Check IP leak". Opens https://ipleak.net/ in the
    /// system browser. Mirrors desktop's <c>OpenLeakTest</c> command —
    /// same affordance, same URL.
    /// </summary>
    private void OnMenuCheckLeaksClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        try
        {
            var intent = new global::Android.Content.Intent(
                global::Android.Content.Intent.ActionView,
                global::Android.Net.Uri.Parse("https://ipleak.net/"));
            intent.SetFlags(global::Android.Content.ActivityFlags.NewTask);
            global::Android.App.Application.Context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            ShowMenuFeedback($"Error: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Diagnostics → "Run Health Check". Wraps Core <c>HealthCheck.RunAll</c>
    /// (same code path as desktop), writes the formatted report to
    /// <c>filesDir/last-health-check.txt</c> + reuses the singbox-log
    /// overlay as a viewer. Mirrors desktop's notepad-pop pattern with
    /// Android's in-app text viewer.
    /// </summary>
    private void OnMenuHealthCheckClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        try
        {
            var results = VPNRouter.Core.Services.HealthCheck.RunAll();
            var report = VPNRouter.Core.Services.HealthCheck.FormatReport(results);

            var ctx = global::Android.App.Application.Context;
            var filesDir = ctx.FilesDir?.AbsolutePath
                           ?? VPNRouter.Core.AppPaths.DataDir;
            var reportPath = System.IO.Path.Combine(filesDir, "last-health-check.txt");
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(reportPath)!);
                System.IO.File.WriteAllText(reportPath, report);
            }
            catch { /* still surface the report inline below */ }

            if (_logOverlay is null) return;
            if (_logViewerTitle is not null)
                _logViewerTitle.Text = Localization.MenuItemHealthCheck;
            if (_logViewerContent is not null)
            {
                _logViewerContent.Text = report;
                if (_logViewerEmptyState is not null) _logViewerEmptyState.IsVisible = false;
                if (_logViewerScroller is not null)
                {
                    _logViewerScroller.IsVisible = true;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_logViewerScroller is null) return;
                        _logViewerScroller.Offset = new Vector(
                            _logViewerScroller.Offset.X, 0);
                    }, DispatcherPriority.Background);
                }
            }
            _logOverlay.IsVisible = true;
        }
        catch (Exception ex)
        {
            ShowMenuFeedback($"Error: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Troubleshooting → "Restart in Safe Mode". Sets a one-shot flag in
    /// AndroidStorage so the next process startup skips the auto-connect /
    /// auto-update steps, then force-restarts the activity. Mirrors
    /// desktop's relaunch-with-<c>--safe</c> flag without the process-arg
    /// vehicle (Android lifecycle uses Intent extras / SharedPreferences).
    /// </summary>
    private void OnMenuRestartSafeModeClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        try
        {
            // Mark next launch as safe-mode. AndroidApp.OnFrameworkInitializationCompleted
            // (or downstream startup hooks) read this flag and skip
            // auto-connect / auto-update / heavy bootstrap if set, then
            // clear the flag so a subsequent launch is normal.
            try { AndroidStorage.SetSafeModeOnNextLaunch(true); }
            catch { /* fall through — restart still helps */ }

            var ctx = global::Android.App.Application.Context;
            var pkg = ctx.PackageName;
            if (string.IsNullOrEmpty(pkg)) return;
            var launchIntent = ctx.PackageManager?.GetLaunchIntentForPackage(pkg);
            if (launchIntent is null) return;
            launchIntent.AddFlags(global::Android.Content.ActivityFlags.ClearTop
                                | global::Android.Content.ActivityFlags.NewTask);
            ctx.StartActivity(launchIntent);
            // Schedule process exit so the new activity comes up fresh
            // (Activity.Recreate doesn't tear down the process; Safe Mode
            // is more useful when the JVM is also restarted).
            global::Java.Lang.JavaSystem.Exit(0);
        }
        catch (Exception ex)
        {
            ShowMenuFeedback($"Error: {ex.GetType().Name}");
        }
    }

    private void CopyToClipboard(string label, string text)
    {
        try
        {
            var ctx = global::Android.App.Application.Context;
            var clipboard = ctx.GetSystemService(global::Android.Content.Context.ClipboardService)
                            as global::Android.Content.ClipboardManager;
            if (clipboard is null) return;
            var clip = global::Android.Content.ClipData.NewPlainText(label, text);
            clipboard.PrimaryClip = clip;
        }
        catch
        {
            // Clipboard unavailable on some restricted devices — silently ignore.
        }
    }

    /// <summary>
    /// Surfaces a short transient message under the status card. Used by
    /// the Phase 7.2 menu actions (log path copied, settings reset done,
    /// update placeholder, error). Auto-clears after ~3 s.
    /// </summary>
    private async void ShowMenuFeedback(string text)
    {
        if (_menuFeedback is null) return;
        _menuFeedback.Text = text;
        _menuFeedback.IsVisible = true;
        try
        {
            await System.Threading.Tasks.Task.Delay(3000);
            if (_menuFeedback is not null && _menuFeedback.Text == text)
            {
                _menuFeedback.IsVisible = false;
            }
        }
        catch { /* swallow */ }
    }

    private void ToggleLanguageAndRefresh()
    {
        Localization.ToggleAndPersist();
        if (_brandTitle is not null) _brandTitle.Text = Localization.BrandTitle;
        // Phase 7.3 — segment controls re-style themselves via
        // RepaintLanguageSegment / RepaintThemeSegment; only the theme
        // segment label switches between RU/EN since it's localized.
        if (_menuThemeLight is not null) _menuThemeLight.Content = Localization.MenuSegLight;
        if (_menuThemeDark is not null) _menuThemeDark.Content = Localization.MenuSegDark;
        // RU/EN segment labels are locale-independent; nothing to update.
        // Phase 7.2 menu items
        if (_menuSettingsItem is not null) _menuSettingsItem.Content = Localization.MenuItemSettings;
        if (_menuOpenLogItem is not null) _menuOpenLogItem.Content = Localization.MenuItemOpenLogs;
        if (_menuCopyLogPathItem is not null) _menuCopyLogPathItem.Content = Localization.MenuItemCopyLogPath;
        if (_menuViewCrashLogItem is not null) _menuViewCrashLogItem.Content = Localization.MenuItemViewCrashLog;
        if (_menuUpdateCheckItem is not null) _menuUpdateCheckItem.Content = Localization.MenuItemUpdateCheck;
        if (_menuResetSettingsItem is not null && !_resetConfirmPending)
            _menuResetSettingsItem.Content = Localization.MenuItemResetSettings;
        // F-12 kebab visual parity (2026-05-09): About row's left text is
        // now a stand-alone TextBlock inside a Grid (not the Button.Content
        // string), so refresh that field directly. Version pill stays put —
        // not localized. _menuRepoItem is a null stub (combined into About).
        if (_menuAboutLabel is not null) _menuAboutLabel.Text = Localization.SmpMenuAbout;
        // Bug-AND-009 follow-up (2026-05-16) — kebab "Advanced ▸"
        // toggle button. Sat as a local var pre-fix and missed
        // language refresh, so its label drifted out of sync with the
        // rest of the menu.
        if (_menuAdvancedToggleBtn is not null)
            _menuAdvancedToggleBtn.Content = Localization.SmpToggleToAdvanced;
        // Bug-AND-014 (2026-05-16, manual test pass iter 18) — Simple-page
        // autostart card text + per-app "Choose apps…" button were
        // baked-in at build time. Refresh on language toggle.
        if (_autostartCardTitleText is not null)
            _autostartCardTitleText.Text = Localization.SmpAutostartCardTitle;
        if (_autostartCardSubText is not null)
            _autostartCardSubText.Text = Localization.SmpAutostartCardSubtitle;
        if (_perAppPickButton is not null)
            _perAppPickButton.Content = Localization.PerAppPickButton;
        // Section headers
        if (_menuSectionView is not null) _menuSectionView.Text = Localization.MenuSectionView;
        if (_menuSectionDiagnostics is not null) _menuSectionDiagnostics.Text = Localization.MenuSectionDiagnostics;
        if (_menuSectionTroubleshooting is not null) _menuSectionTroubleshooting.Text = Localization.MenuSectionTroubleshooting;
        if (_menuSectionAbout is not null) _menuSectionAbout.Text = Localization.MenuSectionAbout;
        // AND-MIGRATE-OVERLAYS (2026-05-09): Free Configs + Tools kebab
        // sections retired; their items migrated to the Advanced shell.
        // The null-check stubs below are kept for the old field references
        // even though the items are intentionally null now.
        if (_menuSectionFreeConfigs is not null) _menuSectionFreeConfigs.Text = Localization.MenuSectionFreeConfigs;
        if (_menuFreeConfigsItem is not null) _menuFreeConfigsItem.Content = Localization.MenuItemOpenFreeConfigs;
        if (_menuSectionProfiles is not null) _menuSectionProfiles.Text = Localization.MenuSectionProfiles;
        if (_menuProfilesItem is not null) _menuProfilesItem.Content = Localization.MenuItemOpenProfiles;
        if (_menuSectionTools is not null) _menuSectionTools.Text = Localization.MenuSectionTools;
        if (_menuToolsItem is not null) _menuToolsItem.Content = Localization.MenuItemOpenTools;
        if (_menuDpiBypassItem is not null) _menuDpiBypassItem.Content = Localization.MenuItemOpenDpiBypass;
        // Refresh Advanced-shell title + tab labels on language toggle.
        RefreshAdvancedShellStrings();
        // F-10 kebab parity (2026-05-09) — refresh new Diagnostics +
        // Troubleshooting items.
        if (_menuCheckLeaksItem is not null) _menuCheckLeaksItem.Content = Localization.MenuItemCheckLeaks;
        if (_menuHealthCheckItem is not null) _menuHealthCheckItem.Content = Localization.MenuItemHealthCheck;
        if (_menuRestartSafeModeItem is not null) _menuRestartSafeModeItem.Content = Localization.MenuItemSafeMode;
        // Profiles overlay header refresh — body cards rebuild on next open
        // (no point refreshing now since they hold non-localizable catalog
        // names like "Discord_Privacy"; only the description + chip labels
        // localize, and those re-resolve through Localization.* at next
        // ShowProfilesOverlay).
        if (_profilesOverlayTitle is not null) _profilesOverlayTitle.Text = Localization.ProfilesOverlayTitle;
        if (_profilesOverlayIntro is not null) _profilesOverlayIntro.Text = Localization.ProfilesIntro;
        // v2.32.0 (Android-led) — refresh config share overlay strings
        // (export/import/QR) along with their kebab entries.
        RefreshConfigShareLocalization();
        if (_statusCard is not null)
        {
            _statusCard.Title = MainActivity.IntendedConnected ? Localization.SimpleStatusTitleOn : Localization.SimpleStatusTitleOff;
            _statusCard.Subtitle = MainActivity.IntendedConnected ? Localization.SimpleStatusDescOn : Localization.SimpleStatusDescOff;
        }
        // v2.32.0 (AND-DIAG) — re-render diagnostics surface so the
        // localized "X s ago" / "Awaiting first check" labels swap to
        // the new language. Title's uptime suffix is re-applied on the
        // very next 1-second tick, so don't manually rewrite here.
        ApplyHealthCheckDisplay();
        ApplyErrorOneLinerDisplay();
        if (_configRowLabel is not null) _configRowLabel.Text = Localization.SmpConfigRowLabel;
        if (_serverInputLabel is not null) _serverInputLabel.Text = Localization.SmpInputLabel;
        if (_serverInput is not null) _serverInput.Watermark = Localization.SmpInputWatermark;
        if (_serverInputHint is not null) _serverInputHint.Text = Localization.SmpInputHint;
        // v2.32.0 (AND-CC) — refresh segmented mode selector + custom
        // section labels. The status banner text below stays as-is —
        // it's the result of the user's last Validate / Save tap, so
        // shouldn't auto-translate (would lie about what was actually
        // returned at click-time).
        if (_ccModeSubBtn is not null) _ccModeSubBtn.Content = Localization.CcModeSubscription;
        if (_ccModeManualBtn is not null) _ccModeManualBtn.Content = Localization.CcModeManual;
        if (_ccModeCustomBtn is not null) _ccModeCustomBtn.Content = Localization.CcModeCustom;
        if (_ccCustomLabel is not null) _ccCustomLabel.Text = Localization.CcCustomLabel;
        if (_ccCustomHint is not null) _ccCustomHint.Text = Localization.CcCustomHint;
        if (_ccCustomInput is not null) _ccCustomInput.Watermark = Localization.CcCustomWatermark;
        if (_ccValidateBtn is not null) _ccValidateBtn.Content = Localization.CcValidateButton;
        if (_ccSaveCustomBtn is not null) _ccSaveCustomBtn.Content = Localization.CcSaveButton;
        if (_ccClearCustomBtn is not null) _ccClearCustomBtn.Content = Localization.CcClearButton;
        if (_tunnelModeLabel is not null) _tunnelModeLabel.Text = Localization.SmpTunnelModeLabel;
        if (_splitLabel is not null) _splitLabel.Text = Localization.SmpSplitOption;
        if (_splitHint is not null) _splitHint.Text = Localization.SmpSplitHint;
        if (_fullLabel is not null) _fullLabel.Text = Localization.SmpFullOption;
        if (_fullHint is not null) _fullHint.Text = Localization.SmpFullHint;
        if (_serverListHeader is not null) _serverListHeader.Text = Localization.AvailableServers;
        if (_advCardTitle is not null) _advCardTitle.Text = Localization.SmpAdvCardTitle;
        if (_advCardSubtitle is not null) _advCardSubtitle.Text = Localization.SmpAdvCardSubtitle;
        if (_ctaConnect is not null) _ctaConnect.Content = Localization.ButtonConnect;
        if (_ctaConnecting is not null) _ctaConnecting.Content = Localization.ButtonConnecting;
        if (_ctaDisconnect is not null) _ctaDisconnect.Content = Localization.ButtonDisconnect;
        // v2.32.0 — refresh Subscribe overlay strings (title, add form,
        // refresh-all button, empty-state hint, per-card text).
        RefreshSubsLocalizedStrings();
        // v2.32.0 (AUTOUPDATE) — auto-update banner copy when visible.
        if (_updateBannerDismiss is not null) _updateBannerDismiss.Content = Localization.UpdateButtonDismiss;
        if (_updateBannerSubtitle is not null && _updateBannerSubtitle.IsVisible)
            _updateBannerSubtitle.Text = Localization.UpdateBannerSubtitle;
        // v2.32.0 (SERVER-TESTING) — refresh Server-list overlay strings.
        RefreshServerListLocalizedStrings();
        UpdateConfigSummary();
    }

    /// <summary>Pre-Phase-4 entry point retained so any stale subscribers
    /// don't break — delegates to the new ToggleLanguageAndRefresh.</summary>
    private void OnLanguageToggleClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ToggleLanguageAndRefresh();
}
