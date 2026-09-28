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
        Func<VlessServerEntry, bool>? isServerAlive = null)
    {
        var resolved = VlessServersResolver.Resolve(settings, logger);

        if (resolved.Count == 0)
        {
            var why = VlessServersResolver.DescribeEmptyReason(settings)
                      ?? "VLESS server not configured.";
            throw new InvalidOperationException(why);
        }

        var sbConfig = ConfigGenerator.Generate(profile, resolvedProcessNames, settings, strictDnsOverride, isServerAlive);

        try
        {
            var validation = LeakProtection.ValidateConfig(sbConfig, settings);

            foreach (var warn in validation.Warnings)
            {
                logger?.Warning("[ConfigPipeline] {Warn}", warn);
                warningSink?.Invoke(warn);
            }

            if (!validation.IsValid)
            {
                var errors = string.Join("; ", validation.Errors);
                if (validationMode == ValidationMode.Strict)
                {
                    throw new InvalidOperationException(
                        $"Config validation failed: {errors}");
                }
                else
                {
                    // On restart validation is advisory, except the static IPv6-leak invariant, which must still block.
                    if (validation.Errors.Any(e =>
                            e.Contains("ipv4_only", StringComparison.OrdinalIgnoreCase)
                            || e.Contains("dns.strategy", StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new InvalidOperationException(
                            $"Config validation failed (non-transient IPv6-leak invariant): {errors}");
                    }
                    logger?.Warning(
                        "[ConfigPipeline] LeakProtection flagged restart config: errors=[{Errors}] warnings=[{Warnings}]",
                        string.Join(" | ", validation.Errors),
                        string.Join(" | ", validation.Warnings));
                }
            }
            else if (validation.Warnings.Count > 0
                     && validationMode == ValidationMode.Advisory)
            {
                logger?.Information(
                    "[ConfigPipeline] LeakProtection restart-config warnings: {Warnings}",
                    string.Join(" | ", validation.Warnings));
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (validationMode == ValidationMode.Strict) throw;
            logger?.Warning(ex,
                "[ConfigPipeline] LeakProtection.ValidateConfig threw (non-fatal in advisory mode)");
        }

        return ConfigGenerator.Serialize(sbConfig);
    }

    public enum ValidationMode
    {
        Strict,

        Advisory,
    }
}
