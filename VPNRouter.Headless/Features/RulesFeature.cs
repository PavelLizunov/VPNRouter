using System;
using System.Collections.Generic;
using System.Text.Json;
using Serilog;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Headless.Storage;

namespace VPNRouter.Headless.Features;

public sealed class RulesFeature
{
    private readonly ConfigStorage _storage;
    private readonly ILogger _logger;

    public RulesFeature(ConfigStorage storage, ILogger? logger = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _logger = logger ?? Log.Logger;
    }

    public object Get(JsonElement parameters = default)
    {
        RouterBackend.EnsureEmptyParameters(parameters);

        var settings = _storage.GetSettings();
        var rules = settings.App?.CustomRules ?? new List<CustomRule>();
        var text = CustomRulesParser.SerializeToText(rules);
        var priority = settings.App?.CustomRulesPriority ?? "toggles_first";

        return new { text, priority };
    }

    public void Set(JsonElement parameters)
    {
        RouterBackend.EnsureAllowedProperties(parameters, "revision", "text", "priority");

        if (!parameters.TryGetProperty("revision", out var revProp) || revProp.ValueKind != JsonValueKind.String)
            throw new RouterException("invalid_argument", "Missing revision parameter");

        if (!parameters.TryGetProperty("text", out var textProp) || textProp.ValueKind != JsonValueKind.String)
            throw new RouterException("invalid_argument", "Missing text parameter");

        if (!parameters.TryGetProperty("priority", out var prioProp) || prioProp.ValueKind != JsonValueKind.String)
            throw new RouterException("invalid_argument", "Missing priority parameter");

        var revision = revProp.GetString()!;
        var text = textProp.GetString()!;
        var priority = prioProp.GetString()!.Trim().ToLowerInvariant();

        if (priority != "toggles_first" && priority != "custom_first")
            throw new RouterException("invalid_argument", "Invalid priority: expected 'toggles_first' or 'custom_first'");

        if (text.Length > 256 * 1024)
            throw new RouterException("invalid_argument", "Rules text exceeds maximum allowed size (256 KiB)");

        _storage.ValidateRevision(revision);

        var parseResult = CustomRulesParser.ParseFromText(text);
        if (parseResult.Errors != null && parseResult.Errors.Count > 0)
        {
            var firstErr = parseResult.Errors[0];
            throw new RouterException("invalid_argument", $"Rule error on line {firstErr.LineNumber}: {firstErr.Reason}");
        }

        var settings = _storage.GetSettings();
        settings.App.CustomRules = parseResult.Rules;
        settings.App.CustomRulesPriority = priority;

        _storage.SaveSettings(settings, revision);
        _logger.Information("[RulesFeature] Updated {Count} custom rules with priority {Priority}", parseResult.Rules.Count, priority);
    }

    public void Import(JsonElement parameters)
    {
        RouterBackend.EnsureAllowedProperties(parameters, "revision", "text", "format");

        if (!parameters.TryGetProperty("revision", out var revProp) || revProp.ValueKind != JsonValueKind.String)
            throw new RouterException("invalid_argument", "Missing revision parameter");

        if (!parameters.TryGetProperty("text", out var textProp) || textProp.ValueKind != JsonValueKind.String)
            throw new RouterException("invalid_argument", "Missing text parameter");

        if (!parameters.TryGetProperty("format", out var formatProp) || formatProp.ValueKind != JsonValueKind.String)
            throw new RouterException("invalid_argument", "Missing format parameter");

        var revision = revProp.GetString()!;
        var text = textProp.GetString()!;
        var formatStr = formatProp.GetString()!.Trim().ToLowerInvariant();

        if (text.Length > 256 * 1024)
            throw new RouterException("invalid_argument", "Rules text exceeds maximum allowed size (256 KiB)");

        var format = ParseFormat(formatStr);
        _storage.ValidateRevision(revision);

        var result = CustomRulesImportExport.ImportFromText(text, format);
        if (result.Rules == null || (result.Rules.Count == 0 && !string.IsNullOrWhiteSpace(text)))
        {
            var err = result.Warnings != null && result.Warnings.Count > 0
                ? string.Join("; ", result.Warnings)
                : "No valid rules could be imported";
            throw new RouterException("invalid_argument", err);
        }

        var settings = _storage.GetSettings();
        settings.App.CustomRules = result.Rules;

        _storage.SaveSettings(settings, revision);
        _logger.Information("[RulesFeature] Imported {Count} rules in {Format} format", result.Rules.Count, format);
    }

    public object Export(JsonElement parameters = default)
    {
        string formatStr = "json";
        if (parameters.ValueKind == JsonValueKind.Object)
        {
            RouterBackend.EnsureAllowedProperties(parameters, "format");
            if (parameters.TryGetProperty("format", out var formatProp))
            {
                if (formatProp.ValueKind != JsonValueKind.String)
                    throw new RouterException("invalid_argument", "format must be a string");
                formatStr = formatProp.GetString()!.Trim().ToLowerInvariant();
            }
        }
        else if (parameters.ValueKind != JsonValueKind.Undefined && parameters.ValueKind != JsonValueKind.Null)
        {
            throw new RouterException("invalid_request", "Expected JSON object parameters");
        }

        var format = ParseFormat(formatStr);
        var settings = _storage.GetSettings();
        var rules = settings.App?.CustomRules ?? new List<CustomRule>();

        var text = CustomRulesImportExport.ExportToText(rules, format);
        return new { text };
    }

    private static CustomRulesImportExport.Format ParseFormat(string formatStr)
    {
        return formatStr switch
        {
            "json" => CustomRulesImportExport.Format.VpnrouterJson,
            "csv" => CustomRulesImportExport.Format.Csv,
            "singbox" => CustomRulesImportExport.Format.SingBoxJson,
            _ => throw new RouterException("invalid_argument", $"Unsupported rules format '{formatStr}': expected 'json', 'csv', or 'singbox'")
        };
    }
}
