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

    internal static BusinessPolicyCoordinator? Apply(AppSettings settings, ISettingsStore store, string? bundleDirectory = null)
        => Apply(settings, store, ReadConnection(), bundleDirectory ?? AppContext.BaseDirectory,
            AppPaths.GeoDir, AppPaths.ConfigYamlPath);

    internal static BusinessPolicyCoordinator? Apply(AppSettings settings, ISettingsStore store, string connection,
        string bundleDirectory, string geoDirectory, string settingsPath,
        Func<string, CancellationToken, Task<bool>>? checkGeoContents = null)
    {
        BusinessProvisioningStore.ValidateBundle(connection);
        BusinessPolicyCoordinator? policy = null;
        try
        {
            if (BusinessClientConfiguration.Parse(connection).Policy is { } configuration)
            {
                var dataDirectory = Path.GetDirectoryName(Path.GetFullPath(settingsPath))!;
                policy = new(configuration, new BusinessPolicyStore(configuration, dataDirectory),
                    phoneStore: new BusinessPolicyStore(configuration, dataDirectory, telephony: true),
                    embeddedRules: ReadRules());
                policy.Load();
                if (policy.IsUnavailable) throw new BusinessPolicyStartupException();
            }
            BusinessDefaults.Apply(settings, BusinessClientConfiguration.SelectStartupConnection(settings, connection),
                ReadRules(), policy?.Current);
            settings.BusinessPolicy = policy?.Current;
            GeoDataDownloader.InstallBusinessBundle(Path.Combine(bundleDirectory, "geo"), geoDirectory,
                checkGeoContents ?? RuleSetFile.CheckContentsAsync);
            store.Save(settings, settingsPath);
            return policy;
        }
        catch
        {
            policy?.Dispose();
            throw;
        }
    }

    internal static string ReadConnection()
    {
        using var embedded = typeof(BusinessBootstrap).Assembly.GetManifestResourceStream("VPNRouter.Business.Connection");
        return ReadConnection(embedded == null ? null : BusinessProvisioningStore.ReadBounded(embedded), AppPaths.DataDir);
    }

    internal static string ReadConnection(string? embeddedBundle, string dataDirectory) =>
        BusinessProvisioningStore.Resolve(embeddedBundle, dataDirectory);

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

internal sealed class BusinessPolicyStartupException : Exception
{
    internal BusinessPolicyStartupException() : base("Business policy recovery is required.") { }
}
