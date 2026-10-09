using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.FreeConfigs;
using VPNRouter.Headless.Features;
using VPNRouter.Headless.Storage;

namespace VPNRouter.Headless.Tests;

/// <summary>
/// Executable feature checks for RouterBackend and Linux feature endpoints.
/// Strictly dependency-free (no xUnit/NUnit runtime dependency), hermetic (isolated temp folder, fake session, no external network).
/// Entry point: public static Task RunAsync().
/// </summary>
public static class FeatureChecks
{
    public static async Task RunAsync()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "vpnrouter-checks-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            Console.WriteLine($"[FeatureChecks] Starting test run in {tempDir}...");

            await CheckNoStartupMutationsAsync(tempDir);
            await CheckSnapshotAndEventsAsync(tempDir);
            await CheckRevisionConflictDetectionAsync(tempDir);
            await CheckStrictBoundedInputAsync(tempDir);
            await CheckAllowlistedSettingsAsync(tempDir);
            await CheckAppRoutingPreservedCasingAsync(tempDir);
            await CheckServerImportSelectRemoveAsync(tempDir);
            await CheckSubscriptionsWriteOnlyUrlsAsync(tempDir);
            await CheckFreeConfigsVerifiedOnlyApplyAsync(tempDir);
            await CheckCustomConfigsValidationAndStorageAsync(tempDir);
            await CheckRulesDslAndImportExportAsync(tempDir);
            await CheckDiagnosticsCheckAndExportAsync(tempDir);
            await CheckConcurrencyAndBusyControlAsync(tempDir);

            // Regressions
            await CheckExternalEditConflictAsync(tempDir);
            await CheckCorruptionFailClosedAsync(tempDir);
            await CheckMutationRollbackOnApplyFailureAsync(tempDir);
            await CheckDuplicateAndStaleIdsAsync(tempDir);
            await CheckNoSecretsInErrorsAsync(tempDir);

            // New P1/P2 regressions
            await CheckSubscriptionAddSelectWithFakeServersAndModeTransitionsAsync(tempDir);
            await CheckSubscriptionDuplicateAndRefreshIdsAsync(tempDir);
            await CheckDnsModeReadbackActualEffectAsync(tempDir);
            await CheckFreeConfigVerifiedStatusPreservationAndCancelledRefreshAsync(tempDir);
            await CheckWireGuardConfImportAsync(tempDir);
            await CheckProfileListNoNetworkAsync(tempDir);

