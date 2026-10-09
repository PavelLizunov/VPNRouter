using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Headless.Features;
using VPNRouter.Headless.Storage;

namespace VPNRouter.Headless.Tests;

/// <summary>
/// Executable hermetic checks for offline profile collection, strictest DNS mode merge,
/// source priority shadowing, fail-closed handling of invalid/oversize profile files,
/// zero network/zero writes for read operations, and custom DNS mode semantics.
/// Entry point: public static Task RunAsync() — invoked via coordinator.
/// </summary>
public static class ProfileChecks
{
    public static async Task RunAsync()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "vpnrouter-profilechecks-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            Console.WriteLine($"[ProfileChecks] Starting profile test suite in {tempDir}...");

            await CheckMultipleActiveStrictestDnsAsync(tempDir);
            await CheckPriorityShadowAsync(tempDir);
            await CheckPlatformPrecedenceAndGenericFallbackAsync(tempDir);
            await CheckInvalidOversizeLocalFileFailClosedAsync(tempDir);
            await CheckNoNetworkNoWritesForListAndGetAsync(tempDir);
            await CheckCustomDnsModeAsync(tempDir);
            await CheckCacheDirectoryIsolationAsync(tempDir);

            Console.WriteLine("[ProfileChecks] All profile checks PASSED successfully!");
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

    /// <summary>
    /// Test 1: Multiple active profiles strictest DNS resolution (vpn_only > smart > direct).
    /// </summary>
    private static async Task CheckMultipleActiveStrictestDnsAsync(string baseDir)
    {
        var dir = Path.Combine(baseDir, "multiple-active-strictest-dns");
        Directory.CreateDirectory(dir);

        var profilesJsonPath = Path.Combine(dir, "profiles.json");
        var collectionJson = /*lang=json*/ @"{
  ""profiles"": [
    {
      ""name"": ""PDirect"",
      ""dns_mode"": ""direct"",
      ""processes"": []
    },
    {
      ""name"": ""PSmart"",
      ""dns_mode"": ""smart"",
      ""processes"": []
    },
    {
      ""name"": ""PVpnOnly"",
      ""dns_mode"": ""vpn_only"",
      ""processes"": []
    }
  ]
}";
        File.WriteAllText(profilesJsonPath, collectionJson);

        var session = new FakeRouterSession();
        var backend = new RouterBackend(session, dir);

        // Configure local profile source
        var storage = new ConfigStorage(dir);
        var settings = storage.GetSettings();
        settings.ProfileSources = new List<ProfileSource>
        {
            new() { Type = "local", Path = profilesJsonPath }
        };
        storage.SaveSettings(settings, storage.CurrentRevision);

        // 1. Single profile modes
        AssertDnsModeForActive(settings, dir, "PDirect", "direct");
        AssertDnsModeForActive(settings, dir, "PSmart", "smart");
        AssertDnsModeForActive(settings, dir, "PVpnOnly", "vpn_only");

        // 2. direct + smart -> smart wins (strictest)
        AssertDnsModeForActive(settings, dir, "PDirect,PSmart", "smart");
        AssertDnsModeForActive(settings, dir, "PSmart,PDirect", "smart");

        // 3. smart + vpn_only -> vpn_only wins (strictest)
        AssertDnsModeForActive(settings, dir, "PSmart,PVpnOnly", "vpn_only");
        AssertDnsModeForActive(settings, dir, "PVpnOnly,PSmart", "vpn_only");

        // 4. direct + vpn_only -> vpn_only wins (strictest)
        AssertDnsModeForActive(settings, dir, "PDirect,PVpnOnly", "vpn_only");
        AssertDnsModeForActive(settings, dir, "PVpnOnly,PDirect", "vpn_only");

        // 5. all three: direct + smart + vpn_only -> vpn_only wins
        AssertDnsModeForActive(settings, dir, "PDirect,PSmart,PVpnOnly", "vpn_only");

        // 6. Verify via settings.get through RouterBackend
        settings.ActiveProfile = "PDirect,PSmart";
        storage.SaveSettings(settings, storage.CurrentRevision);
        var getObj = await backend.ExecuteAsync("settings.get", ToJsonElement(new { }));
        var getElem = JsonSerializer.SerializeToElement(getObj);
        Assert(getElem.GetProperty("dnsMode").GetString() == "smart",
            "settings.get must return 'smart' when merging PDirect and PSmart");

