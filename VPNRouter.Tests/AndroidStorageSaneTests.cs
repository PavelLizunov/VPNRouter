using System;
using System.Collections.Generic;
using System.Linq;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// v2.32.0 (AND-SR-1, plans/vpnrouter-platform-current-diff.md §16.3) —
/// pin the central Android self-repair pass against an in-memory
/// SharedPreferences stand-in. The real
/// <see cref="VPNRouter.Android.AndroidStorage.RepairAllOnLoad"/> entry
/// point hits <c>Application.Context</c> and can't run on net8.0;
/// <see cref="AndroidStorageSane.RepairAllOnLoad"/> takes get/set
/// delegates so we drive it from a <see cref="Dictionary{TKey, TValue}"/>
/// here. The Android entry point is a thin wrapper, exercised on-device.
///
/// <para>Why these tests matter: a regression in the repair pass means a
/// hand-edited / older / corrupted preference value (KeyRoutingMode =
/// "split-old", KeyDnsStrategy = "system", KeyTheme = "auto", …) reaches
/// the routing engine as an unsupported string and either crashes or
/// silently routes wrong. The tests pin:</para>
///
/// <list type="bullet">
///   <item>Empty store → no changes (first-run path is undisturbed —
///   we don't burn SharedPreferences commits on a fresh install).</item>
///   <item>Valid stored values → no changes (idempotent against clean
///   state).</item>
///   <item>Unknown stored value → quarantined + reset to spec default;
///   one human-readable change line per repair so the UI banner matches
///   <see cref="VPNRouter.Core.Services.SettingsLoader.LastRecoveryNotice"/>
///   in tone.</item>
///   <item>Case-insensitive match → silently normalised to canonical
///   casing (no notice — mismatched casing is a stylistic difference,
///   not corruption).</item>
///   <item>A second pass after the first repaired everything → empty
///   result (idempotent).</item>
/// </list>
/// </summary>
[Trait("Category", "Unit")]
[Trait("Phase", "Phase0")]
[Trait("Layer", "Core")]
public class AndroidStorageSaneTests
{
    private sealed class FakeStore
    {
        public Dictionary<string, string?> Live { get; } = new();
        public Dictionary<string, string?> Quarantined { get; } = new();

