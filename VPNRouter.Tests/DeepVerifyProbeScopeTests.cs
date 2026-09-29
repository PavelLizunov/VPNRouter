#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public class DeepVerifyProbeScopeTests
{
    [Fact]
    public void ProbeScope_TracksInFlight_AndDisposeIsIdempotent()
    {
        var baseline = DeepVerifyProbe.ProbesInFlightForTests;

        var a = DeepVerifyProbe.BeginProbeScope();
        var b = DeepVerifyProbe.BeginProbeScope();
        Assert.True(DeepVerifyProbe.ProbesInFlightForTests >= baseline + 2);
        Assert.True(DeepVerifyProbe.AnyProbeInFlight);

        a.Dispose();
        a.Dispose();
        var afterA = DeepVerifyProbe.ProbesInFlightForTests;
        Assert.True(afterA >= baseline + 1, $"underflow: {afterA} < {baseline + 1}");

        b.Dispose();
        Assert.True(DeepVerifyProbe.ProbesInFlightForTests >= baseline);
    }

    [Fact]
    public void DiagnosticBuffer_RedactsAndCapsConcurrentProcessOutput()
    {
        const string uuid = "11111111-2222-3333-4444-555555555555";
        const string token = "never-log-this-token";
        const string shortId = "0123456789abcdef";
        const string plainToken = "deadbeef00112233445566778899aabbcc";
        var key = new string('A', 48);
        var line = $"vless://{uuid}@secret.example:443?token={token} uuid={uuid} " +
                   $"key={key} short_id={shortId} token={plainToken}";
        var buffer = new StringBuilder();

        Parallel.For(0, 100, _ =>
            DeepVerifyProbe.AppendSanitizedLine(buffer, line, maxChars: 512));

        var snippet = DeepVerifyProbe.ReadSanitizedSnippet(buffer, 512);
        Assert.True(buffer.Length <= 512);
        Assert.DoesNotContain(uuid, snippet);
        Assert.DoesNotContain(token, snippet);
        Assert.DoesNotContain(shortId, snippet);
        Assert.DoesNotContain(plainToken, snippet);
        Assert.DoesNotContain(key, snippet);
        Assert.DoesNotContain("secret.example", snippet);
        Assert.Contains("[redacted]", snippet);
    }

    [Fact]
    public void CrossProcessProbe_TrustedImageWithoutGlobalTunOwnership_IsNotATunnel()
    {
        Assert.False(RuntimeStatusDetector.IsTunnelPresent(
            liveTunnelChild: true,
            ownership: TunOwnershipStatus.Free));
    }

    [Fact]
    public void CrossProcessProbe_DifferentParent_CannotBecomeDurableV2Child()
    {
        const int tunnelOwnerPid = 2001;
        var executable = Path.Combine(Path.GetTempPath(), "vpnrouter", "bin", "sing-box-lx.exe");
        var verifier = new OwnedProcessIdentity(
            2002,
            3000,
            executable,
            ParentPid: 2999);
        var tunnelChild = verifier with { Pid = 2003, ParentPid = tunnelOwnerPid };

        Assert.False(ProcessOwnership.CanPublishChildIdentity(
            verifier,
            executable,
            notBeforeUtcTicks: 2500,
            expectedParentPid: tunnelOwnerPid,
            enforceParent: true));
        Assert.True(ProcessOwnership.CanPublishChildIdentity(
            tunnelChild,
            executable,
            notBeforeUtcTicks: 2500,
            expectedParentPid: tunnelOwnerPid,
            enforceParent: true));
    }

    [Fact]
    public void RetainedSemaphoreWithoutRecordedLiveChild_IsNotATunnel()
    {
        Assert.False(RuntimeStatusDetector.IsTunnelPresent(
            liveTunnelChild: false,
            ownership: TunOwnershipStatus.Owned));
    }

    private static string? LoadSource(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        return null;
    }

    private static string StripLineComments(string src)
        => string.Join('\n',
            src.Split('\n').Select(l => l.Contains("//") ? l[..l.IndexOf("//", StringComparison.Ordinal)] : l));
}
