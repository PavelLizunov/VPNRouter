using System.Text;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class RuleSetFileTests
{
    private static bool IsValid(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return RuleSetFile.IsValidBinary(stream);
    }

    [Fact]
    public void PublishedGeositeFile_IsValid()
    {
        var bytes = RuleSetSamples.UpstreamGeositeTldRu();

        Assert.Equal(155, bytes.Length);
        Assert.True(IsValid(bytes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11 * 1024)]
    public void GeneratedSample_IsValid(int minLength)
    {
        var bytes = RuleSetSamples.Valid(minLength);

        Assert.True(bytes.Length >= minLength);
        Assert.True(IsValid(bytes));
    }

    [Fact]
    public void ZeroFilledFileOfThePublishedLength_IsInvalid()
    {
        Assert.False(IsValid(RuleSetSamples.ZeroFilled(155)));
    }

    [Fact]
    public void ValidHeaderWithZeroedBody_IsInvalid()
    {
        Assert.False(IsValid(RuleSetSamples.ValidHeaderZeroedBody(155)));
    }

    [Fact]
    public void HtmlPage_IsInvalid()
    {
        Assert.False(IsValid(Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body>Blocked</body></html>")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void FileCutInsideOrRightAfterTheHeader_IsInvalid(int length)
    {
        var bytes = RuleSetSamples.UpstreamGeositeTldRu().AsSpan(0, length).ToArray();

        Assert.False(IsValid(bytes));
    }

    [Fact]
    public void VersionZero_IsInvalid()
    {
        var bytes = RuleSetSamples.UpstreamGeositeTldRu();
        bytes[3] = 0;

        Assert.False(IsValid(bytes));
    }

    [Fact]
    public void PathOverload_ReadsTheFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "vpnr-ruleset-file-" + Guid.NewGuid().ToString("N") + ".srs");
        try
        {
            File.WriteAllBytes(path, RuleSetSamples.UpstreamGeositeTldRu());
            Assert.True(RuleSetFile.IsValidBinary(path));

            File.WriteAllBytes(path, RuleSetSamples.ZeroFilled(155));
            Assert.False(RuleSetFile.IsValidBinary(path));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
