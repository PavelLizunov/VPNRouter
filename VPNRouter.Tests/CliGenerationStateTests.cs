#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using VPNRouter.CLI.Commands;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public class CliGenerationStateTests
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
    public void ConcurrentReadNeverObservesPartialJson()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "VPNRouter_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var path = Path.Combine(tempDir, "state.json");
        var mutexName = "VPNRouter_StateFile_TestMutex_" + Guid.NewGuid().ToString("N");

        try
        {
            var initialGen = Guid.NewGuid();
            var initialState = new RunState
            {
                ActiveProfile = "InitialProfile",
                SingBoxPid = 100,
                OwnerPid = 50,
                RunGeneration = initialGen
            };
            StateFile.Write(initialState, path, mutexName);

            const int iterations = 50;
            var writeTask = Task.Run(() =>
            {
                for (int i = 0; i < iterations; i++)
                {
                    var state = new RunState
                    {
                        ActiveProfile = $"Profile_{i}",
                        SingBoxPid = 1000 + i,
                        OwnerPid = 500 + i,
                        RunGeneration = Guid.NewGuid()
                    };
                    StateFile.Write(state, path, mutexName);
                }
            });

            var readTask = Task.Run(() =>
            {
                for (int i = 0; i < iterations * 2; i++)
                {
                    var readState = StateFile.Read(path, mutexName);
                    Assert.NotNull(readState);
                    Assert.NotNull(readState.ActiveProfile);
                    Assert.False(string.IsNullOrEmpty(readState.ActiveProfile));
                }
            });

            Task.WaitAll(writeTask, readTask);

            var tempFiles = Directory.GetFiles(tempDir, "*.tmp");
            Assert.Empty(tempFiles);
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
