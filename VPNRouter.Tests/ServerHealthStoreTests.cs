#nullable enable

using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

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
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static VlessServerEntry Entry(string server = "1.2.3.4", int port = 443,
        string? protocol = "vless", string name = "n1")
        => new() { Name = name, Server = server, Port = port, Protocol = protocol! };

    [Fact]
    public void Record_GetFresh_RoundTrips()
    {
        ServerHealthStore.Record(Entry(), ServerHealthVerdict.ProtocolHandshakeBlockedLikely);
        Assert.Equal(ServerHealthVerdict.ProtocolHandshakeBlockedLikely,
            ServerHealthStore.GetFresh(Entry()));
    }

    [Fact]
    public void Unknown_IsIgnored_NeverOverwritesARealVerdict()
    {
        ServerHealthStore.Record(Entry(), ServerHealthVerdict.Healthy);
        ServerHealthStore.Record(Entry(), ServerHealthVerdict.Unknown);
        Assert.Equal(ServerHealthVerdict.Healthy, ServerHealthStore.GetFresh(Entry()));
    }

    [Fact]
    public void SurvivesReload_FromDisk()
    {
        ServerHealthStore.Record(Entry(), ServerHealthVerdict.TcpOpenProtocolUntested);
        ServerHealthStore.ResetForTests();
        Assert.Equal(ServerHealthVerdict.TcpOpenProtocolUntested,
            ServerHealthStore.GetFresh(Entry()));
    }

    [Fact]
    public void CorruptFile_IsGraceful_AndRecoverable()
    {
        Directory.CreateDirectory(AppPaths.CacheDir);
        File.WriteAllText(Path.Combine(AppPaths.CacheDir, "server_health.json"), "{ not json !!");
        ServerHealthStore.ResetForTests();

        Assert.Null(ServerHealthStore.GetFresh(Entry()));
        ServerHealthStore.Record(Entry(), ServerHealthVerdict.Healthy);
        ServerHealthStore.ResetForTests();
        Assert.Equal(ServerHealthVerdict.Healthy, ServerHealthStore.GetFresh(Entry()));
    }
}
