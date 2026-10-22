using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using Serilog;
using VPNRouter.Headless.Storage;

namespace VPNRouter.Headless.Features;

public sealed class AppRoutingFeature
{
    private readonly ConfigStorage _storage;
    private readonly ILogger _logger;

    public AppRoutingFeature(ConfigStorage storage, ILogger? logger = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _logger = logger ?? Log.Logger;
    }

    public object List(JsonElement parameters = default)
    {
        RouterBackend.EnsureEmptyParameters(parameters);

        var settings = _storage.GetSettings();
        var include = settings.App?.RoutingAppsInclude ?? new List<string>();
        var exclude = settings.App?.RoutingAppsExclude ?? new List<string>();

        var running = GetRunningProcessNamesSafe();

        return new
        {
            include = include.ToList(),
            exclude = exclude.ToList(),
            running
        };
    }

    public void SetApps(JsonElement parameters)
    {
        RouterBackend.EnsureAllowedProperties(parameters, "revision", "mode", "names");

        if (!parameters.TryGetProperty("revision", out var revProp) || revProp.ValueKind != JsonValueKind.String)
            throw new RouterException("invalid_argument", "Missing revision parameter");

        if (!parameters.TryGetProperty("mode", out var modeProp) || modeProp.ValueKind != JsonValueKind.String)
            throw new RouterException("invalid_argument", "Missing mode parameter");

        if (!parameters.TryGetProperty("names", out var namesProp) || namesProp.ValueKind != JsonValueKind.Array)
            throw new RouterException("invalid_argument", "Missing names parameter (expected array)");

        var revision = revProp.GetString()!;
        var mode = modeProp.GetString()!.Trim().ToLowerInvariant();

        if (mode != "include" && mode != "exclude")
            throw new RouterException("invalid_argument", "Invalid mode parameter: expected 'include' or 'exclude'");

        var cleanNames = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in namesProp.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new RouterException("invalid_argument", "Process names must be strings");

            var name = item.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (name.Length > 256)
                throw new RouterException("invalid_argument", "Process name exceeds 256 characters");

            // Deduplicate case-insensitively, but preserve original casing
            if (seen.Add(name))
            {
                cleanNames.Add(name);
            }
        }

        if (cleanNames.Count > 10000)
            throw new RouterException("invalid_argument", "Too many process names (maximum 10,000)");

        _storage.ValidateRevision(revision);

        var settings = _storage.GetSettings();
        settings.App.RoutingAppsMode = mode;

        if (mode == "include")
        {
            settings.App.RoutingAppsInclude = cleanNames;
            settings.App.RoutingAppsIncludeInitialized = true;
        }
        else
        {
            settings.App.RoutingAppsExclude = cleanNames;
        }

        _storage.SaveSettings(settings, revision);
        _logger.Information("[AppRoutingFeature] Updated {Mode} apps list with {Count} entries", mode, cleanNames.Count);
    }

    public void SetRouting(JsonElement parameters)
    {
        RouterBackend.EnsureAllowedProperties(parameters, "revision", "routingMode", "routingAppsMode");

        if (!parameters.TryGetProperty("revision", out var revProp) || revProp.ValueKind != JsonValueKind.String)
            throw new RouterException("invalid_argument", "Missing revision parameter");

        if (!parameters.TryGetProperty("routingMode", out var rmProp) || rmProp.ValueKind != JsonValueKind.String)
            throw new RouterException("invalid_argument", "Missing routingMode parameter");

        if (!parameters.TryGetProperty("routingAppsMode", out var ramProp) || ramProp.ValueKind != JsonValueKind.String)
            throw new RouterException("invalid_argument", "Missing routingAppsMode parameter");

        var revision = revProp.GetString()!;
        var routingMode = rmProp.GetString()!.Trim().ToLowerInvariant();
        var routingAppsMode = ramProp.GetString()!.Trim().ToLowerInvariant();

        if (routingMode != "split" && routingMode != "full")
            throw new RouterException("invalid_argument", "Invalid routingMode parameter: expected 'split' or 'full'");

        if (routingAppsMode != "include" && routingAppsMode != "exclude")
            throw new RouterException("invalid_argument", "Invalid routingAppsMode parameter: expected 'include' or 'exclude'");

        _storage.ValidateRevision(revision);

        var settings = _storage.GetSettings();
        settings.App.RoutingMode = routingMode;
        settings.App.RoutingAppsMode = routingAppsMode;

        _storage.SaveSettings(settings, revision);
        _logger.Information("[AppRoutingFeature] Updated routing: mode={Mode}, appsMode={AppsMode}", routingMode, routingAppsMode);
    }

    private static List<string> GetRunningProcessNamesSafe()
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Process[] processes = Array.Empty<Process>();
        try
        {
            processes = Process.GetProcesses();
            foreach (var p in processes)
            {
                try
                {
                    var name = p.ProcessName;
                    if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
                    {
                        result.Add(name);
                    }
                }
                catch
                {
                    // Process may have exited between GetProcesses and reading ProcessName
                }
            }
        }
        catch
        {
            // Best-effort process enumeration
        }
        finally
        {
            foreach (var p in processes)
            {
                try { p.Dispose(); } catch { }
            }
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }
}
