#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.FreeConfigs;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

public class HeadlessRuntimePolicyTests : IDisposable
{
    private readonly string _savedDataDir;
    private readonly string _testDataDir;
    private readonly string? _savedRuntimeDir;

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int Mkfifo(string pathname, uint mode);

    public HeadlessRuntimePolicyTests()
    {
        SingBoxFeatures.ResetForTests();
        SingBoxRuntimePolicy.GeteuidOverrideForTests = () => 1000;

        _savedDataDir = AppPaths.DataDir;
        _testDataDir = Path.Combine(Path.GetTempPath(), $"vpnrouter-policy-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDataDir);
        AppPaths.OverrideDataDir(_testDataDir);
        AppPaths.EnsureDirectories();

        _savedRuntimeDir = LinuxTunOwnership.OverrideRuntimeDirectory;
        if (OperatingSystem.IsLinux())
        {
            var runtime = Path.Combine(_testDataDir, "runtime");
            Directory.CreateDirectory(runtime, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            LinuxTunOwnership.OverrideRuntimeDirectory = runtime;
        }

        ReleaseSingletonTunLockBestEffort();
    }

    public void Dispose()
    {
        SingBoxFeatures.ResetForTests();
        SingBoxRuntimePolicy.GeteuidOverrideForTests = null;

        ReleaseSingletonTunLockBestEffort();

        if (OperatingSystem.IsLinux())
        {
            LinuxTunOwnership.OverrideRuntimeDirectory = _savedRuntimeDir;
        }

        AppPaths.OverrideDataDir(_savedDataDir);
        try
        {
            if (Directory.Exists(_testDataDir))
                Directory.Delete(_testDataDir, recursive: true);
        }
        catch { /* best effort */ }
    }

    private static void ReleaseSingletonTunLockBestEffort()
    {
        try
        {
            var lockInstance = TunOwnershipLock.Instance(null);
            lockInstance.Release();
            lockInstance.Dispose();
        }
        catch
        {
        }
    }

    [Fact]
    public void MissingFile_ThrowsMissingFailure()
    {
        var missingPath = Path.Combine(_testDataDir, "missing-sb-" + Guid.NewGuid().ToString("N") + ".bin");
        var dummyHash = new string('0', 64);

        var policy = SingBoxRuntimePolicy.ForTestFile(missingPath, dummyHash);
        using var _ = SingBoxRuntimePolicy.EnterScope(policy);

        Assert.False(policy.IsAvailable);

        var ex = Assert.Throws<SingBoxRuntimePolicyException>(() => policy.Authorize(SingBoxRuntimeOperation.Inspect));
        Assert.Equal(SingBoxRuntimeFailure.Missing, ex.Failure);
        Assert.Equal(SingBoxRuntimeOperation.Inspect, ex.Operation);
        Assert.Equal("sing-box runtime is unavailable or untrusted.", ex.Message);
    }

    [Fact]
    public void UntrustedFile_InitialHashMismatch_ThrowsUntrustedFailure()
    {
        var tempFile = Path.Combine(_testDataDir, "untrusted-sb-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(tempFile, new byte[] { 0x01, 0x02, 0x03 });

        try
        {
            var wrongHash = new string('a', 64);
            var policy = SingBoxRuntimePolicy.ForTestFile(tempFile, wrongHash);
            using var _ = SingBoxRuntimePolicy.EnterScope(policy);

            Assert.False(policy.IsAvailable);

            var ex = Assert.Throws<SingBoxRuntimePolicyException>(() => policy.Authorize(SingBoxRuntimeOperation.Start));
            Assert.Equal(SingBoxRuntimeFailure.Untrusted, ex.Failure);
            Assert.Equal(SingBoxRuntimeOperation.Start, ex.Operation);
            Assert.Equal("sing-box runtime is unavailable or untrusted.", ex.Message);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public void SameSizeReplacement_ThrowsChangedFailure()
    {
        var tempFile = Path.Combine(_testDataDir, "replace-sb-" + Guid.NewGuid().ToString("N") + ".bin");
        var initialBytes = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
        File.WriteAllBytes(tempFile, initialBytes);

        try
        {
            using var sha = SHA256.Create();
            var initialHash = Convert.ToHexString(sha.ComputeHash(initialBytes));

            var policy = SingBoxRuntimePolicy.ForTestFile(tempFile, initialHash);
            using var _ = SingBoxRuntimePolicy.EnterScope(policy);

            // First validation succeeds
            policy.Authorize(SingBoxRuntimeOperation.Start);
            Assert.True(policy.IsAvailable);

            // Replace with different content of exact same size (4 bytes)
            var replacedBytes = new byte[] { 0x11, 0x22, 0x33, 0x44 };
            File.WriteAllBytes(tempFile, replacedBytes);

            var ex = Assert.Throws<SingBoxRuntimePolicyException>(() => policy.Authorize(SingBoxRuntimeOperation.Start));
            Assert.Equal(SingBoxRuntimeFailure.Changed, ex.Failure);
            Assert.Equal(SingBoxRuntimeOperation.Start, ex.Operation);
            Assert.Equal("sing-box runtime is unavailable or untrusted.", ex.Message);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public void PrerequisiteUnavailable_ThrowsPrerequisiteFailure()
    {
        var tempFile = Path.Combine(_testDataDir, "prereq-sb-" + Guid.NewGuid().ToString("N") + ".bin");
        var bytes = new byte[] { 0x42 };
        File.WriteAllBytes(tempFile, bytes);

        try
        {
            using var sha = SHA256.Create();
            var hash = Convert.ToHexString(sha.ComputeHash(bytes));

            bool prereq = false;
            var policy = SingBoxRuntimePolicy.ForTestFile(tempFile, hash, prerequisites: () => prereq);
            using var _ = SingBoxRuntimePolicy.EnterScope(policy);

            Assert.False(policy.IsAvailable);

            var ex = Assert.Throws<SingBoxRuntimePolicyException>(() => policy.Authorize(SingBoxRuntimeOperation.Verify));
            Assert.Equal(SingBoxRuntimeFailure.PrerequisiteUnavailable, ex.Failure);
            Assert.Equal(SingBoxRuntimeOperation.Verify, ex.Operation);
            Assert.Equal("sing-box runtime is unavailable or untrusted.", ex.Message);

            // Flip to true
            prereq = true;
            Assert.True(policy.IsAvailable);
            policy.Authorize(SingBoxRuntimeOperation.Verify);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public void NestedScopes_RestorePreviousPolicyOnDisposal()
    {
        var tempFileA = Path.Combine(_testDataDir, "nested-sb-a-" + Guid.NewGuid().ToString("N") + ".bin");
        var tempFileB = Path.Combine(_testDataDir, "nested-sb-b-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(tempFileA, new byte[] { 0x01 });
        File.WriteAllBytes(tempFileB, new byte[] { 0x02 });

        try
        {
            using var sha = SHA256.Create();
            var hashA = Convert.ToHexString(sha.ComputeHash(new byte[] { 0x01 }));
            var hashB = Convert.ToHexString(sha.ComputeHash(new byte[] { 0x02 }));
            var policyA = SingBoxRuntimePolicy.ForTestFile(tempFileA, hashA);
            var policyB = SingBoxRuntimePolicy.ForTestFile(tempFileB, hashB);

            Assert.Null(SingBoxRuntimePolicy.Current);

            using (SingBoxRuntimePolicy.EnterScope(policyA))
            {
                Assert.Same(policyA, SingBoxRuntimePolicy.Current);

                using (SingBoxRuntimePolicy.EnterScope(policyB))
                {
                    Assert.Same(policyB, SingBoxRuntimePolicy.Current);
                }

                Assert.Same(policyA, SingBoxRuntimePolicy.Current);
            }

            Assert.Null(SingBoxRuntimePolicy.Current);
        }
        finally
        {
            try { File.Delete(tempFileA); } catch { }
            try { File.Delete(tempFileB); } catch { }
        }
    }

    [Fact]
    public void DefaultDominance_ScopeCannotBeWeakenedByFixturePolicy()
    {
        var tempFile = Path.Combine(_testDataDir, "dominance-sb-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(tempFile, new byte[] { 0x05 });

        try
        {
            using var sha = SHA256.Create();
            var hash = Convert.ToHexString(sha.ComputeHash(new byte[] { 0x05 }));
            var fixturePolicy = SingBoxRuntimePolicy.ForTestFile(tempFile, hash);

            Assert.Null(SingBoxRuntimePolicy.Current);

            using (SingBoxRuntimePolicy.EnterScope(SingBoxRuntimePolicy.DefaultProduction))
            {
                Assert.Same(SingBoxRuntimePolicy.DefaultProduction, SingBoxRuntimePolicy.Current);

                using (SingBoxRuntimePolicy.EnterScope(fixturePolicy))
                {
                    // DefaultProduction cannot be weakened by fixture policy
                    Assert.Same(SingBoxRuntimePolicy.DefaultProduction, SingBoxRuntimePolicy.Current);
                }

                Assert.Same(SingBoxRuntimePolicy.DefaultProduction, SingBoxRuntimePolicy.Current);
            }

            Assert.Null(SingBoxRuntimePolicy.Current);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public void NestedNullScope_PreservesCurrentRestriction_NeverErasesIt()
    {
        var tempFile = Path.Combine(_testDataDir, "scope-sb-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(tempFile, new byte[] { 0x01 });

        try
        {
            using var sha = SHA256.Create();
            var hash = Convert.ToHexString(sha.ComputeHash(new byte[] { 0x01 }));
            var policy = SingBoxRuntimePolicy.ForTestFile(tempFile, hash);

            using (SingBoxRuntimePolicy.EnterScope(policy))
            {
                Assert.Same(policy, SingBoxRuntimePolicy.Current);

                // Nested EnterScope(null) MUST preserve the current restriction, never erase it
                using (SingBoxRuntimePolicy.EnterScope(null))
                {
                    Assert.Same(policy, SingBoxRuntimePolicy.Current);
                    Assert.NotNull(SingBoxRuntimePolicy.Current);
                }

                // After disposal of inner null scope, current restriction is still preserved
                Assert.Same(policy, SingBoxRuntimePolicy.Current);
            }

            // DefaultProduction restriction preserved across null scope
            using (SingBoxRuntimePolicy.EnterScope(SingBoxRuntimePolicy.DefaultProduction))
            {
                Assert.Same(SingBoxRuntimePolicy.DefaultProduction, SingBoxRuntimePolicy.Current);

                using (SingBoxRuntimePolicy.EnterScope(null))
                {
                    Assert.Same(SingBoxRuntimePolicy.DefaultProduction, SingBoxRuntimePolicy.Current);
                }

                Assert.Same(SingBoxRuntimePolicy.DefaultProduction, SingBoxRuntimePolicy.Current);
            }
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public async Task ParallelAsyncIsolation_PoliciesDoNotInterfere()
    {
        var tempFileA = Path.Combine(_testDataDir, "async-sb-a-" + Guid.NewGuid().ToString("N") + ".bin");
        var tempFileB = Path.Combine(_testDataDir, "async-sb-b-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(tempFileA, new byte[] { 0x0A });
        File.WriteAllBytes(tempFileB, new byte[] { 0x0B });

        try
        {
            using var sha = SHA256.Create();
            var hashA = Convert.ToHexString(sha.ComputeHash(new byte[] { 0x0A }));
            var hashB = Convert.ToHexString(sha.ComputeHash(new byte[] { 0x0B }));

            var policyA = SingBoxRuntimePolicy.ForTestFile(tempFileA, hashA);
            var policyB = SingBoxRuntimePolicy.ForTestFile(tempFileB, hashB);

            var taskA = Task.Run(async () =>
            {
                using var _ = SingBoxRuntimePolicy.EnterScope(policyA);
                for (int i = 0; i < 10; i++)
                {
                    await Task.Yield();
                    Assert.Same(policyA, SingBoxRuntimePolicy.Current);
                }
            });

            var taskB = Task.Run(async () =>
            {
                using var _ = SingBoxRuntimePolicy.EnterScope(policyB);
                for (int i = 0; i < 10; i++)
                {
                    await Task.Yield();
                    Assert.Same(policyB, SingBoxRuntimePolicy.Current);
                }
            });

            await Task.WhenAll(taskA, taskB);
        }
        finally
        {
            try { File.Delete(tempFileA); } catch { }
            try { File.Delete(tempFileB); } catch { }
        }
    }

    [Fact]
    public void LegacyNullBehavior_PreservesDefaultWhenPolicyNull()
    {
        // Inside this test, SingBoxRuntimePolicy.Current is null if not in a scope
        SingBoxFeatures.OverrideAwg = true;
        Assert.True(SingBoxFeatures.AwgAvailable);

        SingBoxFeatures.OverrideAwg = false;
        Assert.False(SingBoxFeatures.AwgAvailable);

        SingBoxFeatures.OverrideXhttp = true;
        Assert.True(SingBoxFeatures.XhttpAvailable);
    }

    [Fact]
    public void SingBoxFeatures_PolicyCheckedBeforeGlobalOrOverride()
    {
        SingBoxFeatures.OverrideAwg = true;
        SingBoxFeatures.OverrideXhttp = true;

        using (SingBoxRuntimePolicy.EnterScope(SingBoxRuntimePolicy.DefaultProduction))
        {
            // Policy must be checked BEFORE test overrides or cached probe results
            Assert.False(SingBoxFeatures.AwgAvailable);
            Assert.False(SingBoxFeatures.XhttpAvailable);
        }

        // Outside policy scope, override takes effect again
        Assert.True(SingBoxFeatures.AwgAvailable);
        Assert.True(SingBoxFeatures.XhttpAvailable);
    }

    [Fact]
    public void RetainedManagerPolicy_DeniedStart_UnderRestrictedPolicy()
    {
        SingBoxManager manager;
        var fakeRunner = new FakeProcessRunner();

        using (SingBoxRuntimePolicy.EnterScope(SingBoxRuntimePolicy.DefaultProduction))
        {
            manager = new SingBoxManager(new SingBoxSettings(), Log.Logger, runner: fakeRunner);
        }

        using (manager)
        {
            var ex = Assert.Throws<SingBoxRuntimePolicyException>(() => manager.StartWithJson("{}"));
            Assert.Equal(SingBoxRuntimeFailure.Untrusted, ex.Failure);
            Assert.Equal(SingBoxRuntimeOperation.Start, ex.Operation);
            Assert.Empty(fakeRunner.StartCalls);
        }
    }

    [Fact]
    public async Task RetainedVerifierPolicy_DeniedVerify_UnderRestrictedPolicy()
    {
        VlessDeepVerifier verifier;
        var fakeRunner = new FakeProcessRunner();

        using (SingBoxRuntimePolicy.EnterScope(SingBoxRuntimePolicy.DefaultProduction))
        {
            verifier = new VlessDeepVerifier(Log.Logger, fakeRunner);
        }

        Assert.False(verifier.IsAvailable);

        var server = new VlessServerEntry
        {
            Server = "127.0.0.1",
            Port = 443,
            Uuid = "00000000-0000-0000-0000-000000000001"
        };

        var result = await verifier.VerifyAsync(server, measureBandwidth: false);
        Assert.False(result.Ok);
        Assert.Equal(DeepVerifyFailurePhase.LocalSpawn, result.FailurePhase);
        Assert.Empty(fakeRunner.StartCalls);
    }

    [Fact]
    public void MaliciousFilename_ErrorTextNotLeaked()
    {
        var sensitivePath = Path.Combine(_testDataDir, "secret_token_abcdef_98765", "singbox.bin");
        var dummyHash = new string('e', 64);

        var policy = SingBoxRuntimePolicy.ForTestFile(sensitivePath, dummyHash);
        using var _ = SingBoxRuntimePolicy.EnterScope(policy);

        var ex = Assert.Throws<SingBoxRuntimePolicyException>(() => policy.Authorize(SingBoxRuntimeOperation.Inspect));
        Assert.DoesNotContain("secret_token_abcdef_98765", ex.Message);
        Assert.DoesNotContain("singbox.bin", ex.Message);
        Assert.Equal("sing-box runtime is unavailable or untrusted.", ex.Message);
    }

    [Fact]
    public void RefuseRootLaunch_WhenEuidZero()
    {
        var tempFile = Path.Combine(_testDataDir, "root-sb-" + Guid.NewGuid().ToString("N") + ".bin");
        var bytes = new byte[] { 0x01, 0x02 };
        File.WriteAllBytes(tempFile, bytes);

        try
        {
            using var sha = SHA256.Create();
            var hash = Convert.ToHexString(sha.ComputeHash(bytes));

            var policy = SingBoxRuntimePolicy.ForTestFile(tempFile, hash);
            using var _ = SingBoxRuntimePolicy.EnterScope(policy);

            // Normal non-root operation succeeds
            SingBoxRuntimePolicy.GeteuidOverrideForTests = () => 1000;
            Assert.True(policy.IsAvailable);
            policy.Authorize(SingBoxRuntimeOperation.Inspect);
            policy.Authorize(SingBoxRuntimeOperation.Probe);
            policy.Authorize(SingBoxRuntimeOperation.Verify);
            policy.Authorize(SingBoxRuntimeOperation.Start);
            policy.Authorize(SingBoxRuntimeOperation.Restart);

            // Simulate EUID = 0 (root)
            SingBoxRuntimePolicy.GeteuidOverrideForTests = () => 0;

            // IsAvailable must be consistent with executable operation authorization -> false for root
            Assert.False(policy.IsAvailable);

            var exProbe = Assert.Throws<SingBoxRuntimePolicyException>(() => policy.Authorize(SingBoxRuntimeOperation.Probe));
            Assert.Equal(SingBoxRuntimeFailure.Untrusted, exProbe.Failure);
            Assert.Equal(SingBoxRuntimeOperation.Probe, exProbe.Operation);

            var exVerify = Assert.Throws<SingBoxRuntimePolicyException>(() => policy.Authorize(SingBoxRuntimeOperation.Verify));
            Assert.Equal(SingBoxRuntimeFailure.Untrusted, exVerify.Failure);
            Assert.Equal(SingBoxRuntimeOperation.Verify, exVerify.Operation);

            var exStart = Assert.Throws<SingBoxRuntimePolicyException>(() => policy.Authorize(SingBoxRuntimeOperation.Start));
            Assert.Equal(SingBoxRuntimeFailure.Untrusted, exStart.Failure);
            Assert.Equal(SingBoxRuntimeOperation.Start, exStart.Operation);

            var exRestart = Assert.Throws<SingBoxRuntimePolicyException>(() => policy.Authorize(SingBoxRuntimeOperation.Restart));
            Assert.Equal(SingBoxRuntimeFailure.Untrusted, exRestart.Failure);
            Assert.Equal(SingBoxRuntimeOperation.Restart, exRestart.Operation);
        }
        finally
        {
            SingBoxRuntimePolicy.GeteuidOverrideForTests = () => 1000;
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public void DefaultProduction_SelectedExecutablePath_IsNull_AndAlwaysDenied()
    {
        var prod = SingBoxRuntimePolicy.DefaultProduction;
        Assert.Null(prod.SelectedExecutablePath);
        Assert.False(prod.IsAvailable);

        foreach (var op in Enum.GetValues<SingBoxRuntimeOperation>())
        {
            var ex = Assert.Throws<SingBoxRuntimePolicyException>(() => prod.Authorize(op));
            Assert.Equal(SingBoxRuntimeFailure.Untrusted, ex.Failure);
            Assert.Equal(op, ex.Operation);
        }
    }

    [Fact]
    public void CallbackReentrancy_DeniesRecursiveAuthorization_DoesNotStackOverflow()
    {
        var tempFile = Path.Combine(_testDataDir, "reentrant-sb-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(tempFile, new byte[] { 0x99 });

        try
        {
            using var sha = SHA256.Create();
            var hash = Convert.ToHexString(sha.ComputeHash(new byte[] { 0x99 }));

            SingBoxRuntimePolicy? policy = null;
            int callbackCount = 0;
            bool reentrantDenied = false;

            policy = SingBoxRuntimePolicy.ForTestFile(tempFile, hash, prerequisites: () =>
            {
                callbackCount++;
                if (callbackCount > 5)
                    return false;

                // Attempt recursive authorization from within prerequisite callback
                if (!policy!.TryAuthorize(SingBoxRuntimeOperation.Inspect, out var reentrantFailure))
                {
                    if (reentrantFailure == SingBoxRuntimeFailure.Untrusted)
                    {
                        reentrantDenied = true;
                    }
                    return false;
                }
                return true;
            });

            using var _ = SingBoxRuntimePolicy.EnterScope(policy);

            // Reentrant authorization attempt must be safely denied as Untrusted without stack overflow
            Assert.False(policy.TryAuthorize(SingBoxRuntimeOperation.Inspect, out var topLevelFailure));
            Assert.True(reentrantDenied, "Recursive authorization inside callback was not denied.");
            Assert.Equal(SingBoxRuntimeFailure.PrerequisiteUnavailable, topLevelFailure);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public void FifoFile_ThrowsUntrustedFailure()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var fifoPath = Path.Combine(_testDataDir, "fifo-sb-" + Guid.NewGuid().ToString("N"));
        int res = Mkfifo(fifoPath, 0x180 /* 0600 */);
        if (res != 0)
            return;

        try
        {
            var dummyHash = new string('0', 64);
            var policy = SingBoxRuntimePolicy.ForTestFile(fifoPath, dummyHash);
            using var _ = SingBoxRuntimePolicy.EnterScope(policy);

            Assert.False(policy.IsAvailable);

            var ex = Assert.Throws<SingBoxRuntimePolicyException>(() => policy.Authorize(SingBoxRuntimeOperation.Inspect));
            Assert.Equal(SingBoxRuntimeFailure.Untrusted, ex.Failure);
            Assert.Equal(SingBoxRuntimeOperation.Inspect, ex.Operation);
            Assert.Equal("sing-box runtime is unavailable or untrusted.", ex.Message);
        }
        finally
        {
            try { File.Delete(fifoPath); } catch { }
        }
    }

    [Fact]
    public void SymlinkLeaf_ThrowsUntrustedFailure()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var targetFile = Path.Combine(_testDataDir, "target-sb-" + Guid.NewGuid().ToString("N") + ".bin");
        var symlinkFile = Path.Combine(_testDataDir, "symlink-sb-" + Guid.NewGuid().ToString("N") + ".bin");
        var bytes = new byte[] { 0x05, 0x06, 0x07 };
        File.WriteAllBytes(targetFile, bytes);

        try
        {
            File.CreateSymbolicLink(symlinkFile, targetFile);

            using var sha = SHA256.Create();
            var hash = Convert.ToHexString(sha.ComputeHash(bytes));

            var policy = SingBoxRuntimePolicy.ForTestFile(symlinkFile, hash);
            using var _ = SingBoxRuntimePolicy.EnterScope(policy);

            Assert.False(policy.IsAvailable);

            var ex = Assert.Throws<SingBoxRuntimePolicyException>(() => policy.Authorize(SingBoxRuntimeOperation.Inspect));
            Assert.Equal(SingBoxRuntimeFailure.Untrusted, ex.Failure);
            Assert.Equal(SingBoxRuntimeOperation.Inspect, ex.Operation);
            Assert.Equal("sing-box runtime is unavailable or untrusted.", ex.Message);
        }
        finally
        {
            try { File.Delete(symlinkFile); } catch { }
            try { File.Delete(targetFile); } catch { }
        }
    }

    [Fact]
    public void SymlinkAncestor_ThrowsUntrustedFailure()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var realDir = Path.Combine(_testDataDir, "real-dir-" + Guid.NewGuid().ToString("N"));
        var symlinkDir = Path.Combine(_testDataDir, "symlink-dir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(realDir);

        var targetFile = Path.Combine(realDir, "sb.bin");
        var bytes = new byte[] { 0x10, 0x20 };
        File.WriteAllBytes(targetFile, bytes);

        try
        {
            Directory.CreateSymbolicLink(symlinkDir, realDir);
            var symlinkAncestorFile = Path.Combine(symlinkDir, "sb.bin");

            using var sha = SHA256.Create();
            var hash = Convert.ToHexString(sha.ComputeHash(bytes));

            var policy = SingBoxRuntimePolicy.ForTestFile(symlinkAncestorFile, hash);
            using var _ = SingBoxRuntimePolicy.EnterScope(policy);

            Assert.False(policy.IsAvailable);

            var ex = Assert.Throws<SingBoxRuntimePolicyException>(() => policy.Authorize(SingBoxRuntimeOperation.Inspect));
            Assert.Equal(SingBoxRuntimeFailure.Untrusted, ex.Failure);
            Assert.Equal(SingBoxRuntimeOperation.Inspect, ex.Operation);
            Assert.Equal("sing-box runtime is unavailable or untrusted.", ex.Message);
        }
        finally
        {
            try { Directory.Delete(symlinkDir); } catch { }
            try { Directory.Delete(realDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void OversizedSparseFile_ThrowsUntrustedFailure()
    {
        var sparseFile = Path.Combine(_testDataDir, "oversized-sparse-" + Guid.NewGuid().ToString("N") + ".bin");

        try
        {
            using (var fs = File.Create(sparseFile))
            {
                fs.SetLength(257L * 1024 * 1024); // 257 MiB > 256 MiB cap
            }

            var dummyHash = new string('f', 64);
            var policy = SingBoxRuntimePolicy.ForTestFile(sparseFile, dummyHash);
            using var _ = SingBoxRuntimePolicy.EnterScope(policy);

            Assert.False(policy.IsAvailable);

            var ex = Assert.Throws<SingBoxRuntimePolicyException>(() => policy.Authorize(SingBoxRuntimeOperation.Inspect));
            Assert.Equal(SingBoxRuntimeFailure.Untrusted, ex.Failure);
            Assert.Equal(SingBoxRuntimeOperation.Inspect, ex.Operation);
            Assert.Equal("sing-box runtime is unavailable or untrusted.", ex.Message);
        }
        finally
        {
            try { File.Delete(sparseFile); } catch { }
        }
    }

    [Fact]
    public void UnknownUid_FailsClosedAsUntrusted()
    {
        var tempFile = Path.Combine(_testDataDir, "unknown-uid-sb-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(tempFile, new byte[] { 0x01 });

        try
        {
            using var sha = SHA256.Create();
            var hash = Convert.ToHexString(sha.ComputeHash(new byte[] { 0x01 }));
            var policy = SingBoxRuntimePolicy.ForTestFile(tempFile, hash);
            using var _ = SingBoxRuntimePolicy.EnterScope(policy);

            // Simulate unknown UID failure (e.g. geteuid failure / DllNotFoundException)
            SingBoxRuntimePolicy.GeteuidOverrideForTests = () => throw new DllNotFoundException("Simulated missing libc");

            Assert.False(policy.IsAvailable);

            var ex = Assert.Throws<SingBoxRuntimePolicyException>(() => policy.Authorize(SingBoxRuntimeOperation.Inspect));
            Assert.Equal(SingBoxRuntimeFailure.Untrusted, ex.Failure);
            Assert.Equal(SingBoxRuntimeOperation.Inspect, ex.Operation);
        }
        finally
        {
            SingBoxRuntimePolicy.GeteuidOverrideForTests = () => 1000;
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public void SingBoxManager_PositiveStartAndRestart_UsesSelectedPath_OnLinux()
    {
        // Linux only: calling manager Start/Restart avoids Windows adapter cleanup (netsh / Wintun PnP teardown).
        // This test provides genuine Linux policy evidence and does not claim Windows evidence.
        if (!OperatingSystem.IsLinux())
            return;

        var tempFile = Path.Combine(_testDataDir, "manager-sb-" + Guid.NewGuid().ToString("N") + ".bin");
        var bytes = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        File.WriteAllBytes(tempFile, bytes);

        try
        {
            using var sha = SHA256.Create();
            var hash = Convert.ToHexString(sha.ComputeHash(bytes));
            var policy = SingBoxRuntimePolicy.ForTestFile(tempFile, hash);

            var fakeRunner = new FakeProcessRunner();
            // Impossible PID (e.g. int.MaxValue) to prove fake handle with no actual process kill
            var fakeHandle = new FakeProcessHandle(pid: int.MaxValue);
            var restartHandle = new FakeProcessHandle(pid: int.MaxValue);
            var starts = 0;
            fakeRunner.OnStart(_ => true, _ => ++starts == 1 ? fakeHandle : restartHandle);

            using (SingBoxRuntimePolicy.EnterScope(policy))
            {
                using var manager = new SingBoxManager(new SingBoxSettings(), Log.Logger, runner: fakeRunner);

                // 1. Positive Start
                manager.StartWithJson("{}");

                Assert.Equal(SingBoxState.Running, manager.State);
                Assert.Single(fakeRunner.StartCalls);
                var startReq = fakeRunner.StartCalls[0];
                Assert.Equal(tempFile, startReq.ExecutablePath);
                Assert.Equal(new[] { "run", "-c", AppPaths.CurrentConfigPath }, startReq.Arguments);

                // Inspect process ownership candidate path registration
                Assert.Equal(tempFile, ProcessOwnership.ConfiguredExePath);

                // 2. Positive Restart
                manager.Restart();

                Assert.Equal(SingBoxState.Running, manager.State);
                Assert.Equal(2, fakeRunner.StartCalls.Count);
                var restartReq = fakeRunner.StartCalls[1];
                Assert.Equal(tempFile, restartReq.ExecutablePath);
                Assert.Equal(new[] { "run", "-c", AppPaths.CurrentConfigPath }, restartReq.Arguments);
                Assert.Equal(tempFile, ProcessOwnership.ConfiguredExePath);

                // Verify fake handle was suppressed before kill (orderly stop)
                Assert.Equal(1, fakeHandle.SuppressExitedEventCallCount);
                Assert.Equal(1, fakeHandle.KillCallCount);
            }
        }
        finally
        {
            ProcessOwnership.ConfiguredExePath = null;
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public void SingBoxManager_TamperOnRestart_ThrowsChangedFailure_AndAbortsRestart_OnLinux()
    {
        // Linux only: calling manager Start/Restart avoids Windows adapter cleanup.
        // This test provides genuine Linux policy evidence and does not claim Windows evidence.
        if (!OperatingSystem.IsLinux())
            return;

        var tempFile = Path.Combine(_testDataDir, "tamper-sb-" + Guid.NewGuid().ToString("N") + ".bin");
        var originalBytes = new byte[] { 0x11, 0x22, 0x33, 0x44 };
        File.WriteAllBytes(tempFile, originalBytes);

        try
        {
            using var sha = SHA256.Create();
            var hash = Convert.ToHexString(sha.ComputeHash(originalBytes));
            var policy = SingBoxRuntimePolicy.ForTestFile(tempFile, hash);

            var fakeRunner = new FakeProcessRunner();
            var fakeHandle = new FakeProcessHandle(pid: int.MaxValue);
            fakeRunner.OnStart(_ => true, _ => fakeHandle);

            using (SingBoxRuntimePolicy.EnterScope(policy))
            {
                using var manager = new SingBoxManager(new SingBoxSettings(), Log.Logger, runner: fakeRunner);

                // Initial start succeeds
                manager.StartWithJson("{}");
                Assert.Equal(SingBoxState.Running, manager.State);
                Assert.Single(fakeRunner.StartCalls);

                // Tamper with binary before restart (different content)
                var tamperedBytes = new byte[] { 0x99, 0x88, 0x77, 0x66 };
                File.WriteAllBytes(tempFile, tamperedBytes);

                // Restart must detect tamper, throw SingBoxRuntimePolicyException with Changed failure,
                // and abort without issuing a second Start call to runner
                var ex = Assert.Throws<SingBoxRuntimePolicyException>(() => manager.Restart());
                Assert.Equal(SingBoxRuntimeFailure.Changed, ex.Failure);
                Assert.Equal(SingBoxRuntimeOperation.Restart, ex.Operation);

                // Only 1 start call was ever issued (initial start); restart was refused before spawn
                Assert.Single(fakeRunner.StartCalls);
            }
        }
        finally
        {
            ProcessOwnership.ConfiguredExePath = null;
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public async Task VlessDeepVerifier_ConstructedOutsidePolicy_UsesFixturePathInsideScope()
    {
        var tempFile = Path.Combine(_testDataDir, "vless-dv-" + Guid.NewGuid().ToString("N") + ".bin");
        var bytes = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
        File.WriteAllBytes(tempFile, bytes);

        try
        {
            using var sha = SHA256.Create();
            var hash = Convert.ToHexString(sha.ComputeHash(bytes));
            var policy = SingBoxRuntimePolicy.ForTestFile(tempFile, hash);

            var fakeRunner = new FakeProcessRunner();
            var sentinel = new InvalidOperationException("Sentinel: avoid ANY SOCKS probes or process execution");
            fakeRunner.OnStart(_ => true, _ => throw sentinel);

            // Construct verifier OUTSIDE policy scope
            Assert.Null(SingBoxRuntimePolicy.Current);
            var verifier = new VlessDeepVerifier(Log.Logger, fakeRunner);

            // Passively valid config/entry; no actual Process/HTTP
            var entry = new VlessServerEntry
            {
                Server = "127.0.0.1",
                Port = 443,
                Uuid = "00000000-0000-0000-0000-000000000001"
            };

            // Call within fixture scope
            using (SingBoxRuntimePolicy.EnterScope(policy))
            {
                var result = await verifier.VerifyAsync(entry, measureBandwidth: false);

                // Verifier catches spawn failure and returns LocalSpawn
                Assert.False(result.Ok);
                Assert.Equal(DeepVerifyFailurePhase.LocalSpawn, result.FailurePhase);

                // Assert recorded request despite returned LocalSpawn, using the fixture path
                Assert.Single(fakeRunner.StartCalls);
                Assert.Equal(tempFile, fakeRunner.StartCalls[0].ExecutablePath);
            }
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public async Task Capture_RetentionUnderExecutionContextSuppressFlow_AndDefaultDominance()
    {
        var tempFile = Path.Combine(_testDataDir, "capture-sb-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(tempFile, new byte[] { 0x42 });

        try
        {
            using var sha = SHA256.Create();
            var hash = Convert.ToHexString(sha.ComputeHash(new byte[] { 0x42 }));
            var fixturePolicy = SingBoxRuntimePolicy.ForTestFile(tempFile, hash);

            SingBoxRuntimePolicy? retained = null;

            // 1. Initial capture under fixture scope
            using (SingBoxRuntimePolicy.EnterScope(fixturePolicy))
            {
                var effective = CapturePolicy(ref retained);
                Assert.Same(fixturePolicy, effective);
                Assert.Same(fixturePolicy, retained);

                // 2. Retention under ExecutionContext.SuppressFlow (no AsyncLocal flow)
                Task suppressedTask;
                using (ExecutionContext.SuppressFlow())
                {
                    suppressedTask = Task.Run(() =>
                    {
                        // Because execution context flow was suppressed, ambient Current is null
                        Assert.Null(SingBoxRuntimePolicy.Current);

                        // But retained policy is preserved and effective
                        var innerEffective = CapturePolicy(ref retained);
                        Assert.Same(fixturePolicy, innerEffective);
                        Assert.Same(fixturePolicy, retained);
                    });
                }

                await suppressedTask;

                // 3. Concurrency test: multiple threads capturing concurrently
                var tasks = new Task[10];
                for (int i = 0; i < tasks.Length; i++)
                {
                    tasks[i] = Task.Run(() =>
                    {
                        for (int j = 0; j < 50; j++)
                        {
                            var c = CapturePolicy(ref retained);
                            Assert.NotNull(c);
                        }
                    });
                }
                await Task.WhenAll(tasks);
                Assert.Same(fixturePolicy, retained);
            }

            // 4. After fixture scope disposal: ambient scope returns to null, but retained stays fixture
            Assert.Null(SingBoxRuntimePolicy.Current);
            var afterDisposal = CapturePolicy(ref retained);
            Assert.Same(fixturePolicy, afterDisposal);
            Assert.Same(fixturePolicy, retained);

            // 5. Default dominance: entering DefaultProduction overrides and permanently latches DefaultProduction
            using (SingBoxRuntimePolicy.EnterScope(SingBoxRuntimePolicy.DefaultProduction))
            {
                var dominant = CapturePolicy(ref retained);
                Assert.Same(SingBoxRuntimePolicy.DefaultProduction, dominant);
                Assert.Same(SingBoxRuntimePolicy.DefaultProduction, retained);
            }

            // 6. After DefaultProduction scope disposal: retained MUST NOT be weakened back to fixture or null
            Assert.Null(SingBoxRuntimePolicy.Current);
            var postDefault = CapturePolicy(ref retained);
            Assert.Same(SingBoxRuntimePolicy.DefaultProduction, postDefault);
            Assert.Same(SingBoxRuntimePolicy.DefaultProduction, retained);

            // 7. Even if entered under fixture policy again, DefaultProduction cannot be weakened
            using (SingBoxRuntimePolicy.EnterScope(fixturePolicy))
            {
                var underFixture = CapturePolicy(ref retained);
                Assert.Same(SingBoxRuntimePolicy.DefaultProduction, underFixture);
                Assert.Same(SingBoxRuntimePolicy.DefaultProduction, retained);
            }
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public async Task FreeConfigDeepVerifier_UnavailablePolicy_ThrowsPolicyException_AndPreservesEntryFields()
    {
        using var _ = SingBoxRuntimePolicy.EnterScope(SingBoxRuntimePolicy.DefaultProduction);

        var verifier = new FreeConfigDeepVerifier(Log.Logger);
        var entry = new FreeConfigEntry
        {
            Host = "127.0.0.1",
            Port = 443,
            Status = FreeConfigStatus.Verified,
            LastError = null
        };

        var initialStatus = entry.Status;
        var initialLastError = entry.LastError;

        // Under worker0 new behavior, unavailable policy throws typed policy exception and does NOT alter entry fields
        var ex = await Assert.ThrowsAsync<SingBoxRuntimePolicyException>(async () =>
        {
            await verifier.VerifyOneAsync(entry);
        });

        Assert.Equal(SingBoxRuntimeOperation.Verify, ex.Operation);
        Assert.Equal(initialStatus, entry.Status);
        Assert.Equal(initialLastError, entry.LastError);
    }

    private static SingBoxRuntimePolicy? CapturePolicy(ref SingBoxRuntimePolicy? retained)
        => SingBoxRuntimePolicy.Capture(ref retained);
}
