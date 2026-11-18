using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

internal static class ConfigPipeline
{
    public static string Generate(
        Profile profile,
        IEnumerable<string> resolvedProcessNames,
        AppSettings settings,
        ValidationMode validationMode = ValidationMode.Strict,
        Action<string>? warningSink = null,
        ILogger? logger = null,
        bool? strictDnsOverride = null,
        Func<VlessServerEntry, bool>? isServerAlive = null,
        bool requireGeo = false)
    {
        var resolved = VlessServersResolver.Resolve(settings, logger);

        if (resolved.Count == 0)
        {
            var why = VlessServersResolver.DescribeEmptyReason(settings)
                      ?? "VLESS server not configured.";
            throw new InvalidOperationException(why);
        }

        var sbConfig = ConfigGenerator.Generate(profile, resolvedProcessNames, settings, strictDnsOverride, isServerAlive,
            resolveRuleSet: null, requireGeo: requireGeo);
        ValidateGeneratedConfig(sbConfig, settings, validationMode, warningSink, logger);
        return ConfigGenerator.Serialize(sbConfig);
    }

    internal static void ValidateGeneratedConfig(SingBoxConfig config, AppSettings settings,
        ValidationMode validationMode, Action<string>? warningSink, ILogger? logger)
    {
        ValidationResult validation;
        try
        {
            validation = LeakProtection.ValidateConfig(config, settings);
        }
        catch (Exception ex)
        {
            if (validationMode == ValidationMode.Strict || ex is InvalidOperationException)
                throw new InvalidOperationException("Unable to validate the VPN configuration. Check the selected configuration.");
            logger?.Warning("[ConfigPipeline] Unable to validate restart configuration in advisory mode");
            return;
        }

        if (validation.Warnings.Count > 0)
        {
            logger?.Warning("[ConfigPipeline] Configuration validation reported {WarningCount} warnings", validation.Warnings.Count);
            warningSink?.Invoke("Review VPN routing and DNS settings: configuration validation reported warnings.");
        }
        if (validation.IsValid) return;

        logger?.Warning("[ConfigPipeline] Configuration validation reported {ErrorCount} errors in {Mode} mode",
            validation.Errors.Count, validationMode);
        if (validationMode == ValidationMode.Strict)
            throw new InvalidOperationException("VPN configuration validation failed. Check the selected server, routing and DNS settings.");

        // Restart validation is advisory except for the existing IPv6 leak check.
        if (validation.Errors.Any(e => e.Contains("ipv4_only", StringComparison.OrdinalIgnoreCase)
            || e.Contains("dns.strategy", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("VPN configuration validation failed. Set dns.strategy to ipv4_only before restarting.");
    }

    public enum ValidationMode
    {
        Strict,

        Advisory,
    }
}
