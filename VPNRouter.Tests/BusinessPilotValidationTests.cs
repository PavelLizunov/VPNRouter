using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VPNRouter.App.Services;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

// Executed only by the private packaging job. No secret values go to test output.
[Collection("SingBoxFeaturesSerial")]
public sealed class BusinessPilotValidationTests
{
    [Fact]
    public void EmbeddedConnection_FirstRunPersistsAndGeneratesBothModesWithoutImport()
    {
        var destination = Environment.GetEnvironmentVariable("VPNROUTER_BUSINESS_VALIDATE_DIR");
        var bundle = Environment.GetEnvironmentVariable("VPNROUTER_BUSINESS_PAYLOAD_DIR");
        Assert.SkipUnless(ProductEdition.IsBusiness && !string.IsNullOrEmpty(destination) && !string.IsNullOrEmpty(bundle),
            "Private packaging job only; individual credentials must not be present in public tests.");
        var previousData = AppPaths.DataDir;
        var previousAwg = SingBoxFeatures.OverrideAwg;
        var previousXhttp = SingBoxFeatures.OverrideXhttp;
        var previousPolicy = SingBoxRuntimePolicy.Current;
        AppPaths.OverrideDataDir(Path.Combine(destination!, "data"));
        try
        {
            // The test output directory has no packaged core. Do not infer its
            // capabilities from an empty AppPaths/bin or an unconditional policy.
            Assert.True(previousPolicy == null, "Windows packaging validation must not inherit a scoped Headless runtime policy.");
            var capabilities = ReadPackagedCapabilities(bundle!);
            SingBoxFeatures.OverrideAwg = capabilities.Awg;
            SingBoxFeatures.OverrideXhttp = capabilities.Xhttp;
            Assert.Equal(capabilities.Awg, SingBoxFeatures.AwgAvailable);
            Assert.Equal(capabilities.Xhttp, SingBoxFeatures.XhttpAvailable);

            using var quiet = new ConfidentialOutputScope();
            var connection = PrivateStep("embedded input", BusinessBootstrap.ReadConnection);
            var rows = PrivateStep("subscription row decoding", () => ReadRows(connection));
            Assert.True(rows.Length > 0, "The private fallback must contain subscription rows.");
            var awgRows = rows.Count(IsAwg);
            Assert.True(awgRows == 0 || capabilities.Awg, "The packaged native core must support the bundled AWG rows.");
            Assert.True(rows.All(row => ServerUriParser.IsSupportedScheme(row)),
                "The packaged native core must support every bundled subscription scheme.");
            var parsed = PrivateStep("subscription parsing", () => SubscriptionFetcher.ParseBody(connection));
            Assert.Equal(rows.Length, parsed.Count);
            Assert.Equal(rows.Count(row => row.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)),
                parsed.Count(server => server.Protocol == "vless"));
            Assert.Equal(rows.Count(row => row.StartsWith("hysteria2://", StringComparison.OrdinalIgnoreCase)
                || row.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase)),
                parsed.Count(server => server.Protocol == "hysteria2"));
            Assert.Equal(awgRows, parsed.Count(server => server.Protocol == "amneziawg"));

