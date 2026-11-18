using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VPNRouter.Core.Json;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Headless.Storage;

namespace VPNRouter.Headless.Features;

public sealed class SubscriptionFeature
{
    private readonly ConfigStorage _storage;
    private readonly ILogger _logger;
    private SingBoxRuntimePolicy? _policy;

    private SingBoxRuntimePolicy? EffectivePolicy => SingBoxRuntimePolicy.Capture(ref _policy);

    public SubscriptionFeature(ConfigStorage storage, ILogger? logger = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _logger = logger ?? Log.Logger;
        _policy = SingBoxRuntimePolicy.Current ?? (OperatingSystem.IsLinux() ? SingBoxRuntimePolicy.DefaultProduction : null);
    }

    public object List(JsonElement parameters)
    {
        using var _ = SingBoxRuntimePolicy.EnterScope(EffectivePolicy);
        RouterBackend.EnsureAllowedProperties(parameters, "offset", "limit");

        int offset = 0;
        int limit = 100;

        if (parameters.ValueKind == JsonValueKind.Object)
        {
            if (parameters.TryGetProperty("offset", out var offsetProp))
            {
                if (!offsetProp.TryGetInt32(out var o) || o < 0)
                    throw new RouterException("invalid_argument", "offset must be a non-negative integer");
                offset = o;
            }

            if (parameters.TryGetProperty("limit", out var limitProp))
            {
                if (!limitProp.TryGetInt32(out var l) || l < 1 || l > 100)
                    throw new RouterException("invalid_argument", "limit must be an integer between 1 and 100");
                limit = l;
            }
        }

        var settings = _storage.GetSettings();
        var subs = settings.App?.Subscriptions ?? new List<SubscriptionEntry>();
        var total = subs.Count;

        // Invariant: URLs are strictly write-only and excluded from list snapshots
        var items = subs
            .Skip(offset)
            .Take(limit)
            .Select(s => new
            {
                id = s.Id,
                name = s.Name,
                enabled = s.Enabled,
                serverCount = s.Servers?.Count ?? s.LastServerCount
            })
            .ToList();

        return new { items, total };
    }

    public void Add(JsonElement parameters)
    {
        using var _ = SingBoxRuntimePolicy.EnterScope(EffectivePolicy);
        RouterBackend.EnsureAllowedProperties(parameters, "revision", "name", "url");

        if (!parameters.TryGetProperty("revision", out var revProp) || revProp.ValueKind != JsonValueKind.String)
            throw new RouterException("invalid_argument", "Missing revision parameter");

        if (!parameters.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
            throw new RouterException("invalid_argument", "Missing name parameter");

        if (!parameters.TryGetProperty("url", out var urlProp) || urlProp.ValueKind != JsonValueKind.String)
            throw new RouterException("invalid_argument", "Missing url parameter");

        var revision = revProp.GetString()!;
        var name = nameProp.GetString()!.Trim();
        var url = urlProp.GetString()!.Trim();

        if (string.IsNullOrWhiteSpace(name))
            throw new RouterException("invalid_argument", "Subscription name must not be empty");

        if (name.Length > 256)
            throw new RouterException("invalid_argument", "Subscription name exceeds 256 characters");

        if (string.IsNullOrWhiteSpace(url))
            throw new RouterException("invalid_argument", "Subscription URL must not be empty");

        if (url.Length > 2048)
            throw new RouterException("invalid_argument", "Subscription URL exceeds 2048 characters");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new RouterException("invalid_argument", "Subscription URL must be a valid http or https URL");
        }

        _storage.ValidateRevision(revision);

        var settings = _storage.GetSettings();
        settings.App.Subscriptions ??= new List<SubscriptionEntry>();

        var entry = new SubscriptionEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Url = url,
            Enabled = true,
            Servers = new List<VlessServerEntry>()
        };

