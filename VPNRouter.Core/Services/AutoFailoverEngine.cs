using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// F-E (2026-05-11) — orchestrates auto-failover when
/// <see cref="ConfigSanityCheck"/> flags the active config as dead.
///
/// <para>Picks the next available subscription / manual server that hasn't
/// already been tried this session, mutates
/// <c>settings.Vless.ActiveServer</c> + <c>settings.App.ActiveSubscriptionServer</c>
/// to the new pick, persists via <see cref="SettingsLoader.Save"/>, and
/// surfaces a user-readable message describing what happened.</para>
///
/// <para>The actual restart is delegated to a caller-supplied delegate
/// (typically <c>VpnEngine.StartAsync</c> or <c>VpnEngine.ApplyAsync</c>) —
/// keeps this class decoupled from the engine's lifecycle quirks and
/// trivially testable. <c>HandleDeadConfigAsync</c> does NOT recursively
/// re-call itself: the caller (VpnEngine) calls ConfigSanityCheck again on
/// the next start and feeds back into this engine if the new server is
/// also dead.</para>
///
/// <para>State: <see cref="TriedServers"/> is per-session (lifetime of the
/// AutoFailoverEngine instance). DI should register it scoped to a single
/// connect attempt so a successful connect resets the cycle.</para>
/// </summary>
public sealed class AutoFailoverEngine
{
    /// <summary>Hard cap on cycle length. 3 attempts = ~3 × startup time
    /// (worst case ~30s) before we surface the "all dead" alert. Higher
    /// values risk Wagner's-law-style starvation of the foreground.</summary>
    public const int MaxAttempts = 3;

    private readonly AppSettings _settings;
    private readonly ConfigSanityCheck _sanity;
    private readonly ILogger? _logger;
    private readonly Func<CancellationToken, Task<bool>>? _restart;
    private readonly ISettingsStore _store;

    // Tracks the active-server names we've already cycled THROUGH. The
    // entry being switched FROM is added before the switch, so a failed
    // retry on the same name doesn't loop. Case-insensitive.
    public IReadOnlySet<string> TriedServers => _tried;
    private readonly HashSet<string> _tried = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Construct the engine. <paramref name="restart"/> is a delegate that
    /// stops + restarts the VPN with the now-current <paramref name="settings"/>
    /// (typically <c>vpnEngine.StartAsync</c>). Returns true if the new
    /// start succeeded.
    /// </summary>
    /// <param name="store">3G-1 (v3.0 refactor): persistence seam. Defaults
    /// to <see cref="RealSettingsStore.Instance"/> for back-compat with the
    /// pre-3G callers that constructed <see cref="AutoFailoverEngine"/>
    /// without a store parameter. Tests inject <c>InMemorySettingsStore</c>
    /// to avoid writing to <c>%ProgramData%\VPNRouter\config.yaml</c>.</param>
    public AutoFailoverEngine(
        AppSettings settings,
        ConfigSanityCheck sanity,
        Func<CancellationToken, Task<bool>>? restart = null,
        ILogger? logger = null,
        ISettingsStore? store = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _sanity = sanity ?? throw new ArgumentNullException(nameof(sanity));
        _restart = restart;
        _logger = logger;
        _store = store ?? RealSettingsStore.Instance;
    }

