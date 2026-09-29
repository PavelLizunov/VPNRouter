using System;
using System.Collections.Generic;
using System.Linq;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

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

        Assert.Empty(result.Changes);
        Assert.Empty(store.Quarantined);
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
        Assert.Equal("garbage", store.Live["routing_mode"]);
    }

    [Fact]
    public void GetThrows_KeyIsSkipped_OtherKeysStillProcessed()
    {
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
