using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
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
        var subs = settings.App?.Subscriptions ?? new List<SubscriptionEntry>();

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
            targets = subs.Where(s => s.Enabled).ToList();
        }

        int done = 0;
        int total = targets.Count;
        foreach (var sub in targets)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(new { stage = "refresh_subscription", completed = done, total });
            await SubscriptionFetcher.RefreshEntryAsync(sub, _logger, ct);
            done++;
            progress?.Invoke(new { stage = "refresh_subscription", completed = done, total });
        }

        ReconcileActiveSubscriptionServer(settings);

        _storage.SaveSettings(settings, revision);
        _logger.Information("[SubscriptionFeature] Refreshed {Count} subscriptions", targets.Count);
    }

    private static void ReconcileActiveSubscriptionServer(AppSettings settings)
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