        settings.ActiveProfile = "PDirect,PSmart,PVpnOnly";
        storage.SaveSettings(settings, storage.CurrentRevision);
        var getObj2 = await backend.ExecuteAsync("settings.get", ToJsonElement(new { }));
        var getElem2 = JsonSerializer.SerializeToElement(getObj2);
        Assert(getElem2.GetProperty("dnsMode").GetString() == "vpn_only",
            "settings.get must return 'vpn_only' when merging PDirect, PSmart, and PVpnOnly");
    }

    private static void AssertDnsModeForActive(AppSettings settings, string dir, string activeProfile, string expectedMode)
    {
        settings.ActiveProfile = activeProfile;
        var resolved = OfflineProfileHelper.ResolveLocalProfileDnsMode(settings, dir);
        Assert(resolved == expectedMode,
            $"Expected DNS mode '{expectedMode}' for active profile '{activeProfile}', but resolved '{resolved}'");
    }

    /// <summary>
    /// Test 2: Configured profile source precedence and whole collection selection.
    /// ProfileManager.LoadAsync evaluates sources in ascending Priority order and selects
    /// the first whole non-empty collection; it does NOT perform a per-profile shadow merge.
    /// Configured local source (priority 20) wins over user profiles dir (priority 87).
    /// </summary>
    private static async Task CheckPriorityShadowAsync(string baseDir)
    {
        var dir = Path.Combine(baseDir, "priority-shadow");
        Directory.CreateDirectory(dir);

        // Create user profiles dir in dataDir: dataDir/profiles/default.json (priority 87)
        var userProfilesDir = Path.Combine(dir, "profiles");
        Directory.CreateDirectory(userProfilesDir);
        var defaultJsonPath = Path.Combine(userProfilesDir, "default.json");
        var lowPriJson = /*lang=json*/ @"{
  ""profiles"": [
    {
      ""name"": ""ShadowProfile"",
      ""dns_mode"": ""vpn_only"",
      ""processes"": []
    },
    {
      ""name"": ""UserDirOnlyProfile"",
      ""dns_mode"": ""vpn_only"",
      ""processes"": []
    }
  ]
}";
        File.WriteAllText(defaultJsonPath, lowPriJson);

        // Create high priority local profile file (priority 10 + 10 = 20)
        var highPriPath = Path.Combine(dir, "local_custom.json");
        var highPriJson = /*lang=json*/ @"{
  ""profiles"": [
    {
      ""name"": ""ShadowProfile"",
      ""dns_mode"": ""direct"",
      ""processes"": []
    },
    {
      ""name"": ""ConfiguredOnlyProfile"",
      ""dns_mode"": ""direct"",
      ""processes"": []
    }
  ]
}";
        File.WriteAllText(highPriPath, highPriJson);

        var session = new FakeRouterSession();
        var backend = new RouterBackend(session, dir);

        var storage = new ConfigStorage(dir);
        var settings = storage.GetSettings();
        settings.ProfileSources = new List<ProfileSource>
        {
            new() { Type = "local", Path = highPriPath }
        };
        settings.ActiveProfile = "ShadowProfile";
        storage.SaveSettings(settings, storage.CurrentRevision);

        // Configured local source (priority 20) wins over user profiles dir (priority 87)
        var resolvedDns = OfflineProfileHelper.ResolveLocalProfileDnsMode(settings, dir);
        Assert(resolvedDns == "direct",
            $"Configured local source (priority 20) must be selected before user profiles dir (priority 87): expected 'direct', got '{resolvedDns}'");

        var getObj = await backend.ExecuteAsync("settings.get", ToJsonElement(new { }));
        var getElem = JsonSerializer.SerializeToElement(getObj);
        Assert(getElem.GetProperty("dnsMode").GetString() == "direct",
            "settings.get must reflect profile from first selected source with DNS mode 'direct'");

        // Verify whole collection selection semantics: the collection from highPriPath is selected,
        // so UserDirOnlyProfile from the lower-priority source must NOT be merged into loaded profiles.
        var offlineCol = OfflineProfileHelper.GetOfflineProfileCollection(settings, dir);
        Assert(offlineCol.Profiles.Any(p => p.Name == "ConfiguredOnlyProfile"),
            "Selected configured source collection must contain its own profiles");
        Assert(!offlineCol.Profiles.Any(p => p.Name == "UserDirOnlyProfile"),
            "ProfileManager.LoadAsync must select whole collection by ascending Priority, NOT per-profile shadow merge");

        // Test two local sources with different priorities in settings.ProfileSources
        var localSource1Path = Path.Combine(dir, "local1.json");
        var localSource2Path = Path.Combine(dir, "local2.json");
        File.WriteAllText(localSource1Path, /*lang=json*/ @"{
  ""profiles"": [
    { ""name"": ""OrderTest"", ""dns_mode"": ""smart"", ""processes"": [] },
    { ""name"": ""Source1Only"", ""dns_mode"": ""smart"", ""processes"": [] }
  ]
}");
        File.WriteAllText(localSource2Path, /*lang=json*/ @"{
  ""profiles"": [
    { ""name"": ""OrderTest"", ""dns_mode"": ""vpn_only"", ""processes"": [] },
    { ""name"": ""Source2Only"", ""dns_mode"": ""vpn_only"", ""processes"": [] }
  ]
}");

        settings.ProfileSources = new List<ProfileSource>
        {
            new() { Type = "local", Path = localSource1Path }, // priority 10 + 10 = 20
            new() { Type = "local", Path = localSource2Path }  // priority 20 + 10 = 30
        };
        settings.ActiveProfile = "OrderTest";
        storage.SaveSettings(settings, storage.CurrentRevision);

        var orderResolved = OfflineProfileHelper.ResolveLocalProfileDnsMode(settings, dir);
        Assert(orderResolved == "smart",
            $"Earlier configured local source (priority 20) must be selected before later local source (priority 30): expected 'smart', got '{orderResolved}'");

        var orderCol = OfflineProfileHelper.GetOfflineProfileCollection(settings, dir);
        Assert(orderCol.Profiles.Any(p => p.Name == "Source1Only"),
            "Earlier source collection must be loaded");
        Assert(!orderCol.Profiles.Any(p => p.Name == "Source2Only"),
            "Later source collection must not be merged into loaded collection");
    }

    /// <summary>
    /// Test: Platform bundled catalog (priority 80) takes precedence over generic bundled catalog (priority 82).
    /// Both Core factory manager (VpnEngine.BuildProfileSources) and Headless offline manager
    /// (OfflineProfileHelper.CreateOfflineProfileManager) select the platform catalog when both are present,
    /// choosing Linux unique profile names. Generic default.json is selected only when platform catalog is absent.
    /// Uses temp fixture paths and restoration in finally, without mutating shipped files outside temp test output.
    /// Also verifies user directory tier (85 vs 87), bundled-over-user precedence (80/82 over 85/87),
    /// and configured source precedence over bundled/user/built-in.
    /// </summary>
    private static async Task CheckPlatformPrecedenceAndGenericFallbackAsync(string baseDir)
    {
        var fixtureDir = Path.Combine(baseDir, "platform-order-fixture");
        Directory.CreateDirectory(fixtureDir);

        var bundledDir = Path.Combine(AppContext.BaseDirectory, "profiles");
        var backupDir = Path.Combine(fixtureDir, "bundled_backup");
        bool bundledDirExisted = Directory.Exists(bundledDir);

        try
        {
            // Back up any pre-existing files in AppContext.BaseDirectory/profiles to keep test output clean and hermetic
            if (bundledDirExisted)
            {
                Directory.CreateDirectory(backupDir);
                foreach (var file in Directory.GetFiles(bundledDir))
                {
                    File.Copy(file, Path.Combine(backupDir, Path.GetFileName(file)), overwrite: true);
                }
            }
            else
            {
                Directory.CreateDirectory(bundledDir);
            }

            var platformDefaultName = OperatingSystem.IsMacOS() ? "default-macos.json"
                                    : OperatingSystem.IsLinux() ? "default-linux.json"
                                    : "default.json";

            var platformJson = /*lang=json*/ @"{
  ""profiles"": [
    {
      ""name"": ""Linux_Unique_Catalog_Profile"",
      ""description"": ""Linux-specific catalog profile"",
      ""processes"": [
        {
          ""name"": ""bash"",
          ""include_children"": true,
          ""scan_patterns"": [""bash"", ""zsh""]
        }
      ],
      ""dns_mode"": ""vpn_only"",
      ""block_on_vpn_fail"": false
    },
    {
      ""name"": ""Messengers"",
      ""description"": ""Linux Messengers"",
      ""processes"": [
        {
          ""name"": ""telegram-desktop"",
          ""include_children"": true,
          ""scan_patterns"": [""telegram-desktop""]
        }
      ],
      ""dns_mode"": ""vpn_only"",
      ""block_on_vpn_fail"": false
    }
  ]
}";

            var genericJson = /*lang=json*/ @"{
  ""profiles"": [
    {
      ""name"": ""Generic_Fallback_Catalog_Profile"",
      ""description"": ""Generic Windows catalog profile"",
      ""processes"": [
        {
          ""name"": ""WindowsTerminal.exe"",
          ""include_children"": true,
          ""scan_patterns"": [""WindowsTerminal.exe""]
        }
      ],
      ""dns_mode"": ""vpn_only"",
      ""block_on_vpn_fail"": false
    },
    {
      ""name"": ""Messengers"",
      ""description"": ""Windows Messengers"",
      ""processes"": [
        {
          ""name"": ""Telegram.exe"",
          ""include_children"": true,
          ""scan_patterns"": [""Telegram.exe""]
        }
      ],
      ""dns_mode"": ""vpn_only"",
      ""block_on_vpn_fail"": false
    }
  ]
}";

            var platformBundledFile = Path.Combine(bundledDir, platformDefaultName);
            var genericBundledFile = Path.Combine(bundledDir, "default.json");

            File.WriteAllText(platformBundledFile, platformJson);
            if (!genericBundledFile.Equals(platformBundledFile, StringComparison.Ordinal))
            {
                File.WriteAllText(genericBundledFile, genericJson);
            }

            var emptyDataDir = Path.Combine(fixtureDir, "data_empty");
            Directory.CreateDirectory(emptyDataDir);
            var settings = new AppSettings();

            // 1. Both generic + platform present in bundled directory:
            // Core factory manager:
            var coreSources = VpnEngine.BuildProfileSources(settings, emptyDataDir);
            var coreManager = new ProfileManager(coreSources);
            var coreCollection = await coreManager.LoadAsync();

            // Headless offline manager:
            var headlessManager = OfflineProfileHelper.CreateOfflineProfileManager(settings, emptyDataDir);
            var headlessCollection = await headlessManager.LoadAsync();

            if (OperatingSystem.IsLinux())
            {
                // Verify Core factory manager chose Linux platform catalog (priority 80) over generic (priority 82)
                Assert(coreCollection.Profiles.Any(p => p.Name == "Linux_Unique_Catalog_Profile"),
                    "Core factory manager must choose Linux platform profile over generic when both are present");
                Assert(!coreCollection.Profiles.Any(p => p.Name == "Generic_Fallback_Catalog_Profile"),
                    "Core factory manager must not contain generic profile when platform profile is present");

                var coreMessengers = coreCollection.Profiles.FirstOrDefault(p => p.Name == "Messengers");
                Assert(coreMessengers != null && coreMessengers.Processes.Any(pr => pr.Name == "telegram-desktop"),
                    "Core factory manager must resolve Linux process telegram-desktop");

                // Verify Headless offline manager chose Linux platform catalog (priority 80) over generic (priority 82)
                Assert(headlessCollection.Profiles.Any(p => p.Name == "Linux_Unique_Catalog_Profile"),
                    "Headless offline manager must choose Linux platform profile over generic when both are present");
                Assert(!headlessCollection.Profiles.Any(p => p.Name == "Generic_Fallback_Catalog_Profile"),
                    "Headless offline manager must not contain generic profile when platform profile is present");

                var headlessMessengers = headlessCollection.Profiles.FirstOrDefault(p => p.Name == "Messengers");
                Assert(headlessMessengers != null && headlessMessengers.Processes.Any(pr => pr.Name == "telegram-desktop"),
                    "Headless offline manager must resolve Linux process telegram-desktop");
            }

            // 2. Generic fallback only when platform absent:
            if (!genericBundledFile.Equals(platformBundledFile, StringComparison.Ordinal))
            {
                File.Delete(platformBundledFile);

                var coreFallbackSources = VpnEngine.BuildProfileSources(settings, emptyDataDir);
                var coreFallbackCollection = await new ProfileManager(coreFallbackSources).LoadAsync();

                var headlessFallbackManager = OfflineProfileHelper.CreateOfflineProfileManager(settings, emptyDataDir);
                var headlessFallbackCollection = await headlessFallbackManager.LoadAsync();

                Assert(coreFallbackCollection.Profiles.Any(p => p.Name == "Generic_Fallback_Catalog_Profile"),
                    "Core factory manager must fall back to generic default.json when platform variant is absent");
                Assert(!coreFallbackCollection.Profiles.Any(p => p.Name == "Linux_Unique_Catalog_Profile"),
                    "Core fallback collection must not contain platform-specific profile when platform file is absent");

                Assert(headlessFallbackCollection.Profiles.Any(p => p.Name == "Generic_Fallback_Catalog_Profile"),
                    "Headless offline manager must fall back to generic default.json when platform variant is absent");
                Assert(!headlessFallbackCollection.Profiles.Any(p => p.Name == "Linux_Unique_Catalog_Profile"),
                    "Headless fallback collection must not contain platform-specific profile when platform file is absent");

                // Clean up generic file before testing user directory tier
                File.Delete(genericBundledFile);
            }
            else
            {
                File.Delete(platformBundledFile);
            }

            // 3. User directory tier: platform (85) before generic (87)
            var userDataDir = Path.Combine(fixtureDir, "user_data_tier");
            var userProfilesDir = Path.Combine(userDataDir, "profiles");
            Directory.CreateDirectory(userProfilesDir);

            var userPlatformFile = Path.Combine(userProfilesDir, platformDefaultName);
            var userGenericFile = Path.Combine(userProfilesDir, "default.json");

            var userPlatformJson = /*lang=json*/ @"{
  ""profiles"": [
    { ""name"": ""User_Linux_Unique"", ""dns_mode"": ""smart"", ""processes"": [] }
  ]
}";
            var userGenericJson = /*lang=json*/ @"{
  ""profiles"": [
    { ""name"": ""User_Generic_Fallback"", ""dns_mode"": ""direct"", ""processes"": [] }
  ]
}";
            File.WriteAllText(userPlatformFile, userPlatformJson);
            if (!userGenericFile.Equals(userPlatformFile, StringComparison.Ordinal))
            {
                File.WriteAllText(userGenericFile, userGenericJson);
            }

            var coreUserSources = VpnEngine.BuildProfileSources(settings, userDataDir);
            var coreUserCol = await new ProfileManager(coreUserSources).LoadAsync();

            var headlessUserManager = OfflineProfileHelper.CreateOfflineProfileManager(settings, userDataDir);
            var headlessUserCol = await headlessUserManager.LoadAsync();

            if (OperatingSystem.IsLinux())
            {
                Assert(coreUserCol.Profiles.Any(p => p.Name == "User_Linux_Unique"),
                    "User platform file (85) must take precedence over user generic file (87) in Core");
                Assert(!coreUserCol.Profiles.Any(p => p.Name == "User_Generic_Fallback"),
                    "User generic file must not be loaded when user platform file is present in Core");

                Assert(headlessUserCol.Profiles.Any(p => p.Name == "User_Linux_Unique"),
                    "User platform file (85) must take precedence over user generic file (87) in Headless");
                Assert(!headlessUserCol.Profiles.Any(p => p.Name == "User_Generic_Fallback"),
                    "User generic file must not be loaded when user platform file is present in Headless");

                // User generic fallback when user platform absent
                if (!userGenericFile.Equals(userPlatformFile, StringComparison.Ordinal))
                {
                    File.Delete(userPlatformFile);
                    var coreUserFallback = await new ProfileManager(VpnEngine.BuildProfileSources(settings, userDataDir)).LoadAsync();
                    var headlessUserFallback = await OfflineProfileHelper.CreateOfflineProfileManager(settings, userDataDir).LoadAsync();

                    Assert(coreUserFallback.Profiles.Any(p => p.Name == "User_Generic_Fallback"),
                        "User generic file (87) must be selected as fallback when user platform file is absent in Core");
                    Assert(headlessUserFallback.Profiles.Any(p => p.Name == "User_Generic_Fallback"),
                        "User generic file (87) must be selected as fallback when user platform file is absent in Headless");
                }
            }

            // 4. Bundled tier (80/82) before user tier (85/87):
            // Re-create bundled platform file (80) while user generic file (87) is still present
            File.WriteAllText(platformBundledFile, platformJson);
            var coreTierCol = await new ProfileManager(VpnEngine.BuildProfileSources(settings, userDataDir)).LoadAsync();
            var headlessTierCol = await OfflineProfileHelper.CreateOfflineProfileManager(settings, userDataDir).LoadAsync();

            Assert(coreTierCol.Profiles.Any(p => p.Name == "Linux_Unique_Catalog_Profile"),
                "Bundled platform source (80) must take precedence over user directory source (87) in Core");
            Assert(!coreTierCol.Profiles.Any(p => p.Name == "User_Generic_Fallback"),
                "User directory source must not be loaded when bundled source succeeds in Core");
            Assert(headlessTierCol.Profiles.Any(p => p.Name == "Linux_Unique_Catalog_Profile"),
                "Bundled platform source (80) must take precedence over user directory source (87) in Headless");
            Assert(!headlessTierCol.Profiles.Any(p => p.Name == "User_Generic_Fallback"),
                "User directory source must not be loaded when bundled source succeeds in Headless");

            // 5. Configured source still wins over bundled:
            var configuredFile = Path.Combine(fixtureDir, "configured_source.json");
            var configuredJson = /*lang=json*/ @"{
  ""profiles"": [
    { ""name"": ""Configured_Source_Profile"", ""dns_mode"": ""direct"", ""processes"": [] }
  ]
}";
            File.WriteAllText(configuredFile, configuredJson);

            var settingsWithConfigured = new AppSettings
            {
                ProfileSources = new List<ProfileSource>
                {
                    new() { Type = "local", Path = configuredFile }
                }
            };

            var coreConfCol = await new ProfileManager(VpnEngine.BuildProfileSources(settingsWithConfigured, userDataDir)).LoadAsync();
            var headlessConfCol = await OfflineProfileHelper.CreateOfflineProfileManager(settingsWithConfigured, userDataDir).LoadAsync();

            Assert(coreConfCol.Profiles.Any(p => p.Name == "Configured_Source_Profile"),
                "Explicit configured source (20) must take precedence over bundled (80) in Core");
            Assert(!coreConfCol.Profiles.Any(p => p.Name == "Linux_Unique_Catalog_Profile"),
                "Bundled profile must not be merged into collection when configured source is selected in Core");
            Assert(headlessConfCol.Profiles.Any(p => p.Name == "Configured_Source_Profile"),
                "Explicit configured source (20) must take precedence over bundled (80) in Headless");
            Assert(!headlessConfCol.Profiles.Any(p => p.Name == "Linux_Unique_Catalog_Profile"),
                "Bundled profile must not be merged into collection when configured source is selected in Headless");

            // 6. Verify the original published catalogs, never the synthetic fixture.
            // The project reference copies Headless's profile payload into test output;
            // its untouched originals were saved before any fixture writes above.
            var shippedProfilesDir = backupDir;
            if (OperatingSystem.IsLinux())
            {
                Assert(File.Exists(Path.Combine(shippedProfilesDir, "default-linux.json")) &&
                       File.Exists(Path.Combine(shippedProfilesDir, "default.json")),
                    "Published Linux and generic profile catalogs must be present in test output");
                File.Delete(platformBundledFile);
                if (File.Exists(genericBundledFile)) File.Delete(genericBundledFile);
                var actualFixtureDataDir = Path.Combine(fixtureDir, "actual_catalog_test");
                var actualProfilesDir = Path.Combine(actualFixtureDataDir, "profiles");
                Directory.CreateDirectory(actualProfilesDir);

                // Copy actual shipped catalog files into temp fixture profiles dir (read-only from source, writing only into temp fixture)
                File.Copy(Path.Combine(shippedProfilesDir, "default-linux.json"), Path.Combine(actualProfilesDir, "default-linux.json"), overwrite: true);
                File.Copy(Path.Combine(shippedProfilesDir, "default.json"), Path.Combine(actualProfilesDir, "default.json"), overwrite: true);

                var actualCoreCol = await new ProfileManager(VpnEngine.BuildProfileSources(new AppSettings(), actualFixtureDataDir)).LoadAsync();
                var actualHeadlessCol = await OfflineProfileHelper.CreateOfflineProfileManager(new AppSettings(), actualFixtureDataDir).LoadAsync();

                // Linux default-linux.json contains "telegram-desktop" in Messengers, whereas default.json has "Telegram.exe"
                var actualCoreMessengers = actualCoreCol.Profiles.FirstOrDefault(p => p.Name == "Messengers");
                Assert(actualCoreMessengers != null && actualCoreMessengers.Processes.Any(pr => pr.Name == "telegram-desktop"),
                    "Actual shipped catalog in Core must resolve Linux process telegram-desktop");
                Assert(!actualCoreMessengers!.Processes.Any(pr => pr.Name == "Telegram.exe"),
                    "Actual shipped catalog in Core must not resolve Windows process Telegram.exe");

                var actualHeadlessMessengers = actualHeadlessCol.Profiles.FirstOrDefault(p => p.Name == "Messengers");
                Assert(actualHeadlessMessengers != null && actualHeadlessMessengers.Processes.Any(pr => pr.Name == "telegram-desktop"),
                    "Actual shipped catalog in Headless must resolve Linux process telegram-desktop");
                Assert(!actualHeadlessMessengers!.Processes.Any(pr => pr.Name == "Telegram.exe"),
                    "Actual shipped catalog in Headless must not resolve Windows process Telegram.exe");
            }
        }
        finally
        {
            // Guaranteed restoration of bundled profiles directory: never mutate shipped files outside temp test output
            try
            {
                if (Directory.Exists(bundledDir))
                {
                    foreach (var file in Directory.GetFiles(bundledDir))
                    {
                        try { File.Delete(file); } catch { }
                    }

                    if (bundledDirExisted && Directory.Exists(backupDir))
                    {
                        foreach (var file in Directory.GetFiles(backupDir))
                        {
                            File.Copy(file, Path.Combine(bundledDir, Path.GetFileName(file)), overwrite: true);
                        }
                    }
                    else if (!bundledDirExisted)
                    {
                        Directory.Delete(bundledDir, recursive: true);
                    }
                }
            }
            catch
            {
                // Best effort restoration
            }
        }
    }

    /// <summary>
    /// Test 3: Invalid or oversize local file fails closed (read-only, no corruption repair writes, safe fallback).
    /// </summary>
    private static async Task CheckInvalidOversizeLocalFileFailClosedAsync(string baseDir)
    {
        var dir = Path.Combine(baseDir, "invalid-oversize-failclosed");
        Directory.CreateDirectory(dir);

        var corruptFile = Path.Combine(dir, "corrupt_profile.json");
        File.WriteAllText(corruptFile, "{ invalid json data :::: [[[\n\0bad");

        var oversizeFile = Path.Combine(dir, "oversize_profile.json");
        // Exceed 1 MiB limit (1024 * 1024 = 1,048,576 bytes)
        var padding = new string('x', 1024 * 1024 + 1024);
        File.WriteAllText(oversizeFile, /*lang=json*/ $"{{\"profiles\": [], \"_pad\": \"{padding}\"}}");

        var session = new FakeRouterSession();
        var backend = new RouterBackend(session, dir);

        var storage = new ConfigStorage(dir);
        var settings = storage.GetSettings();
        settings.ProfileSources = new List<ProfileSource>
        {
            new() { Type = "local", Path = corruptFile },
            new() { Type = "local", Path = oversizeFile }
        };
        settings.ActiveProfile = "NonExistentOrCorrupt";
        storage.SaveSettings(settings, storage.CurrentRevision);

        // Snapshot disk files before test
        var filesBefore = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => (Size: new FileInfo(f).Length, MTime: File.GetLastWriteTimeUtc(f)));

        // 1. settings.get must fail closed, skip corrupt/oversize sources, and fall back to built-in vpn_only
        var getObj = await backend.ExecuteAsync("settings.get", ToJsonElement(new { }));
        var getElem = JsonSerializer.SerializeToElement(getObj);
        Assert(getElem.GetProperty("dnsMode").GetString() == "vpn_only",
            "Corrupt/oversize profiles must fail closed to built-in vpn_only");

        // 2. profiles.list must fail closed and return built-in profiles without crashing
        var listObj = await backend.ExecuteAsync("profiles.list", ToJsonElement(new { }));
        var listElem = JsonSerializer.SerializeToElement(listObj);
        var items = listElem.GetProperty("items").EnumerateArray().ToList();
        Assert(items.Count > 0, "profiles.list must return built-in profiles when local sources are invalid");
        Assert(items.Any(i => i.GetProperty("name").GetString() == "Discord_Privacy"),
            "Built-in fallback profiles must be present in profile list");

        // 3. Verify ZERO corruption repair writes: corrupt file untouched, no .corrupt files generated
        var filesAfter = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
        foreach (var f in filesAfter)
        {
            Assert(!f.Contains(".corrupt"), $"No corruption quarantine files must be created: found {f}");
        }
        Assert(File.ReadAllText(corruptFile) == "{ invalid json data :::: [[[\n\0bad",
            "Corrupt file must not be modified or overwritten");
        Assert(new FileInfo(oversizeFile).Length > 1024 * 1024,
            "Oversize file must remain untouched");
    }

    /// <summary>
    /// Test 4: Read operations (settings.get, profiles.list) must never hit the network and never write to disk.
    /// </summary>
    private static async Task CheckNoNetworkNoWritesForListAndGetAsync(string baseDir)
    {
        var dir = Path.Combine(baseDir, "no-network-no-writes");
        Directory.CreateDirectory(dir);

        var localProfilePath = Path.Combine(dir, "valid_profile.json");
        File.WriteAllText(localProfilePath, /*lang=json*/ @"{
  ""profiles"": [
    { ""name"": ""TestNoNet"", ""dns_mode"": ""smart"", ""processes"": [] }
  ]
}");

        var session = new FakeRouterSession();
        var backend = new RouterBackend(session, dir);

        var storage = new ConfigStorage(dir);
        var settings = storage.GetSettings();
        settings.ProfileSources = new List<ProfileSource>
        {
            // Point github source to unreachable loopback endpoint that would fail if accessed
            new() { Type = "github", Url = "http://127.0.0.1:9999/unreachable_profile.json" },
            new() { Type = "local", Path = localProfilePath }
        };
        settings.ActiveProfile = "TestNoNet";
        storage.SaveSettings(settings, storage.CurrentRevision);

        // Record exact filesystem state
        var snapshotBefore = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => (Length: new FileInfo(f).Length, LastWrite: File.GetLastWriteTimeUtc(f)));

        // Call settings.get multiple times
        for (int i = 0; i < 3; i++)
        {
            var getObj = await backend.ExecuteAsync("settings.get", ToJsonElement(new { }));
            var getElem = JsonSerializer.SerializeToElement(getObj);
            Assert(getElem.GetProperty("dnsMode").GetString() == "smart", "settings.get must succeed offline");
        }

        // Call profiles.list multiple times
        for (int i = 0; i < 3; i++)
        {
            var listObj = await backend.ExecuteAsync("profiles.list", ToJsonElement(new { }));
            var listElem = JsonSerializer.SerializeToElement(listObj);
            var items = listElem.GetProperty("items").EnumerateArray().ToList();
            Assert(items.Any(p => p.GetProperty("name").GetString() == "TestNoNet"),
                "profiles.list must include local profile without network");
        }

        // Verify ZERO disk writes occurred
        var filesAfter = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
        Assert(filesAfter.Length == snapshotBefore.Count,
            $"No new files must be created by get/list: before {snapshotBefore.Count}, after {filesAfter.Length}");

        foreach (var file in filesAfter)
        {
            Assert(snapshotBefore.TryGetValue(file, out var prevInfo), $"Unexpected new file created: {file}");
            var currentInfo = new FileInfo(file);
            Assert(currentInfo.Length == prevInfo.Length, $"File size must not change: {file}");
            Assert(currentInfo.LastWriteTimeUtc == prevInfo.LastWrite, $"File write time must not change: {file}");
        }
    }

    /// <summary>
    /// Test 5: Custom DNS mode semantics and DnsModeOverride preservation.
    /// </summary>
    private static async Task CheckCustomDnsModeAsync(string baseDir)
    {
        var dir = Path.Combine(baseDir, "custom-dns-mode");
        Directory.CreateDirectory(dir);

        var session = new FakeRouterSession();
        var backend = new RouterBackend(session, dir);
        var storage = new ConfigStorage(dir);

        // 1. Standard mode: setting DnsModeOverride to "direct"
        var snap0 = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("snapshot", ToJsonElement(new { })));
        var rev0 = snap0.GetProperty("revision").GetString()!;

        var setDirectSnap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("settings.set", ToJsonElement(new
        {
            revision = rev0,
            values = new { dnsMode = "direct" }
        })));
        var rev1 = setDirectSnap.GetProperty("revision").GetString()!;

        var getDirect = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("settings.get", ToJsonElement(new { })));
        Assert(getDirect.GetProperty("dnsModeOverride").GetString() == "direct", "Explicit override marker must survive settings.get");
        Assert(getDirect.GetProperty("dnsMode").GetString() == "direct",
            "settings.get must return explicit DnsModeOverride 'direct'");

        // 2. Standard mode: reset DnsModeOverride to null
        var setNullSnap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("settings.set", ToJsonElement(new
        {
            revision = rev1,
            values = new { dnsMode = (string?)null }
        })));
        var rev2 = setNullSnap.GetProperty("revision").GetString()!;

        var getNull = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("settings.get", ToJsonElement(new { })));
        Assert(getNull.GetProperty("dnsModeOverride").ValueKind == JsonValueKind.Null, "Reset must retain a null override marker after refresh");
        Assert(getNull.GetProperty("dnsMode").GetString() == "vpn_only",
            "settings.get must revert to profile default 'vpn_only' when DnsModeOverride is null");

        // Verify no sidecar user_profiles.json was created
        var sidecarPath = Path.Combine(dir, "user_profiles.json");
        Assert(!File.Exists(sidecarPath), "user_profiles.json sidecar must never be created");

        // 3. Custom config mode: import custom config
        var customJson = /*lang=json*/ @"{
  ""outbounds"": [
    {
      ""type"": ""vless"",
      ""tag"": ""proxy"",
      ""server"": ""198.51.100.1"",
      ""server_port"": 443,
      ""uuid"": ""00000000-0000-0000-0000-000000000001""
    }
  ]
}";
        var importSnap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("custom.import", ToJsonElement(new
        {
            revision = rev2,
            name = "custom_test",
            text = customJson
        })));
        var revCustom = importSnap.GetProperty("revision").GetString()!;

        // 4. Custom config mode: returns dnsMode = "custom" with non-empty semantics
        var getCustom = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("settings.get", ToJsonElement(new { })));
        Assert(getCustom.GetProperty("dnsMode").GetString() == "custom",
            "Custom mode must report dnsMode 'custom'");
        Assert(getCustom.TryGetProperty("dnsModeSemantics", out var semProp) && !string.IsNullOrWhiteSpace(semProp.GetString()),
            "Custom mode must include dnsModeSemantics");

        // 5. Custom config mode with strictDns = true forces dnsMode = "vpn_only"
        var setStrictSnap = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("settings.set", ToJsonElement(new
        {
            revision = revCustom,
            values = new { strictDns = true }
        })));
        var revStrict = setStrictSnap.GetProperty("revision").GetString()!;

        var getStrict = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("settings.get", ToJsonElement(new { })));
        Assert(getStrict.GetProperty("dnsMode").GetString() == "vpn_only",
            "Custom config with strictDns=true must force dnsMode to 'vpn_only'");

        // 6. Custom mode: settings.set rejecting dnsMode
        bool customSetRejected = false;
        try
        {
            await backend.ExecuteAsync("settings.set", ToJsonElement(new
            {
                revision = revStrict,
                values = new { dnsMode = "smart" }
            }));
        }
        catch (RouterException ex) when (ex.Code == "invalid_argument")
        {
            customSetRejected = true;
        }
        Assert(customSetRejected, "settings.set must reject dnsMode under custom config mode");
    }

    private static async Task CheckCacheDirectoryIsolationAsync(string baseDir)
    {
        var globalDir = Path.Combine(baseDir, "global-cache-fixture");
        var isolatedDir = Path.Combine(baseDir, "isolated-cache-fixture");
        Directory.CreateDirectory(Path.Combine(globalDir, "cache"));
        Directory.CreateDirectory(isolatedDir);
        var cachePath = Path.Combine(globalDir, "cache", "profiles.json");
        var cache = new ProfileCacheFile
        {
            Profiles = new ProfileCollection
            {
                Profiles = new List<Profile> { new() { Name = "ForeignCacheMarker", DnsMode = "smart" } }
            }
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(cache, VPNRouter.Core.Json.AppJsonContext.Default.ProfileCacheFile);
        await File.WriteAllBytesAsync(cachePath, bytes);
        var dataDirField = typeof(VPNRouter.Core.AppPaths).GetField("_dataDir",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var previous = dataDirField.GetValue(null);
        try
        {
            VPNRouter.Core.AppPaths.OverrideDataDir(globalDir);
            var settings = new AppSettings { ProfileSources = new List<ProfileSource>(), ActiveProfile = "ForeignCacheMarker" };
            var isolated = OfflineProfileHelper.GetOfflineProfileCollection(settings, isolatedDir);
            Assert(isolated.Profiles.All(p => p.Name != "ForeignCacheMarker"),
                "Explicit data directory must not inherit global profile cache");
            Assert(OfflineProfileHelper.ResolveLocalProfileDnsMode(settings, isolatedDir) != "smart",
                "Explicit data directory must not inherit foreign cached DNS mode");
            Assert(!Directory.EnumerateFileSystemEntries(isolatedDir).Any(), "Offline reads must not create isolated files");

            var defaultCollection = OfflineProfileHelper.GetOfflineProfileCollection(settings, null);
            Assert(defaultCollection.Profiles.Any(p => p.Name == "ForeignCacheMarker"),
                "Default directory must still read its own cache");

            Directory.CreateDirectory(Path.Combine(isolatedDir, "cache"));
            var ownCachePath = Path.Combine(isolatedDir, "cache", "profiles.json");
            cache.Profiles.Profiles[0].Name = "OwnCacheMarker";
            var ownBytes = JsonSerializer.SerializeToUtf8Bytes(cache, VPNRouter.Core.Json.AppJsonContext.Default.ProfileCacheFile);
            await File.WriteAllBytesAsync(ownCachePath, ownBytes);
            var own = OfflineProfileHelper.GetOfflineProfileCollection(settings, isolatedDir);
            Assert(own.Profiles.Any(p => p.Name == "OwnCacheMarker"), "Explicit directory must use its own cache");
            Assert(own.Profiles.All(p => p.Name != "ForeignCacheMarker"), "Own cache must not merge foreign entries");
            Assert(File.ReadAllBytes(cachePath).SequenceEqual(bytes), "Global cache must remain unchanged");
            Assert(File.ReadAllBytes(ownCachePath).SequenceEqual(ownBytes), "Own cache must remain unchanged");
        }
        finally
        {
            dataDirField.SetValue(null, previous);
        }
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
