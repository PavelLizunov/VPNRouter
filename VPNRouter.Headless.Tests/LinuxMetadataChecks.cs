using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Headless.Features;
using VPNRouter.Headless.Lifecycle;
using VPNRouter.Headless.Storage;

namespace VPNRouter.Headless.Tests;

/// <summary>Offline Linux metadata contract fixtures. No engine start, network probe or desktop shell access.</summary>
public static class LinuxMetadataChecks
{
    public static async Task RunAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vpnrouter-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var previousDataDir = AppPaths.DataDir;
        try
        {
            AppPaths.OverrideDataDir(Path.Combine(dir, "health"));
            CheckLinuxApps(dir);
            CheckServers(dir);
            await CheckUnavailableCodes(dir);
            CheckDiagnostics();
            Console.WriteLine("[LinuxMetadataChecks] All offline shared contract checks passed");
        }
        finally
        {
            AppPaths.OverrideDataDir(previousDataDir);
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void CheckLinuxApps(string dir)
    {
        if (!OperatingSystem.IsLinux()) return;
        var proc = Path.Combine(dir, "proc");
        var user = Path.Combine(dir, "user-apps");
        var system = Path.Combine(dir, "system-apps");
        Directory.CreateDirectory(proc);
        Directory.CreateDirectory(user);
        Directory.CreateDirectory(system);
        var browser = "VeryLongBrowserExecutable";
        ProcessFixture(proc, 1, 1000, "truncated-comm", "/opt/browser/" + browser);
        ProcessFixture(proc, 2, 1000, "duplicate", "/opt/browser/" + browser);
        ProcessFixture(proc, 3, 1001, "foreign-daemon", "/usr/bin/foreign-daemon");
        ProcessFixture(proc, 4, 1000, "kernel-thread", null, "");
        ProcessFixture(proc, 5, 1000, "fallback-name", null);
        ProcessFixture(proc, 6, 1000, "ignored-comm", "/opt/Space App");
        ProcessFixture(proc, 7, 1000, "hidden", null);
        ProcessFixture(proc, 8, 1000, "wmclass", null);
        ProcessFixture(proc, 9, 1000, "gone", null);
        File.Delete(Path.Combine(proc, "9", "cmdline"));
        ProcessFixture(proc, 10, 1000, "ignored", "/opt/ edge app ");
        ProcessFixture(proc, 11, 1000, "deleted", "/opt/DeletedApp (deleted)");
        WriteDesktop(user, "browser.desktop", $"Name=Fixture Browser\nIcon=fixture-browser\nExec=/usr/bin/env MODE=test /opt/browser/{browser} %U\nStartupWMClass=BrowserClass\n");
        WriteDesktop(user, "space.desktop", "Name=Space App\nIcon=/private/icon.png\nExec=\"/opt/Space App\" %U\n");
        WriteDesktop(user, "hidden.desktop", "Name=Hidden User\nHidden=true\nExec=hidden\n");
        WriteDesktop(system, "hidden.desktop", "Name=System Hidden\nExec=hidden\n");
        WriteDesktop(system, "nodisplay.desktop", "Name=No Display\nNoDisplay=true\nExec=fallback-name\n");
        WriteDesktop(system, "wm.desktop", "Name=Window Class App\nStartupWMClass=wmclass\nExec=launcher\n");
        WriteDesktop(system, "browser.desktop", $"Name=Wrong System Browser\nExec={browser}\n");
        Assert(LinuxRunningApps.ParseDesktop("x.desktop", "[Desktop Action Foo]\nName=Wrong\nExec=wrong") == null, "Ignore action sections");
        Assert(LinuxRunningApps.ExecBasename("\"/opt/Space App\" %U") == "Space App", "Quoted Exec basename");
        Assert(LinuxRunningApps.ExecBasename("\"broken") == null, "Reject incomplete quoted Exec");
        Assert(LinuxRunningApps.ParseUid("Name:\tx\nUid:\t1000\t1000\t1000\t1000\n") == 1000, "Parse UID");
        Assert(LinuxRunningApps.ParseUid("Uid:\tinvalid") == null, "Malformed UID skipped");
        var dirs = LinuxRunningApps.ApplicationDirectories("/opt/data:/usr/share:relative", "/home/fixture");
        Assert(dirs.SequenceEqual(new[] { "/home/fixture/.local/share/applications", "/opt/data/applications", "/usr/share/applications" }), "XDG roots and user precedence");
        var clock = DateTime.UtcNow;
        var scanner = new LinuxRunningApps(proc, 1000, new[] { user, system }, () => clock);
        var timer = Stopwatch.StartNew();
        var apps = scanner.Read();
        var cold = timer.Elapsed.TotalMilliseconds;
        Assert(apps.Count == 7, "UID, kernel, exited process filtering and name deduplication");
        var first = apps.Single(a => a.name == browser);
        Assert(first == new RunningApp(browser, "Fixture Browser", "fixture-browser", "browser.desktop", false), "Exact executable name and desktop metadata");
        Assert(apps.Take(3).All(a => a.desktopId != null), "Desktop apps come first");
        Assert(apps.Single(a => a.name == "Space App").icon == null, "Do not expose absolute icon paths");
        Assert(apps.Single(a => a.name == "fallback-name").desktopId == null, "NoDisplay suppresses metadata");
        Assert(apps.Single(a => a.name == "hidden").desktopId == null, "Hidden user entry shadows system entry");
        Assert(apps.Any(a => a.name == "DeletedApp"), "Deleted executable basename");
        WriteDesktop(user, "browser.desktop", $"Name=Updated Browser\nExec={browser}\n");
        Assert(scanner.Read().Single(a => a.name == browser).displayName == "Fixture Browser", "Desktop index cached before TTL");
        clock = clock.AddSeconds(31);
        Assert(scanner.Read().Single(a => a.name == browser).displayName == "Updated Browser", "Desktop index refreshed after TTL");
        var many = LinuxRunningApps.Match(Enumerable.Range(0, 1000).Select(i => "unknown-" + i).Append(browser),
            LinuxRunningApps.ReadDesktopIndex(new[] { user }));
        Assert(many.Count == 200 && many[0].name == browser, "200-entry cap prefers desktop apps");
        for (int i = 100; i < 300; i++) ProcessFixture(proc, i, 1000, "fallback-" + i, null);
        timer.Restart();
        var bounded = scanner.Read();
        Console.WriteLine($"[LinuxMetadataChecks] Fixture apps discovery: cold={cold:F2} ms, warm/211 proc dirs={timer.Elapsed.TotalMilliseconds:F2} ms");
        Assert(bounded.Count == 200 && bounded.Any(a => a.name == browser), "Bounded proc reader retains desktop matches");
        var storage = new ConfigStorage(Path.Combine(dir, "apps"));
        var feature = new AppRoutingFeature(storage, scanner);
        var list = Json(feature.List());
        Assert(list.GetProperty("running").EnumerateArray().Any(n => n.GetString() == browser), "Legacy running retained");
        var names = list.GetProperty("runningApps").EnumerateArray().Select(a => a.GetProperty("name").GetString()!).ToArray();
        feature.SetApps(Json(new { revision = storage.CurrentRevision, mode = "include", names }));
        var saved = storage.GetSettings();
        Assert(saved.App.RoutingAppsInclude.SequenceEqual(names), "apps.set accepts every exact runningApps.name including spaces");
        // Verify the existing generator consumes the persisted exact name as process_name.
        saved.Vless = new VlessConfig { Servers = new() { Server("manual") } };
        var config = ConfigGenerator.Generate(new Profile { Name = "Fixture" }, saved.App.RoutingAppsInclude, saved);
        Assert(config.Route.Rules.Any(r => r.ProcessName?.Contains(browser) == true && r.Outbound == "proxy"), "Generator emits exact Linux process_name");
        Sample("apps.list", new { runningApps = apps });
    }

    private static void CheckServers(string dir)
    {
        var storage = new ConfigStorage(Path.Combine(dir, "servers"));
        var settings = storage.GetSettings();
        // Identical entries across sources exercise the single shared ID map.
        settings.Vless.Servers = new() { Server("Same") };
        settings.App.Subscriptions = new() {
            new() { Id = "sub-fixture", Name = "Fixture Subscription", Enabled = true, Servers = new() { Server("Same"), Server("Same") } },
            new() { Id = "sub-disabled", Enabled = false, Servers = new() { Server("Disabled") } }
        };
        storage.SaveSettings(settings, storage.CurrentRevision);
        var feature = new ServerFeature(storage);
        var listed = Json(feature.List(Json(new { })));
        var items = listed.GetProperty("items").EnumerateArray().ToArray();
        Assert(items.Length == 3 && items.Select(s => s.GetProperty("id").GetString()).Distinct().Count() == 3, "Source duplicates have unique IDs");
        Assert(items[0].GetProperty("source").GetString() == "manual" && items[0].GetProperty("subscriptionId").ValueKind == JsonValueKind.Null && items[0].GetProperty("removable").GetBoolean(), "Manual source metadata");
        foreach (var item in items.Skip(1))
        {
            Assert(item.GetProperty("source").GetString() == "subscription" && item.GetProperty("subscriptionId").GetString() == "sub-fixture" && !item.GetProperty("removable").GetBoolean(), "Subscription source metadata");
            var revision = storage.CurrentRevision;
            ExpectCode("not_removable", () => feature.Remove(Json(new { revision, id = item.GetProperty("id").GetString() })));
            Assert(storage.CurrentRevision == revision && Json(feature.List(Json(new { }))).GetRawText() == listed.GetRawText(), "Refused removal leaves settings/revision intact");
        }
        Sample("servers.list", listed);
        Sample("servers.remove", new { error = new { code = "not_removable", message = RouterException.GetSafeMessage("not_removable") } });
        feature.Remove(Json(new { revision = storage.CurrentRevision, id = items[0].GetProperty("id").GetString() }));
        Assert(Json(feature.List(Json(new { }))).GetProperty("total").GetInt32() == 2, "Manual removal works with duplicate subscription servers");
        ExpectCode("not_found", () => feature.Remove(Json(new { revision = storage.CurrentRevision, id = "absent" })));
        var legacyStorage = new ConfigStorage(Path.Combine(dir, "legacy"));
        var legacy = legacyStorage.GetSettings();
        legacy.Vless.Servers.Clear();
        legacy.Vless.Server = "192.0.2.4";
        legacyStorage.SaveSettings(legacy, legacyStorage.CurrentRevision);
        var legacyFeature = new ServerFeature(legacyStorage);
        var legacyItem = Json(legacyFeature.List(Json(new { }))).GetProperty("items")[0];
        Assert(!legacyItem.GetProperty("removable").GetBoolean(), "Legacy synthesized server cannot be removed individually");
        ExpectCode("not_removable", () => legacyFeature.Remove(Json(new { revision = legacyStorage.CurrentRevision, id = legacyItem.GetProperty("id").GetString() })));
    }

    private static async Task CheckUnavailableCodes(string dir)
    {
        var cases = new[] {
            (SingBoxRuntimeFailure.None, (string?)null), (SingBoxRuntimeFailure.Missing, "runtime-missing"),
            (SingBoxRuntimeFailure.MissingCapabilities, "runtime-capabilities"), (SingBoxRuntimeFailure.Untrusted, "runtime-untrusted"),
            (SingBoxRuntimeFailure.Changed, "runtime-untrusted"), (SingBoxRuntimeFailure.PrerequisiteUnavailable, "sandbox"),
            ((SingBoxRuntimeFailure)999, "other")
        };
        foreach (var (failure, code) in cases)
        {
            Assert(PlatformCapabilityVerifier.RuntimeFailureCode(failure) == code, "Typed runtime failure mapping " + failure);
            if (failure != (SingBoxRuntimeFailure)999)
                Assert(PlatformCapabilityVerifier.GetUnavailableReasonCode(() => OwnershipCheckResult.Free(), capabilityReadinessFunc: () =>
                {
                    if (failure != SingBoxRuntimeFailure.None) throw new SingBoxRuntimePolicyException(SingBoxRuntimeOperation.Probe, failure);
                    return true;
                }) == code, "Typed failure from readiness path " + failure);
        }
        foreach (var ownership in new[] { OwnershipCheckResult.HeldByAnother(42, "fixture"), OwnershipCheckResult.Unavailable("fixture") })
            Assert(PlatformCapabilityVerifier.GetUnavailableReasonCode(() => ownership, capabilityReadinessFunc: () => true) == "ownership", "Held/unverified ownership code");
        Assert(PlatformCapabilityVerifier.GetUnavailableReasonCode(() => throw new IOException(), capabilityReadinessFunc: () => true) == "ownership", "Ownership probe exception");
        Assert(PlatformCapabilityVerifier.GetUnavailableReasonCode(capabilityReadinessFunc: () => false) == "other", "Unspecified readiness failure");
        if (!OperatingSystem.IsLinux()) return;
        var binary = Path.Combine(dir, "fixture-runtime");
        File.WriteAllText(binary, "fixture content, never executed");
        using var scope = SingBoxRuntimePolicy.EnterScope(SingBoxRuntimePolicy.ForTestFile(binary,
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(binary))), () => true));
        var runtimeFailure = SingBoxRuntimeFailure.None;
        var ownershipStatus = OwnershipCheckResult.Unavailable("fixture");
        var engine = new RuntimePolicyChecks.FakeLifecycleEngine { CapabilityReadinessFunc = () =>
        {
            if (runtimeFailure != SingBoxRuntimeFailure.None)
                throw new SingBoxRuntimePolicyException(SingBoxRuntimeOperation.Probe, runtimeFailure);
            return true;
        } };
        await using var session = new RouterSession(engine, () => ownershipStatus);
        await using var backend = new RouterBackend(session, Path.Combine(dir, "snapshot"));
        var snap = Json(backend.GetSnapshot());
        Assert(!snap.GetProperty("capabilities").GetProperty("connect").GetBoolean(), "Ownership blocks connect capability");
        Assert(snap.GetProperty("unavailableReasonCode").GetString() == "ownership", "Snapshot owns availability code");
        Sample("snapshot", new { unavailableReasonCode = snap.GetProperty("unavailableReasonCode"), unavailableReason = snap.GetProperty("unavailableReason") });
        ownershipStatus = OwnershipCheckResult.Free();
        foreach (var (failure, code) in cases.Where(c => c.Item1 != (SingBoxRuntimeFailure)999))
        {
            runtimeFailure = failure;
            var snapshot = Json(backend.GetSnapshot());
            Assert(snapshot.GetProperty("unavailableReasonCode").GetString() == code, "Snapshot maps " + failure);
            Assert(snapshot.GetProperty("capabilities").GetProperty("connect").GetBoolean() == (code == null), "Availability consistent with code " + failure);
            Sample("snapshot.code", new { unavailableReasonCode = snapshot.GetProperty("unavailableReasonCode") });
        }
        Assert(engine.StartCount == 0, "Availability never starts engine");
    }

    private static void CheckDiagnostics()
    {
        Assert(!File.Exists(AppPaths.ConfigYamlPath), "First launch fixture has no config");
        var check = Json(new DiagnosticsFeature(() => false).Check());
        var items = check.GetProperty("items").EnumerateArray().ToArray();
        var onboarding = items.Where(i => i.GetProperty("message").GetString() == "Not configured yet: add a subscription or a server").ToArray();
        Assert(onboarding.Length >= 2 && onboarding.All(i => i.GetProperty("status").GetString() == "info"), "Missing first-launch config and directories are informational");
        Assert(!items.Any(i => i.GetProperty("status").GetString() == "warn" &&
            (i.GetProperty("message").GetString()!.Contains("directory missing") || i.GetProperty("message").GetString()!.Contains("config.yaml missing"))), "No first-launch config/dir warning");
        Sample("diagnostics.check", new { items = onboarding.Take(1).ToArray() });
        Assert(!Directory.Exists(AppPaths.DataDir), "Diagnostics do not create first-launch directories");
        Directory.CreateDirectory(AppPaths.DataDir);
        File.WriteAllText(AppPaths.ConfigYamlPath, "app: [malformed");
        var broken = Json(new DiagnosticsFeature(() => false).Check()).GetProperty("items").EnumerateArray().ToArray();
        Assert(broken.Any(i => i.GetProperty("status").GetString() == "error"), "Malformed existing config remains error");
        Assert(broken.Any(i => i.GetProperty("status").GetString() == "warn" && i.GetProperty("message").GetString()!.Contains("directory missing")), "Existing config missing directories remain warning");
    }

    private static VlessServerEntry Server(string name) => new() { Name = name, Server = "192.0.2.1", Port = 443,
        Protocol = "vless", Uuid = "00000000-0000-0000-0000-000000000001", Security = "tls" };

    private static void ProcessFixture(string root, int pid, uint uid, string comm, string? exe, string cmdline = "app\0")
    {
        var dir = Path.Combine(root, pid.ToString());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "status"), $"Name:\t{comm}\nUid:\t{uid}\t{uid}\t{uid}\t{uid}\n");
        File.WriteAllText(Path.Combine(dir, "comm"), comm + "\n");
        File.WriteAllText(Path.Combine(dir, "cmdline"), cmdline);
        if (exe != null) File.CreateSymbolicLink(Path.Combine(dir, "exe"), exe);
    }

    private static void WriteDesktop(string dir, string id, string fields) =>
        File.WriteAllText(Path.Combine(dir, id), "[Desktop Entry]\nType=Application\n" + fields);
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static void Sample(string method, object value) => Console.WriteLine("METADATA_SAMPLE " + method + " " + JsonSerializer.Serialize(value));
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void ExpectCode(string code, Action action)
    {
        try { action(); }
        catch (RouterException ex) when (ex.Code == code) { return; }
        throw new InvalidOperationException("Expected " + code);
    }
}