            PrivateStep("private data directories", AppPaths.EnsureDirectories);
            Assert.False(File.Exists(AppPaths.ConfigYamlPath));
            var settings = PrivateStep("first settings load", () => RealSettingsStore.Instance.Load(AppPaths.ConfigYamlPath));
            PrivateStep("Business bootstrap", () => BusinessBootstrap.Apply(settings, RealSettingsStore.Instance, bundle));
            var services = PrivateStep("service metadata", () => BusinessClientConfiguration.Parse(connection));
            Assert.True(services.Subscription != null, "Private delivery must include the subscription reference.");
            Assert.True(services.SupportEndpoint != null && !string.IsNullOrWhiteSpace(services.SupportToken),
                "Private delivery must include the individual diagnostic upload identity.");
            PrivateStep("complete response validation", () => BusinessClientConfiguration.ValidateResponse(connection));
            settings = PrivateStep("persisted settings load", () => RealSettingsStore.Instance.Load(AppPaths.ConfigYamlPath));
            Assert.Equal(rows.Length, settings.Vless.Servers.Count);
            Assert.Equal(awgRows, settings.Vless.Servers.Count(server => server.Protocol == "amneziawg"));
            Assert.Equal(6, settings.App.RoutingAppsInclude.Count);
            Assert.True(GeoDataDownloader.AreGeoFilesAvailable());
            foreach (var mode in new[] { "split", "full" })
            {
                settings.App.RoutingMode = mode;
                var config = PrivateStep("native configuration generation", () => ConfigGenerator.Generate(new Profile(), [], settings));
                Assert.Equal(mode == "split" ? "direct" : "proxy", config.Route.Final);
                Assert.Equal("direct", config.Route.Rules.First(r => r.RuleSet != null).Outbound);
                Assert.Contains(config.Dns.Rules, r => r.RuleSet != null && r.Server == "local-dns");
                Assert.Contains(config.Route.Rules, r => r.DomainSuffix?.Contains("uiscom.ru") == true && r.Outbound == "direct");
                PrivateStep("private native configuration output", () =>
                    AppPaths.WritePrivateText(Path.Combine(destination!, mode + ".json"), ConfigGenerator.Serialize(config)));
            }
        }
        finally
        {
            SingBoxFeatures.OverrideAwg = previousAwg;
            SingBoxFeatures.OverrideXhttp = previousXhttp;
            AppPaths.OverrideDataDir(previousData);
            Assert.True(ReferenceEquals(previousPolicy, SingBoxRuntimePolicy.Current), "Packaging validation must preserve the prior runtime policy.");
        }
    }

    private static (bool Awg, bool Xhttp) ReadPackagedCapabilities(string bundle)
    {
        var core = Path.Combine(Path.GetFullPath(bundle), "sing-box.exe");
        Assert.True(File.Exists(core), "The actual packaged native core is required for private validation.");
        Assert.True((File.GetAttributes(core) & FileAttributes.ReparsePoint) == 0,
            "Packaged native core must be a regular file.");
        var start = new ProcessStartInfo(core)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("version");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Packaged native core did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            Assert.True(process.WaitForExit(5000), "Packaged native core capability inspection timed out.");
            Assert.True(Task.WaitAll(new Task[] { stdout, stderr }, 5000), "Packaged native core capability output did not finish.");
            Assert.True(process.ExitCode == 0, "Packaged native core capability inspection failed.");
            var tags = stdout.GetAwaiter().GetResult().Split('\n')
                .Select(line => line.Trim()).Where(line => line.StartsWith("Tags:", StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert.True(tags.Length == 1, "Packaged native core must report one capability-tag line.");
            var declared = tags[0]["Tags:".Length..].Split([',', ' ', '\t'],
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return (declared.Contains("with_awg", StringComparer.Ordinal), declared.Contains("with_xhttp", StringComparer.Ordinal));
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); process.WaitForExit(5000); }
                catch { /* Inspection is already failing; do not emit raw process details. */ }
            }
        }
    }

    private static string[] ReadRows(string connection)
    {
        using var doc = JsonDocument.Parse(connection);
        var encoded = doc.RootElement.GetProperty("config").GetString()
            ?? throw new InvalidDataException("Private fallback missing.");
        return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(encoded))
            .Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool IsAwg(string row) => new[] { "awg://", "awg3://", "amneziawg://", "amneziawg3://" }
        .Any(prefix => row.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static T PrivateStep<T>(string stage, Func<T> operation)
    {
        try { return operation(); }
        catch { throw new InvalidOperationException("Private Business validation failed during " + stage + "."); }
    }

    private static void PrivateStep(string stage, Action operation) => PrivateStep(stage, () => { operation(); return true; });

    private sealed class ConfidentialOutputScope : IDisposable
    {
        private readonly TextWriter _stdout = Console.Out, _stderr = Console.Error;
        private readonly Serilog.ILogger _logger = Serilog.Log.Logger;
        private readonly Serilog.Core.Logger _quiet = new Serilog.LoggerConfiguration().CreateLogger();

        internal ConfidentialOutputScope()
        {
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            Serilog.Log.Logger = _quiet;
        }

        public void Dispose()
        {
            Serilog.Log.Logger = _logger;
            Console.SetOut(_stdout);
            Console.SetError(_stderr);
            _quiet.Dispose();
        }
    }
}
