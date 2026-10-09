using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Headless.Storage;

namespace VPNRouter.Headless.Features;

public sealed class CustomConfigFeature
{
    private readonly ConfigStorage _storage;
    private readonly CustomConfigStorage _customStorage;
    private readonly ILogger _logger;
    private SingBoxRuntimePolicy? _policy;

    private SingBoxRuntimePolicy? EffectivePolicy => SingBoxRuntimePolicy.Capture(ref _policy);

    public CustomConfigFeature(ConfigStorage storage, ILogger? logger = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _logger = logger ?? Log.Logger;
        _customStorage = new CustomConfigStorage(_logger);
        _policy = SingBoxRuntimePolicy.Current ?? (OperatingSystem.IsLinux() ? SingBoxRuntimePolicy.DefaultProduction : null);
    }

    public object List(JsonElement parameters = default)
    {
        using var _ = SingBoxRuntimePolicy.EnterScope(EffectivePolicy);
        RouterBackend.EnsureEmptyParameters(parameters);

        var settings = _storage.GetSettings();
        var configs = settings.App?.CustomConfigs ?? new List<CustomConfigEntry>();
        var activeConfig = settings.App?.ActiveCustomConfig ?? string.Empty;
        var isCustomMode = string.Equals(settings.App?.ConfigMode, "custom", StringComparison.OrdinalIgnoreCase);

        var items = configs.Select(c => new
        {
            id = ComputeCustomConfigId(c.Name),
            name = c.Name,
            selected = isCustomMode && string.Equals(activeConfig, c.Name, StringComparison.OrdinalIgnoreCase)
        }).ToList();

        return new { items };
    }

    public void Import(JsonElement parameters)
    {
        using var _ = SingBoxRuntimePolicy.EnterScope(EffectivePolicy);
        RouterBackend.EnsureAllowedProperties(parameters, "revision", "name", "text");

        if (!parameters.TryGetProperty("revision", out var revProp) || revProp.ValueKind != JsonValueKind.String)
            throw new RouterException("invalid_argument", "Missing revision parameter");

        if (!parameters.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
            throw new RouterException("invalid_argument", "Missing name parameter");

        if (!parameters.TryGetProperty("text", out var textProp) || textProp.ValueKind != JsonValueKind.String)
            throw new RouterException("invalid_argument", "Missing text parameter");

        var revision = revProp.GetString()!;
        var name = nameProp.GetString()!.Trim();
        var text = textProp.GetString()!;

        _storage.ValidateRevision(revision);

        string? previousText = null;
        try
        {
            previousText = _customStorage.GetCustomConfigText(name);
        }
        catch (Exception ex)
        {
            _logger.Warning("[CustomConfigFeature] Could not read existing custom config prior to overwrite: {ErrorType}", ex.GetType().Name);
        }

        var persistedPath = _customStorage.SaveCustomConfig(name, text);

        var settings = _storage.GetSettings();
        settings.App.CustomConfigs ??= new List<CustomConfigEntry>();

        var existing = settings.App.CustomConfigs.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.Path = persistedPath;
        }
        else
        {
            settings.App.CustomConfigs.Add(new CustomConfigEntry
            {
                Name = name,
                Path = persistedPath
            });
        }

        if (settings.App.CustomConfigs.Count == 1 || string.IsNullOrWhiteSpace(settings.App.ActiveCustomConfig))
        {
            settings.App.ActiveCustomConfig = name;
            settings.App.ConfigMode = "custom";
        }

        try
        {
            _storage.SaveSettings(settings, revision);
        }
        catch
        {
            // Transactional rollback of disk state if settings commit failed
            try
            {
                if (previousText != null)
                {
                    _customStorage.SaveCustomConfig(name, previousText);
                }
                else
                {
                    _customStorage.DeleteCustomConfig(name);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning("[CustomConfigFeature] Failed to rollback disk state after SaveSettings failure: {ErrorType}", ex.GetType().Name);
            }
            throw;
        }

        _logger.Information("[CustomConfigFeature] Imported custom config {Name}", name);
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
        var configs = settings.App?.CustomConfigs ?? new List<CustomConfigEntry>();
        var match = configs.FirstOrDefault(c => string.Equals(ComputeCustomConfigId(c.Name), id, StringComparison.Ordinal));
        if (match == null)
            throw new RouterException("not_found", "Custom config not found");

        settings.App.ActiveCustomConfig = match.Name;
        settings.App.ConfigMode = "custom";

        _storage.SaveSettings(settings, revision);
        _logger.Information("[CustomConfigFeature] Selected custom config {Name}", match.Name);
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
        var configs = settings.App?.CustomConfigs ?? new List<CustomConfigEntry>();
        var match = configs.FirstOrDefault(c => string.Equals(ComputeCustomConfigId(c.Name), id, StringComparison.Ordinal));
        if (match == null)
            throw new RouterException("not_found", "Custom config not found");

        var wasActive = string.Equals(settings.App?.ActiveCustomConfig, match.Name, StringComparison.OrdinalIgnoreCase);
        configs.Remove(match);

        if (wasActive)
        {
            if (configs.Count > 0)
            {
                settings.App.ActiveCustomConfig = configs[0].Name;
            }
            else
            {
                settings.App.ActiveCustomConfig = string.Empty;
                settings.App.ConfigMode = "generated";
            }
        }

        _storage.SaveSettings(settings, revision);

        // Delete the file on disk ONLY after settings are safely committed
        try
        {
            _customStorage.DeleteCustomConfig(match.Name);
        }
        catch (Exception ex)
        {
            _logger.Warning("[CustomConfigFeature] Failed to delete custom config on disk after settings commit: {ErrorType}", ex.GetType().Name);
        }

        _logger.Information("[CustomConfigFeature] Removed custom config {Name}", match.Name);
    }

    private static string ComputeCustomConfigId(string name)
    {
        var raw = $"custom:{name.Trim().ToLowerInvariant()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return "cfg_" + Convert.ToHexStringLower(hash)[..16];
    }
}
