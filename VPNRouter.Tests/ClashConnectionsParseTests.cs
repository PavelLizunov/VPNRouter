using System.Text;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

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

    [Fact]
    public void NonInt64Total_ReturnsFalse()
    {
        Assert.False(ClashSingBoxApi.ParseConnectionsSummary(
            Encoding.UTF8.GetBytes("{\"downloadTotal\":1.5,\"uploadTotal\":2,\"connections\":[]}"),
            out _, out _, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("[1, 2, 3]")]
    [InlineData("\"string\"")]
    [InlineData("123")]
    [InlineData("true")]
    [InlineData("null")]
    public void NonObject_ReturnsFalse(string nonObjectJson)
    {
        Assert.False(ClashSingBoxApi.ParseConnectionsSummary(
            Encoding.UTF8.GetBytes(nonObjectJson), out _, out _, out _));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"downloadTotal\":100,\"uploadTotal\":50}")]
    [InlineData("{\"downloadTotal\":100,\"connections\":[]}")]
    [InlineData("{\"uploadTotal\":50,\"connections\":[]}")]
    [InlineData("{\"otherField\":123}")]
    public void MissingRequiredFields_ReturnsFalse(string incompleteJson)
    {
        Assert.False(ClashSingBoxApi.ParseConnectionsSummary(
            Encoding.UTF8.GetBytes(incompleteJson), out _, out _, out _));
    }

    [Theory]
    [InlineData("{\"downloadTotal\":-1,\"uploadTotal\":50,\"connections\":[]}")]
    [InlineData("{\"downloadTotal\":100,\"uploadTotal\":-1,\"connections\":[]}")]
    [InlineData("{\"downloadTotal\":-100,\"uploadTotal\":-50,\"connections\":[]}")]
    public void NegativeCounts_ReturnsFalse(string negativeJson)
    {
        Assert.False(ClashSingBoxApi.ParseConnectionsSummary(
            Encoding.UTF8.GetBytes(negativeJson), out _, out _, out _));
    }

    [Theory]
    [InlineData("{\"downloadTotal\":9999999999999999999999999999999999999999,\"uploadTotal\":50,\"connections\":[]}")]
    [InlineData("{\"downloadTotal\":100,\"uploadTotal\":9999999999999999999999999999999999999999,\"connections\":[]}")]
    public void OverflowCounts_ReturnsFalse(string overflowJson)
    {
        Assert.False(ClashSingBoxApi.ParseConnectionsSummary(
            Encoding.UTF8.GetBytes(overflowJson), out _, out _, out _));
    }

    [Theory]
    [InlineData("{\"downloadTotal\":\"100\",\"uploadTotal\":50,\"connections\":[]}")]
    [InlineData("{\"downloadTotal\":100,\"uploadTotal\":\"50\",\"connections\":[]}")]
    [InlineData("{\"downloadTotal\":100,\"uploadTotal\":50,\"connections\":{}}")]
    [InlineData("{\"downloadTotal\":100,\"uploadTotal\":50,\"connections\":\"invalid\"}")]
    [InlineData("{\"downloadTotal\":100,\"uploadTotal\":50,\"connections\":123}")]
    public void InvalidFieldTypes_ReturnsFalse(string invalidTypeJson)
    {
        Assert.False(ClashSingBoxApi.ParseConnectionsSummary(
            Encoding.UTF8.GetBytes(invalidTypeJson), out _, out _, out _));
    }

    [Fact]
    public void TrailingGarbageAfterObject_ReturnsFalse()
    {
        Assert.False(ClashSingBoxApi.ParseConnectionsSummary(
            Encoding.UTF8.GetBytes("{\"downloadTotal\":100,\"uploadTotal\":50,\"connections\":[]} extra"),
            out _, out _, out _));
    }
}
