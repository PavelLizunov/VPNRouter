using System.Reflection;
using System.Text.Json;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.App.Services;

internal static class BusinessBootstrap
{
    internal static List<CustomRule> ReadRules() =>
        JsonSerializer.Deserialize<List<CustomRule>>(ReadResource("VPNRouter.Business.Rules"),
            ProfileManager.SafeJsonOptions) ?? throw new InvalidDataException("Business rules missing.");

    internal static void Apply(AppSettings settings, ISettingsStore store, string? bundleDirectory = null)
    {
        BusinessDefaults.Apply(settings, ReadResource("VPNRouter.Business.Connection"), ReadRules());
        foreach (var name in new[] { "geoip-ru.srs", "geosite-ru.srs" })
        {
            var source = Path.Combine(bundleDirectory ?? AppContext.BaseDirectory, "geo", name);
            if (!File.Exists(source) || !RuleSetFile.IsValidBinary(source))
                throw new InvalidDataException("Bundled Business bypass data missing.");
            File.Copy(source, Path.Combine(AppPaths.GeoDir, name), overwrite: true);
        }
        store.Save(settings, AppPaths.ConfigYamlPath);
    }

    private static string ReadResource(string name)
    {
        using var stream = typeof(BusinessBootstrap).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidDataException("Business resource missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