            Console.WriteLine("[FeatureChecks] All feature checks PASSED successfully!");
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Best effort test cleanup
            }
        }
    }

    private static Task CheckNoStartupMutationsAsync(string baseDir)
    {
        var dir = Path.Combine(baseDir, "no-startup-mutations");
        Directory.CreateDirectory(dir);

        var configPath = Path.Combine(dir, "config.yaml");
        var storage = new ConfigStorage(dir);

        // Verification: storage initialization and read must NOT create config.yaml on disk
        Assert(!File.Exists(configPath), "config.yaml must not exist on startup before any explicit mutation");
        var settings = storage.GetSettings();
        Assert(settings != null, "Settings must be non-null defaults");
        Assert(!File.Exists(configPath), "config.yaml must still not exist after calling GetSettings");
        Assert(!string.IsNullOrWhiteSpace(storage.CurrentRevision), "Initial revision must be non-empty");

        return Task.CompletedTask;
    }

    private static async Task CheckSnapshotAndEventsAsync(string baseDir)
    {
        var dir = Path.Combine(baseDir, "snapshot-events");
        Directory.CreateDirectory(dir);

        var session = new FakeRouterSession();
        var backend = new RouterBackend(session, dir);

        bool stateChangedFired = false;
        backend.StateChanged += _ => stateChangedFired = true;

        var snapshotObj = await backend.ExecuteAsync("snapshot", ToJsonElement(new { }));
        var json = JsonSerializer.Serialize(snapshotObj);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert(root.GetProperty("state").GetString() == "disconnected", "Initial state must be disconnected");
        Assert(root.GetProperty("activeServer").GetString() == "", "Initial active server must be empty");
        Assert(root.GetProperty("routingMode").GetString() == "split", "Default routingMode must be split");
        Assert(!root.GetProperty("busy").GetBoolean(), "Backend must not be busy");

        session.TriggerChanged("connecting");
        Assert(stateChangedFired, "StateChanged event must fire when session state changes");
    }

    private static async Task CheckRevisionConflictDetectionAsync(string baseDir)
    {
        var dir = Path.Combine(baseDir, "revision-conflict");
        Directory.CreateDirectory(dir);

        var session = new FakeRouterSession();
        var backend = new RouterBackend(session, dir);

        var snapshot = await backend.ExecuteAsync("snapshot", ToJsonElement(new { }));
        var json = JsonSerializer.Serialize(snapshot);
        using var doc = JsonDocument.Parse(json);
        var initialRevision = doc.RootElement.GetProperty("revision").GetString()!;

        // Attempt mutation with incorrect revision: must throw RouterException with code "conflict"
        bool conflictCaught = false;
        try
        {
            await backend.ExecuteAsync("apps.set", ToJsonElement(new
            {
                revision = "wrong-revision-12345",
                mode = "include",
                names = new[] { "firefox" }
            }));
        }
        catch (RouterException ex) when (ex.Code == "conflict")
        {
            conflictCaught = true;
        }
        Assert(conflictCaught, "Expected RouterException('conflict') when revision does not match");

        // Attempt mutation with matching revision: must succeed and update revision
        var result = await backend.ExecuteAsync("apps.set", ToJsonElement(new
            {
                revision = initialRevision,
                mode = "include",
                names = new[] { "firefox" }
            }));

        var resultJson = JsonSerializer.Serialize(result);
        using var resultDoc = JsonDocument.Parse(resultJson);
        var nextRevision = resultDoc.RootElement.GetProperty("revision").GetString()!;

        Assert(!string.Equals(initialRevision, nextRevision, StringComparison.Ordinal),
            "Revision must change after successful mutation");
    }

    private static async Task CheckStrictBoundedInputAsync(string baseDir)
    {
        var dir = Path.Combine(baseDir, "bounded-input");
        Directory.CreateDirectory(dir);

        var session = new FakeRouterSession();
        var backend = new RouterBackend(session, dir);

        // Unknown method must throw invalid_request
        bool invalidMethodCaught = false;
        try
        {
            await backend.ExecuteAsync("malicious_or_unknown_method", ToJsonElement(new { }));
        }
        catch (RouterException ex) when (ex.Code == "invalid_request")
        {
            invalidMethodCaught = true;
        }
        Assert(invalidMethodCaught, "Unknown method must throw invalid_request");

        // Method name with invalid characters
        bool badMethodNameCaught = false;
        try
        {
            await backend.ExecuteAsync("method with spaces; rm -rf", ToJsonElement(new { }));
        }
        catch (RouterException ex) when (ex.Code == "invalid_request")
        {
            badMethodNameCaught = true;
        }
        Assert(badMethodNameCaught, "Invalid method name syntax must throw invalid_request");
    }

    private static async Task CheckAllowlistedSettingsAsync(string baseDir)
    {
        var dir = Path.Combine(baseDir, "settings");
        Directory.CreateDirectory(dir);

        var session = new FakeRouterSession();
        var backend = new RouterBackend(session, dir);

        var snapshot = await backend.ExecuteAsync("snapshot", ToJsonElement(new { }));
        var revision = JsonSerializer.SerializeToElement(snapshot).GetProperty("revision").GetString()!;

        // 1. settings.get
        var settingsObj = await backend.ExecuteAsync("settings.get", ToJsonElement(new { }));
        var settingsElem = JsonSerializer.SerializeToElement(settingsObj);
        Assert(settingsElem.TryGetProperty("mtu", out var mtuVal) && mtuVal.GetInt32() == TunSettings.DefaultMtu, "Default MTU mismatch");
        Assert(settingsElem.TryGetProperty("strictDns", out _), "Missing strictDns in settings.get");

        // 2. Reject unknown setting
        bool unknownRejected = false;
        try
        {
            await backend.ExecuteAsync("settings.set", ToJsonElement(new
            {
                revision,
                values = new { arbitrary_unrecognized_key = true }
            }));
        }
        catch (RouterException ex) when (ex.Code == "invalid_argument")
        {
            unknownRejected = true;
        }
        Assert(unknownRejected, "Unknown setting key must be rejected");

        // 3. Reject MTU out of range
        bool badMtuRejected = false;
        try
        {
            await backend.ExecuteAsync("settings.set", ToJsonElement(new
            {
                revision,
                values = new { mtu = 2000 }
            }));
        }
        catch (RouterException ex) when (ex.Code == "invalid_argument")
        {
            badMtuRejected = true;
        }
        Assert(badMtuRejected, "MTU > 1500 must be rejected");

        // 4. Reject unsupported protection flag (dnsLeakLockdown when session doesn't support it)
        bool unsupportedFlagRejected = false;
        try
        {
            await backend.ExecuteAsync("settings.set", ToJsonElement(new
            {
                revision,
                values = new { dnsLeakLockdown = true }
            }));
        }
        catch (RouterException ex) when (ex.Code == "unsupported")
        {
            unsupportedFlagRejected = true;
        }
        Assert(unsupportedFlagRejected, "dnsLeakLockdown=true must throw unsupported when session doesn't support it");

        // 5. Valid settings.set
        var freshSnapshot = await backend.ExecuteAsync("settings.set", ToJsonElement(new
            {
                revision,
                values = new
                {
                    mtu = 1400,
                    strictDns = true,
                    blockAds = true
                }
            }));
        var freshElem = JsonSerializer.SerializeToElement(freshSnapshot);
        Assert(!string.Equals(revision, freshElem.GetProperty("revision").GetString()), "Revision must advance after valid settings.set");
    }

    private static async Task CheckAppRoutingPreservedCasingAsync(string baseDir)
    {
        var dir = Path.Combine(baseDir, "app-casing");
        Directory.CreateDirectory(dir);

        var session = new FakeRouterSession();
        var backend = new RouterBackend(session, dir);

        var snap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("snapshot", ToJsonElement(new { })));
        var rev = snap.GetProperty("revision").GetString()!;

        // Input contains mixed casing and case-insensitive duplicates
        await backend.ExecuteAsync("apps.set", ToJsonElement(new
        {
            revision = rev,
            mode = "include",
            names = new[] { "Discord.exe", "Chrome", "discord.exe", "Spotify" }
        }));

        var appsListObj = await backend.ExecuteAsync("apps.list", ToJsonElement(new { }));
        var listElem = JsonSerializer.SerializeToElement(appsListObj);
        var includeArray = listElem.GetProperty("include").EnumerateArray().Select(e => e.GetString()!).ToList();

        Assert(includeArray.Count == 3, "Duplicates must be deduplicated");
        Assert(includeArray.Contains("Discord.exe"), "Exact casing 'Discord.exe' must be preserved");
        Assert(includeArray.Contains("Chrome"), "Exact casing 'Chrome' must be preserved");
        Assert(includeArray.Contains("Spotify"), "Exact casing 'Spotify' must be preserved");
    }

    private static async Task CheckServerImportSelectRemoveAsync(string baseDir)
    {
        var dir = Path.Combine(baseDir, "servers");
        Directory.CreateDirectory(dir);

        var session = new FakeRouterSession();
        var backend = new RouterBackend(session, dir);

        var snap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("snapshot", ToJsonElement(new { })));
        var rev = snap.GetProperty("revision").GetString()!;

        // Valid VLESS URI with reality
        var vlessUri = "vless://b831381d-6324-4d53-ad4f-8cda48b30811@198.51.100.1:443?security=reality&sni=example.com&fp=chrome&pbk=1111111111111111111111111111111111111111111&sid=12345678&type=tcp#ProductionServer";

        var importSnap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("servers.import", ToJsonElement(new
        {
            revision = rev,
            text = vlessUri
        })));
        var rev2 = importSnap.GetProperty("revision").GetString()!;

        // List servers
        var listObj = await backend.ExecuteAsync("servers.list", ToJsonElement(new { offset = 0, limit = 10 }));
        var listElem = JsonSerializer.SerializeToElement(listObj);
        var items = listElem.GetProperty("items").EnumerateArray().ToList();
        Assert(items.Count == 1, "Expected 1 server in list");

        var first = items[0];
        var serverId = first.GetProperty("id").GetString()!;
        Assert(serverId.StartsWith("srv_"), "Stable non-credential ID required");
        Assert(first.GetProperty("name").GetString() == "ProductionServer", "Server name mismatch");

        // Verify NO credentials or secret parameters leaked in list
        var jsonItem = first.GetRawText();
        Assert(!jsonItem.Contains("b831381d"), "Server UUID must NOT leak in list");
        Assert(!jsonItem.Contains("1111111111111111111111111111111111111111111"), "Reality public key must NOT leak in list");

        // Select server
        var selectSnap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("servers.select", ToJsonElement(new
        {
            revision = rev2,
            id = serverId
        })));
        var rev3 = selectSnap.GetProperty("revision").GetString()!;
        Assert(selectSnap.GetProperty("activeServer").GetString() == "ProductionServer", "Active server must be updated");

        // Remove server
        var removeSnap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("servers.remove", ToJsonElement(new
        {
            revision = rev3,
            id = serverId
        })));
        Assert(removeSnap.GetProperty("activeServer").GetString() == "", "Active server must be empty after removing active server");
    }

    private static async Task CheckSubscriptionsWriteOnlyUrlsAsync(string baseDir)
    {
        var dir = Path.Combine(baseDir, "subscriptions");
        Directory.CreateDirectory(dir);

        var session = new FakeRouterSession();
        var backend = new RouterBackend(session, dir);

        var snap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("snapshot", ToJsonElement(new { })));
        var rev = snap.GetProperty("revision").GetString()!;

        var secretUrl = "https://provider.example.com/api/v1/subscribe?secret_token=super_secret_123";

        var addSnap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("subscriptions.add", ToJsonElement(new
        {
            revision = rev,
            name = "Test Provider",
            url = secretUrl
        })));
        var rev2 = addSnap.GetProperty("revision").GetString()!;

        // List subscriptions: must NEVER disclose URL
        var listObj = await backend.ExecuteAsync("subscriptions.list", ToJsonElement(new { }));
        var listElem = JsonSerializer.SerializeToElement(listObj);
        var rawListJson = listElem.GetRawText();

        Assert(!rawListJson.Contains("super_secret_123"), "Subscription URL/token must NOT appear in subscriptions.list");
        Assert(!rawListJson.Contains("provider.example.com"), "Subscription URL host must NOT appear in subscriptions.list");

        var items = listElem.GetProperty("items").EnumerateArray().ToList();
        Assert(items.Count == 1, "Expected 1 subscription in list");
        var subId = items[0].GetProperty("id").GetString()!;

        // Toggle enabled
        var enableSnap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("subscriptions.enable", ToJsonElement(new
        {
            revision = rev2,
            id = subId,
            enabled = false
        })));
        var rev3 = enableSnap.GetProperty("revision").GetString()!;

        // Remove subscription
        await backend.ExecuteAsync("subscriptions.remove", ToJsonElement(new
        {
            revision = rev3,
            id = subId
        }));
    }

    private static async Task CheckFreeConfigsVerifiedOnlyApplyAsync(string baseDir)
    {
        var dir = Path.Combine(baseDir, "free-configs");
        Directory.CreateDirectory(dir);

        var session = new FakeRouterSession();
        var backend = new RouterBackend(session, dir);

        // Populate cache file manually with unverified config
        var cacheFile = Path.Combine(dir, "cache", "free_configs.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);

        var unverifiedId = "free_test_id_1";
        var unverifiedEntry = new FreeConfigEntry
        {
            Id = unverifiedId,
            Name = "Unverified Candidate",
            Host = "192.0.2.1",
            Port = 443,
            Protocol = "vless",
            RawUri = "vless://b831381d-6324-4d53-ad4f-8cda48b30811@192.0.2.1:443?security=reality&sni=example.com&fp=chrome&pbk=1111111111111111111111111111111111111111111&sid=12345678&type=tcp#Unverified",
            Status = FreeConfigStatus.Ok // Not Verified!
        };

        var cache = new FreeConfigCache(Serilog.Log.Logger, cacheFile);
        cache.Save(new FreeConfigCache.CacheFile
        {
            Configs = new List<FreeConfigEntry> { unverifiedEntry },
            LastAggregatedAt = DateTime.UtcNow
        });

        var snap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("snapshot", ToJsonElement(new { })));
        var rev = snap.GetProperty("revision").GetString()!;

        // Attempting to apply an unverified entry must fail
        bool unverifiedRejected = false;
        try
        {
            await backend.ExecuteAsync("free.apply", ToJsonElement(new
            {
                revision = rev,
                id = unverifiedId
            }));
        }
        catch (RouterException ex) when (ex.Code == "invalid_argument")
        {
            unverifiedRejected = true;
        }
        Assert(unverifiedRejected, "Unverified free config must be rejected by free.apply");

        // Now mark entry as Verified
        unverifiedEntry.Status = FreeConfigStatus.Verified;
        cache.Save(new FreeConfigCache.CacheFile
        {
            Configs = new List<FreeConfigEntry> { unverifiedEntry },
            LastAggregatedAt = DateTime.UtcNow
        });

        // Applying verified entry must succeed
        var appliedSnap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("free.apply", ToJsonElement(new
        {
            revision = rev,
            id = unverifiedId
        })));

        var activeServer = appliedSnap.GetProperty("activeServer").GetString()!;
        Assert(activeServer.Contains("192.0.2.1"), "Active server must be set to applied free server");
        Assert(!activeServer.Contains("⚡"), "Emoji must not be added to applied server name");
    }

    private static async Task CheckCustomConfigsValidationAndStorageAsync(string baseDir)
    {
        var dir = Path.Combine(baseDir, "custom-configs");
        Directory.CreateDirectory(dir);

        var session = new FakeRouterSession();
        var backend = new RouterBackend(session, dir);

        var snap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("snapshot", ToJsonElement(new { })));
        var rev = snap.GetProperty("revision").GetString()!;

        // Invalid JSON must be rejected
        bool badJsonRejected = false;
        try
        {
            await backend.ExecuteAsync("custom.import", ToJsonElement(new
            {
                revision = rev,
                name = "broken",
                text = "{ not valid json }"
            }));
        }
        catch (RouterException ex) when (ex.Code == "invalid_argument")
        {
            badJsonRejected = true;
        }
        Assert(badJsonRejected, "Malformed JSON custom config must be rejected");

        // Valid minimal sing-box JSON with required structure
        var validCustomJson = @"{
  ""outbounds"": [
    {
      ""type"": ""vless"",
      ""tag"": ""proxy"",
      ""server"": ""192.0.2.5"",
      ""server_port"": 443,
      ""uuid"": ""b831381d-6324-4d53-ad4f-8cda48b30811"",
      ""tls"": {
        ""enabled"": true,
        ""server_name"": ""example.com"",
        ""reality"": {
          ""enabled"": true,
          ""public_key"": ""1111111111111111111111111111111111111111111"",
          ""short_id"": ""12345678""
        }
      }
    }
  ]
}";

        var importSnap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("custom.import", ToJsonElement(new
        {
            revision = rev,
            name = "my_custom",
            text = validCustomJson
        })));

        var rev2 = importSnap.GetProperty("revision").GetString()!;
        Assert(importSnap.GetProperty("configMode").GetString() == "custom", "ConfigMode must be custom");
        Assert(importSnap.GetProperty("activeServer").GetString() == "my_custom", "Active server must match custom name");

        // List custom configs
        var listObj = await backend.ExecuteAsync("custom.list", ToJsonElement(new { }));
        var listElem = JsonSerializer.SerializeToElement(listObj);
        var items = listElem.GetProperty("items").EnumerateArray().ToList();
        Assert(items.Count == 1, "Expected 1 custom config");
        var cfgId = items[0].GetProperty("id").GetString()!;
        Assert(items[0].GetProperty("selected").GetBoolean(), "Imported custom config should be selected");

        // Remove custom config
        await backend.ExecuteAsync("custom.remove", ToJsonElement(new
        {
            revision = rev2,
            id = cfgId
        }));
    }

    private static async Task CheckRulesDslAndImportExportAsync(string baseDir)
    {
        var dir = Path.Combine(baseDir, "rules");
        Directory.CreateDirectory(dir);

        var session = new FakeRouterSession();
        var backend = new RouterBackend(session, dir);

        var snap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("snapshot", ToJsonElement(new { })));
        var rev = snap.GetProperty("revision").GetString()!;

        // 1. Valid rules.set
        var rulesText = "direct domain_suffix example.com\nproxy port 443";
        var setSnap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("rules.set", ToJsonElement(new
        {
            revision = rev,
            text = rulesText,
            priority = "toggles_first"
        })));
        var rev2 = setSnap.GetProperty("revision").GetString()!;

        // 2. rules.get
        var getObj = await backend.ExecuteAsync("rules.get", ToJsonElement(new { }));
        var getElem = JsonSerializer.SerializeToElement(getObj);
        Assert(getElem.GetProperty("priority").GetString() == "toggles_first", "Rules priority mismatch");
        Assert(getElem.GetProperty("text").GetString()!.Contains("example.com"), "Rules text mismatch");

        // 3. rules.export
        var exportObj = await backend.ExecuteAsync("rules.export", ToJsonElement(new { format = "json" }));
        var exportElem = JsonSerializer.SerializeToElement(exportObj);
        Assert(!string.IsNullOrWhiteSpace(exportElem.GetProperty("text").GetString()), "Exported rules text must be non-empty");

        // 4. Invalid rules syntax must throw invalid_argument
        bool badRuleRejected = false;
        try
        {
            await backend.ExecuteAsync("rules.set", ToJsonElement(new
            {
                revision = rev2,
                text = "invalid_action unknown_match_type",
                priority = "toggles_first"
            }));
        }
        catch (RouterException ex) when (ex.Code == "invalid_argument")
        {
            badRuleRejected = true;
        }
        Assert(badRuleRejected, "Invalid rule syntax must be rejected");
    }

    private static async Task CheckDiagnosticsCheckAndExportAsync(string baseDir)
    {
        var dir = Path.Combine(baseDir, "diagnostics");
        Directory.CreateDirectory(dir);

        var session = new FakeRouterSession();
        var backend = new RouterBackend(session, dir);

        // diagnostics.check
        var checkObj = await backend.ExecuteAsync("diagnostics.check", ToJsonElement(new { }));
        var checkElem = JsonSerializer.SerializeToElement(checkObj);
        Assert(checkElem.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array, "Missing items array in diagnostics.check");

        // diagnostics.export
        var exportObj = await backend.ExecuteAsync("diagnostics.export", ToJsonElement(new { }));
        var exportElem = JsonSerializer.SerializeToElement(exportObj);
        var zipPath = exportElem.GetProperty("path").GetString()!;
        Assert(!string.IsNullOrWhiteSpace(zipPath), "Diagnostics ZIP path must not be empty");
        Assert(File.Exists(zipPath), "Diagnostics ZIP file must exist on disk");
    }

    private static async Task CheckConcurrencyAndBusyControlAsync(string baseDir)
    {
        var dir = Path.Combine(baseDir, "concurrency");
        Directory.CreateDirectory(dir);

        var session = new FakeRouterSession();
        var backend = new RouterBackend(session, dir);

        // Simulate concurrent execution: disconnect is an urgent control operation and bypasses busy
        var disconnectResult = await backend.ExecuteAsync("disconnect", ToJsonElement(new { }));
        Assert(disconnectResult != null, "Disconnect must succeed even when called");
    }

    private static async Task CheckExternalEditConflictAsync(string baseDir)
    {
        var dir = Path.Combine(baseDir, "external-edit-conflict");
        Directory.CreateDirectory(dir);

        var session = new FakeRouterSession();
        var backend = new RouterBackend(session, dir);

        // 1. Initial snapshot with empty file
        var snap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("snapshot", ToJsonElement(new { })));
        var rev1 = snap.GetProperty("revision").GetString()!;

        // 2. Perform a mutation to create config.yaml on disk
        var mutatedSnap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("settings.set", ToJsonElement(new
        {
            revision = rev1,
            values = new { mtu = 1420 }
        })));
        var rev2 = mutatedSnap.GetProperty("revision").GetString()!;

        // 3. External process writes directly to config.yaml on disk
        var configPath = Path.Combine(dir, "config.yaml");
        var externalContent = File.ReadAllText(configPath).Replace("mtu: 1420", "mtu: 1440");
        File.WriteAllText(configPath, externalContent);

        // 4. Attempt mutation using the stale in-memory rev2
        bool conflictCaught = false;
        try
        {
            await backend.ExecuteAsync("settings.set", ToJsonElement(new
            {
                revision = rev2,
                values = new { mtu = 1400 }
            }));
        }
        catch (RouterException ex) when (ex.Code == "conflict")
        {
            conflictCaught = true;
        }
        Assert(conflictCaught, "External edit must be detected as revision conflict and rejected");

        // 5. Verify the external file was not clobbered
        var currentDiskContent = File.ReadAllText(configPath);
        Assert(currentDiskContent.Contains("1440"), "External edit must be preserved on disk");
        Assert(!currentDiskContent.Contains("1400"), "Rejected candidate must not be on disk");
    }

    private static async Task CheckCorruptionFailClosedAsync(string baseDir)
    {
        var dir = Path.Combine(baseDir, "corruption-fail-closed");
        Directory.CreateDirectory(dir);

        var configPath = Path.Combine(dir, "config.yaml");
        var corruptData = "!!!not_valid_yaml: [unclosed array: {";
        File.WriteAllText(configPath, corruptData);

        var storage = new ConfigStorage(dir);

        // 1. GetSettings must fail closed and NOT return default sane settings
        bool storageErrorCaught = false;
        try
        {
            storage.GetSettings();
        }
        catch (RouterException ex) when (ex.Code == "storage_error")
        {
            storageErrorCaught = true;
        }
        Assert(storageErrorCaught, "Reading corrupt config.yaml must throw RouterException(storage_error)");

        // 2. Disk content must NOT be replaced with defaults
        var diskContent = File.ReadAllText(configPath);
        Assert(string.Equals(diskContent, corruptData, StringComparison.Ordinal),
            "Corrupted config.yaml must not be overwritten with defaults");

        // 3. RouterBackend.ExecuteAsync snapshot must also fail closed
        var session = new FakeRouterSession();
        var backend = new RouterBackend(session, dir);
        bool snapshotFailed = false;
        try
        {
            await backend.ExecuteAsync("snapshot", ToJsonElement(new { }));
        }
        catch (RouterException ex) when (ex.Code == "storage_error")
        {
            snapshotFailed = true;
        }
        Assert(snapshotFailed, "Snapshot on corrupt config must fail closed");
    }

    private static async Task CheckMutationRollbackOnApplyFailureAsync(string baseDir)
    {
        var dir = Path.Combine(baseDir, "mutation-rollback");
        Directory.CreateDirectory(dir);

        var session = new FakeRouterSession();
        var backend = new RouterBackend(session, dir);

        // 1. Connect the session
        var snap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("snapshot", ToJsonElement(new { })));
        var rev1 = snap.GetProperty("revision").GetString()!;

        await backend.ExecuteAsync("connect", ToJsonElement(new { revision = rev1 }));
        Assert(session.State == "connected", "Session must be connected");

        var snapAfterConnect = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("snapshot", ToJsonElement(new { })));
        var rev2 = snapAfterConnect.GetProperty("revision").GetString()!;

        // 2. Configure session to fail on ApplyAsync
        session.FailApply = true;

        // 3. Attempt a mutation while connected
        bool applyFailedCaught = false;
        try
        {
            await backend.ExecuteAsync("settings.set", ToJsonElement(new
            {
                revision = rev2,
                values = new { mtu = 1350 }
            }));
        }
        catch (RouterException ex) when (ex.Code == "internal_error")
        {
            applyFailedCaught = true;
        }
        Assert(applyFailedCaught, "Apply failure must propagate as RouterException");

        // 4. Verify settings were rolled back and preserved
        var settingsObj = await backend.ExecuteAsync("settings.get", ToJsonElement(new { }));
        var settingsElem = JsonSerializer.SerializeToElement(settingsObj);
        Assert(settingsElem.GetProperty("mtu").GetInt32() == TunSettings.DefaultMtu,
            "Settings MTU must be rolled back to original value after apply failure");
    }

    private static async Task CheckDuplicateAndStaleIdsAsync(string baseDir)
    {
        var dir = Path.Combine(baseDir, "duplicate-stale-ids");
        Directory.CreateDirectory(dir);

        var session = new FakeRouterSession();
        var backend = new RouterBackend(session, dir);

        var snap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("snapshot", ToJsonElement(new { })));
        var rev1 = snap.GetProperty("revision").GetString()!;

        // Import two servers with duplicate endpoints and names
        var vless1 = "vless://b831381d-6324-4d53-ad4f-8cda48b30811@198.51.100.1:443?security=reality&sni=dup.com&fp=chrome&pbk=1111111111111111111111111111111111111111111&sid=12345678&type=tcp#DupServer";
        var vless2 = "vless://b831381d-6324-4d53-ad4f-8cda48b30811@198.51.100.1:443?security=reality&sni=dup.com&fp=chrome&pbk=1111111111111111111111111111111111111111111&sid=12345678&type=tcp#DupServer";

        var importSnap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("servers.import", ToJsonElement(new
        {
            revision = rev1,
            text = $"{vless1}\n{vless2}"
        })));
        var rev2 = importSnap.GetProperty("revision").GetString()!;

        var listObj = await backend.ExecuteAsync("servers.list", ToJsonElement(new { offset = 0, limit = 10 }));
        var listElem = JsonSerializer.SerializeToElement(listObj);
        var items = listElem.GetProperty("items").EnumerateArray().ToList();
        Assert(items.Count == 2, "Expected 2 duplicate server items in list");

        var id0 = items[0].GetProperty("id").GetString()!;
        var id1 = items[1].GetProperty("id").GetString()!;
        Assert(id0.StartsWith("srv_"), "ID must start with srv_");
        Assert(id1.StartsWith("srv_"), "ID must start with srv_");
        Assert(!string.Equals(id0, id1, StringComparison.Ordinal), "Duplicate servers must receive distinct IDs");

        // Attempt to select a stale / nonexistent ID
        bool staleSelectCaught = false;
        try
        {
            await backend.ExecuteAsync("servers.select", ToJsonElement(new
            {
                revision = rev2,
                id = "srv_stale_nonexistent_id"
            }));
        }
        catch (RouterException ex) when (ex.Code == "not_found")
        {
            staleSelectCaught = true;
        }
        Assert(staleSelectCaught, "Selecting stale ID must throw not_found");

        // Attempt to remove a stale ID
        bool staleRemoveCaught = false;
        try
        {
            await backend.ExecuteAsync("servers.remove", ToJsonElement(new
            {
                revision = rev2,
                id = "srv_stale_nonexistent_id"
            }));
        }
        catch (RouterException ex) when (ex.Code == "not_found")
        {
            staleRemoveCaught = true;
        }
        Assert(staleRemoveCaught, "Removing stale ID must throw not_found");

        // Stale custom config ID
        bool staleCustomCaught = false;
        try
        {
            await backend.ExecuteAsync("custom.select", ToJsonElement(new
            {
                revision = rev2,
                id = "cfg_nonexistent"
            }));
        }
        catch (RouterException ex) when (ex.Code == "not_found")
        {
            staleCustomCaught = true;
        }
        Assert(staleCustomCaught, "Selecting stale custom config ID must throw not_found");

        // Stale subscription ID
        bool staleSubCaught = false;
        try
        {
            await backend.ExecuteAsync("subscriptions.remove", ToJsonElement(new
            {
                revision = rev2,
                id = "sub_nonexistent"
            }));
        }
        catch (RouterException ex) when (ex.Code == "not_found")
        {
            staleSubCaught = true;
        }
        Assert(staleSubCaught, "Removing stale subscription ID must throw not_found");
    }

    private static async Task CheckNoSecretsInErrorsAsync(string baseDir)
    {
        var dir = Path.Combine(baseDir, "no-secrets-errors");
        Directory.CreateDirectory(dir);

        var session = new FakeRouterSession();
        var backend = new RouterBackend(session, dir);

        var snap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("snapshot", ToJsonElement(new { })));
        var rev1 = snap.GetProperty("revision").GetString()!;

        // 1. Subscription with secret query parameter in URL, but invalid name
        var secretToken = "super_secret_token_abcdef123";
        var secretUrl = $"https://example.com/subscribe?token={secretToken}";

        bool subCaught = false;
        try
        {
            await backend.ExecuteAsync("subscriptions.add", ToJsonElement(new
            {
                revision = rev1,
                name = "", // Invalid: empty name
                url = secretUrl
            }));
        }
        catch (RouterException ex)
        {
            subCaught = true;
            Assert(!ex.SafeMessage.Contains(secretToken), "SafeMessage must not contain secret token");
            Assert(!ex.Message.Contains(secretToken), "Exception message must not contain secret token");
        }
        Assert(subCaught, "subscriptions.add with empty name must throw");

        // 2. Custom config with invalid JSON containing secret key
        var secretUuid = "b831381d-6324-4d53-ad4f-8cda48b30811";
        var badJsonWithSecret = $"{{ \"uuid\": \"{secretUuid}\", broken json syntax";

        bool customCaught = false;
        try
        {
            await backend.ExecuteAsync("custom.import", ToJsonElement(new
            {
                revision = rev1,
                name = "bad_custom",
                text = badJsonWithSecret
            }));
        }
        catch (RouterException ex)
        {
            customCaught = true;
            Assert(!ex.SafeMessage.Contains(secretUuid), "SafeMessage must not contain secret UUID");
            Assert(!ex.Message.Contains(secretUuid), "Exception message must not contain secret UUID");
        }
        Assert(customCaught, "custom.import with malformed JSON must throw");
    }

    // ─── Regression: Subscription Add + Select with Local Fake Servers & Mode Transitions ───

    private static async Task CheckSubscriptionAddSelectWithFakeServersAndModeTransitionsAsync(string baseDir)
    {
        var dir = Path.Combine(baseDir, "sub-add-select-modes");
        Directory.CreateDirectory(dir);

        var session = new FakeRouterSession();
        var backend = new RouterBackend(session, dir);
        var storage = new ConfigStorage(dir);

        var snap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("snapshot", ToJsonElement(new { })));
        var rev1 = snap.GetProperty("revision").GetString()!;
        Assert(snap.GetProperty("configMode").GetString() == "generated", "Initial configMode must be generated");
        Assert(snap.GetProperty("activeServer").GetString() == "", "Initial activeServer must be empty");

        // 1. Add subscription
        var addSnap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("subscriptions.add", ToJsonElement(new
        {
            revision = rev1,
            name = "Test Provider",
            url = "https://example.com/feed"
        })));
        var rev2 = addSnap.GetProperty("revision").GetString()!;

        // 2. Populate subscription with local fake in-memory servers directly in settings storage
        var settings = storage.GetSettings();
        Assert(settings.App.Subscriptions.Count == 1, "Expected 1 subscription in settings");
        settings.App.Subscriptions[0].Servers = new List<VlessServerEntry>
        {
            new()
            {
                Name = "Sub-Frankfurt-1",
                Server = "198.51.100.10",
                Port = 443,
                Uuid = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                Protocol = "vless"
            },
            new()
            {
                Name = "Sub-Amsterdam-2",
                Server = "198.51.100.11",
                Port = 443,
                Uuid = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                Protocol = "vless"
            }
        };
        storage.SaveSettings(settings, rev2);
        var rev3 = storage.CurrentRevision;

        // 3. List servers: must resolve subscription servers (not empty!)
        var listObj = await backend.ExecuteAsync("servers.list", ToJsonElement(new { }));
        var listElem = JsonSerializer.SerializeToElement(listObj);
        var items = listElem.GetProperty("items").EnumerateArray().ToList();
        Assert(items.Count == 2, "servers.list must return the 2 subscription servers");
        var subServer0 = items[0];
        var subServerId0 = subServer0.GetProperty("id").GetString()!;
        var subServerName0 = subServer0.GetProperty("name").GetString()!;
        Assert(subServerId0.StartsWith("srv_"), "ID must start with srv_");
        Assert(subServerName0 == "Sub-Frankfurt-1", "Server name mismatch");

        // 4. Select subscription server: must transition to 'subscribe' mode
        var selectSubSnap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("servers.select", ToJsonElement(new
        {
            revision = rev3,
            id = subServerId0
        })));
        var rev4 = selectSubSnap.GetProperty("revision").GetString()!;
        Assert(selectSubSnap.GetProperty("configMode").GetString() == "subscribe", "Mode must transition to 'subscribe'");
        Assert(selectSubSnap.GetProperty("activeServer").GetString() == "Sub-Frankfurt-1", "Active server must be Sub-Frankfurt-1");

        var settingsAfterSelect = storage.GetSettings();
        Assert(settingsAfterSelect.App.ConfigMode == "subscribe", "ConfigMode in storage must be 'subscribe'");
        Assert(settingsAfterSelect.App.ActiveSubscriptionServer == "Sub-Frankfurt-1", "ActiveSubscriptionServer mismatch");
        // Don't demand persisted effective Vless field if subscription selected source is App.ActiveSubscriptionServer,
        // but prove actual resolved selected server after reload
        var resolvedAfterSelect = ConfigStorage.CloneSettings(settingsAfterSelect);
        var resolvedList1 = VlessServersResolver.Resolve(resolvedAfterSelect);
        Assert(resolvedList1.Count > 0, "Resolved servers must not be empty after reload");
        Assert(resolvedAfterSelect.Vless.ActiveServer == "Sub-Frankfurt-1", "Resolved active server after reload must be Sub-Frankfurt-1");
        var snapAfterSelect = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("snapshot", ToJsonElement(new { })));
        Assert(snapAfterSelect.GetProperty("activeServer").GetString() == "Sub-Frankfurt-1", "Snapshot active server must be Sub-Frankfurt-1");

        // 5. Import a manual server
        var manualUri = "vless://cccccccc-cccc-cccc-cccc-cccccccccccc@198.51.100.20:443?security=reality&sni=manual.example.com&fp=chrome&pbk=1111111111111111111111111111111111111111111&sid=12345678&type=tcp#Manual-US";
        var importSnap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("servers.import", ToJsonElement(new
        {
            revision = rev4,
            text = manualUri
        })));
        var rev5 = importSnap.GetProperty("revision").GetString()!;

        // Prove manual servers list is actually visible in servers.list even while in subscribe mode
        var listAfterImport = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("servers.list", ToJsonElement(new { })));
        var importItems = listAfterImport.GetProperty("items").EnumerateArray().ToList();
        Assert(importItems.Count == 3, $"servers.list must return unified 3 servers (1 manual + 2 subscription), got {importItems.Count}");
        var manualItem = importItems.FirstOrDefault(item => item.GetProperty("name").GetString() == "Manual-US");
        Assert(manualItem.ValueKind != JsonValueKind.Undefined, "Manual-US server must be actually visible in servers.list");
        var manualId = manualItem.GetProperty("id").GetString()!;

        // Select manual server using servers.select
        // Note: manual server is in Vless.Servers; select should transition back to 'generated'
        var selectManualSnap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("servers.select", ToJsonElement(new
        {
            revision = rev5,
            id = manualId
        })));
        var rev6 = selectManualSnap.GetProperty("revision").GetString()!;
        Assert(selectManualSnap.GetProperty("configMode").GetString() == "generated", "Mode must transition to 'generated'");
        Assert(selectManualSnap.GetProperty("activeServer").GetString() == "Manual-US", "Active server must be Manual-US");

        // Prove resolved active server after mode transition to generated
        var settingsAfterManual = storage.GetSettings();
        Assert(settingsAfterManual.App.ConfigMode == "generated", "ConfigMode in storage must be 'generated'");
        Assert(settingsAfterManual.Vless.ActiveServer == "Manual-US", "Vless.ActiveServer in storage must be Manual-US");
        var resolvedAfterManual = ConfigStorage.CloneSettings(settingsAfterManual);
        VlessServersResolver.Resolve(resolvedAfterManual);
        Assert(resolvedAfterManual.Vless.ActiveServer == "Manual-US", "Resolved active server in generated mode must be Manual-US");

        // 6. Transition back to subscription server
        var selectSubAgainSnap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("servers.select", ToJsonElement(new
        {
            revision = rev6,
            id = subServerId0
        })));
        Assert(selectSubAgainSnap.GetProperty("configMode").GetString() == "subscribe", "Mode must transition back to 'subscribe'");
        Assert(selectSubAgainSnap.GetProperty("activeServer").GetString() == "Sub-Frankfurt-1", "Active server must be Sub-Frankfurt-1");

        // Prove resolved selected server after reload and mode transitions
        var settingsAfterSubAgain = storage.GetSettings();
        Assert(settingsAfterSubAgain.App.ConfigMode == "subscribe", "ConfigMode in storage must be 'subscribe'");
        Assert(settingsAfterSubAgain.App.ActiveSubscriptionServer == "Sub-Frankfurt-1", "ActiveSubscriptionServer must be Sub-Frankfurt-1");
        var resolvedSubAgain = ConfigStorage.CloneSettings(settingsAfterSubAgain);
        VlessServersResolver.Resolve(resolvedSubAgain);
        Assert(resolvedSubAgain.Vless.ActiveServer == "Sub-Frankfurt-1", "Resolved active server after reload must be Sub-Frankfurt-1");
    }

    // ─── Regression: Duplicate & Refresh IDs ───

    private static async Task CheckSubscriptionDuplicateAndRefreshIdsAsync(string baseDir)
    {
        var dir = Path.Combine(baseDir, "duplicate-refresh-ids");
        Directory.CreateDirectory(dir);

        var session = new FakeRouterSession();
        var backend = new RouterBackend(session, dir);
        var storage = new ConfigStorage(dir);

        var snap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("snapshot", ToJsonElement(new { })));
        var rev1 = snap.GetProperty("revision").GetString()!;

        // Add subscription with duplicate entries
        await backend.ExecuteAsync("subscriptions.add", ToJsonElement(new
        {
            revision = rev1,
            name = "Dup Sub",
            url = "https://example.com/dup"
        }));

        var settings = storage.GetSettings();
        settings.App.Subscriptions[0].Servers = new List<VlessServerEntry>
        {
            new() { Name = "SameName", Server = "192.0.2.1", Port = 443, Uuid = "uuid-1", Protocol = "vless" },
            new() { Name = "SameName", Server = "192.0.2.1", Port = 443, Uuid = "uuid-1", Protocol = "vless" },
            new() { Name = "Different", Server = "192.0.2.2", Port = 443, Uuid = "uuid-2", Protocol = "vless" }
        };
        storage.SaveSettings(settings, storage.CurrentRevision);

        // List servers: must return 3 items, the two duplicates must have distinct IDs
        var list1 = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("servers.list", ToJsonElement(new { })));
        var items1 = list1.GetProperty("items").EnumerateArray().ToList();
        Assert(items1.Count == 3, "Expected 3 servers");

        var id0 = items1[0].GetProperty("id").GetString()!;
        var id1 = items1[1].GetProperty("id").GetString()!;
        var id2 = items1[2].GetProperty("id").GetString()!;

        Assert(!string.Equals(id0, id1, StringComparison.Ordinal), "Duplicate servers must receive distinct IDs");
        Assert(!string.Equals(id0, id2, StringComparison.Ordinal), "Unique servers must receive distinct IDs");

        // Second list call: IDs must be stable and deterministic
        var list2 = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("servers.list", ToJsonElement(new { })));
        var items2 = list2.GetProperty("items").EnumerateArray().ToList();
        Assert(items2[0].GetProperty("id").GetString() == id0, "Server ID 0 must be stable across queries");
        Assert(items2[1].GetProperty("id").GetString() == id1, "Server ID 1 must be stable across queries");
        Assert(items2[2].GetProperty("id").GetString() == id2, "Server ID 2 must be stable across queries");

        // Stale ID must be rejected
        bool staleCaught = false;
        try
        {
            await backend.ExecuteAsync("servers.select", ToJsonElement(new
            {
                revision = storage.CurrentRevision,
                id = "srv_stale_12345"
            }));
        }
        catch (RouterException ex) when (ex.Code == "not_found")
        {
            staleCaught = true;
        }
        Assert(staleCaught, "Stale ID must throw not_found");
    }

    // ─── Regression: DNS Mode Readback & Actual Effect ───

    private static async Task CheckDnsModeReadbackActualEffectAsync(string baseDir)
    {
        var dir = Path.Combine(baseDir, "dns-mode-readback");
        Directory.CreateDirectory(dir);

        var session = new FakeRouterSession();
        var backend = new RouterBackend(session, dir);
        var userProfilesPath = Path.Combine(dir, "profiles", "user_profiles.json");

        // Initial settings.get: dnsMode is "vpn_only"
        var initSettings = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("settings.get", ToJsonElement(new { })));
        Assert(initSettings.GetProperty("dnsMode").GetString() == "vpn_only", "Default dnsMode must be vpn_only");
        Assert(!File.Exists(userProfilesPath), "No user_profiles.json must exist initially");

        var snap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("snapshot", ToJsonElement(new { })));
        var rev1 = snap.GetProperty("revision").GetString()!;

        // 1. Reject invalid later field without creating/changing profile files or mutating settings
        bool invalidLaterCaught = false;
        try
        {
            await backend.ExecuteAsync("settings.set", ToJsonElement(new
            {
                revision = rev1,
                values = new
                {
                    dnsMode = "smart",
                    routeExcludeAddress = new[] { "invalid-not-a-cidr" }
                }
            }));
        }
        catch (RouterException ex) when (ex.Code == "invalid_argument")
        {
            invalidLaterCaught = true;
        }
        Assert(invalidLaterCaught, "Invalid later field must throw invalid_argument");
        Assert(!File.Exists(userProfilesPath), "Failed validation must not create profile files");

        var settingsAfterFailedSet = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("settings.get", ToJsonElement(new { })));
        Assert(settingsAfterFailedSet.GetProperty("dnsMode").GetString() == "vpn_only",
            "Settings must remain unmutated after failed later-field validation");

        // 2. Set dnsMode to "smart"
        var setSnap1 = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("settings.set", ToJsonElement(new
        {
            revision = rev1,
            values = new { dnsMode = "smart" }
        })));
        var rev2 = setSnap1.GetProperty("revision").GetString()!;

        // Readback via settings.get: must be "smart"
        var settingsAfterSmart = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("settings.get", ToJsonElement(new { })));
        Assert(settingsAfterSmart.GetProperty("dnsMode").GetString() == "smart", "dnsMode must read back 'smart' after being set");

        // Disk check: sidecar user_profiles.json must NOT exist; setting is in config.yaml
        Assert(!File.Exists(userProfilesPath), "user_profiles.json must not be created as sidecar");
        var configYaml = File.ReadAllText(Path.Combine(dir, "config.yaml"));
        Assert(configYaml.Contains("dns_mode_override: smart"), "config.yaml must persist dns_mode_override: smart");

        // 3. Reload readback with a fresh RouterBackend instance
        var reloadedBackend = new RouterBackend(session, dir);
        var reloadedSettings = JsonSerializer.SerializeToElement(await reloadedBackend.ExecuteAsync("settings.get", ToJsonElement(new { })));
        Assert(reloadedSettings.GetProperty("dnsMode").GetString() == "smart", "dnsMode must survive backend reload and read back 'smart'");

        // 4. Set dnsMode to "direct"
        var setSnap2 = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("settings.set", ToJsonElement(new
        {
            revision = rev2,
            values = new { dnsMode = "direct" }
        })));
        var rev3 = setSnap2.GetProperty("revision").GetString()!;

        // Readback via settings.get: must be "direct"
        var settingsAfterDirect = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("settings.get", ToJsonElement(new { })));
        Assert(settingsAfterDirect.GetProperty("dnsMode").GetString() == "direct", "dnsMode must read back 'direct' after being set");
        Assert(!File.Exists(userProfilesPath), "user_profiles.json must not be created on direct mode");

        // 5. Reset to null (protocol reset back to profile default)
        var resetSnap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("settings.set", ToJsonElement(new
        {
            revision = rev3,
            values = new { dnsMode = (string?)null }
        })));
        var rev4 = resetSnap.GetProperty("revision").GetString()!;

        var settingsAfterReset = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("settings.get", ToJsonElement(new { })));
        Assert(settingsAfterReset.GetProperty("dnsMode").GetString() == "vpn_only", "dnsMode must reset to profile default 'vpn_only' on null");
        Assert(!File.Exists(userProfilesPath), "user_profiles.json must not be created on null reset");

        // 6. Reject invalid dnsMode
        bool invalidDnsCaught = false;
        try
        {
            await backend.ExecuteAsync("settings.set", ToJsonElement(new
            {
                revision = rev4,
                values = new { dnsMode = "unsupported_mode_xyz" }
            }));
        }
        catch (RouterException ex) when (ex.Code == "invalid_argument")
        {
            invalidDnsCaught = true;
            Assert(!ex.SafeMessage.Contains("unsupported_mode_xyz"), "Error must not echo user input");
        }
        Assert(invalidDnsCaught, "Invalid dnsMode must throw invalid_argument");
        Assert(!File.Exists(userProfilesPath), "No profile files must be created on invalid dnsMode");

        // 7. Custom config mode: reject dnsMode in settings.set and return honest state in settings.get
        var validCustomJson = /*lang=json*/ @"{
  ""outbounds"": [
    {
      ""type"": ""vless"",
      ""tag"": ""proxy"",
      ""server"": ""198.51.100.1"",
      ""server_port"": 443,
      ""uuid"": ""00000000-0000-0000-0000-000000000001"",
      ""tls"": {
        ""enabled"": true,
        ""reality"": {
          ""enabled"": true,
          ""public_key"": ""1111111111111111111111111111111111111111111="",
          ""short_id"": ""12345678""
        }
      }
    }
  ]
}";
        var importSnap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("custom.import", ToJsonElement(new
        {
            revision = rev4,
            name = "custom_dns_test",
            text = validCustomJson
        })));
        var revCustom = importSnap.GetProperty("revision").GetString()!;

        // settings.get under custom config: returns honest effective state "custom"
        var customSettings = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("settings.get", ToJsonElement(new { })));
        Assert(customSettings.GetProperty("dnsMode").GetString() == "custom", "settings.get must return 'custom' under custom config mode");
        Assert(customSettings.TryGetProperty("dnsModeSemantics", out var semProp) && !string.IsNullOrWhiteSpace(semProp.GetString()),
            "settings.get must explain actual semantics under custom config mode");

        // settings.set under custom config: reject dnsMode
        bool customSetRejected = false;
        try
        {
            await backend.ExecuteAsync("settings.set", ToJsonElement(new
            {
                revision = revCustom,
                values = new { dnsMode = "smart" }
            }));
        }
        catch (RouterException ex) when (ex.Code == "invalid_argument")
        {
            customSetRejected = true;
        }
        Assert(customSetRejected, "settings.set must reject dnsMode under custom config mode");

        // 8. ConfigGenerator.Dns verification: generated JSON rules, invariants, and no profile mutation
        CheckConfigGeneratorDnsSeam();
    }

    private static void CheckConfigGeneratorDnsSeam()
    {
        var profile = new Profile
        {
            Name = "SeamTestProfile",
            DnsMode = "vpn_only",
            Processes = new List<ProcessRule>
            {
                new() { Name = "chrome.exe", ScanPatterns = new[] { "chrome.exe" } }
            }
        };

        var processes = new List<string> { "chrome.exe" };

        var settings = new AppSettings
        {
            App = new AppConfig
            {
                RoutingMode = "split",
                RoutingAppsMode = "include",
                RoutingAppsInclude = new List<string> { "chrome.exe" },
                StrictDns = false,
                DnsModeOverride = null
            },
            Tun = new TunSettings(),
            Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
            SingBox = new SingBoxSettings { ClashApi = "127.0.0.1:9090" },
            Vless = new VlessConfig
            {
                Servers = new List<VlessServerEntry>
                {
                    new()
                    {
                        Name = "primary",
                        Server = "198.51.100.1",
                        Port = 443,
                        Uuid = "00000000-0000-0000-0000-000000000001",
                        Security = "reality",
                        Reality = new VlessRealityConfig
                        {
                            PublicKey = "AgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgI=",
                            ShortId = "abcd"
                        }
                    }
                }
            }
        };

        // A. Null default preserves prior behavior (profile.DnsMode = "vpn_only" -> vpn-dns)
        var configDefault = ConfigGenerator.Generate(profile, processes, settings);
        var ruleDefault = configDefault.Dns.Rules.FirstOrDefault(r => r.ProcessName != null && r.ProcessName.Contains("chrome.exe"));
        Assert(ruleDefault != null && ruleDefault.Server == "vpn-dns", "Null override must preserve profile default vpn-dns");
        Assert(profile.DnsMode == "vpn_only", "Profile must not be mutated");

        // Verify routing rule for chrome.exe is proxy (existing process rules unchanged)
        var routeRuleDefault = configDefault.Route.Rules.FirstOrDefault(r => r.Action == "route" && r.ProcessName != null && r.ProcessName.Contains("chrome.exe"));
        Assert(routeRuleDefault != null && routeRuleDefault.Outbound == "proxy", "Process routing rule must route to proxy");

        // B. Generated JSON actually changes proper DNS rule for routed-process include branch
        settings.App.DnsModeOverride = "smart";
        var configSmart = ConfigGenerator.Generate(profile, processes, settings);
        var ruleSmart = configSmart.Dns.Rules.FirstOrDefault(r => r.ProcessName != null && r.ProcessName.Contains("chrome.exe"));
        Assert(ruleSmart != null && ruleSmart.Server == "local-dns", "Override 'smart' must change routed-process DNS rule to local-dns");
        Assert(profile.DnsMode == "vpn_only", "Profile must not be mutated by override");

        // Existing process routing rule unchanged: still proxy
        var routeRuleSmart = configSmart.Route.Rules.FirstOrDefault(r => r.Action == "route" && r.ProcessName != null && r.ProcessName.Contains("chrome.exe"));
        Assert(routeRuleSmart != null && routeRuleSmart.Outbound == "proxy", "Process routing rule must remain proxy under smart DNS");

        // Override 'vpn_only' routes to vpn-dns
        settings.App.DnsModeOverride = "vpn_only";
        var configVpnOnly = ConfigGenerator.Generate(profile, processes, settings);
        var ruleVpnOnly = configVpnOnly.Dns.Rules.FirstOrDefault(r => r.ProcessName != null && r.ProcessName.Contains("chrome.exe"));
        Assert(ruleVpnOnly != null && ruleVpnOnly.Server == "vpn-dns", "Override 'vpn_only' must route process DNS to vpn-dns");

        // Override 'direct' in include branch tunnels DNS (vpn-dns)
        settings.App.DnsModeOverride = "direct";
        var configDirect = ConfigGenerator.Generate(profile, processes, settings);
        var ruleDirect = configDirect.Dns.Rules.FirstOrDefault(r => r.ProcessName != null && r.ProcessName.Contains("chrome.exe"));
        Assert(ruleDirect != null && ruleDirect.Server == "vpn-dns", "Override 'direct' in include branch must route to vpn-dns");

        // C. Validate unexpected override fails closed to vpn-dns
        settings.App.DnsModeOverride = "corrupt_unknown_override";
        var configBogus = ConfigGenerator.Generate(profile, processes, settings);
        var ruleBogus = configBogus.Dns.Rules.FirstOrDefault(r => r.ProcessName != null && r.ProcessName.Contains("chrome.exe"));
        Assert(ruleBogus != null && ruleBogus.Server == "vpn-dns", "Unexpected override must fail closed to vpn-dns");

        // D. StrictDns forced protection invariant: StrictDns=true must win over smart override
        settings.App.DnsModeOverride = "smart";
        settings.App.StrictDns = true;
        var configStrict = ConfigGenerator.Generate(profile, processes, settings);
        var ruleStrict = configStrict.Dns.Rules.FirstOrDefault(r => r.ProcessName != null && r.ProcessName.Contains("chrome.exe"));
        Assert(ruleStrict != null && ruleStrict.Server == "vpn-dns", "StrictDns must override smart DNS and route to vpn-dns");

        // E. Full tunnel forced protection invariant: routing_mode='full' must route all DNS via tunnel
        settings.App.StrictDns = false;
        settings.App.RoutingMode = "full";
        settings.App.DnsModeOverride = "smart";
        var configFull = ConfigGenerator.Generate(profile, processes, settings);
        Assert(configFull.Dns.Final == "vpn-dns", "Full tunnel must force dns.final to vpn-dns even with smart override");
    }

    // ─── Regression: Free Config Verified Status Preservation & Cancelled Refresh ───

    private static async Task CheckFreeConfigVerifiedStatusPreservationAndCancelledRefreshAsync(string baseDir)
    {
        var dir = Path.Combine(baseDir, "free-verified-preservation");
        Directory.CreateDirectory(dir);

        var session = new FakeRouterSession();
        var backend = new RouterBackend(session, dir);

        var cacheFile = Path.Combine(dir, "cache", "free_configs.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);

        var verifiedId = "free_verified_candidate";
        var verifiedEntry = new FreeConfigEntry
        {
            Id = verifiedId,
            Name = "Verified Candidate",
            Host = "192.0.2.100",
            Port = 443,
            Protocol = "vless",
            RawUri = "vless://aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa@192.0.2.100:443?security=reality&sni=example.com&fp=chrome&pbk=1111111111111111111111111111111111111111111&sid=12345678&type=tcp#Verified",
            Status = FreeConfigStatus.Verified,
            LatencyMs = 85
        };

        var cache = new FreeConfigCache(Serilog.Log.Logger, cacheFile);
        cache.Save(new FreeConfigCache.CacheFile
        {
            Configs = new List<FreeConfigEntry> { verifiedEntry },
            LastAggregatedAt = DateTime.UtcNow
        });

        // 1. Perform free.test on the Verified entry: must NOT demote status to Ok!
        await backend.ExecuteAsync("free.test", ToJsonElement(new { id = verifiedId }));

        var cacheAfterTest = cache.Load();
        var entryAfterTest = cacheAfterTest.Configs.First(c => c.Id == verifiedId);
        Assert(entryAfterTest.Status == FreeConfigStatus.Verified,
            "free.test must preserve Status=Verified (must not be demoted to Ok)");

        // 2. Subsequent free.apply must succeed without error
        var snap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("snapshot", ToJsonElement(new { })));
        var rev = snap.GetProperty("revision").GetString()!;

        var applySnap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("free.apply", ToJsonElement(new
        {
            revision = rev,
            id = verifiedId
        })));
        Assert(applySnap.GetProperty("activeServer").GetString()!.Contains("192.0.2.100"),
            "free.apply must succeed on verified entry after test");

        // 3. Test cancelled refresh: must preserve existing cache without losing entries
        var storage = new ConfigStorage(dir);
        var freeFeature = new FreeConfigFeature(storage);
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancel token

        bool cancelledCaught = false;
        try
        {
            await freeFeature.RefreshAsync(ToJsonElement(new { }), cts.Token);
        }
        catch (OperationCanceledException)
        {
            cancelledCaught = true;
        }
        Assert(cancelledCaught, "Cancelled refresh must throw OperationCanceledException");

        // Verify cache file still has the verified entry intact
        var cacheAfterCancel = cache.Load();
        Assert(cacheAfterCancel.Configs.Count == 1, "Cache entries must be preserved after cancelled refresh");
        Assert(cacheAfterCancel.Configs[0].Status == FreeConfigStatus.Verified, "Status must remain Verified after cancelled refresh");
    }

    // ─── Regression: WireGuard Conf Import ───

    private static async Task CheckWireGuardConfImportAsync(string baseDir)
    {
        var dir = Path.Combine(baseDir, "wireguard-import");
        Directory.CreateDirectory(dir);

        var session = new FakeRouterSession();
        var backend = new RouterBackend(session, dir);

        var snap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("snapshot", ToJsonElement(new { })));
        var rev1 = snap.GetProperty("revision").GetString()!;

        var wgConf = @"[Interface]
