using System.IO.Compression;
using VPNRouter.Core;
using VPNRouter.Core.Services.Diagnostics;
using Xunit;

namespace VPNRouter.Tests;

public sealed class DiagnosticsExporterTests
{
    private const string Uuid = "2d54442d-158f-49e2-b225-67ba1a5b77f4";
    private const string Password = "superSecretPass123";
    private const string ShortId = "deadbeef01";
    private const string SubToken = "SECRETTOKEN123456";
    private const string LogSecret = "vless://2d54442d-158f-49e2-b225-67ba1a5b77f4@9.9.9.9:443";

    [Fact]
    public void Export_ProducesZip_WithNoSecretsAndExpectedEntries()
    {
        var previous = AppPaths.DataDir;
        var dataDir = Path.Combine(Path.GetTempPath(), "vpnrouter-diag-test-" + Guid.NewGuid().ToString("N"));
        var outDir = Path.Combine(Path.GetTempPath(), "vpnrouter-diag-out-" + Guid.NewGuid().ToString("N"));

        try
        {
            AppPaths.OverrideDataDir(dataDir);
            AppPaths.EnsureDirectories();

            File.WriteAllText(AppPaths.ConfigYamlPath, $@"
app:
  config_mode: subscribe
  subscriptions:
    - name: main
      url: https://ninitux.com/api/v1/app/config/{SubToken}
      enabled: true
vless:
  servers:
    - name: srv1
      server: 1.2.3.4
      port: 443
      uuid: {Uuid}
      password: {Password}
      server_name: www.microsoft.com
");

            File.WriteAllText(AppPaths.CurrentConfigPath, $@"{{
  ""outbounds"": [ {{ ""type"": ""vless"", ""tag"": ""proxy"", ""server"": ""1.2.3.4"",
    ""uuid"": ""{Uuid}"", ""tls"": {{ ""reality"": {{ ""short_id"": ""{ShortId}"" }} }} }} ],
  ""route"": {{ ""final"": ""direct"" }}
}}");

            File.WriteAllText(Path.Combine(AppPaths.LogsDir, "vpnrouter20260602.log"),
                $"2026-06-02 00:00:00 [INF] connecting {LogSecret} ok\n");

            var newestBackup = Path.Combine(AppPaths.DataDir, "config.yaml.unloadable-20260815-120000");
            File.WriteAllText(newestBackup, $@"
app:
  config_mode: subscribe
  subscriptions:
    - name: backup_sub
      url: https://ninitux.com/api/v1/app/config/{SubToken}
      enabled: true
vless:
  servers:
    - name: backup_srv
      uuid: {Uuid}
");
            File.SetLastWriteTimeUtc(newestBackup, new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc));
            for (var i = 1; i <= 5; i++)
            {
                var olderBackup = Path.Combine(AppPaths.DataDir, $"config.yaml.invalid-2026080{i}-120000");
                File.WriteAllText(olderBackup, "app:\n  config_mode: generated\n");
                File.SetLastWriteTimeUtc(olderBackup, new DateTime(2026, 8, i, 12, 0, 0, DateTimeKind.Utc));
            }

            var result = DiagnosticsExporter.Export(
                new DateTime(2026, 6, 2, 1, 2, 3), connected: false, destinationDir: outDir);

            Assert.True(File.Exists(result.ZipPath), "diagnostics zip should exist");
            Assert.EndsWith("VPNRouter-diagnostics-20260602-010203.zip", result.ZipPath);

            var all = new System.Text.StringBuilder();
            var entryNames = new List<string>();
            using (var zip = ZipFile.OpenRead(result.ZipPath))
            {
                foreach (var entry in zip.Entries)
                {
                    entryNames.Add(entry.Name);
                    using var sr = new StreamReader(entry.Open());
                    all.Append(sr.ReadToEnd());
                }
            }
            var bundle = all.ToString();

            Assert.DoesNotContain(Uuid, bundle);
            Assert.DoesNotContain(Password, bundle);
            Assert.DoesNotContain(ShortId, bundle);
            Assert.DoesNotContain(SubToken, bundle);
            Assert.DoesNotContain(LogSecret, bundle);

            Assert.Contains("README.txt", entryNames);
            Assert.Contains("summary.txt", entryNames);
            Assert.Contains("windows-services.txt", entryNames);
            Assert.Contains("antivirus-integrity.txt", entryNames);
            Assert.Contains("config.redacted.yaml", entryNames);
            Assert.Contains("current.redacted.json", entryNames);
            Assert.Contains("vpnrouter20260602.log", entryNames);
            Assert.Contains("config.unloadable-20260815-120000.redacted.yaml", entryNames);
            Assert.Equal(5, entryNames.Count(n =>
                n.StartsWith("config.unloadable-", StringComparison.Ordinal) ||
                n.StartsWith("config.invalid-", StringComparison.Ordinal)));
            Assert.DoesNotContain("config.invalid-20260801-120000.redacted.yaml", entryNames);

            Assert.Contains("1.2.3.4", bundle);
            Assert.Contains("www.microsoft.com", bundle);
            Assert.Contains(AppVersion.Version, bundle);
        }
        finally
        {
            AppPaths.OverrideDataDir(previous);
            TryDelete(dataDir);
            TryDelete(outDir);
        }
    }

