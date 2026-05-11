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
    public AutoFailoverEngine(
        AppSettings settings,
        ConfigSanityCheck sanity,
        Func<CancellationToken, Task<bool>>? restart = null,
        ILogger? logger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _sanity = sanity ?? throw new ArgumentNullException(nameof(sanity));
        _restart = restart;
        _logger = logger;
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
            return new FailoverOutcome(
                Switched: false,
                NewActiveServer: null,
                UserFacingMessage:
                    poolSource == "subscriptions"
                        ? "В подписке нет других серверов. Попробуйте 'Обновить' на вкладке Подписки."
                        : "Нет других доступных серверов в списке VLESS.");
        }

        // 4. Record the OLD active server in _tried so we never loop back
        // to it within this session. We add the OLD one, not the NEW —
        // because the NEW one is what we're about to test.
        var oldActive = _settings.Vless.ActiveServer ?? "";
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
        _settings.Vless.ActiveServer = newName;
        _settings.App.ActiveSubscriptionServer = newName;

        // 6. Persist. SettingsLoader.Save is best-effort — if it throws we
        // still proceed with the in-memory swap so the user gets the
        // failover for THIS session. Next launch will re-pick the dead
        // server unless persistence succeeded, but the user will at least
        // see the symptom (not a silent leak).
        try
        {
            SettingsLoader.Save(_settings);
            _logger?.Information(
                "[AutoFailover] Switched ActiveServer '{Old}' → '{New}' and persisted",
                oldActive, newName);
        }
        catch (Exception ex)
        {
            _logger?.Warning(ex,
                "[AutoFailover] Failed to persist ActiveServer migration — proceeding in-memory only");
        }

        // 7. Optional restart via the caller-supplied delegate. If no
        // delegate is wired (e.g. tests), we just return Switched=true and
        // let the caller drive the restart.
        if (_restart != null)
        {
            try
            {
                var ok = await _restart(ct);
                _logger?.Information(
                    "[AutoFailover] Restart delegate returned {Ok}", ok);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Warning(ex, "[AutoFailover] Restart delegate threw");
            }
        }

        return new FailoverOutcome(
            Switched: true,
            NewActiveServer: newName,
            UserFacingMessage: $"Переключение на сервер: {newName}");
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
        // confirmed-dead entry. Reality config lives under entry.Reality.
        if (ConfigSanityCheck.KnownPlaceholderServers.Contains(entry.Server))
            return false;
        var pubkey = entry.Reality?.PublicKey;
        if (!string.IsNullOrEmpty(pubkey)
            && ConfigSanityCheck.KnownPlaceholderPubkeys.Contains(pubkey))
            return false;
        var shortId = entry.Reality?.ShortId;
        if (!string.IsNullOrEmpty(shortId)
            && ConfigSanityCheck.KnownPlaceholderShortIds.Contains(shortId))
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
