using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
public class EmergencyChannelEngineTests
{
    private static EmergencyChannelConfig ValidConfig() =>
        new() { WgturnUrl = "wgturn://eyJ2IjoxfQ", VkLink = "https://vk.com/call/join/x" };

    [Fact]
    public void Initial_State_IsDisconnected()
    {
        using var engine = new EmergencyChannelEngine();
        Assert.Equal(EmergencyChannelState.Disconnected, engine.State);
        Assert.Null(engine.Pid);
        Assert.Null(engine.ActiveLabel);
    }

    [Fact]
    public void StopBeforeStart_NoOp()
    {
        using var engine = new EmergencyChannelEngine();
        engine.Stop();
        engine.Stop();
        Assert.Equal(EmergencyChannelState.Disconnected, engine.State);
    }

    [Fact]
    public async Task RestartWithoutPriorStart_Throws()
    {
        using var engine = new EmergencyChannelEngine();
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await engine.RestartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StartAsync_NullConfig_Throws()
    {
        using var engine = new EmergencyChannelEngine();
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await engine.StartAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task StartAsync_MissingBinary_TransitionsToFailedAndRaisesError()
    {
        // Inject a manager pointed at a non-existent binary.
        var bogusPath = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}.exe");
        var logPath = Path.Combine(Path.GetTempPath(), $"wgturn-test-{Guid.NewGuid():N}.log");

        using var engine = new EmergencyChannelEngine(
            managerFactory: () => new EmergencyChannelManager(bogusPath, logPath));

        var transitions = new List<EmergencyChannelState>();
        var error = (string?)null;
        engine.StateChanged += s => transitions.Add(s);
        engine.ErrorOccurred += msg => error = msg;

        await Assert.ThrowsAsync<FileNotFoundException>(
            async () => await engine.StartAsync(ValidConfig(), CancellationToken.None));

        Assert.Equal(EmergencyChannelState.Failed, engine.State);
        Assert.Contains(EmergencyChannelState.Connecting, transitions);
        Assert.Contains(EmergencyChannelState.Failed, transitions);
        Assert.NotNull(error);
        Assert.Contains("wgturn-cli", error!);
    }

    [Fact]
    public async Task StartAsync_Cancelled_BeforeManagerCreated()
    {
        using var engine = new EmergencyChannelEngine();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await engine.StartAsync(ValidConfig(), cts.Token));

        // Engine must remain in Disconnected — cancellation before any
        // work happened means no state transitions.
        Assert.Equal(EmergencyChannelState.Disconnected, engine.State);
    }
}
