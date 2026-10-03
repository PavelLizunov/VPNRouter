using Serilog;
using VPNRouter.Core.Localization;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public sealed class AutoFailoverEngine
{
    public const int MaxAttempts = 3;

    private readonly AppSettings _settings;
    private readonly ConfigSanityCheck _sanity;
    private readonly ILogger? _logger;
    private readonly Func<CancellationToken, Task<bool>>? _restart;
    private readonly ISettingsStore _store;

    public IReadOnlySet<string> TriedServers => _tried;
    private readonly HashSet<string> _tried = new(StringComparer.OrdinalIgnoreCase);

    internal Func<bool>? IsCurrentIntent { get; init; }

    private bool IsIntentCurrent() => IsCurrentIntent?.Invoke() ?? true;

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

    public async Task<FailoverOutcome> HandleDeadConfigAsync(
        string reason,
        CancellationToken ct = default)
    {
        _logger?.Warning("[AutoFailover] Dead config: {Reason}", reason);

        var rejected = TryRejectBeforeSwitch(reason, out var pending);
        if (rejected != null)
            return rejected;
        var candidate = pending!;

        var oldActive = _settings.Vless.ActiveServer ?? "";
        var oldActiveSub = _settings.App.ActiveSubscriptionServer;

        var newName = candidate.Name;
        if (string.IsNullOrWhiteSpace(newName))
        {
            newName = $"{candidate.Server}:{candidate.Port}";
        }

        if (!IsIntentCurrent())
        {
            _logger?.Information(
                "[AutoFailover] Stale failover intent before selector mutation — aborting without changes");
            return new FailoverOutcome(Switched: false, NewActiveServer: null, UserFacingMessage: null);
        }

        if (!string.IsNullOrWhiteSpace(oldActive))
            _tried.Add(oldActive);

        _settings.Vless.ActiveServer = newName;
        _settings.App.ActiveSubscriptionServer = newName;

        var committed = await RestartWithRollbackAsync(newName, oldActive, oldActiveSub, ct);

        if (!IsIntentCurrent())
        {
            _logger?.Information(
                "[AutoFailover] Stale failover intent after restart — aborting without rollback or persist");
            return new FailoverOutcome(Switched: false, NewActiveServer: null, UserFacingMessage: null);
        }

        if (!committed)
        {
            if (!ct.IsCancellationRequested && !string.IsNullOrWhiteSpace(newName))
                _tried.Add(newName);
            _settings.Vless.ActiveServer = oldActive;
            _settings.App.ActiveSubscriptionServer = oldActiveSub;
            _logger?.Information(
                "[AutoFailover] Replacement start not confirmed (cancelled/failed) — reverted ActiveServer to '{Old}', selection NOT persisted",
                oldActive);
            return new FailoverOutcome(Switched: false, NewActiveServer: null, UserFacingMessage: null);
        }

        PersistSelection(newName, oldActive);

        return new FailoverOutcome(
            Switched: true,
            NewActiveServer: newName,
            UserFacingMessage: Strings.FailoverSwitching(newName));
    }

    private FailoverOutcome? TryRejectBeforeSwitch(string reason, out VlessServerEntry? candidate)
    {
        candidate = null;

        if (!IsIntentCurrent())
        {
            _logger?.Information("[AutoFailover] Stale failover intent at entry — aborting without changes");
            return new FailoverOutcome(Switched: false, NewActiveServer: null, UserFacingMessage: null);
        }

        var configMode = (_settings.App.ConfigMode ?? "generated").Trim().ToLowerInvariant();
        if (configMode == "custom")
        {
            return new FailoverOutcome(
                Switched: false,
                NewActiveServer: null,
                UserFacingMessage: Strings.FailoverCustomConfigUnusable);
        }

        var hasEnabledSub = _settings.App.Subscriptions?
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
                UserFacingMessage: Strings.FailoverManualServerSilent(
                    _settings.Vless.ActiveServer ?? "", reason));
        }

        if (_tried.Count >= MaxAttempts)
        {
            _logger?.Warning(
                "[AutoFailover] Exceeded MaxAttempts ({Max}) — surfacing alert",
                MaxAttempts);
            return new FailoverOutcome(
                Switched: false,
                NewActiveServer: null,
                UserFacingMessage: Strings.FailoverAllServersDown(_tried.Count));
        }

        candidate = PickNextCandidate(out var poolSource);
        if (candidate == null)
        {
            _logger?.Warning(
                "[AutoFailover] No candidate servers left (pool source: {Source})",
                poolSource);
            return new FailoverOutcome(
                Switched: false,
                NewActiveServer: null,
                UserFacingMessage: poolSource == "subscriptions"
                    ? Strings.FailoverNoOtherInSubscription
                    : Strings.FailoverNoOtherInList);
        }

        return null;
    }

    private async Task<bool> RestartWithRollbackAsync(
        string newName, string oldActive, string oldActiveSub, CancellationToken ct)
    {
        bool committed = true;
        if (_restart != null)
        {
            try { committed = await _restart(ct); }
            catch (OperationCanceledException)
            {
                if (IsIntentCurrent())
                {
                    _settings.Vless.ActiveServer = oldActive;
                    _settings.App.ActiveSubscriptionServer = oldActiveSub;
                }
                throw;
            }
            catch (Exception ex) { _logger?.Warning(ex, "[AutoFailover] Restart delegate threw"); committed = false; }
            _logger?.Information("[AutoFailover] Restart delegate returned {Ok}", committed);
        }

        return committed;
    }

    private void PersistSelection(string newName, string oldActive)
    {
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
    }

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

    private VlessServerEntry? PickNextCandidate(out string poolSource)
    {
        var oldActive = _settings.Vless.ActiveServer ?? "";

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

    private static bool IsCandidateUsable(VlessServerEntry? entry)
    {
        if (entry == null) return false;
        if (string.IsNullOrWhiteSpace(entry.Server)) return false;
        if (entry.Server == "your.server.com") return false;
        if (entry.Port <= 0 || entry.Port > 65535) return false;

        if (PlaceholderDefense.Inspect(entry) is not null)
            return false;

        return true;
    }

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

    public void ResetCycle()
    {
        _tried.Clear();
    }
}

public sealed record FailoverOutcome(
    bool Switched,
    string? NewActiveServer,
    string? UserFacingMessage);