        settings.App.Subscriptions.Add(entry);
        _storage.SaveSettings(settings, revision);
        _logger.Information("[SubscriptionFeature] Added subscription {Name}", name);
    }

    public void Remove(JsonElement parameters)
    {
        using var _ = SingBoxRuntimePolicy.EnterScope(EffectivePolicy);
        RouterBackend.EnsureAllowedProperties(parameters, "revision", "id");

        if (!parameters.TryGetProperty("revision", out var revProp) || revProp.ValueKind != JsonValueKind.String)
            throw new RouterException("invalid_argument", "Missing revision parameter");

        if (!parameters.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.String)
            throw new RouterException("invalid_argument", "Missing id parameter");

        var revision = revProp.GetString()!;
        var id = idProp.GetString()!;

        _storage.ValidateRevision(revision);

        var settings = _storage.GetSettings();
        var subs = settings.App?.Subscriptions ?? new List<SubscriptionEntry>();
        var sub = subs.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
        if (sub == null)
            throw new RouterException("not_found", "Subscription not found");

        subs.Remove(sub);
        ReconcileActiveSubscriptionServer(settings);

        _storage.SaveSettings(settings, revision);
        _logger.Information("[SubscriptionFeature] Removed subscription {Id}", id);
    }

    public void Enable(JsonElement parameters)
    {
        using var _ = SingBoxRuntimePolicy.EnterScope(EffectivePolicy);
        RouterBackend.EnsureAllowedProperties(parameters, "revision", "id", "enabled");

        if (!parameters.TryGetProperty("revision", out var revProp) || revProp.ValueKind != JsonValueKind.String)
            throw new RouterException("invalid_argument", "Missing revision parameter");

        if (!parameters.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.String)
            throw new RouterException("invalid_argument", "Missing id parameter");

        if (!parameters.TryGetProperty("enabled", out var enabledProp) ||
            (enabledProp.ValueKind != JsonValueKind.True && enabledProp.ValueKind != JsonValueKind.False))
        {
            throw new RouterException("invalid_argument", "Missing or invalid enabled parameter");
        }

        var revision = revProp.GetString()!;
        var id = idProp.GetString()!;
        var enabled = enabledProp.GetBoolean();

        _storage.ValidateRevision(revision);

        var settings = _storage.GetSettings();
        var subs = settings.App?.Subscriptions ?? new List<SubscriptionEntry>();
        var sub = subs.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
        if (sub == null)
            throw new RouterException("not_found", "Subscription not found");

        sub.Enabled = enabled;
        ReconcileActiveSubscriptionServer(settings);

        _storage.SaveSettings(settings, revision);
        _logger.Information("[SubscriptionFeature] Set subscription {Id} enabled={Enabled}", id, enabled);
    }

    public async Task RefreshAsync(JsonElement parameters, Action<object>? progress, CancellationToken ct)
    {
        using var _ = SingBoxRuntimePolicy.EnterScope(EffectivePolicy);
        RouterBackend.EnsureAllowedProperties(parameters, "revision", "id");

        if (!parameters.TryGetProperty("revision", out var revProp) || revProp.ValueKind != JsonValueKind.String)
            throw new RouterException("invalid_argument", "Missing revision parameter");

        var revision = revProp.GetString()!;
        _storage.ValidateRevision(revision);

        string? targetId = null;
        if (parameters.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
        {
            targetId = idProp.GetString();
        }

        var settings = _storage.GetSettings();
        var app = settings.App;
        var subs = app.Subscriptions;
        List<SubscriptionEntry> targets;
        if (!string.IsNullOrWhiteSpace(targetId))
        {
            var match = subs.FirstOrDefault(s => string.Equals(s.Id, targetId, StringComparison.Ordinal));
            if (match == null)
                throw new RouterException("not_found", "Subscription not found");
            targets = new List<SubscriptionEntry> { match };
        }
        else
        {
            if (subs.Count == 0 && !string.IsNullOrWhiteSpace(app.SubscriptionUrl))
            {
                // Stage the legacy feed in the candidate; a transient failure keeps the original file.
                subs.Add(new SubscriptionEntry
                {
                    Name = "Default", Url = app.SubscriptionUrl, Servers = app.SubscriptionServers
                });
                app.SubscriptionServers = new();
            }
            targets = subs.Where(s => s.Enabled).ToList();
        }

        var previousRows = targets.SelectMany(s => s.Servers).ToList();
        int done = 0;
        int total = targets.Count;
        foreach (var sub in targets)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(new { stage = "refresh_subscription", completed = done, total });
            int count;
            try
            {
                count = await SubscriptionFetcher.RefreshEntryAsync(sub, _logger, ct);
            }
            catch (UnsupportedSubscriptionChainException)
            {
                PersistRejectedSubscription(sub, revision);
                ct.ThrowIfCancellationRequested();
                throw new RouterException("subscription_chain_unsupported");
            }
            ct.ThrowIfCancellationRequested();
            if (count == 0)
                throw new RouterException("subscription_refresh_failed");
            done++;
            progress?.Invoke(new { stage = "refresh_subscription", completed = done, total });
        }

        RemoveRetiredCopies(settings, previousRows, targets.Any(s => s.Url == app.SubscriptionUrl));
        ReconcileActiveSubscriptionServer(settings);

        _storage.SaveSettings(settings, revision);
        _logger.Information("[SubscriptionFeature] Refreshed {Count} subscriptions", targets.Count);
    }

    private void PersistRejectedSubscription(SubscriptionEntry rejected, string revision)
    {
        // A failed batch must not commit successful candidates from earlier in the loop.
        var settings = _storage.GetSettings();
        var app = settings.App;
        if (app.Subscriptions.Count == 0 && app.SubscriptionUrl == rejected.Url)
            app.Subscriptions.Add(rejected);
        var registrations = app.Subscriptions.Where(s => s.Url == rejected.Url).ToList();
        var fetched = registrations.FirstOrDefault(s => s.Id == rejected.Id);
        if (fetched == null) throw new RouterException("conflict");

        var evicted = registrations.SelectMany(s => s.Servers).ToList();
        foreach (var registration in registrations)
        {
            registration.Servers.Clear();
            registration.LastServerCount = 0;
        }
        fetched.LastRefreshedAt = rejected.LastRefreshedAt;
        fetched.UserInfo = rejected.UserInfo;

        RemoveRetiredCopies(settings, evicted, app.SubscriptionUrl == rejected.Url);
        ReconcileActiveSubscriptionServer(settings, activateSubscription: false);
        _storage.SaveSettings(settings, revision);
    }

    private static void RemoveRetiredCopies(AppSettings settings, List<VlessServerEntry> evicted, bool refreshedLegacyUrl)
    {
        var app = settings.App;
        if (refreshedLegacyUrl && app.SubscriptionServers.Count > 0)
        {
            evicted.AddRange(app.SubscriptionServers);
            var sourceKeys = app.Subscriptions.Where(s => s.Url == app.SubscriptionUrl)
                .SelectMany(s => s.Servers).Select(SavedRowKey).ToHashSet(StringComparer.Ordinal);
            app.SubscriptionServers.RemoveAll(row => !sourceKeys.Contains(SavedRowKey(row)));
        }
        if (evicted.Count == 0) return;

        // Only retire a detached copy once no registration or separately owned legacy feed retains it.
        var retained = app.Subscriptions.SelectMany(s => s.Servers);
        if (!string.IsNullOrEmpty(app.SubscriptionUrl))
            retained = retained.Concat(app.SubscriptionServers);
        var retainedKeys = retained.Select(SavedRowKey).ToHashSet(StringComparer.Ordinal);
        evicted.RemoveAll(row => retainedKeys.Contains(SavedRowKey(row)));
        if (evicted.Count == 0) return;
        var evictedKeys = evicted.Select(SavedRowKey).ToHashSet(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(app.SubscriptionUrl))
            app.SubscriptionServers.RemoveAll(row => evictedKeys.Contains(SavedRowKey(row)));

        var vless = settings.Vless;
        var selected = vless.Servers.FirstOrDefault(s => string.Equals(s.Name, vless.ActiveServer, StringComparison.OrdinalIgnoreCase));
        // Older desktop saves copied only the selected row's basic fields and Reality to the scalar fallback.
        var selectedScalarCopy = selected != null && evictedKeys.Contains(SavedRowKey(selected))
            && MatchesLegacyVless(vless, selected, desktopProjection: true);
        var removedManualCopy = vless.Servers.RemoveAll(row => evictedKeys.Contains(SavedRowKey(row))) > 0;
        var removeScalar = (selectedScalarCopy || evicted.Any(row => MatchesLegacyVless(vless, row)))
            && !vless.Servers.Any(row => MatchesLegacyVless(vless, row))
            && !retained.Any(row => MatchesLegacyVless(vless, row));
        if (removeScalar)
        {
            vless.Server = string.Empty;
            vless.Uuid = string.Empty;
            vless.Port = 443;
            vless.Flow = string.Empty;
            vless.Security = "reality";
            vless.Tls = new();
            vless.Reality = new();
            vless.Transport = new();
        }
        if ((removedManualCopy || removeScalar) && evicted.Any(s => string.Equals(s.Name, vless.ActiveServer, StringComparison.OrdinalIgnoreCase))
            && !vless.Servers.Any(s => string.Equals(s.Name, vless.ActiveServer, StringComparison.OrdinalIgnoreCase)))
            vless.ActiveServer = vless.Servers.FirstOrDefault()?.Name ?? string.Empty;
    }

    // Historical settings can contain a detached copy of a subscription row among
    // manual servers. Compare the full saved profile, including its name and route.
    private static string SavedRowKey(VlessServerEntry row) =>
        JsonSerializer.Serialize(row, AppJsonContext.Default.VlessServerEntry);

    private static bool MatchesLegacyVless(VlessConfig vless, VlessServerEntry row, bool desktopProjection = false)
    {
        if (string.IsNullOrEmpty(vless.Server) || vless.Server != row.Server ||
            vless.Port != row.Port || vless.Uuid != row.Uuid) return false;
        var legacy = new VlessServerEntry
        {
            Name = row.Name, Server = vless.Server, Port = vless.Port, Uuid = vless.Uuid,
            Flow = vless.Flow, Security = vless.Security, Reality = vless.Reality,
            Tls = desktopProjection ? row.Tls : vless.Tls,
            Transport = desktopProjection ? row.Transport : vless.Transport
        };
        return SavedRowKey(legacy) == SavedRowKey(row);
    }

    private static bool HasUsableManualServer(AppSettings settings)
    {
        var active = settings.Vless?.ActiveServer;
        return !string.IsNullOrWhiteSpace(active)
            && (settings.Vless?.Servers ?? new List<VlessServerEntry>()).Any(s =>
                string.Equals(s.Name, active, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(s.Server) && s.Server != "your.server.com");
    }

    private static void ReconcileActiveSubscriptionServer(AppSettings settings, bool activateSubscription = true)
    {
        var subs = settings.App?.Subscriptions ?? new List<SubscriptionEntry>();
        var remainingServers = subs
            .Where(s => s != null && s.Enabled && s.Servers != null)
            .SelectMany(s => s.Servers)
            .Where(s => !string.IsNullOrWhiteSpace(s?.Server) && s.Server != "your.server.com")
            .ToList();

        if (string.Equals(settings.App?.ConfigMode, "subscribe", StringComparison.OrdinalIgnoreCase))
        {
            if (remainingServers.Count == 0)
            {
                settings.App.ConfigMode = "generated";
                settings.App.ActiveSubscriptionServer = string.Empty;
                settings.Vless ??= new VlessConfig();
                settings.Vless.ActiveServer = settings.Vless.Servers?.FirstOrDefault()?.Name ?? string.Empty;
            }
            else
            {
                var match = remainingServers.FirstOrDefault(s =>
                    !string.IsNullOrEmpty(s.Name) &&
                    string.Equals(s.Name, settings.App.ActiveSubscriptionServer, StringComparison.OrdinalIgnoreCase));

                if (match == null)
                {
                    var newActive = remainingServers[0].Name;
                    settings.App.ActiveSubscriptionServer = newActive;
                    settings.Vless ??= new VlessConfig();
                    settings.Vless.ActiveServer = newActive;
                }
                else
                {
                    settings.Vless ??= new VlessConfig();
                    settings.Vless.ActiveServer = match.Name;
                }
            }
        }
        else if (activateSubscription && remainingServers.Count > 0 && !HasUsableManualServer(settings)
                 && !string.Equals(settings.App?.ConfigMode, "custom", StringComparison.OrdinalIgnoreCase))
        {
            // Nothing else to connect with: start using the
            // subscription that just brought servers instead of leaving none selected.
            settings.App!.ConfigMode = "subscribe";
            settings.App.ActiveSubscriptionServer = remainingServers[0].Name;
            settings.Vless ??= new VlessConfig();
            settings.Vless.ActiveServer = remainingServers[0].Name;
        }
        else
        {
            if (!string.IsNullOrEmpty(settings.App?.ActiveSubscriptionServer))
            {
                var match = remainingServers.FirstOrDefault(s =>
                    !string.IsNullOrEmpty(s.Name) &&
                    string.Equals(s.Name, settings.App.ActiveSubscriptionServer, StringComparison.OrdinalIgnoreCase));

                if (match == null)
                {
                    settings.App.ActiveSubscriptionServer = remainingServers.FirstOrDefault()?.Name ?? string.Empty;
                }
            }
        }
    }
}
