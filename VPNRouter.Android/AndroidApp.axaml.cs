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
    private Avalonia.Controls.Button? _menuExportDiagItem;
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
    // Phase 4 (Wave 18, 2026-05-18) — pending-update snapshot is now the
    // platform-neutral UpdateSourceInfo record (returned by
    // IUpdateSource.CheckAsync) instead of the Android-only legacy
    // AndroidUpdateInfo. AndroidUpdater still lives but only as a host
    // for permission-gate static helpers + the APK download primitives
    // that AndroidInstallerAdapter wraps.
    private global::VPNRouter.Core.Services.UpdateSources.UpdateSourceInfo? _pendingUpdate;
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
    private Avalonia.Controls.CheckBox? _externalControlToggle;   // P4: allow broadcast START/STOP/TOGGLE
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
    // KeyApplicationsActiveCategory; null/empty falls back to catch-all.
    // Custom user categories live in
    // _advAppsCustomCategories (loaded from AndroidStorage on tab activation,
    // persisted via SetCustomCategories).
    private string? _advAppsActiveCategoryId;
    private WrapPanel? _advAppsCategoryWrapHost;
    private TextBox? _advAppsNewCategoryInput;
    private Avalonia.Controls.Button? _advAppsAddCategoryBtn;
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

        // DNS-tunnel (slipstream) availability gate — probe whether the native
        // Slipstream client (libslipstream_jni.so) is bundled + loadable for
        // this device ABI. ServerUriParser refuses the dns-tunnel:// scheme when
        // this is false, so a build/ABI without the .so degrades cleanly (the
        // scheme is rejected at parse time, not mid-connect with an
        // UnsatisfiedLinkError). Same .so the service loads — System.loadLibrary
        // is idempotent, so this just primes it. Must run before ReloadServerList
        // (which parses cached servers through ServerUriParser).
        try
        {
            global::Java.Lang.JavaSystem.LoadLibrary("slipstream_jni");
            ServerUriParser.SlipstreamRuntimeAvailable = true;
            global::Android.Util.Log.Info("VpnRouter",
                "dns-tunnel: native Slipstream library available — dns-tunnel:// enabled");
        }
        catch (Exception ex)
        {
            ServerUriParser.SlipstreamRuntimeAvailable = false;
            global::Android.Util.Log.Info("VpnRouter",
                $"dns-tunnel: native Slipstream library unavailable ({ex.GetType().Name}) — dns-tunnel:// disabled");
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

    // ConsumeAndSurfaceRecoveryNotice moved to AndroidApp.Notifications.cs
    // (Phase 2C Wave 9, 2026-05-18).

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
        // shows: mascot 🐧 + "Virtual Penguin Network" bold + two
        // status chips (VPN / Zapret) + ⋯ kebab menu. The kebab
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
        // 2026-05-15 (Bug-AND-002 brat live-test): hide Zapret chip
        // entirely on Android. Pre-fix: chips were always rendered Off
        // because «those features aren't ported yet». User feedback:
        // «не нужно отображать zapret и tg прокси так как из нет, условно
        // ведь на мак мы их не отображет». Same rationale as Mac/Linux —
        // platform-not-applicable features should be hidden, not shown
        // as perpetually-Off. The _zapretChip field is kept because update
        // paths still mirror state into it, but it stays out of the visual row.
        _vpnChip = MakeChip("VPN", "SurfaceSunkenBrush", "TextMutedBrush");
        _zapretChip = MakeChip("Zapret", "SurfaceSunkenBrush", "TextMutedBrush");

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
        // v2.40.0 night-shift — Android parity for desktop "Export diagnostics".
        _menuExportDiagItem = MakeMenuItem(Localization.MenuItemExportDiag,
                                           "TextPrimaryBrush", OnMenuExportDiagClicked);
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
        // the standalone "GitHub repository" row used to point at).
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

        var menuStack = new StackPanel
        {
            Spacing = 1,
        };

        AppendMenuSectionWithControls(menuStack, Localization.MenuSectionView,
                                      new Control[] { themeRow, langRow });
        // v2.32.0 desktop parity (2026-05-10): Diagnostics = Open log +
        // Check IP leak + Check for updates (3 items, matches MainWindow.axaml
        // line 506-523). Other items previously here (Settings, Copy log
        // path, View crash log, Export/Import config) were post-v2.32.0
        // additions and are removed.
        AppendMenuSection(menuStack, Localization.MenuSectionDiagnostics,
                          new[] { _menuOpenLogItem, _menuExportDiagItem, _menuCheckLeaksItem,
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

        // Bug-AND-023 (2026-05-17, user-requested "сканирование qr code
        // чтоб добавить подписку или конфиг через qr") — QR scan button
        // next to the Simple-page input field. Tap → MainActivity
        // dispatches MediaStore camera intent → ZXing decodes JPEG →
        // callback fills _serverInput.Text with the decoded URI.
        // QrCodeDecoder + RequestQrCodeScan plumbing already existed
        // (lucid-pike, 2026-05-09) but the button entry had been
        // removed during earlier UX polish; this restores it.
        var smpQrButton = new Avalonia.Controls.Button
        {
            Content = "📷",
            FontSize = 14,
            Width = 44,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            CornerRadius = new CornerRadius(radiusXs),
        };
        smpQrButton.BindToken(Avalonia.Controls.Button.BackgroundProperty, "AccentBgSubtleBrush");
        smpQrButton.BindToken(Avalonia.Controls.Button.ForegroundProperty, "AccentFgBrush");
        ToolTip.SetTip(smpQrButton, Localization.SmpScanQrButton);
        smpQrButton.Click += OnSimpleQrScanClicked;

        var inputRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 6,
        };
        Grid.SetColumn(_serverInput, 0);
        Grid.SetColumn(smpQrButton, 1);
        inputRow.Children.Add(_serverInput);
        inputRow.Children.Add(smpQrButton);

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
                inputRow,
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

        // Bug-AND-023 v3 (2026-05-17, user-reported "там был список серверов
        // подписки, чего нет в desktop версии"): the Simple-page subscription
        // server list was Android-only and rendered every server from every
        // subscription as a ListBox embedded INSIDE the form. Desktop's
        // SimplePage has no such picker — server selection lives entirely
        // in Advanced → Servers. v3 drops the list from the Simple page to
        // bring it to parity. The _serverList field is kept (still nullable)
        // so the existing UpdateServerListView / OnServerSelectionChanged
        // paths can stay; they all no-op when the controls are null.
        // ReloadServerList() is still useful as a side-effect-free cache
        // warmer for _cachedServers, which is read by the Advanced-tab
        // server-picker code.

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
                // Bug-AND-023 v3 — Children mirror desktop exactly:
                // input → tunnel → autostart. The old Android-only
                // listSection (subscription server picker) has been
                // dropped per user-reported desktop-parity request.
                Children = { inputSection, tunnelSection, autostartCard }
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
                // v2.36.0-r3 UX-2 fix (EOStārāTheia 2026-05-23): _updateBanner
                // was here. Moved out to the root Grid as a top-floating
                // overlay so the banner is visible in BOTH Simple and
                // Advanced shell modes. Pre-r3 banner was a child of this
                // Simple-page innerStack — when user switched to Advanced
                // shell (which overlays Simple), the banner became hidden
                // and user couldn't access the update flow without
                // switching back to Simple. See root Grid below.
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

        // v2.36.0-r3 UX-2 fix (EOStārāTheia 2026-05-23): wrap the
        // _updateBanner in a top-aligned floating container so it appears
        // ABOVE both Simple page (mainScroller) and Advanced shell
        // (_advShellOverlay). Margin pushes it below the system status
        // bar (16dp safe gap). The banner's own IsVisible toggle controls
        // when it surfaces.
        var updateBannerFloating = new Grid
        {
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(16, 16, 16, 0),
            Background = Brushes.Transparent,
            Children = { _updateBanner! },
        };

        return new Grid
        {
            // Per-feature overlays folded into Advanced shell tabs (AND-ADV-MIGRATE).
            // Kebab QR-share also removed (chip naughty-darwin) — _cfgQrOverlay gone.
            // r3: updateBannerFloating last → top z-order, visible in both
            // Simple + Advanced modes.
            Children = { mainScroller, _logOverlay,
                         _cfgExportOverlay, _cfgImportOverlay, _profilesOverlay,
                         _advShellOverlay,
                         updateBannerFloating }
        };
    }

    // BuildLogOverlay (in-app log viewer overlay, last 50 KB of singbox.log)
    // moved to AndroidApp.Notifications.cs (Phase 2C Wave 9, 2026-05-18).

    // BuildNetworkTabContent / BuildSettingsSideNav /
    // MakeSettingsSubSectionButton / SettingsSubSectionLabel /
    // StyleSettingsSubSectionButton / BuildSettingsContentPane /
    // WrapSubSectionScroller / SelectSettingsSubSection /
    // BuildSettingsFooterBar / BuildSettingsRoutingSection /
    // BuildDpiBypassCard / BuildSettingsRulesSection /
    // BuildSettingsLeakSection / BuildSettingsContentSection /
    // BuildSettingsUpdatesSection / BuildSettingsAutostartSection /
    // MakeSectionTitle / WrapSection / MakeRadioCard /
    // MakeCheckboxCard / MakeLabeledCheckboxRow / MakeAutostartRow
    // moved to AndroidApp.UiBindings.cs (Phase 2C Wave 9, 2026-05-18).

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
    /// label) for the sub-header VPN/Zapret indicators. Mirrors
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

    /// <summary>
    /// Bug-AND-023 (2026-05-17) — QR scan handler on Simple page. Sets
    /// the MainActivity static one-shot callback, then dispatches the
    /// camera intent. On success the callback marshals back to UI
    /// thread, fills _serverInput.Text with the decoded URI, and
    /// surfaces a toast. On error the toast surfaces the failure
    /// (permission denied / not recognised). One-shot — re-tap to scan
    /// again.
    /// </summary>
    private void OnSimpleQrScanClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var activity = MainActivity.Instance;
        if (activity is null) return;
        MainActivity.PendingQrScanCallback = (success, text) =>
        {
            Dispatcher.UIThread.Post(async () =>
            {
                if (!success)
                {
                    // Bug-AND-023 v2 (2026-05-17): "cancelled" arrives when
                    // the user back-presses out of the live scanner. That
                    // isn't an error — they just changed their mind — so
                    // skip the toast. Other failure codes still surface so
                    // the user knows why nothing happened.
                    if (text == "cancelled") return;
                    var msg = text switch
                    {
                        "permission_denied" => Localization.SmpQrPermissionDenied,
                        _                    => Localization.SmpQrNotRecognized,
                    };
                    ShowMenuFeedback(msg);
                    return;
                }
                // Bug-AND-023 v3 (2026-05-17) — "магия 1-действия". Auto-
                // dispatch by URI scheme: vless:// → add as server +
                // Connect; http(s):// → add subscription, refresh, pick
                // first server, Connect. See AndroidApp.QrScanApply.cs for
                // the full routing logic.
                await ApplyScannedTextAsync(text);
            });
        };
        activity.RequestQrCodeScan();
    }

    // VPN lifecycle / chip state / diagnostics pump methods + the
    // _lifecycleEventsAttached field all live in AndroidApp.VpnLifecycle.cs
    // (Phase 2C Wave 9, 2026-05-18; multi-instance subscriber-swap removed
    // 2026-06-13 — Avalonia 12 = one AndroidApp per process).

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
            catch (PlaceholderConfigException ex)
            {
                // v2.32.3 — see ApplyScannedServerUri for context. Paste
                // path uses the same guard, distinct error so user knows
                // it's a credential problem.
                global::Android.Util.Log.Warn("VpnRouter.Simple",
                    $"Paste: placeholder rejected — field={ex.OffendingField}");
                _serverInputError.Text = Localization.PlaceholderCredentialRejected;
                _serverInputError.IsVisible = true;
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
    private async void OnMenuHealthCheckClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        // A7 (2026-06-13): HealthCheck.RunAll runs DNS/network probes that can
        // stall, so offload the whole compute + file-write to a background
        // thread (mirrors OnMenuExportDiagClicked's await Task.Run pattern).
        // Disable the menu item for the duration so a deliberate re-tap can't
        // launch a second probe; re-enable in finally so it recovers on
        // success, exception, and the early-return path alike. The await
        // resumes on the UI SynchronizationContext, so the UI mutations below
        // stay UI-safe.
        if (_menuHealthCheckItem is not null) _menuHealthCheckItem.IsEnabled = false;
        try
        {
            var report = await System.Threading.Tasks.Task.Run(() =>
            {
                var results = VPNRouter.Core.Services.HealthCheck.RunAll();
                var formatted = VPNRouter.Core.Services.HealthCheck.FormatReport(results);

                var ctx = global::Android.App.Application.Context;
                var filesDir = ctx.FilesDir?.AbsolutePath
                               ?? VPNRouter.Core.AppPaths.DataDir;
                var reportPath = System.IO.Path.Combine(filesDir, "last-health-check.txt");
                try
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(reportPath)!);
                    System.IO.File.WriteAllText(reportPath, formatted);
                }
                catch { /* still surface the report inline below */ }

                return formatted;
            });

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
        finally
        {
            if (_menuHealthCheckItem is not null) _menuHealthCheckItem.IsEnabled = true;
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

    // CopyToClipboard / ShowMenuFeedback moved to AndroidApp.Notifications.cs
    // (Phase 2C Wave 9, 2026-05-18).

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
        if (_menuExportDiagItem is not null) _menuExportDiagItem.Content = Localization.MenuItemExportDiag;
        if (_menuCopyLogPathItem is not null) _menuCopyLogPathItem.Content = Localization.MenuItemCopyLogPath;
        if (_menuViewCrashLogItem is not null) _menuViewCrashLogItem.Content = Localization.MenuItemViewCrashLog;
        if (_menuUpdateCheckItem is not null) _menuUpdateCheckItem.Content = Localization.MenuItemUpdateCheck;
        if (_menuResetSettingsItem is not null && !_resetConfirmPending)
            _menuResetSettingsItem.Content = Localization.MenuItemResetSettings;
        // F-12 kebab visual parity (2026-05-09): About row's left text is
        // now a stand-alone TextBlock inside a Grid (not the Button.Content
        // string), so refresh that field directly. Version pill stays put —
        // not localized.
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
