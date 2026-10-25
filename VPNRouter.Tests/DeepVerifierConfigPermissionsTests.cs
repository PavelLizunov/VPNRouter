using System.Security.Cryptography;
using Serilog;
using VPNRouter.Core;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class DeepVerifierConfigPermissionsTests
{
    [Fact]
    public async Task VerifyAsync_CreatesPrivateConfigBeforeSpawnAndDeletesItOnSpawnFailure()
    {
        if (!OperatingSystem.IsLinux()) return;

        var runtimePath = Path.Combine(Path.GetTempPath(), $"vpnrouter-dv-fixture-{Guid.NewGuid():N}.bin");
        byte[] runtimeBytes = [0x01, 0x02, 0x03];
        File.WriteAllBytes(runtimePath, runtimeBytes);
        string? configPath = null;
        UnixFileMode? configMode = null;
        try
        {
            var policy = SingBoxRuntimePolicy.ForTestFile(runtimePath,
                Convert.ToHexString(SHA256.HashData(runtimeBytes)));
            using var scope = SingBoxRuntimePolicy.EnterScope(policy);
            var runner = new FakeProcessRunner();
            runner.OnStart(_ => true, request =>
            {
                configPath = request.Arguments[2];
                configMode = File.GetUnixFileMode(configPath);
                throw new InvalidOperationException("Simulated spawn failure; no process or network probe");
            });
            var verifier = new VlessDeepVerifier(Log.Logger, runner);

            var result = await verifier.VerifyAsync(VlessDeepVerifierTests.CleanVlessEntry(), measureBandwidth: false);

            Assert.Equal(DeepVerifyFailurePhase.LocalSpawn, result.FailurePhase);
            Assert.Single(runner.StartCalls);
            Assert.NotNull(configPath);
            Assert.Equal((UnixFileMode?)AppPaths.PrivateUnixFileMode, configMode);
            Assert.False(File.Exists(configPath));
        }
        finally
        {
            File.Delete(runtimePath);
            if (configPath != null) File.Delete(configPath);
        }
    }
}
