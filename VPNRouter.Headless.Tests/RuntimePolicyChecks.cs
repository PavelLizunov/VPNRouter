#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Headless;
using VPNRouter.Headless.Features;
using VPNRouter.Headless.Lifecycle;
using VPNRouter.Headless.Storage;
using System.Collections.Generic;
using System.Linq;
using VPNRouter.Core.Services.FreeConfigs;

namespace VPNRouter.Headless.Tests;

public static class RuntimePolicyChecks
{
    public static async Task RunAsync()
    {
        Console.WriteLine("[RuntimePolicyChecks] Starting Headless runtime policy checks...");

        await CheckProductionPolicyDefaultsUnavailableAsync();
        Console.WriteLine("  [OK] CheckProductionPolicyDefaultsUnavailable passed");

        await CheckTestPolicyWithValidFixtureAsync();
        Console.WriteLine("  [OK] CheckTestPolicyWithValidFixture passed");

        await CheckTestPolicyMissingFileRejectionAsync();
        Console.WriteLine("  [OK] CheckTestPolicyMissingFileRejection passed");

        await CheckTestPolicyUntrustedFileRejectionAsync();
        Console.WriteLine("  [OK] CheckTestPolicyUntrustedFileRejection passed");

        await CheckTestPolicySameSizeReplacementRejectionAsync();
        Console.WriteLine("  [OK] CheckTestPolicySameSizeReplacementRejection passed");

        await CheckTestPolicyPrerequisiteRejectionAsync();
        Console.WriteLine("  [OK] CheckTestPolicyPrerequisiteRejection passed");

        await CheckScopedIsolationAcrossAsyncBranchesAsync();
        Console.WriteLine("  [OK] CheckScopedIsolationAcrossAsyncBranches passed");

        await CheckFeatureEndpointsUnderRestrictedPolicyAsync();
        Console.WriteLine("  [OK] CheckFeatureEndpointsUnderRestrictedPolicy passed");

        await CheckNoSecretOrPathLeakedInPolicyExceptionAsync();
        Console.WriteLine("  [OK] CheckNoSecretOrPathLeakedInPolicyException passed");

        CheckRetainedPolicyFeatureParserWithOverrides();
        Console.WriteLine("  [OK] CheckRetainedPolicyFeatureParserWithOverrides passed");

        await CheckDefaultEmbeddedRouterSessionReadinessRefusalAsync();
        Console.WriteLine("  [OK] CheckDefaultEmbeddedRouterSessionReadinessRefusal passed");

        await CheckFreeConfigVerifyUnderRestrictedPolicyRefusalAsync();
        Console.WriteLine("  [OK] CheckFreeConfigVerifyUnderRestrictedPolicyRefusal passed");

        Console.WriteLine("[RuntimePolicyChecks] All Headless runtime policy checks PASSED successfully!");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"Assertion failed: {message}");
    }

    private static async Task CheckProductionPolicyDefaultsUnavailableAsync()
    {
        var prevDataDir = AppPaths.DataDir;
        var prevRuntimeDir = LinuxTunOwnership.OverrideRuntimeDirectory;
        var tempDir = Path.Combine(Path.GetTempPath(), "vpnrouter-policy-test-" + Guid.NewGuid().ToString("N"));
        var tempRuntimeDir = Path.Combine(tempDir, "runtime");
        Directory.CreateDirectory(tempRuntimeDir);
        AppPaths.OverrideDataDir(tempDir);
        LinuxTunOwnership.OverrideRuntimeDirectory = tempRuntimeDir;

        try
        {
            using var _ = SingBoxRuntimePolicy.EnterScope(SingBoxRuntimePolicy.DefaultProduction);

            Assert(!SingBoxRuntimePolicy.DefaultProduction.IsAvailable, "Default production policy must not be available");

            // Verify PlatformCapabilityVerifier returns false under production policy even if a fake probe is null
            var canConnect = PlatformCapabilityVerifier.VerifyCanConnect(
                ownershipProbe: () => OwnershipCheckResult.Free(),
                logger: null,
                capabilityReadinessFunc: null);
            Assert(!canConnect, "PlatformCapabilityVerifier.VerifyCanConnect must return false under default production policy");

            var fakeEngine = new FakeLifecycleEngine { CapabilityReadinessFunc = null };
            var session = new RouterSession(fakeEngine, () => OwnershipCheckResult.Free());
            Assert(!session.CanConnect, "RouterSession.CanConnect must be false under default production policy");

            var backend = new RouterBackend(session, tempDir);
            try
            {
                var snapshotObj = backend.GetSnapshot();
                var snapshotJson = JsonSerializer.Serialize(snapshotObj);
                using var doc = JsonDocument.Parse(snapshotJson);
                var root = doc.RootElement;

                Assert(root.TryGetProperty("capabilities", out var caps), "Snapshot must contain capabilities");
                Assert(caps.TryGetProperty("connect", out var connectCap), "Capabilities must contain connect");
                Assert(!connectCap.GetBoolean(), "capabilities.connect must be false under default production policy");

                // Attempting to connect must throw RouterException with code 'unavailable'
                bool caughtUnavailable = false;
                try
                {
                    var settings = new AppSettings();
                    await session.ConnectAsync(settings, default);
                }
                catch (RouterException rex) when (rex.Code == "unavailable")
                {
                    caughtUnavailable = true;
                    Assert(rex.SafeMessage == "The requested service or feature is unavailable.", "Protocol error must carry the fixed safe message");
                }
                Assert(caughtUnavailable, "Session ConnectAsync must throw unavailable under production policy");
                Assert(fakeEngine.StartCount == 0, "Fake engine start counter must remain 0 on unavailable");
            }
            finally
            {
                await backend.DisposeAsync();
                await session.DisposeAsync();
            }
        }
        finally
        {
            LinuxTunOwnership.OverrideRuntimeDirectory = prevRuntimeDir;
            AppPaths.OverrideDataDir(prevDataDir);
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static async Task CheckTestPolicyWithValidFixtureAsync()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "sb-fixture-" + Guid.NewGuid().ToString("N") + ".bin");
        var bytes = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
        await File.WriteAllBytesAsync(tempFile, bytes);

        try
        {
            using var sha = SHA256.Create();
            var hash = Convert.ToHexString(sha.ComputeHash(bytes));

            var policy = SingBoxRuntimePolicy.ForTestFile(tempFile, hash, prerequisites: () => true);
            using var _ = SingBoxRuntimePolicy.EnterScope(policy);

            Assert(policy.IsAvailable, "Policy with valid fixture must report IsAvailable = true");

            var canConnect = PlatformCapabilityVerifier.VerifyCanConnect(
                ownershipProbe: () => OwnershipCheckResult.Free(),
                logger: null,
                capabilityReadinessFunc: null);
            Assert(canConnect, "PlatformCapabilityVerifier must report true when test policy is satisfied");

            policy.Authorize(SingBoxRuntimeOperation.Inspect);
            policy.Authorize(SingBoxRuntimeOperation.Probe);
            policy.Authorize(SingBoxRuntimeOperation.Verify);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    private static Task CheckTestPolicyMissingFileRejectionAsync()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), "non-existent-" + Guid.NewGuid().ToString("N") + ".bin");
        var dummyHash = new string('0', 64);

        var policy = SingBoxRuntimePolicy.ForTestFile(missingPath, dummyHash);
        using var _ = SingBoxRuntimePolicy.EnterScope(policy);

        Assert(!policy.IsAvailable, "Policy for missing file must not be available");

        bool caught = false;
        try
        {
            policy.Authorize(SingBoxRuntimeOperation.Inspect);
        }
        catch (SingBoxRuntimePolicyException ex)
        {
            caught = true;
            Assert(ex.Failure == SingBoxRuntimeFailure.Missing, "Failure must be Missing");
            Assert(ex.Message == "sing-box runtime is unavailable or untrusted.", "Exception message must be fixed safe string");
        }
        Assert(caught, "Authorize must throw SingBoxRuntimePolicyException for missing file");
        return Task.CompletedTask;
    }

    private static async Task CheckTestPolicyUntrustedFileRejectionAsync()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "sb-untrusted-" + Guid.NewGuid().ToString("N") + ".bin");
        await File.WriteAllBytesAsync(tempFile, new byte[] { 0xAA, 0xBB, 0xCC });

        try
        {
            var wrongHash = new string('f', 64);
            var policy = SingBoxRuntimePolicy.ForTestFile(tempFile, wrongHash);
            using var _ = SingBoxRuntimePolicy.EnterScope(policy);

            Assert(!policy.IsAvailable, "Policy for file with mismatched hash must not be available");

            bool caught = false;
            try
            {
                policy.Authorize(SingBoxRuntimeOperation.Start);
            }
            catch (SingBoxRuntimePolicyException ex)
            {
                caught = true;
                Assert(ex.Failure == SingBoxRuntimeFailure.Untrusted, "Failure must be Untrusted on initial hash mismatch");
                Assert(ex.Message == "sing-box runtime is unavailable or untrusted.", "Exception message must be fixed safe string");
            }
            Assert(caught, "Authorize must throw SingBoxRuntimePolicyException for untrusted file");
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    private static async Task CheckTestPolicySameSizeReplacementRejectionAsync()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "sb-replace-" + Guid.NewGuid().ToString("N") + ".bin");
        var initialBytes = new byte[] { 0x10, 0x20, 0x30, 0x40 };
        await File.WriteAllBytesAsync(tempFile, initialBytes);

        try
        {
            using var sha = SHA256.Create();
            var initialHash = Convert.ToHexString(sha.ComputeHash(initialBytes));

            var policy = SingBoxRuntimePolicy.ForTestFile(tempFile, initialHash);
            using var _ = SingBoxRuntimePolicy.EnterScope(policy);

            // First validation succeeds
            policy.Authorize(SingBoxRuntimeOperation.Start);

            // Replace with different content of the exact same length
            var replacedBytes = new byte[] { 0x99, 0x88, 0x77, 0x66 };
            await File.WriteAllBytesAsync(tempFile, replacedBytes);

            bool caught = false;
            try
            {
                policy.Authorize(SingBoxRuntimeOperation.Start);
            }
            catch (SingBoxRuntimePolicyException ex)
            {
                caught = true;
                Assert(ex.Failure == SingBoxRuntimeFailure.Changed, "Failure must be Changed for same-size replacement");
                Assert(ex.Message == "sing-box runtime is unavailable or untrusted.", "Exception message must be fixed safe string");
            }
            Assert(caught, "Authorize must throw Changed failure when previously verified fixture is replaced");
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    private static async Task CheckTestPolicyPrerequisiteRejectionAsync()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "sb-prereq-" + Guid.NewGuid().ToString("N") + ".bin");
        var bytes = new byte[] { 0x01, 0x02, 0x03 };
        await File.WriteAllBytesAsync(tempFile, bytes);

        try
        {
            using var sha = SHA256.Create();
            var hash = Convert.ToHexString(sha.ComputeHash(bytes));

            bool prereqState = false;
            var policy = SingBoxRuntimePolicy.ForTestFile(tempFile, hash, prerequisites: () => prereqState);
            using var _ = SingBoxRuntimePolicy.EnterScope(policy);

            Assert(!policy.IsAvailable, "Policy must report unavailable when prerequisite is false");

            bool caught = false;
            try
            {
                policy.Authorize(SingBoxRuntimeOperation.Verify);
            }
            catch (SingBoxRuntimePolicyException ex)
            {
                caught = true;
                Assert(ex.Failure == SingBoxRuntimeFailure.PrerequisiteUnavailable, "Failure must be PrerequisiteUnavailable");
                Assert(ex.Message == "sing-box runtime is unavailable or untrusted.", "Exception message must be fixed safe string");
            }
            Assert(caught, "Authorize must throw PrerequisiteUnavailable failure");

            // Flip prerequisite to true -> now succeeds
            prereqState = true;
            Assert(policy.IsAvailable, "Policy must report available when prerequisite flips to true");
            policy.Authorize(SingBoxRuntimeOperation.Verify);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    private static async Task CheckScopedIsolationAcrossAsyncBranchesAsync()
    {
        var policyA = SingBoxRuntimePolicy.DefaultProduction;
        var tempFile = Path.Combine(Path.GetTempPath(), "sb-async-" + Guid.NewGuid().ToString("N") + ".bin");
        await File.WriteAllBytesAsync(tempFile, new byte[] { 0x01 });

        try
        {
            using var sha = SHA256.Create();
            var hash = Convert.ToHexString(sha.ComputeHash(new byte[] { 0x01 }));
            var policyB = SingBoxRuntimePolicy.ForTestFile(tempFile, hash);

            var taskA = Task.Run(async () =>
            {
                using var _ = SingBoxRuntimePolicy.EnterScope(policyA);
                for (int i = 0; i < 5; i++)
                {
                    await Task.Yield();
                    Assert(ReferenceEquals(SingBoxRuntimePolicy.Current, policyA), "Task A must preserve policyA");
                }
            });

            var taskB = Task.Run(async () =>
            {
                using var _ = SingBoxRuntimePolicy.EnterScope(policyB);
                for (int i = 0; i < 5; i++)
                {
                    await Task.Yield();
                    Assert(ReferenceEquals(SingBoxRuntimePolicy.Current, policyB), "Task B must preserve policyB");
                }
            });

            await Task.WhenAll(taskA, taskB);
            Assert(SingBoxRuntimePolicy.Current == null, "Outer scope must remain clean after task completion");
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    private static async Task CheckFeatureEndpointsUnderRestrictedPolicyAsync()
    {
        var prevDataDir = AppPaths.DataDir;
        var prevRuntimeDir = LinuxTunOwnership.OverrideRuntimeDirectory;
        var tempDir = Path.Combine(Path.GetTempPath(), "vpnrouter-feat-test-" + Guid.NewGuid().ToString("N"));
        var tempRuntimeDir = Path.Combine(tempDir, "runtime");
        Directory.CreateDirectory(tempRuntimeDir);
        AppPaths.OverrideDataDir(tempDir);
        LinuxTunOwnership.OverrideRuntimeDirectory = tempRuntimeDir;

        try
        {
            using var _ = SingBoxRuntimePolicy.EnterScope(SingBoxRuntimePolicy.DefaultProduction);

            var storage = new ConfigStorage(tempDir);
            var serverFeature = new ServerFeature(storage);

            // Server deep verify under restricted policy must fail gracefully
            var settings = storage.GetSettings();
            settings.Vless.Servers.Add(new VlessServerEntry
            {
                Server = "192.0.2.1",
                Port = 443,
                Uuid = "00000000-0000-0000-0000-000000000001"
            });
            storage.SaveSettings(settings, storage.CurrentRevision);

            // Use actual List method to retrieve valid server id
            var listObj = serverFeature.List(JsonSerializer.SerializeToElement(new { }));
            var listJson = JsonSerializer.Serialize(listObj);
            using var listDoc = JsonDocument.Parse(listJson);
            var root = listDoc.RootElement;
            Assert(root.TryGetProperty("items", out var items) && items.GetArrayLength() > 0,
                "Server list must contain at least one item");
            var serverId = items[0].GetProperty("id").GetString()!;

            var verifyResultObj = await serverFeature.VerifyAsync(
                JsonSerializer.SerializeToElement(new { id = serverId }),
                null,
                default);
            var verifyResultJson = JsonSerializer.Serialize(verifyResultObj);
            using var vDoc = JsonDocument.Parse(verifyResultJson);
            Assert(!vDoc.RootElement.GetProperty("ok").GetBoolean(), "Verify on unprovisioned runtime must report ok=false");
        }
        finally
        {
            LinuxTunOwnership.OverrideRuntimeDirectory = prevRuntimeDir;
            AppPaths.OverrideDataDir(prevDataDir);
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static Task CheckNoSecretOrPathLeakedInPolicyExceptionAsync()
    {
        var sensitivePath = Path.Combine(Path.GetTempPath(), "secret_token_abcdef123456", "singbox.bin");
        var dummyHash = new string('a', 64);

        var policy = SingBoxRuntimePolicy.ForTestFile(sensitivePath, dummyHash);
        using var _ = SingBoxRuntimePolicy.EnterScope(policy);

        try
        {
            policy.Authorize(SingBoxRuntimeOperation.Inspect);
            Assert(false, "Authorize should have thrown");
        }
        catch (SingBoxRuntimePolicyException ex)
        {
            Assert(!ex.Message.Contains("secret_token_abcdef123456"), "Secret path token must not be leaked in exception message");
            Assert(!ex.Message.Contains("singbox.bin"), "Binary filename must not be leaked in exception message");
            Assert(ex.Message == "sing-box runtime is unavailable or untrusted.", "Exception message must match fixed safe string");
        }

        return Task.CompletedTask;
    }

    private static void CheckRetainedPolicyFeatureParserWithOverrides()
    {
        SingBoxFeatures.ResetForTests();
        try
        {
            SingBoxFeatures.OverrideAwg = true;
            SingBoxFeatures.OverrideXhttp = true;

            // Outside restricted policy: overrides take effect
            Assert(SingBoxFeatures.AwgAvailable, "Outside policy, OverrideAwg=true must yield AwgAvailable=true");
            Assert(SingBoxFeatures.XhttpAvailable, "Outside policy, OverrideXhttp=true must yield XhttpAvailable=true");

            // Inside restricted policy scope: fork features are strictly false
            using (SingBoxRuntimePolicy.EnterScope(SingBoxRuntimePolicy.DefaultProduction))
            {
                Assert(!SingBoxFeatures.AwgAvailable, "Under restricted policy, AwgAvailable must remain false");
                Assert(!SingBoxFeatures.XhttpAvailable, "Under restricted policy, XhttpAvailable must remain false");
            }

            // Outside scope again: overrides take effect again
            Assert(SingBoxFeatures.AwgAvailable, "Outside scope, OverrideAwg=true must yield AwgAvailable=true");

            // Feature constructed under policy retains policy and enforces restricted behavior in operations
            var tempDir = Path.Combine(Path.GetTempPath(), "vpnrouter-retained-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var storage = new ConfigStorage(tempDir);
                ServerFeature serverFeature;
                using (SingBoxRuntimePolicy.EnterScope(SingBoxRuntimePolicy.DefaultProduction))
                {
                    serverFeature = new ServerFeature(storage);
                }

                // Call Import under ambient null scope: retained policy re-enters DefaultProduction
                var uri = "vless://00000000-0000-0000-0000-000000000001@192.0.2.1:443?security=none&type=xhttp#fixture";
                Assert(ServerUriParser.ParseMultiple(uri).Count == 1,
                    "Positive control must parse with legacy feature override outside policy");
                var revision = storage.CurrentRevision;
                var rejected = false;
                try
                {
                    serverFeature.Import(JsonSerializer.SerializeToElement(new { revision, text = uri }));
                }
                catch (RouterException ex) when (ex.Code == "invalid_argument")
                {
                    rejected = true;
                }
                Assert(rejected, "Retained restricted policy must reject unsupported XHTTP import");
                Assert(storage.CurrentRevision == revision, "Rejected import must not alter storage");
                Assert(SingBoxFeatures.XhttpAvailable, "Feature call must restore outer legacy scope");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
        finally
        {
            SingBoxFeatures.ResetForTests();
        }
    }

    private static async Task CheckDefaultEmbeddedRouterSessionReadinessRefusalAsync()
    {
        var prevDataDir = AppPaths.DataDir;
        var prevRuntimeDir = LinuxTunOwnership.OverrideRuntimeDirectory;
        var tempDir = Path.Combine(Path.GetTempPath(), "vpnrouter-embedded-test-" + Guid.NewGuid().ToString("N"));
        var tempRuntimeDir = Path.Combine(tempDir, "runtime");
        Directory.CreateDirectory(tempRuntimeDir);
        AppPaths.OverrideDataDir(tempDir);
        LinuxTunOwnership.OverrideRuntimeDirectory = tempRuntimeDir;

        try
        {
            // Create passive dummy binary at SingBoxExePath
            var binDir = Path.GetDirectoryName(AppPaths.SingBoxExePath)!;
            Directory.CreateDirectory(binDir);
            await File.WriteAllBytesAsync(AppPaths.SingBoxExePath, new byte[] { 0x7F, 0x45, 0x4C, 0x46 });
            Assert(File.Exists(AppPaths.SingBoxExePath), "Passive sing-box binary file must exist on disk");

            // Default embedded constructor wires production engine WITH Linux default policy active
            var session = new RouterSession();
            try
            {
                // Readiness must be refused despite passive file existing on disk
                Assert(!session.CanConnect, "Default embedded RouterSession must refuse CanConnect under production policy even when binary exists");

                bool caught = false;
                try
                {
                    await session.ConnectAsync(new AppSettings(), default);
                }
                catch (RouterException rex) when (rex.Code == "unavailable")
                {
                    caught = true;
                }
                Assert(caught, "Default embedded RouterSession.ConnectAsync must throw unavailable");
            }
            finally
            {
                await session.DisposeAsync();
            }
        }
        finally
        {
            LinuxTunOwnership.OverrideRuntimeDirectory = prevRuntimeDir;
            AppPaths.OverrideDataDir(prevDataDir);
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static async Task CheckFreeConfigVerifyUnderRestrictedPolicyRefusalAsync()
    {
        var prevDataDir = AppPaths.DataDir;
        var prevRuntimeDir = LinuxTunOwnership.OverrideRuntimeDirectory;
        var tempDir = Path.Combine(Path.GetTempPath(), "vpnrouter-policy-freeconfig-" + Guid.NewGuid().ToString("N"));
        var tempRuntimeDir = Path.Combine(tempDir, "runtime");
        Directory.CreateDirectory(tempRuntimeDir);
        AppPaths.OverrideDataDir(tempDir);
        LinuxTunOwnership.OverrideRuntimeDirectory = tempRuntimeDir;

        try
        {
            using var _ = SingBoxRuntimePolicy.EnterScope(SingBoxRuntimePolicy.DefaultProduction);
            Assert(!SingBoxRuntimePolicy.DefaultProduction.IsAvailable, "Default production policy must not be available");

            var storage = new ConfigStorage(tempDir);
            var initialRevision = storage.CurrentRevision;

            var cacheFilePath = Path.Combine(AppPaths.CacheDir, "free_configs.json");
            var cache = new FreeConfigCache(Serilog.Log.Logger, cacheFilePath);

            var configId = "verified-free-config-" + Guid.NewGuid().ToString("N");
            var initialTestedAt = DateTime.UtcNow.AddMinutes(-20);
            var initialDeepVerifyAt = DateTime.UtcNow.AddMinutes(-20);
            var initialAggregatedAt = DateTime.UtcNow.AddMinutes(-30);

            var verifiedEntry = new FreeConfigEntry
            {
                Id = configId,
                Name = "Verified Test Candidate",
                Host = "192.0.2.200",
                Port = 443,
                Uuid = "00000000-0000-0000-0000-000000000002",
                Protocol = "vless",
                RawUri = "vless://00000000-0000-0000-0000-000000000002@192.0.2.200:443?security=reality&sni=example.com&fp=chrome&pbk=1111111111111111111111111111111111111111111&sid=12345678&type=tcp#Verified",
                Status = FreeConfigStatus.Verified,
                LatencyMs = 42,
                LastTestedAt = initialTestedAt,
                LastDeepVerifyAt = initialDeepVerifyAt,
                LastError = null
            };

            var initialCacheFile = new FreeConfigCache.CacheFile
            {
                SchemaVersion = FreeConfigCache.CurrentSchemaVersion,
                LastAggregatedAt = initialAggregatedAt,
                Configs = new List<FreeConfigEntry> { verifiedEntry }
            };

            cache.Save(initialCacheFile);
            Assert(File.Exists(cacheFilePath), "Persisted FreeConfigCache file must exist on disk");

            var originalBytes = await File.ReadAllBytesAsync(cacheFilePath);
            Assert(originalBytes.Length > 0, "Persisted FreeConfigCache must not be empty");

            // 1. Direct FreeConfigFeature.VerifyAsync under restricted policy
            var freeConfigFeature = new FreeConfigFeature(storage);
            var verifyParams = JsonSerializer.SerializeToElement(new { id = configId });

            bool caughtUnavailable = false;
            try
            {
                await freeConfigFeature.VerifyAsync(verifyParams, null, default);
            }
            catch (RouterException rex) when (rex.Code == "unavailable")
            {
                caughtUnavailable = true;
                Assert(rex.SafeMessage == "The requested service or feature is unavailable.",
                    "Protocol error must carry the fixed safe message for unavailable");
            }
            Assert(caughtUnavailable, "FreeConfigFeature.VerifyAsync must throw RouterException with code 'unavailable' under runtime refusal");

            // Verify cache file bytes on disk are completely unchanged (no cache save occurred)
            var bytesAfterFeatureVerify = await File.ReadAllBytesAsync(cacheFilePath);
            Assert(originalBytes.SequenceEqual(bytesAfterFeatureVerify),
                "Persisted FreeConfigCache file bytes on disk must be identical (no mutation/save on runtime refusal)");

            // Verify reloaded cache preserves prior Verified item (not false tlsfailed and not stale ok=true)
            var reloadedAfterFeature = cache.Load();
            Assert(reloadedAfterFeature.Configs != null && reloadedAfterFeature.Configs.Count == 1,
                "Reloaded cache must contain exactly one config");
            var entryAfterFeature = reloadedAfterFeature.Configs[0];
            Assert(entryAfterFeature.Id == configId, "Config Id must match");
            Assert(entryAfterFeature.Status == FreeConfigStatus.Verified,
                "Prior item status must remain Verified (must not be mutated to false TlsFailed)");
            Assert(entryAfterFeature.Status != FreeConfigStatus.TlsFailed,
                "Prior item status must not be false tlsfailed");
            Assert(entryAfterFeature.LatencyMs == 42, "LatencyMs must remain unchanged");
            Assert(entryAfterFeature.LastError == null, "LastError must remain null");
            Assert(entryAfterFeature.LastTestedAt == initialTestedAt, "LastTestedAt must remain unchanged");
            Assert(entryAfterFeature.LastDeepVerifyAt == initialDeepVerifyAt, "LastDeepVerifyAt must remain unchanged");
            Assert(reloadedAfterFeature.SchemaVersion == FreeConfigCache.CurrentSchemaVersion,
                "Cache schema version must remain unchanged");
            Assert(reloadedAfterFeature.LastAggregatedAt == initialAggregatedAt,
                "Cache LastAggregatedAt must remain unchanged");
            Assert(storage.CurrentRevision == initialRevision,
                "Storage revision must remain unchanged");

            // 2. End-to-end RouterBackend mapping under restricted policy
            var fakeEngine = new FakeLifecycleEngine { CapabilityReadinessFunc = null };
            var session = new RouterSession(fakeEngine, () => OwnershipCheckResult.Free());
            var backend = new RouterBackend(session, tempDir);
            try
            {
                bool backendCaughtUnavailable = false;
                try
                {
                    await backend.ExecuteAsync("free.verify", verifyParams, default);
                }
                catch (RouterException rex) when (rex.Code == "unavailable")
                {
                    backendCaughtUnavailable = true;
                    Assert(rex.SafeMessage == "The requested service or feature is unavailable.",
                        "Backend protocol error must carry the fixed safe message for unavailable");
                }
                Assert(backendCaughtUnavailable,
                    "RouterBackend.ExecuteAsync('free.verify') must throw unavailable under runtime refusal");

                var bytesAfterBackendVerify = await File.ReadAllBytesAsync(cacheFilePath);
                Assert(originalBytes.SequenceEqual(bytesAfterBackendVerify),
                    "Persisted cache bytes on disk must remain unchanged after backend dispatch");

                var reloadedAfterBackend = cache.Load();
                Assert(reloadedAfterBackend.Configs != null && reloadedAfterBackend.Configs.Count == 1,
                    "Cache must retain single entry after backend dispatch");
                var entryAfterBackend = reloadedAfterBackend.Configs[0];
                Assert(entryAfterBackend.Status == FreeConfigStatus.Verified,
                    "Status must remain Verified after backend dispatch");
                Assert(entryAfterBackend.Status != FreeConfigStatus.TlsFailed,
                    "Status must not be false tlsfailed after backend dispatch");
                Assert(storage.CurrentRevision == initialRevision,
                    "Storage revision must remain unchanged after backend dispatch");
            }
            finally
            {
                await backend.DisposeAsync();
                await session.DisposeAsync();
            }
        }
        finally
        {
            LinuxTunOwnership.OverrideRuntimeDirectory = prevRuntimeDir;
            AppPaths.OverrideDataDir(prevDataDir);
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private sealed class FakeLifecycleEngine : ILifecycleEngine
    {
        public bool IsRunning { get; set; }
        public string ActiveProfileName { get; set; } = "Default";
        public int? SingBoxPid { get; set; }
        public string ActiveConfigMode { get; set; } = "generated";
        public string ActiveRoutingMode { get; set; } = "split";
        public string ActiveServerAddress { get; set; } = "127.0.0.1";
        public int StartCount { get; private set; }

        public event Action<int>? SingBoxStarted;
        public event Action<int>? Connected;
        public event Action<string>? StatusChanged;
        public event Action<string>? Warning;

        public Func<bool>? CapabilityReadinessFunc { get; set; }

        public Task StartAsync(AppSettings settings, CancellationToken ct)
        {
            StartCount++;
            return Task.CompletedTask;
        }

        public Task<bool> ApplyAsync(AppSettings settings, CancellationToken ct) => Task.FromResult(true);
        public void Stop() { }
        public Func<bool>? CaptureReadinessGuard(int pid) => () => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
