using System.Text;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// F2 (v2.45.0): the streaming /connections summary parser reads totals + the
/// connection count via Utf8JsonReader without materializing a JsonElement per
/// connection (the ~2s connected-poll hot path).
/// </summary>
public sealed class ClashConnectionsParseTests
{
    private static (long down, long up, int count) Parse(string json)
    {
        var ok = ClashSingBoxApi.ParseConnectionsSummary(
            Encoding.UTF8.GetBytes(json), out var d, out var u, out var c);
        Assert.True(ok);
        return (d, u, c);
    }

    [Fact]
    public void ReadsTotals_AndCountsConnections()
    {
        var (d, u, c) = Parse(
            "{\"downloadTotal\":12345,\"uploadTotal\":678,\"connections\":[" +
            "{\"id\":\"a\",\"upload\":1,\"download\":2,\"metadata\":{\"host\":\"x\"}}," +
            "{\"id\":\"b\",\"upload\":3,\"download\":4}]}");
        Assert.Equal(12345, d);
        Assert.Equal(678, u);
        Assert.Equal(2, c);
    }

    [Fact]
    public void EmptyConnections_CountZero()
    {
        var (d, u, c) = Parse("{\"downloadTotal\":5,\"uploadTotal\":6,\"connections\":[]}");
        Assert.Equal(5, d);
        Assert.Equal(6, u);
        Assert.Equal(0, c);
    }

    [Fact]
    public void NullConnections_CountZero()
    {
        var (_, _, c) = Parse("{\"downloadTotal\":5,\"uploadTotal\":6,\"connections\":null}");
        Assert.Equal(0, c);
    }

    [Fact]
    public void PropertyOrderIndependent()
    {
        var (d, u, c) = Parse("{\"connections\":[{\"id\":\"a\"}],\"uploadTotal\":9,\"downloadTotal\":8}");
        Assert.Equal(8, d);
        Assert.Equal(9, u);
        Assert.Equal(1, c);
    }

    [Fact]
    public void NestedConnectionFieldsDoNotLeak()
    {
        // A connection element carrying its OWN nested downloadTotal / connections
        // must NOT override the top-level totals or inflate the count — Skip()
        // walks past each element's subtree.
        var (d, u, c) = Parse(
            "{\"downloadTotal\":100,\"connections\":[" +
            "{\"metadata\":{\"downloadTotal\":999,\"connections\":[1,2,3]}}],\"uploadTotal\":50}");
        Assert.Equal(100, d);
        Assert.Equal(50, u);
        Assert.Equal(1, c);
    }

    [Fact]
    public void Malformed_ReturnsFalse()
    {
        Assert.False(ClashSingBoxApi.ParseConnectionsSummary(
            Encoding.UTF8.GetBytes("{not valid json"), out _, out _, out _));
    }
}