    [Fact]
    public void Export_IncludesCrashReportsAndAnIndexOfShutdownMarkers_WithoutSecrets()
    {
        var previous = AppPaths.DataDir;
        var dataDir = Path.Combine(Path.GetTempPath(), "vpnrouter-diag-crash-" + Guid.NewGuid().ToString("N"));
        var outDir = Path.Combine(Path.GetTempPath(), "vpnrouter-diag-crash-out-" + Guid.NewGuid().ToString("N"));

        try
        {
            AppPaths.OverrideDataDir(dataDir);
            AppPaths.EnsureDirectories();
            File.WriteAllText(Path.Combine(AppPaths.LogsDir, "vpnrouter20261002.log"), "2026-10-02 00:00:00 [INF] started\n");

            var crashes = Path.Combine(AppPaths.DataDir, "crashes");
            Directory.CreateDirectory(crashes);
            for (var i = 1; i <= 7; i++)
            {
                var report = Path.Combine(crashes, $"crash-2026100{i}-120000-000.txt");
                File.WriteAllText(report, $"VPNRouter crash report {i}\nSystem.InvalidOperationException: Visual was invalidated during the render pass\nconnecting {LogSecret}\n");
                File.SetLastWriteTimeUtc(report, new DateTime(2026, 10, i, 12, 0, 0, DateTimeKind.Utc));
            }
            File.WriteAllText(Path.Combine(crashes, "shutdown-20261001-120000-000.txt"), "graceful\n");

            var result = DiagnosticsExporter.Export(new DateTime(2026, 10, 2, 11, 43, 52), connected: false, destinationDir: outDir);

            var names = new List<string>();
            var all = new System.Text.StringBuilder();
            using (var zip = ZipFile.OpenRead(result.ZipPath))
            {
                foreach (var entry in zip.Entries)
                {
                    names.Add(entry.Name);
                    using var sr = new StreamReader(entry.Open());
                    all.Append(sr.ReadToEnd());
                }
            }

            Assert.Contains("crash-index.txt", names);
            Assert.Equal(DiagnosticsExporter.MaxCrashReports, names.Count(n => n.StartsWith("crash-2026", StringComparison.Ordinal)));
            Assert.Contains("crash-20261007-120000-000.txt", names);
            Assert.DoesNotContain("crash-20261001-120000-000.txt", names);
            Assert.Contains("shutdown-20261001-120000-000.txt", all.ToString());
            Assert.Contains("Visual was invalidated during the render pass", all.ToString());
            Assert.DoesNotContain(LogSecret, all.ToString());
            Assert.DoesNotContain(Uuid, all.ToString());
        }
        finally
        {
            AppPaths.OverrideDataDir(previous);
            TryDelete(dataDir);
            TryDelete(outDir);
        }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }
}
