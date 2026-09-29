#nullable enable

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Services;
using Xunit;

using P = VPNRouter.Core.Services.SplitTunnelDriverProtocol;

namespace VPNRouter.Tests;

public class SplitTunnelManagerTests
{
    private const string Ours = @"C:\Program Files\VPNRouter\app\driver\mullvad-split-tunnel.sys";

    [Fact]
    public void ClassifyServiceBinPath_ExactMatch_StartExisting()
        => Assert.Equal(P.ServiceCollisionAction.StartExisting,
            SplitTunnelPolicy.ClassifyServiceBinPath(Ours, Ours));

    [Fact]
    public void ClassifyServiceBinPath_ExactMatch_ToleratesQuotes_NtPrefix_Case()
    {
        const string existing = "\"\\??\\C:\\PROGRAM FILES\\VPNROUTER\\APP\\DRIVER\\MULLVAD-SPLIT-TUNNEL.SYS\"";
        Assert.Equal(P.ServiceCollisionAction.StartExisting,
            SplitTunnelPolicy.ClassifyServiceBinPath(existing, Ours));
    }

    [Fact]
    public void ClassifyServiceBinPath_RealMullvad_BailForeign()
    {
        const string existing = @"C:\Program Files\Mullvad VPN\resources\mullvad-split-tunnel.sys";
        Assert.Equal(P.ServiceCollisionAction.BailForeign,
            SplitTunnelPolicy.ClassifyServiceBinPath(existing, Ours));
    }

    [Fact]
    public void Manager_ConstructAndDispose_NoThrow_TouchesNothing()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var mgr = new SplitTunnelDriverManager(driverDir: NonexistentDir(), ownTunName: "VPNRouter-TUN");
        Assert.False(mgr.IsEngaged);
        Assert.True(mgr.IsPumpHealthy);
    }

    [Fact]
    public async Task EngageAsync_DriverFileMissing_ReturnsFalse_FailOpen_NoThrow()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var mgr = new SplitTunnelDriverManager(driverDir: NonexistentDir());
        var req = new SplitTunnelEngageRequest(
            new[] { @"C:\Windows\System32\curl.exe" }, TunnelIpv4: "172.19.0.2", TunnelIpv6: null);

        bool engaged = await mgr.EngageAsync(req, CancellationToken.None);

        Assert.False(engaged);
        Assert.False(mgr.IsEngaged);
        Assert.True(mgr.IsPumpHealthy);
    }

    [Fact]
    public async Task DisengageAsync_WhenNeverEngaged_Idempotent_NoThrow()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var mgr = new SplitTunnelDriverManager(driverDir: NonexistentDir());
        await mgr.DisengageAsync(CancellationToken.None);
        await mgr.DisengageAsync(CancellationToken.None);
        Assert.False(mgr.IsEngaged);
    }

    [Fact]
    public void IsForeignSplitDriverService_DetectsAmneziaOwner()
    {
        Assert.True(SplitTunnelPolicy.IsForeignSplitDriverService(
            "AmneziaVPNSplitTunnel",
            @"\??\C:\Program Files\AmneziaVPN\mullvad-split-tunnel.sys"));

        Assert.False(SplitTunnelPolicy.IsForeignSplitDriverService(
            "mullvad-split-tunnel",
            Ours));
    }

    [Fact]
    public void FormatForeignSplitDriverOwner_NamesOwnerAndAction()
    {
        string text = SplitTunnelPolicy.FormatForeignSplitDriverOwner(
            "AmneziaVPNSplitTunnel",
            "Amnezia Split Tunnel Service",
            @"\??\C:\Program Files\AmneziaVPN\mullvad-split-tunnel.sys");

        Assert.Contains("Amnezia Split Tunnel Service (AmneziaVPNSplitTunnel)", text);
        Assert.Contains("will not stop this kernel driver automatically", text);
        Assert.Contains("disable", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reboot Windows", text);
    }

    [Fact]
    public void FormatDriverStartFailure_WfpAlreadyExists_NamesConflictAndFallback()
    {
        string? text = SplitTunnelPolicy.FormatDriverStartFailure(0x80320009);

        Assert.NotNull(text);
        Assert.Contains("WFP/BFE", text);
        Assert.Contains("0x80320009", text);
        Assert.Contains("Ordinary split is active", text);
        Assert.Contains("Amnezia/Mullvad", text);
    }

    private static string NonexistentDir()
        => Path.Combine(Path.GetTempPath(), "vpnrouter-split-none-" + Guid.NewGuid().ToString("N"));
}
