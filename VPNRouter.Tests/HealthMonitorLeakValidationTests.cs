using System.Reflection;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class HealthMonitorLeakValidationTests
{
    [Fact]
    public void GenerateConfigJson_RoutesThroughConfigPipeline()
    {
        var hmType = typeof(HealthMonitor);
        var generate = hmType.GetMethod("GenerateConfigJson",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(generate);

        var body = generate!.GetMethodBody();
        Assert.NotNull(body);

        var ilBytes = body!.GetILAsByteArray();
        Assert.NotNull(ilBytes);

        var module = generate.Module;
        var found = false;
        for (int i = 0; i < ilBytes!.Length - 4; i++)
        {
            if (ilBytes[i] == 0x28 || ilBytes[i] == 0x6F)
            {
                int token = System.BitConverter.ToInt32(ilBytes, i + 1);
                MethodBase? called;
                try
                {
                    called = module.ResolveMethod(token);
                }
                catch
                {
                    continue;
                }
                if (called?.DeclaringType == typeof(ConfigPipeline)
                    && called.Name == nameof(ConfigPipeline.Generate))
                {
                    found = true;
                    break;
                }
            }
        }

        Assert.True(found,
            "HealthMonitor.GenerateConfigJson must call ConfigPipeline.Generate (Phase 2F extraction).");
    }

    [Fact]
    public void ConfigPipelineGenerate_ContainsValidateConfigCall()
    {
        var pipeline = typeof(ConfigPipeline);
        var generate = pipeline.GetMethod(
            nameof(ConfigPipeline.Generate),
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(generate);

        var body = generate!.GetMethodBody();
        Assert.NotNull(body);

        var ilBytes = body!.GetILAsByteArray();
        Assert.NotNull(ilBytes);

        var module = generate.Module;
        var found = false;
        for (int i = 0; i < ilBytes!.Length - 4; i++)
        {
            if (ilBytes[i] == 0x28 || ilBytes[i] == 0x6F)
            {
                int token = System.BitConverter.ToInt32(ilBytes, i + 1);
                MethodBase? called;
                try
                {
                    called = module.ResolveMethod(token);
                }
                catch
                {
                    continue;
                }
                if (called?.DeclaringType == typeof(LeakProtection)
                    && called.Name == nameof(LeakProtection.ValidateConfig))
                {
                    found = true;
                    break;
                }
            }
        }

        Assert.True(found,
            "ConfigPipeline.Generate must call LeakProtection.ValidateConfig — see r5 leak-validation chokepoint comment.");
    }
}
