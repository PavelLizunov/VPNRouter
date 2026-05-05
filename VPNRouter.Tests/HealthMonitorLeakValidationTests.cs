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
/// <para>HealthMonitor.GenerateConfigJson is private + filesystem-
/// dependent (it reads custom config paths). Rather than refactoring
/// for DI, we use reflection to confirm the call is wired in the
/// method body — this catches accidental removal in a refactor.</para>
/// </summary>
public sealed class HealthMonitorLeakValidationTests
{
    [Fact]
    public void GenerateConfigJson_ContainsValidateConfigCall()
    {
        // Read the compiled IL of GenerateConfigJson via reflection and
        // verify it references LeakProtection.ValidateConfig. We don't
        // care about call ordering or arguments — just presence.
        var hmType = typeof(HealthMonitor);
        var generate = hmType.GetMethod("GenerateConfigJson",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(generate);

        var body = generate!.GetMethodBody();
        Assert.NotNull(body);

        var ilBytes = body!.GetILAsByteArray();
        Assert.NotNull(ilBytes);

        // Walk the IL stream looking for `call`/`callvirt` opcodes that
        // resolve to LeakProtection.ValidateConfig. This is a coarse
        // pin — enough to fail loudly if the call is removed in a
        // future refactor.
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
                if (called?.DeclaringType == typeof(LeakProtection)
                    && called.Name == nameof(LeakProtection.ValidateConfig))
                {
                    found = true;
                    break;
                }
            }
        }

        Assert.True(found,
            "HealthMonitor.GenerateConfigJson must call LeakProtection.ValidateConfig — see r5 leak-validation chokepoint comment.");
    }
}
