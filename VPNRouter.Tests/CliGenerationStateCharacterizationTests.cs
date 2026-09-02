#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using VPNRouter.CLI.Commands;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public class CliGenerationStateCharacterizationTests
{
    [Fact]
    public void LegacyState_RemainsReadable_ButCannotBeConditionallyCleared()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "VPNRouter_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var path = Path.Combine(tempDir, "state.json");
        var mutexName = "VPNRouter_StateFile_TestMutex_" + Guid.NewGuid().ToString("N");

        try
        {
            File.WriteAllText(
                path,
                """
                {
                  "schema_version": 1,
                  "ActiveProfile": "LegacyProfile",
                  "SingBoxPid": 1234,
                  "OwnerPid": 5678
                }
                """);

            var readState = StateFile.Read(path, mutexName);
            Assert.NotNull(readState);
            Assert.Equal("LegacyProfile", readState.ActiveProfile);
            Assert.Equal(Guid.Empty, readState.RunGeneration);

            bool clearedEmpty = StateFile.ClearIfGeneration(Guid.Empty, path, mutexName);
            Assert.False(clearedEmpty);
            Assert.True(File.Exists(path));

            bool clearedRandom = StateFile.ClearIfGeneration(Guid.NewGuid(), path, mutexName);
            Assert.False(clearedRandom);
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void Write_DoesNotOpenPreplantedLegacyTempPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "VPNRouter_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var path = Path.Combine(tempDir, "state.json");
        var legacyTemp = path + ".tmp";
        var mutexName = "VPNRouter_StateFile_TestMutex_" + Guid.NewGuid().ToString("N");

        try
        {
            File.WriteAllText(legacyTemp, "sentinel");
            StateFile.Write(
                new RunState { ActiveProfile = "OwnedRun", RunGeneration = Guid.NewGuid() },
                path,
                mutexName);

            Assert.Equal("sentinel", File.ReadAllText(legacyTemp));
            Assert.NotNull(StateFile.Read(path, mutexName));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void MatchingGeneration_UpdatesExactChildAndClearsState()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "VPNRouter_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var path = Path.Combine(tempDir, "state.json");
        var mutexName = "VPNRouter_StateFile_TestMutex_" + Guid.NewGuid().ToString("N");

        try
        {
            var generation = Guid.NewGuid();
            StateFile.Write(
                new RunState
                {
                    ActiveProfile = "OwnedRun",
                    SingBoxPid = 100,
                    OwnerPid = 50,
                    RunGeneration = generation
                },
                path,
                mutexName);

            var child = new OwnedProcessIdentity(3000, 100000, Path.Combine(tempDir, "sing-box"));
            Assert.True(StateFile.TryUpdateChild(generation, child, path, mutexName));
            var staleChild = new OwnedProcessIdentity(
                child.Pid + 1,
                child.StartedAtUtcTicks - 1,
                Path.Combine(tempDir, "stale-sing-box"));
            Assert.False(StateFile.TryUpdateChild(generation, staleChild, path, mutexName));

            var current = Assert.IsType<RunState>(StateFile.Read(path, mutexName));
            Assert.Equal(child.Pid, current.SingBoxPid);
            Assert.Equal(child.StartedAtUtcTicks, current.SingBoxStartedAtUtcTicks);
            Assert.Equal(child.ExecutablePath, current.SingBoxExecutablePath);

            Assert.True(StateFile.ClearIfGeneration(generation, path, mutexName));
            Assert.False(File.Exists(path));
            Assert.Null(StateFile.Read(path, mutexName));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void OldGeneration_UpdateCannotOverwriteReplacement()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "VPNRouter_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var path = Path.Combine(tempDir, "state.json");
        var mutexName = "VPNRouter_StateFile_TestMutex_" + Guid.NewGuid().ToString("N");

        try
        {
            var genA = Guid.NewGuid();
            var genB = Guid.NewGuid();

            var stateB = new RunState
            {
                ActiveProfile = "ReplacementB",
                SingBoxPid = 2000,
                OwnerPid = 1000,
                RunGeneration = genB
            };

            StateFile.Write(stateB, path, mutexName);

            var childA = new OwnedProcessIdentity(3000, 100000, @"C:\path\to\sing-box.exe");

            bool updated = StateFile.TryUpdateChild(genA, childA, path, mutexName);
            Assert.False(updated);

            var current = StateFile.Read(path, mutexName);
            Assert.NotNull(current);
            Assert.Equal(genB, current.RunGeneration);
            Assert.Equal("ReplacementB", current.ActiveProfile);
            Assert.NotEqual(3000, current.SingBoxPid);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void OldGeneration_ClearCannotDeleteReplacement()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "VPNRouter_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var path = Path.Combine(tempDir, "state.json");
        var mutexName = "VPNRouter_StateFile_TestMutex_" + Guid.NewGuid().ToString("N");

        try
        {
            var genA = Guid.NewGuid();
            var genB = Guid.NewGuid();

            var stateB = new RunState
            {
                ActiveProfile = "ReplacementB",
                SingBoxPid = 2000,
                OwnerPid = 1000,
                RunGeneration = genB
            };

            StateFile.Write(stateB, path, mutexName);

            bool cleared = StateFile.ClearIfGeneration(genA, path, mutexName);
            Assert.False(cleared);

            Assert.True(File.Exists(path));
            var current = StateFile.Read(path, mutexName);
            Assert.NotNull(current);
            Assert.Equal(genB, current.RunGeneration);
            Assert.Equal("ReplacementB", current.ActiveProfile);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void MalformedState_IsNotReportedAsConditionallyCleared()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "VPNRouter_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var path = Path.Combine(tempDir, "state.json");
        var mutexName = "VPNRouter_StateFile_TestMutex_" + Guid.NewGuid().ToString("N");

        try
        {
            File.WriteAllText(path, "{not-json");
            Assert.False(StateFile.ClearIfGeneration(Guid.NewGuid(), path, mutexName));
            Assert.NotEmpty(Directory.GetFiles(tempDir, "state.json.corrupt-*"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task ConcurrentReadNeverObservesPartialJson()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "VPNRouter_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var path = Path.Combine(tempDir, "state.json");
        var mutexName = "VPNRouter_StateFile_TestMutex_" + Guid.NewGuid().ToString("N");
        var cancellationToken = TestContext.Current.CancellationToken;

        try
        {
            StateFile.Write(
                new RunState
                {
                    ActiveProfile = "InitialProfile",
                    SingBoxPid = 100,
                    OwnerPid = 50,
                    RunGeneration = Guid.NewGuid()
                },
                path,
                mutexName);

            const int iterations = 200;
            using var start = new Barrier(2);
            var writeTask = Task.Run(() =>
            {
                start.SignalAndWait(cancellationToken);
                for (var i = 0; i < iterations; i++)
                {
                    StateFile.Write(
                        new RunState
                        {
                            ActiveProfile = $"Profile_{i}",
                            SingBoxPid = 1000 + i,
                            OwnerPid = 500 + i,
                            RunGeneration = Guid.NewGuid()
                        },
                        path,
                        mutexName);
                }
            }, cancellationToken);

            var readTask = Task.Run(() =>
            {
                start.SignalAndWait(cancellationToken);
                for (var i = 0; i < iterations * 2; i++)
                {
                    var readState = StateFile.Read(path, mutexName);
                    Assert.NotNull(readState);
                    Assert.False(string.IsNullOrEmpty(readState.ActiveProfile));
                }
            }, cancellationToken);

            await Task.WhenAll(writeTask, readTask).WaitAsync(cancellationToken);
            Assert.Empty(Directory.GetFiles(tempDir, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }
}