    /// <summary>
    /// Called when <see cref="ConfigSanityCheck"/> flagged the config as
    /// dead. Tries to pick the next available server, persist the new
    /// selection, and (optionally) trigger a restart via the supplied
    /// delegate.
    /// </summary>
    public async Task<FailoverOutcome> HandleDeadConfigAsync(
        string reason,
        CancellationToken ct = default)
    {
        _logger?.Warning("[AutoFailover] Dead config: {Reason}", reason);

        // 1. Custom-mode escape — we never silently swap a user's JSON.
        var configMode = (_settings.App.ConfigMode ?? "generated").Trim().ToLowerInvariant();
        if (configMode == "custom")
        {
            return new FailoverOutcome(
                Switched: false,
                NewActiveServer: null,
                UserFacingMessage:
                    "Кастомный конфиг недоступен. Проверьте JSON в Серверы → Custom — " +
                    "поле server, server_port, uuid или Reality public_key выглядят неверно.");
        }

        // r10 r8 (Bug-r10-F, 2026-05-11 brat report) — generated-mode +
        // subscription enabled: user explicitly picked a manual VLESS
        // entry (Free Configs, paste) DESPITE having a subscription
        // available. That's a clear "I want THIS server, not the
        // subscription". Pre-r8 F-E silently swapped their choice to
        // subscription's first server and persisted, breaking the
        // user-clear "I clicked X, why does it route via Y" expectation.
        // Brat's log: picked ⚡ [EE], probe timed out (Clash API 504 —
        // many causes including network glitch, Reality protocol's
        // masking that refuses naïve probes), F-E silently swapped to
        // de-01.
        //
        // Legacy direct-VLESS mode (no subscription) keeps auto-switch
        // because there is no alternative pool the user could've meant
        // by "manual" — they want SOME working server in their manual list.
        //
        // Subscribe mode keeps auto-switch because "best available from
        // subscription" is what subscribe mode means.
        var hasEnabledSub = _settings.App?.Subscriptions?
            .Any(s => s != null && s.Enabled && (s.Servers?.Count ?? 0) > 0) == true;

        if (configMode == "generated"
            && hasEnabledSub
            && IsActiveLegitimateManual())
        {
            _logger?.Information(
                "[AutoFailover] Skipping auto-swap in generated mode — active '{Active}' is a legitimate manual choice; surfacing error instead",
                _settings.Vless.ActiveServer);
            return new FailoverOutcome(
                Switched: false,
                NewActiveServer: null,
                UserFacingMessage:
                    $"Сервер '{_settings.Vless.ActiveServer}' не отвечает на probe " +
                    $"({reason}). VPN запущен, но прямая проверка через сервер не " +
                    "проходит — возможно ложное срабатывание (Reality маскируется) " +
                    "или сервер действительно недоступен. Выберите другой сервер из " +
                    "списка вручную или переключитесь на подписку.");
        }

        // 2. Cap retries — after MaxAttempts cycles we stop and surface the
        // "all dead" alert so the user can pick a working server manually
        // or refresh their subscription.
        if (_tried.Count >= MaxAttempts)
        {
            _logger?.Warning(
                "[AutoFailover] Exceeded MaxAttempts ({Max}) — surfacing alert",
                MaxAttempts);
            return new FailoverOutcome(
                Switched: false,
                NewActiveServer: null,
                UserFacingMessage:
                    $"Все серверы недоступны ({_tried.Count} попыток). " +
                    "Проверьте подписку (Обновить) или сетевое подключение.");
        }

        // 3. Pick the next candidate. Pool depends on whether we're in
        // subscribe-mode or legacy direct-list mode.
        var candidate = PickNextCandidate(out var poolSource);
        if (candidate == null)
        {
            _logger?.Warning(
                "[AutoFailover] No candidate servers left (pool source: {Source})",
                poolSource);
            // 2026-06-09 (rectuspc report — subscription trimmed to one server
            // that the user's ISP blocks by IP): the previous wording ("нет
            // ДРУГИХ серверов, нажмите Обновить") was misleading — the active
            // server isn't absent, it's UNREACHABLE, and Refresh won't help.
            // Convey the real situation: the server isn't responding (likely
            // ISP-blocked or down) and there's no alternative to fail over to.
            return new FailoverOutcome(
                Switched: false,
                NewActiveServer: null,
                UserFacingMessage:
                    poolSource == "subscriptions"
                        ? "Сервер не отвечает, а других в подписке нет — возможно, провайдер " +
                          "блокирует его IP или сервер недоступен. Смените сервер или попросите " +
                          "обновить подписку."
                        : "Сервер не отвечает, а других в списке VLESS нет — возможно, он " +
                          "заблокирован или недоступен. Добавьте другой сервер.");
        }

        // 4. Record the OLD active server in _tried so we never loop back
        // to it within this session. We add the OLD one, not the NEW —
        // because the NEW one is what we're about to test.
        var oldActive = _settings.Vless.ActiveServer ?? "";
        var oldActiveSub = _settings.App.ActiveSubscriptionServer;
        if (!string.IsNullOrWhiteSpace(oldActive))
            _tried.Add(oldActive);

        // 5. Mutate settings.
        var newName = candidate.Name;
        if (string.IsNullOrWhiteSpace(newName))
        {
            // Some legacy entries don't have a Name. Fall back to "server:port"
            // so ActiveServer matching still works downstream.
            newName = $"{candidate.Server}:{candidate.Port}";
        }
        // 5. Mutate ONLY in memory. The restart delegate consumes the mutated
        // _settings to bring the replacement up, so the swap must be visible to it —
        // but disk persistence is delayed to step 7 (see P1.5 below).
        _settings.Vless.ActiveServer = newName;
        _settings.App.ActiveSubscriptionServer = newName;

        // 6. Bring the replacement up BEFORE persisting (P1.5 user-intent guard,
        // audit handoff). A user Disconnect during the failover window cancels
        // _sessionCts, so ExecuteProbeFailoverRestartAsync returns false — we must
        // NOT persist (or announce) a swap the user never saw. A missing delegate
        // (tests / pre-start placeholder recovery) is treated as committed so the
        // caller-driven restart path keeps its existing behaviour.
        bool committed = true;
        if (_restart != null)
        {
            try { committed = await _restart(ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger?.Warning(ex, "[AutoFailover] Restart delegate threw"); committed = false; }
            _logger?.Information("[AutoFailover] Restart delegate returned {Ok}", committed);
        }

        if (!committed)
        {
            // Replacement never came up (user Disconnect cancelled the session, or the
            // restart failed). Roll the in-memory swap back to what the user last chose,
            // persist NOTHING, and stay quiet — no failover message after a Disconnect.
            if (!ct.IsCancellationRequested && !string.IsNullOrWhiteSpace(newName))
                _tried.Add(newName);
            _settings.Vless.ActiveServer = oldActive;
            _settings.App.ActiveSubscriptionServer = oldActiveSub;
            _logger?.Information(
                "[AutoFailover] Replacement start not confirmed (cancelled/failed) — reverted ActiveServer to '{Old}', selection NOT persisted",
                oldActive);
            return new FailoverOutcome(Switched: false, NewActiveServer: null, UserFacingMessage: null);
        }

        // 7. Committed — NOW persist the active-server selection. v2.44.3 (P1
        // subscription-leak): persist via a RELOAD-FRESH of the on-disk settings +
        // only the two selector fields — NOT _store.Save(_settings). In subscribe
        // mode the resolver populated in-memory _settings.Vless.Servers with a
        // transient aggregate; saving _settings directly serializes it into
        // vless.servers YAML (the v2.28.2 / v2.30.0-r8 silent-leak class). Reloading
        // fresh keeps on-disk vless.servers as the user set it while the in-memory
        // _settings keeps the aggregate for THIS session. Best-effort on throw.
        try
        {
            var onDisk = _store.Load();
            onDisk.Vless.ActiveServer = newName;
            onDisk.App.ActiveSubscriptionServer = newName;
            _store.Save(onDisk);
            _logger?.Information(
                "[AutoFailover] Switched ActiveServer '{Old}' → '{New}' and persisted",
                oldActive, newName);
        }
        catch (Exception ex)
        {
            _logger?.Warning(ex,
                "[AutoFailover] Failed to persist ActiveServer migration — proceeding in-memory only");
        }

        return new FailoverOutcome(
            Switched: true,
            NewActiveServer: newName,
            UserFacingMessage: $"Переключение на сервер: {newName}");
    }

    // ─── Active-server intent classification ──────────────────────────────

    /// <summary>
    /// r10 r8 (Bug-r10-F) — true when the current
    /// <see cref="VlessConfig.ActiveServer"/> points at an entry that:
    /// (a) exists in <c>vless.servers</c> (so user explicitly added/picked it),
    /// AND
    /// (b) is NOT a known placeholder per <see cref="VlessServersResolver.IsPlaceholderEntry"/>.
    ///
    /// <para>Used to gate auto-failover in generated mode — for legitimate
    /// manual choices we surface an error instead of swapping. Subscribe
    /// mode doesn't call this; its auto-swap path is appropriate because
    /// "best available subscription server" is the subscribe-mode contract.</para>
    /// </summary>
    private bool IsActiveLegitimateManual()
    {
        var active = _settings.Vless?.ActiveServer;
        if (string.IsNullOrWhiteSpace(active)) return false;

        var entry = (_settings.Vless?.Servers ?? new())
            .FirstOrDefault(s => !string.IsNullOrEmpty(s?.Name)
                && s.Name.Equals(active, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return false;

        return !VlessServersResolver.IsPlaceholderEntry(entry);
    }

    // ─── Candidate selection ──────────────────────────────────────────────

    /// <summary>
    /// Pick the next server to try. Excludes:
    /// <list type="bullet">
    ///   <item>The currently-active server (we wouldn't be here if it worked)</item>
    ///   <item>Anything already in <see cref="_tried"/></item>
    ///   <item>Entries matching known placeholder fingerprints — we know they're dead</item>
    /// </list>
    /// </summary>
    private VlessServerEntry? PickNextCandidate(out string poolSource)
    {
        var oldActive = _settings.Vless.ActiveServer ?? "";

        // Subscribe-mode: union enabled subscription servers.
        var subs = _settings.App.Subscriptions ?? new List<SubscriptionEntry>();
        var subscriptionPool = subs
            .Where(s => s != null && s.Enabled && s.Servers != null)
            .SelectMany(s => s.Servers ?? new List<VlessServerEntry>())
            .Where(IsCandidateUsable)
            .Where(s => !IsAlreadyTried(s, oldActive))
            .ToList();

        if (subscriptionPool.Count > 0)
        {
            poolSource = "subscriptions";
            return subscriptionPool[0];
        }

        // Direct VLESS list (manual or legacy single-server).
        var manualPool = (_settings.Vless?.Servers ?? new List<VlessServerEntry>())
            .Where(IsCandidateUsable)
            .Where(s => !IsAlreadyTried(s, oldActive))
            .ToList();

        if (manualPool.Count > 0)
        {
            poolSource = "vless.servers";
            return manualPool[0];
        }

        poolSource = subs.Count > 0 ? "subscriptions" : "vless.servers";
        return null;
    }

    /// <summary>
    /// True if the entry is structurally usable: has a non-empty server,
    /// a valid port, isn't the documentation placeholder "your.server.com",
    /// and doesn't match a known-bad fingerprint.
    /// </summary>
    private static bool IsCandidateUsable(VlessServerEntry? entry)
    {
        if (entry == null) return false;
        if (string.IsNullOrWhiteSpace(entry.Server)) return false;
        if (entry.Server == "your.server.com") return false;
        if (entry.Port <= 0 || entry.Port > 65535) return false;

        // Drop placeholder fingerprints so we don't cycle into another
        // confirmed-dead entry. v3.0 Phase 3D (2026-05-18): single-call
        // forward to PlaceholderDefense so adding a new fingerprint to the
        // consolidated list automatically gates failover too — no parallel
        // hash-set reach-in required.
        if (PlaceholderDefense.Inspect(entry) is not null)
            return false;

        return true;
    }

    /// <summary>
    /// True if the entry should be skipped because we've already tried it
    /// this session, OR because it's the currently-active one (the one
    /// that just failed).
    /// </summary>
    private bool IsAlreadyTried(VlessServerEntry entry, string oldActive)
    {
        var nameKey = string.IsNullOrWhiteSpace(entry.Name)
            ? $"{entry.Server}:{entry.Port}"
            : entry.Name;

        if (string.Equals(nameKey, oldActive, StringComparison.OrdinalIgnoreCase))
            return true;
        if (_tried.Contains(nameKey))
            return true;
        return false;
    }

    /// <summary>Reset cycle state — call this after a successful connect
    /// so subsequent failures can use the full pool again.</summary>
    public void ResetCycle()
    {
        _tried.Clear();
    }
}

/// <summary>
/// Outcome of <see cref="AutoFailoverEngine.HandleDeadConfigAsync"/>.
/// <para><c>Switched=true</c> means a candidate was picked, persisted, and
/// (if a restart delegate was wired) the restart was attempted.
/// <c>Switched=false</c> means no swap happened — surface
/// <see cref="UserFacingMessage"/> to the user.</para>
/// </summary>
public sealed record FailoverOutcome(
    bool Switched,
    string? NewActiveServer,
    string? UserFacingMessage);
