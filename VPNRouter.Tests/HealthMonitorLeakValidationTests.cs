using System.Reflection;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

/// <summary>
/// v2.31.9-r5 regression pin: <see cref="HealthMonitor"/> auto-restart
/// path must run <see cref="LeakProtection.ValidateConfig"/>.
///
/// <para>Pre-r5 ValidateConfig was called from
/// <see cref="VpnEngine.StartAsync"/> + <see cref="VpnEngine.Apply"/>
/// only — HealthMonitor's debounced rescan / crash-recovery path
/// regenerated the config but skipped validation. Subscription edge
/// cases or VlessServersResolver glitches could ship a leak-prone
/// config to sing-box silently. r5 closes the gap with an advisory
/// validator call that warns but doesn't block recovery.</para>
///
/// <para>Phase 2F (2026-05-17) wires HealthMonitor.GenerateConfigJson
/// through <see cref="ConfigPipeline.Generate"/>, which now owns the
/// LeakProtection.ValidateConfig call. So this pin verifies two
/// links: (a) HealthMonitor calls ConfigPipeline.Generate, and (b)
/// ConfigPipeline.Generate calls LeakProtection.ValidateConfig.
/// If either link is removed in a future refactor the test fails
/// loudly.</para>
/// </summary>
public sealed class HealthMonitorLeakValidationTests
{
    [Fact]
    public void GenerateConfigJson_RoutesThroughConfigPipeline()
    {
        // Read the compiled IL of GenerateConfigJson via reflection and
        // verify it references ConfigPipeline.Generate.
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
            // 0x28 = call, 0x6F = callvirt
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
        // Second link: ConfigPipeline.Generate must call
        // LeakProtection.ValidateConfig. Together with the first test
        // this pins the HealthMonitor → ConfigPipeline → LeakProtection
        // chain end-to-end so r5's chokepoint stays effective even
        // though the call now lives one indirection away.
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