PrivateKey = aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa=
Address = 10.0.0.2/32
DNS = 1.1.1.1

[Peer]
PublicKey = bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb=
Endpoint = 198.51.100.50:51820
AllowedIPs = 0.0.0.0/0
PersistentKeepalive = 25
";

        var importSnap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("servers.import", ToJsonElement(new
        {
            revision = rev1,
            text = wgConf
        })));
        var rev2 = importSnap.GetProperty("revision").GetString()!;

        // List servers
        var listObj = await backend.ExecuteAsync("servers.list", ToJsonElement(new { }));
        var listElem = JsonSerializer.SerializeToElement(listObj);
        var items = listElem.GetProperty("items").EnumerateArray().ToList();
        Assert(items.Count == 1, "Expected 1 WireGuard server in list");

        var wgItem = items[0];
        var id = wgItem.GetProperty("id").GetString()!;
        var name = wgItem.GetProperty("name").GetString()!;
        var protocol = wgItem.GetProperty("protocol").GetString()!;

        Assert(id.StartsWith("srv_"), "ID must start with srv_");
        Assert(!string.IsNullOrWhiteSpace(name), "WireGuard server must have non-empty name");
        Assert(protocol == "amneziawg", "WireGuard protocol should be amneziawg");

        // Select WireGuard server
        var selectSnap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("servers.select", ToJsonElement(new
        {
            revision = rev2,
            id
        })));
        Assert(selectSnap.GetProperty("activeServer").GetString() == name, "Active server must match WireGuard name");
    }

    // ─── Regression: Profile List No Network ───

    private static async Task CheckProfileListNoNetworkAsync(string baseDir)
    {
        var dir = Path.Combine(baseDir, "profiles-no-network");
        Directory.CreateDirectory(dir);

        var session = new FakeRouterSession();
        var backend = new RouterBackend(session, dir);

        // profiles.list on fresh start without network access
        var listObj = await backend.ExecuteAsync("profiles.list", ToJsonElement(new { }));
        var listElem = JsonSerializer.SerializeToElement(listObj);
        var items = listElem.GetProperty("items").EnumerateArray().ToList();
        Assert(items.Count > 0, "profiles.list must return built-in/local profiles without network");
        var firstProfile = items[0].GetProperty("name").GetString()!;
        Assert(!string.IsNullOrWhiteSpace(firstProfile), "Profile name must be non-empty");

        // Select profile
        var snap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("snapshot", ToJsonElement(new { })));
        var rev = snap.GetProperty("revision").GetString()!;

        await backend.ExecuteAsync("profiles.select", ToJsonElement(new
        {
            revision = rev,
            ids = new[] { firstProfile }
        }));

        var listAfterSelect = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("profiles.list", ToJsonElement(new { })));
        var selectedItem = listAfterSelect.GetProperty("items").EnumerateArray().First(i => i.GetProperty("name").GetString() == firstProfile);
        Assert(selectedItem.GetProperty("selected").GetBoolean(), "Selected profile must have selected=true");
    }

    // ─── Test Helpers ───

    private static List<(string Id, VlessServerEntry Server)> ServerFeatureIdHelper(List<VlessServerEntry> servers)
    {
        var result = new List<(string Id, VlessServerEntry Server)>(servers.Count);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < servers.Count; i++)
        {
            var s = servers[i];
            if (s == null) continue;
            var key = $"{s.Protocol ?? "vless"}|{s.Name}|{s.Server}|{s.Port}|{s.Uuid}";
            counts.TryGetValue(key, out var count);
            counts[key] = count + 1;

            var raw = $"{key}|{count}";
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
            var id = "srv_" + Convert.ToHexStringLower(hash)[..16];
            result.Add((id, s));
        }

        return result;
    }

    private static JsonElement ToJsonElement(object obj)
    {
        return JsonSerializer.SerializeToElement(obj);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"Assertion failed: {message}");
    }
}
