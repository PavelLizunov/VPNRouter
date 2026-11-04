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
        => Apply(settings, store, ReadConnection(), bundleDirectory ?? AppContext.BaseDirectory,
            AppPaths.GeoDir, AppPaths.ConfigYamlPath);

    internal static void Apply(AppSettings settings, ISettingsStore store, string connection,
        string bundleDirectory, string geoDirectory, string settingsPath)
    {
        BusinessDefaults.Apply(settings, BusinessClientConfiguration.SelectStartupConnection(settings, connection), ReadRules());
        foreach (var name in new[] { "geoip-ru.srs", "geosite-ru.srs" })
        {
            var source = Path.Combine(bundleDirectory, "geo", name);
            if (!File.Exists(source) || !RuleSetFile.IsValidBinary(source))
                throw new InvalidDataException("Bundled Business bypass data missing.");
            File.Copy(source, Path.Combine(geoDirectory, name), overwrite: true);
        }
        store.Save(settings, settingsPath);
    }

    internal static string ReadConnection() => ReadResource("VPNRouter.Business.Connection");

    internal static BusinessClientConfiguration ReadClientConfiguration()
    {
        try { return BusinessClientConfiguration.Parse(ReadConnection()); }
        catch { return BusinessClientConfiguration.Empty; }
    }

    private static string ReadResource(string name)
    {
        using var stream = typeof(BusinessBootstrap).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidDataException("Business resource missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