        public string? Get(string key) => Live.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string? value) => Live[key] = value;
        public void Quarantine(string key, string? value) =>
            Quarantined[$"{key}__corrupt"] = value;
    }

    /// <summary>
    /// The exact spec list AndroidStorage.RepairAllOnLoad uses on-device.
    /// Duplicated here so the tests stay decoupled from the Android-only
    /// VPNRouter.Android.AndroidStorage class — but if either side adds a
    /// new key, the corresponding test should grow alongside.
    /// </summary>
    private static IReadOnlyList<AndroidStorageSane.EnumKeySpec> Specs => new[]
    {
        new AndroidStorageSane.EnumKeySpec(
            "routing_mode", new[] { "split", "full" }, "split"),
        new AndroidStorageSane.EnumKeySpec(
            "dns_strategy",
            new[] { "ipv4_only", "ipv6_only", "prefer_ipv4", "prefer_ipv6", "default" },
            "ipv4_only"),
        new AndroidStorageSane.EnumKeySpec(
            "update_channel", new[] { "stable", "experimental" }, "stable"),
        new AndroidStorageSane.EnumKeySpec(
            "theme", new[] { "light", "dark", "system" }, "light"),
        new AndroidStorageSane.EnumKeySpec(
            "dpi_bypass_mode", new[] { "off", "standard", "aggressive" }, "off"),
    };

    [Fact]
    public void EmptyStore_NoChanges_NoWrites()
    {
        var store = new FakeStore();

        var result = AndroidStorageSane.RepairAllOnLoad(
            store.Get, store.Set, Specs, store.Quarantine);

        Assert.Empty(result.Changes);
        Assert.Empty(store.Live);
        Assert.Empty(store.Quarantined);
    }

    [Fact]
    public void ValidValues_NoChanges()
    {
        var store = new FakeStore();
        store.Live["routing_mode"] = "split";
        store.Live["dns_strategy"] = "ipv4_only";
        store.Live["theme"] = "dark";
        store.Live["update_channel"] = "stable";
        store.Live["dpi_bypass_mode"] = "standard";

        var result = AndroidStorageSane.RepairAllOnLoad(
            store.Get, store.Set, Specs, store.Quarantine);

        Assert.Empty(result.Changes);
        Assert.Empty(store.Quarantined);
        Assert.Equal("split", store.Live["routing_mode"]);
        Assert.Equal("ipv4_only", store.Live["dns_strategy"]);
        Assert.Equal("dark", store.Live["theme"]);
        Assert.Equal("stable", store.Live["update_channel"]);
        Assert.Equal("standard", store.Live["dpi_bypass_mode"]);
    }

    [Fact]
    public void BadRoutingMode_QuarantinedAndRepairedToSplit()
    {
        var store = new FakeStore();
        store.Live["routing_mode"] = "garbage";

        var result = AndroidStorageSane.RepairAllOnLoad(
            store.Get, store.Set, Specs, store.Quarantine);

        Assert.Single(result.Changes);
        Assert.Contains("routing_mode", result.Changes[0]);
        Assert.Contains("garbage", result.Changes[0]);
        Assert.Contains("split", result.Changes[0]);
        Assert.Equal("split", store.Live["routing_mode"]);
        Assert.Equal("garbage", store.Quarantined["routing_mode__corrupt"]);
    }

    [Fact]
    public void BadDnsStrategy_RepairsToIpv4Only()
    {
        var store = new FakeStore();
        store.Live["dns_strategy"] = "weird_strategy";

        var result = AndroidStorageSane.RepairAllOnLoad(
            store.Get, store.Set, Specs, store.Quarantine);

        Assert.Single(result.Changes);
        Assert.Equal("ipv4_only", store.Live["dns_strategy"]);
        Assert.Equal("weird_strategy", store.Quarantined["dns_strategy__corrupt"]);
    }

    [Fact]
    public void BadTheme_RepairsToLight()
    {
        var store = new FakeStore();
        store.Live["theme"] = "neon";

        var result = AndroidStorageSane.RepairAllOnLoad(
            store.Get, store.Set, Specs, store.Quarantine);

        Assert.Single(result.Changes);
        Assert.Equal("light", store.Live["theme"]);
    }

    [Fact]
    public void MultipleBadValues_AllRepaired_AllRecorded()
    {
        var store = new FakeStore();
        store.Live["routing_mode"] = "tunnel";
        store.Live["dns_strategy"] = "auto";
        store.Live["theme"] = "midnight";

        var result = AndroidStorageSane.RepairAllOnLoad(
            store.Get, store.Set, Specs, store.Quarantine);

        Assert.Equal(3, result.Changes.Count);
        Assert.Equal("split", store.Live["routing_mode"]);
        Assert.Equal("ipv4_only", store.Live["dns_strategy"]);
        Assert.Equal("light", store.Live["theme"]);
        Assert.Equal(3, store.Quarantined.Count);
    }

    [Fact]
    public void CaseInsensitiveMatch_NormalizedSilently()
    {
        var store = new FakeStore();
        store.Live["routing_mode"] = "SPLIT";
        store.Live["theme"] = "Dark";

        var result = AndroidStorageSane.RepairAllOnLoad(
            store.Get, store.Set, Specs, store.Quarantine);

        // Casing diff is not corruption — no recovery notice.
        Assert.Empty(result.Changes);
        Assert.Empty(store.Quarantined);
        // …but the canonical casing IS persisted so downstream ordinal
        // comparisons don't surprise on next read.
        Assert.Equal("split", store.Live["routing_mode"]);
        Assert.Equal("dark", store.Live["theme"]);
    }

    [Fact]
    public void Idempotent_SecondPassNoChanges()
    {
        var store = new FakeStore();
        store.Live["routing_mode"] = "garbage";
        store.Live["theme"] = "weird";

        var first = AndroidStorageSane.RepairAllOnLoad(
            store.Get, store.Set, Specs, store.Quarantine);
        Assert.Equal(2, first.Changes.Count);

        var second = AndroidStorageSane.RepairAllOnLoad(
            store.Get, store.Set, Specs, store.Quarantine);
        Assert.Empty(second.Changes);
    }

    [Fact]
    public void NoQuarantineDelegate_StillRepairsAndRecords()
    {
        // The quarantine callback is optional — Android's on-device path
        // always supplies one, but the helper must not crash if a future
        // caller (or test) skips it.
        var store = new FakeStore();
        store.Live["routing_mode"] = "garbage";

        var result = AndroidStorageSane.RepairAllOnLoad(
            store.Get, store.Set, Specs, quarantine: null);

        Assert.Single(result.Changes);
        Assert.Equal("split", store.Live["routing_mode"]);
        Assert.Empty(store.Quarantined);
    }

    [Fact]
    public void EmptyKeySpecList_NoChanges_NoCrash()
    {
        var store = new FakeStore();
        store.Live["routing_mode"] = "garbage";

        var result = AndroidStorageSane.RepairAllOnLoad(
            store.Get, store.Set,
            Array.Empty<AndroidStorageSane.EnumKeySpec>(),
            store.Quarantine);

        Assert.Empty(result.Changes);
        // The bad value is left alone — no spec means no rule.
        Assert.Equal("garbage", store.Live["routing_mode"]);
    }

    [Fact]
    public void GetThrows_KeyIsSkipped_OtherKeysStillProcessed()
    {
        // SR-4: backend failure on one key must not block repair of others.
        var store = new FakeStore();
        store.Live["dns_strategy"] = "weird";
        store.Live["theme"] = "neon";

        string? throwingGet(string key)
        {
            if (key == "routing_mode")
                throw new InvalidOperationException("simulated backend failure");
            return store.Get(key);
        }

        var result = AndroidStorageSane.RepairAllOnLoad(
            throwingGet, store.Set, Specs, store.Quarantine);

        Assert.Equal(2, result.Changes.Count);
        Assert.Equal("ipv4_only", store.Live["dns_strategy"]);
        Assert.Equal("light", store.Live["theme"]);
    }

    [Fact]
    public void NullDelegates_ThrowArgumentNull()
    {
        var store = new FakeStore();
        Assert.Throws<ArgumentNullException>(() =>
            AndroidStorageSane.RepairAllOnLoad(null!, store.Set, Specs));
        Assert.Throws<ArgumentNullException>(() =>
            AndroidStorageSane.RepairAllOnLoad(store.Get, null!, Specs));
        Assert.Throws<ArgumentNullException>(() =>
            AndroidStorageSane.RepairAllOnLoad(store.Get, store.Set, null!));
    }
}
