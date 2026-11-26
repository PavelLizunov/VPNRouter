#nullable enable

using System.Linq;
using System.Text.Json;
using VPNRouter.Core;
using VPNRouter.Core.Json;
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
    public void Record_LeavesNoTempFile()
    {
        ServerHealthStore.Record(Entry(), ServerHealthVerdict.Healthy);
        Assert.Empty(Directory.GetFiles(AppPaths.CacheDir, "*.tmp"));
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

    private static string StorePath => Path.Combine(AppPaths.CacheDir, "server_health.json");

    private static void WriteStore(ServerHealthFileDto dto)
    {
        Directory.CreateDirectory(AppPaths.CacheDir);
        File.WriteAllText(StorePath,
            JsonSerializer.Serialize(dto, AppJsonContext.Default.ServerHealthFileDto));
    }

    private static ServerHealthFileDto ReadStore()
    {
        var dto = JsonSerializer.Deserialize(
            File.ReadAllText(StorePath), AppJsonContext.Default.ServerHealthFileDto);
        Assert.NotNull(dto);
        return dto!;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Load_CompactsDuplicateRecords_AndKeepsTheLastValue(bool repeatedArray)
    {
        var entry = Entry();
        var record = new ServerHealthRecordDto
        {
            Key = ServerHealthStore.KeyFor(entry),
            RecordedAt = DateTimeOffset.UtcNow,
            Verdict = ServerHealthVerdict.TcpOpenProtocolUntested,
        };
        var first = JsonSerializer.Serialize(record, AppJsonContext.Default.ServerHealthRecordDto);
        record.Verdict = ServerHealthVerdict.Healthy;
        var last = JsonSerializer.Serialize(record, AppJsonContext.Default.ServerHealthRecordDto);
        var json = repeatedArray
            ? $"{{\"Records\":[{first}],\"records\":[{last}]}}"
            : $"{{\"Records\":[{first},{last}]}}";
        Directory.CreateDirectory(AppPaths.CacheDir);
        File.WriteAllText(StorePath, json);

        Assert.Equal(ServerHealthVerdict.Healthy, ServerHealthStore.GetFresh(entry));
        var compacted = File.ReadAllText(StorePath);
        using var document = JsonDocument.Parse(compacted);
        Assert.Single(document.RootElement.EnumerateObject().Where(property =>
            string.Equals(property.Name, "Records", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(ServerHealthVerdict.Healthy, Assert.Single(ReadStore().Records).Verdict);
        Assert.NotEqual(json, compacted);
    }

    [Fact]
    public void ExpiredRecords_ArePrunedOnLoad_AndFileIsRewritten()
    {
        var now = DateTimeOffset.UtcNow;
        var fresh = Entry("1.1.1.1");
        var stale = Entry("2.2.2.2");
        var future = Entry("3.3.3.3");

        WriteStore(new ServerHealthFileDto
        {
            Records =
            {
                new ServerHealthRecordDto
                {
                    Key = ServerHealthStore.KeyFor(fresh),
                    Verdict = ServerHealthVerdict.Healthy,
                    RecordedAt = now.AddHours(-1),
                },
                new ServerHealthRecordDto
                {
                    Key = ServerHealthStore.KeyFor(stale),
                    Verdict = ServerHealthVerdict.ProtocolHandshakeBlockedLikely,
                    RecordedAt = now - ServerHealthStore.FreshTtl - TimeSpan.FromMinutes(1),
                },
                new ServerHealthRecordDto
                {
                    Key = ServerHealthStore.KeyFor(future),
                    Verdict = ServerHealthVerdict.TcpOpenProtocolUntested,
                    RecordedAt = now.AddHours(1),
                },
            },
        });
        ServerHealthStore.ResetForTests();

        Assert.Equal(ServerHealthVerdict.Healthy, ServerHealthStore.GetFresh(fresh));
        Assert.Null(ServerHealthStore.GetFresh(stale));
        Assert.Equal(ServerHealthVerdict.TcpOpenProtocolUntested,
            ServerHealthStore.GetFresh(future));

        var persisted = ReadStore();
        Assert.Equal(2, persisted.Records.Count);
        Assert.DoesNotContain(persisted.Records,
            record => record.Key == ServerHealthStore.KeyFor(stale));
        Assert.Contains(persisted.Records,
            record => record.Key == ServerHealthStore.KeyFor(fresh));
        Assert.Contains(persisted.Records,
            record => record.Key == ServerHealthStore.KeyFor(future));
    }

    [Fact]
    public void ExpiredRecordWrite_IsNotPersisted()
    {
        var fresh = Entry("1.1.1.1");
        var stale = Entry("2.2.2.2");

        ServerHealthStore.Record(fresh, ServerHealthVerdict.Healthy);
        ServerHealthStore.Record(
            stale,
            ServerHealthVerdict.ProtocolHandshakeBlockedLikely,
            now: DateTimeOffset.UtcNow - ServerHealthStore.FreshTtl - TimeSpan.FromMinutes(1));
        ServerHealthStore.ResetForTests();

        var persisted = ReadStore();
        Assert.Single(persisted.Records);
        Assert.Equal(ServerHealthStore.KeyFor(fresh), persisted.Records[0].Key);
    }

    [Fact]
    public void FreshRecordsOverCap_KeepNewest()
    {
        var now = DateTimeOffset.UtcNow;
        var total = ServerHealthStore.MaxRecords + 25;
        var records = Enumerable.Range(0, total)
            .Select(i => new ServerHealthRecordDto
            {
                Key = ServerHealthStore.KeyFor(Entry(
                    $"10.0.{i / 256}.{i % 256}", name: $"srv-{i}")),
                Verdict = ServerHealthVerdict.Healthy,
                RecordedAt = now.AddSeconds(-total + i),
            })
            .ToList();

        WriteStore(new ServerHealthFileDto { Records = records });
        ServerHealthStore.ResetForTests();

        var newest = Entry(
            $"10.0.{(total - 1) / 256}.{(total - 1) % 256}", name: "newest");
        var oldest = Entry("10.0.0.0", name: "oldest");

        Assert.Equal(ServerHealthVerdict.Healthy, ServerHealthStore.GetFresh(newest));
        Assert.Null(ServerHealthStore.GetFresh(oldest));

        var persisted = ReadStore();
        Assert.Equal(ServerHealthStore.MaxRecords, persisted.Records.Count);
        Assert.Contains(persisted.Records,
            record => record.Key == ServerHealthStore.KeyFor(newest));
        Assert.DoesNotContain(persisted.Records,
            record => record.Key == ServerHealthStore.KeyFor(oldest));
    }

    [Fact]
    public void RepeatedRecord_UpdatesVerdict_AndPreservesProviderKey()
    {
        var first = Entry("1.1.1.1");
        var second = Entry("2.2.2.2");

        ServerHealthStore.Record(first, ServerHealthVerdict.Healthy, providerKey: "net:one");
        ServerHealthStore.Record(second, ServerHealthVerdict.TcpOpenProtocolUntested,
            providerKey: "net:two");
        ServerHealthStore.ResetForTests();

        ServerHealthStore.Record(first, ServerHealthVerdict.ProtocolHandshakeBlockedLikely);
        ServerHealthStore.ResetForTests();

        var firstRecord = ServerHealthStore.GetFreshRecord(first);
        Assert.NotNull(firstRecord);
        Assert.Equal(ServerHealthVerdict.ProtocolHandshakeBlockedLikely, firstRecord!.Verdict);
        Assert.Equal("net:one", firstRecord.ProviderKey);

        var secondRecord = ServerHealthStore.GetFreshRecord(second);
        Assert.NotNull(secondRecord);
        Assert.Equal(ServerHealthVerdict.TcpOpenProtocolUntested, secondRecord!.Verdict);
        Assert.Equal("net:two", secondRecord.ProviderKey);
    }

    [Fact]
    public void EquivalentRecord_DoesNotRewriteTheFile()
    {
        var entry = Entry("1.1.1.1");
        var recordedAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        Directory.CreateDirectory(AppPaths.CacheDir);
        var compact = JsonSerializer.Serialize(
            new ServerHealthFileDto
            {
                Records =
                {
                    new ServerHealthRecordDto
                    {
                        Key = ServerHealthStore.KeyFor(entry),
                        Verdict = ServerHealthVerdict.Healthy,
                        RecordedAt = recordedAt,
                        ProviderKey = "net:one",
                    },
                },
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = false,
            });
        File.WriteAllText(StorePath, compact);
        var stamp = File.GetLastWriteTimeUtc(StorePath);

        ServerHealthStore.ResetForTests();
        ServerHealthStore.Record(
            entry, ServerHealthVerdict.Healthy, now: recordedAt, providerKey: "net:one");

        Assert.Equal(compact, File.ReadAllText(StorePath));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(StorePath));
    }

    [Fact]
    public void LargeLegacyHistory_PreservesFreshEntriesWhileDroppingExpiredRecords()
    {
        Directory.CreateDirectory(AppPaths.CacheDir);
        var now = DateTimeOffset.UtcNow;
        var fresh = new ServerHealthRecordDto
        {
            Key = ServerHealthStore.KeyFor(Entry()),
            Verdict = ServerHealthVerdict.Healthy,
            RecordedAt = now,
        };
        var stale = new ServerHealthRecordDto
        {
            Verdict = ServerHealthVerdict.TcpOpenProtocolUntested,
            RecordedAt = now.AddDays(-1),
            ProviderKey = new string('x', 256),
        };
        using (var stream = File.Create(StorePath))
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteNumber("schemaVersion", 1);
            json.WriteStartArray("records");
            for (var i = 0; i < 16_000; i++)
            {
                stale.Key = $"legacy-{i}.example:443:vless";
                JsonSerializer.Serialize(json, stale, AppJsonContext.Default.ServerHealthRecordDto);
            }
            JsonSerializer.Serialize(json, fresh, AppJsonContext.Default.ServerHealthRecordDto);
            json.WriteEndArray();
            json.WriteEndObject();
        }
        Assert.True(new FileInfo(StorePath).Length > 4 * 1024 * 1024);

        ServerHealthStore.ResetForTests();

        Assert.Equal(ServerHealthVerdict.Healthy, ServerHealthStore.GetFresh(Entry()));
        Assert.Equal(fresh.Key, Assert.Single(ReadStore().Records).Key);
    }
}
