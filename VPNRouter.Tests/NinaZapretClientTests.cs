using VPNRouter.App.Services;

namespace VPNRouter.Tests;

public sealed class NinaZapretClientTests
{
    [Fact]
    public void DecodesTrialAndFinalProfile()
    {
        var trial = NinaZapretClient.TryReadEvent(
            """{"schema":1,"event":"trying","profile":"fake-split"}""");
        var result = NinaZapretClient.TryReadEvent(
            """{"schema":1,"event":"result","code":"repaired","profile":"fake-split"}""");

        Assert.Equal(new NinaZapretClient.Reply("trying", null, "fake-split"), trial);
        Assert.Equal(new NinaZapretClient.Reply("result", "repaired", "fake-split"), result);
    }

    [Fact]
    public void AcceptsCancellationWithoutACandidate()
    {
        var reply = NinaZapretClient.TryReadEvent(
            """{"schema":1,"event":"result","code":"cancelled","profile":null}""");

        Assert.Equal(new NinaZapretClient.Reply("result", "cancelled", null), reply);
    }

    [Theory]
    [InlineData("""{"schema":2,"event":"result","code":"repaired","profile":"split"}""")]
    [InlineData("""{"schema":1,"event":"trying","profile":"unsafe/path"}""")]
    [InlineData("""{"schema":1,"event":"trying","profile":"split\nbreak"}""")]
    [InlineData("""{"schema":1,"event":"trying","profile":null}""")]
    [InlineData("""{"schema":1,"event":"result","profile":"split"}""")]
    [InlineData("invalid JSON")]
    public void IgnoresInvalidProtocolLines(string line) => Assert.Null(NinaZapretClient.TryReadEvent(line));
}
