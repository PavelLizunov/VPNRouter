#nullable enable

using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Pins <see cref="ServerHealthStore"/> (urltest R5): identity-keyed persisted
/// verdicts with a freshness TTL. Isolated per-test via AppPaths.OverrideDataDir
/// so nothing touches the real ProgramData cache.
/// </summary>
public class ServerHealthStoreTests : IDisposable
{
    private readonly string _prevDataDir;
    private readonly string _tempDir;

    public ServerHealthStoreTests()
    {
        _prevDataDir = AppPaths.DataDir;
        _tempDir = Path.Combine(Path.GetTempPath(), $"vpnrouter-shs-{Guid.NewGuid():N}");
        AppPaths.OverrideDataDir(_tempDir);
        ServerHealthStore.ResetForTests();
    }

    public void Dispose()
    {
        ServerHealthStore.ResetForTests();
        AppPaths.OverrideDataDir(_prevDataDir);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private static VlessServerEntry Entry(string server = "1.2.3.4", int port = 443,
        string? protocol = "vless", string name = "n1")
        => new() { Name = name, Server = server, Port = port, Protocol = protocol };

    [Fact]
    public void Record_GetFresh_RoundTrips()
    {
        ServerHealthStore.Record(Entry(), ServerHealthVerdict.ProtocolHandshakeBlockedLikely);
        Assert.Equal(ServerHealthVerdict.ProtocolHandshakeBlockedLikely,
            ServerHealthStore.GetFresh(Entry()));
    }

    [Fact]
    public void Key_IsNameIndependent_SubscriptionRefreshSafe()
    {
        // Subscription refresh recreates entries with new names — the verdict
        // must survive because identity = server:port:protocol.
        ServerHealthStore.Record(Entry(name: "old-name"), ServerHealthVerdict.Healthy);
        Assert.Equal(ServerHealthVerdict.Healthy,
            ServerHealthStore.GetFresh(Entry(name: "renamed-after-refresh")));
    }

    [Fact]
    public void Unknown_IsIgnored_NeverOverwritesARealVerdict()
    {
        ServerHealthStore.Record(Entry(), ServerHealthVerdict.Healthy);
        ServerHealthStore.Record(Entry(), ServerHealthVerdict.Unknown);
        Assert.Equal(ServerHealthVerdict.Healthy, ServerHealthStore.GetFresh(Entry()));
    }

    [Fact]
    public void StaleRecord_PastTtl_IsNotFresh()
    {
        var t0 = new DateTimeOffset(2026, 7, 9, 12, 0, 0, TimeSpan.Zero);
        ServerHealthStore.Record(Entry(), ServerHealthVerdict.ProtocolHandshakeBlockedLikely, now: t0);

        var justInside = t0 + ServerHealthStore.FreshTtl;
        var justPast = t0 + ServerHealthStore.FreshTtl + TimeSpan.FromMinutes(1);
        Assert.NotNull(ServerHealthStore.GetFresh(Entry(), now: justInside));
        Assert.Null(ServerHealthStore.GetFresh(Entry(), now: justPast));
    }

    [Fact]
    public void SurvivesReload_FromDisk()
    {
        ServerHealthStore.Record(Entry(), ServerHealthVerdict.TcpOpenProtocolUntested);
        ServerHealthStore.ResetForTests();   // drop in-memory cache → forces file read
        Assert.Equal(ServerHealthVerdict.TcpOpenProtocolUntested,
            ServerHealthStore.GetFresh(Entry()));
    }

    [Fact]
    public void CorruptFile_IsGraceful_AndRecoverable()
    {
        Directory.CreateDirectory(AppPaths.CacheDir);
        File.WriteAllText(Path.Combine(AppPaths.CacheDir, "server_health.json"), "{ not json !!");
        ServerHealthStore.ResetForTests();

        Assert.Null(ServerHealthStore.GetFresh(Entry()));                       // no throw
        ServerHealthStore.Record(Entry(), ServerHealthVerdict.Healthy);         // still writable
        ServerHealthStore.ResetForTests();
        Assert.Equal(ServerHealthVerdict.Healthy, ServerHealthStore.GetFresh(Entry()));
    }

    [Fact]
    public void DifferentPortOrProtocol_AreDistinctIdentities()
    {
        ServerHealthStore.Record(Entry(port: 443), ServerHealthVerdict.ProtocolHandshakeBlockedLikely);
        Assert.Null(ServerHealthStore.GetFresh(Entry(port: 8443)));
        Assert.Null(ServerHealthStore.GetFresh(Entry(protocol: "hysteria2")));
    }

    // ── R3: provider key persistence ─────────────────────────────────────────

    [Fact]
    public void ProviderKey_RoundTrips_AndSurvivesKeylessOverwrite()
    {
        ServerHealthStore.Record(Entry(), ServerHealthVerdict.ProtocolHandshakeBlockedLikely,
            providerKey: "net:1.2.3.0/24");
        Assert.Equal("net:1.2.3.0/24", ServerHealthStore.GetFreshRecord(Entry())!.ProviderKey);

        // A later verdict written WITHOUT a key (e.g. before the background DNS
        // resolve lands) must not wipe the established grouping key.
        ServerHealthStore.Record(Entry(), ServerHealthVerdict.Healthy);
        var rec = ServerHealthStore.GetFreshRecord(Entry())!;
        Assert.Equal(ServerHealthVerdict.Healthy, rec.Verdict);
        Assert.Equal("net:1.2.3.0/24", rec.ProviderKey);
    }
}
