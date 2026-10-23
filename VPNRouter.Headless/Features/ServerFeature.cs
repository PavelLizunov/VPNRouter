using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Headless.Storage;

namespace VPNRouter.Headless.Features;

public sealed class ServerFeature
{
    private readonly ConfigStorage _storage;
    private readonly ILogger _logger;
    private SingBoxRuntimePolicy? _policy;

    private SingBoxRuntimePolicy? EffectivePolicy => SingBoxRuntimePolicy.Capture(ref _policy);

    public ServerFeature(ConfigStorage storage, ILogger? logger = null)
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
        var configMode = settings.App?.ConfigMode?.Trim().ToLowerInvariant() ?? "generated";
        var activeServer = configMode == "subscribe"
            ? (settings.App?.ActiveSubscriptionServer ?? string.Empty)
            : (settings.Vless?.ActiveServer ?? string.Empty);

        if (string.IsNullOrEmpty(activeServer))
        {
            var detached = ConfigStorage.CloneSettings(settings);
            var resolved = VlessServersResolver.Resolve(detached, _logger);
            activeServer = configMode == "subscribe"
                ? (detached.App?.ActiveSubscriptionServer ?? detached.Vless?.ActiveServer ?? string.Empty)
                : (detached.Vless?.ActiveServer ?? string.Empty);
        }

        var mapped = BuildUnifiedServerIdMap(settings);
        var total = mapped.Count;

        var items = mapped
            .Skip(offset)
            .Take(limit)
            .Select(tuple =>
            {
                var s = tuple.Server;
                var isSelected = !string.IsNullOrEmpty(activeServer) &&
                                 string.Equals(activeServer, s.Name, StringComparison.OrdinalIgnoreCase);
                var displayName = string.IsNullOrWhiteSpace(s.Name) ? $"{s.Server}:{s.Port}" : s.Name;

                return new
                {
                    id = tuple.Id,
                    name = displayName,
                    protocol = string.IsNullOrWhiteSpace(s.Protocol) ? "vless" : s.Protocol.ToLowerInvariant(),
                    selected = isSelected,
                    latencyMs = (int?)null,
                    source = tuple.IsSubscription ? "subscription" : "manual",
                    subscriptionId = tuple.SubscriptionId,
                    removable = tuple.Removable
                };
            })
            .ToList();

        return new { items, total };
    }

    public void Import(JsonElement parameters)
    {
        using var _ = SingBoxRuntimePolicy.EnterScope(EffectivePolicy);
        RouterBackend.EnsureAllowedProperties(parameters, "revision", "text");

        if (!parameters.TryGetProperty("revision", out var revProp) || revProp.ValueKind != JsonValueKind.String)
            throw new RouterException("invalid_argument", "Missing revision parameter");

        if (!parameters.TryGetProperty("text", out var textProp) || textProp.ValueKind != JsonValueKind.String)
            throw new RouterException("invalid_argument", "Missing text parameter");

        var revision = revProp.GetString()!;
        var text = textProp.GetString()!;

        if (text.Length > 256 * 1024)
            throw new RouterException("invalid_argument", "Import text exceeds maximum allowed size (256 KiB)");

        _storage.ValidateRevision(revision);

        List<VlessServerEntry> parsedServers;
        if (ServerUriParser.IsWireGuardConf(text))
        {
            try
            {
                var wgServer = ServerUriParser.ParseWireGuardConf(text);
                parsedServers = new List<VlessServerEntry> { wgServer };
            }
            catch (FormatException)
            {
                throw new RouterException("invalid_argument", "Invalid WireGuard configuration");
            }
        }
        else
        {
            parsedServers = ServerUriParser.ParseMultiple(text);
        }

        if (parsedServers == null || parsedServers.Count == 0)
            throw new RouterException("invalid_argument", "No valid server configurations found in input");

        var settings = _storage.GetSettings();
        settings.Vless ??= new VlessConfig();
        settings.Vless.Servers ??= new List<VlessServerEntry>();

        foreach (var s in parsedServers)
        {
            if (string.IsNullOrWhiteSpace(s.Name))
            {
                s.Name = $"{s.Server}:{s.Port}";
            }
            settings.Vless.Servers.Add(s);
        }

        if (string.IsNullOrWhiteSpace(settings.Vless.ActiveServer) && settings.Vless.Servers.Count > 0)
        {
            settings.Vless.ActiveServer = settings.Vless.Servers[0].Name;
        }

        _storage.SaveSettings(settings, revision);
        _logger.Information("[ServerFeature] Imported {Count} servers", parsedServers.Count);
    }

    public void Select(JsonElement parameters)
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
        var mapped = BuildUnifiedServerIdMap(settings);
        var match = mapped.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));

        if (match.Server == null)
            throw new RouterException("not_found", "Server not found");

        var server = match.Server;
        var serverName = string.IsNullOrWhiteSpace(server.Name) ? $"{server.Server}:{server.Port}" : server.Name;

        settings.Vless ??= new VlessConfig();

        if (match.IsSubscription)
        {
            settings.App.ConfigMode = "subscribe";
            settings.App.ActiveSubscriptionServer = serverName;
            settings.Vless.ActiveServer = serverName;
        }
        else
        {
            settings.App.ConfigMode = "generated";
            settings.Vless.ActiveServer = serverName;
        }

        _storage.SaveSettings(settings, revision);
        _logger.Information("[ServerFeature] Selected server {Name} (mode: {Mode})", serverName, settings.App.ConfigMode);
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
        var match = BuildUnifiedServerIdMap(settings).FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
        if (match.Server == null)
            throw new RouterException("not_found", "Server not found");
        if (!match.Removable)
            throw new RouterException("not_removable");
        var server = match.Server;

        var wasActive = string.Equals(settings.Vless?.ActiveServer, server.Name, StringComparison.OrdinalIgnoreCase);
        settings.Vless!.Servers.Remove(server);

        if (wasActive)
        {
            settings.Vless.ActiveServer = settings.Vless.Servers.Count > 0 ? settings.Vless.Servers[0].Name : string.Empty;
        }

        _storage.SaveSettings(settings, revision);
        _logger.Information("[ServerFeature] Removed server {Name}", server.Name);
    }

    public async Task<object> TestAsync(JsonElement parameters, CancellationToken ct)
    {
        using var _ = SingBoxRuntimePolicy.EnterScope(EffectivePolicy);
        RouterBackend.EnsureAllowedProperties(parameters, "id");

        if (!parameters.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.String)
            throw new RouterException("invalid_argument", "Missing id parameter");

        var id = idProp.GetString()!;
        var settings = _storage.GetSettings();
        var mapped = BuildUnifiedServerIdMap(settings);
        var match = mapped.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));

        if (match.Server == null)
            throw new RouterException("not_found", "Server not found");

        var server = match.Server;
        var probe = await TcpTlsProbe.ProbeServerAsync(server, ct);
        var reachable = probe.Status is ServerProbeStatus.Ok
                                      or ServerProbeStatus.Slow
                                      or ServerProbeStatus.Implausible;

        return new
        {
            id,
            reachable,
            // A silent UDP port (Hysteria2, TUIC) reports the probe timeout, not a latency.
            latencyMs = reachable && probe.IsVerified ? (int?)probe.LatencyMs : null
        };
    }

    public async Task<object> VerifyAsync(JsonElement parameters, Action<object>? progress, CancellationToken ct)
    {
        using var _ = SingBoxRuntimePolicy.EnterScope(EffectivePolicy);
        RouterBackend.EnsureAllowedProperties(parameters, "id");

        if (!parameters.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.String)
            throw new RouterException("invalid_argument", "Missing id parameter");

        var id = idProp.GetString()!;
        var settings = _storage.GetSettings();
        var mapped = BuildUnifiedServerIdMap(settings);
        var match = mapped.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));

        if (match.Server == null)
            throw new RouterException("not_found", "Server not found");

        var server = match.Server;
        progress?.Invoke(new { id, stage = "verify_start", completed = 0, total = 1 });

        var verifier = new VlessDeepVerifier(_logger);
        var result = await verifier.VerifyAsync(server, measureBandwidth: false, ct);

        progress?.Invoke(new { id, stage = "verify_complete", completed = 1, total = 1 });

        string? safeError = null;
        if (!result.Ok)
        {
            if (EffectivePolicy != null && (!EffectivePolicy.IsAvailable || result.FailurePhase == DeepVerifyFailurePhase.LocalSpawn))
            {
                safeError = "unavailable";
            }
            else
            {
                safeError = result.FailurePhase != DeepVerifyFailurePhase.None
                    ? result.FailurePhase.ToString()
                    : "Verification failed";
            }
        }

        return new
        {
            id,
            ok = result.Ok,
            latencyMs = result.Ok ? (int?)result.HttpLatencyMs : null,
            error = safeError
        };
    }

    private static List<(VlessServerEntry Server, bool IsSubscription, string? SubscriptionId, bool Removable)> GetUnifiedServersWithSource(AppSettings settings)
    {
        var result = new List<(VlessServerEntry Server, bool IsSubscription, string? SubscriptionId, bool Removable)>();
        if (settings.Vless?.Servers is { Count: > 0 })
        {
            foreach (var s in settings.Vless.Servers)
            {
                if (s != null && !string.IsNullOrWhiteSpace(s.Server) && s.Server != "your.server.com")
                    result.Add((s, false, null, true));
            }
        }
        else if (settings.Vless != null)
        {
            foreach (var s in settings.Vless.GetEffectiveServers())
            {
                if (s != null && !string.IsNullOrWhiteSpace(s.Server) && s.Server != "your.server.com")
                    result.Add((s, false, null, false));
            }
        }

        if (settings.App?.Subscriptions != null)
        {
            foreach (var sub in settings.App.Subscriptions.Where(s => s != null && s.Enabled && s.Servers != null))
            {
                foreach (var server in sub.Servers.Where(s => s != null && !string.IsNullOrWhiteSpace(s.Server) && s.Server != "your.server.com"))
                    result.Add((server, true, sub.Id, false));
            }
        }

        return result;
    }

    private static List<(string Id, VlessServerEntry Server, bool IsSubscription, string? SubscriptionId, bool Removable)> BuildUnifiedServerIdMap(AppSettings settings)
    {
        var unified = GetUnifiedServersWithSource(settings);
        var result = new List<(string Id, VlessServerEntry Server, bool IsSubscription, string? SubscriptionId, bool Removable)>(unified.Count);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < unified.Count; i++)
        {
            var (s, isSub, subscriptionId, removable) = unified[i];
            if (s == null) continue;
            var key = $"{s.Protocol ?? "vless"}|{s.Name}|{s.Server}|{s.Port}|{s.Uuid}";
            counts.TryGetValue(key, out var count);
            counts[key] = count + 1;

            var raw = $"{key}|{count}";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            var id = "srv_" + Convert.ToHexStringLower(hash)[..16];
            result.Add((id, s, isSub, subscriptionId, removable));
        }

        return result;
    }

}
