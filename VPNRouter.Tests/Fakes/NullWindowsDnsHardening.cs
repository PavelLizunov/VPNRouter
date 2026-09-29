#nullable enable

using System.Collections.Generic;
using Serilog;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests.Fakes;

public sealed class NullWindowsDnsHardening : IWindowsDnsHardening
{
    public List<(string Op, AppSettings? Settings)> Calls { get; } = new();

    public int ApplyCount => Calls.Count(c => c.Op == "Apply");

    public int RestoreCount => Calls.Count(c => c.Op == "Restore");

    public int EnableLockdownCount =>
        Calls.Count(c => c.Op == "EnableLockdownIfConfigured");

    public List<(bool TunnelServing, AppSettings? Settings)> ReconcileCalls { get; } = new();

    public int ReconcileCount => ReconcileCalls.Count;

    public bool? LastReconcileServing =>
        ReconcileCalls.Count == 0 ? null : ReconcileCalls[^1].TunnelServing;

    public void Apply(AppSettings? settings, ILogger? logger) =>
        Calls.Add(("Apply", settings));

    public void Restore(ILogger? logger) =>
        Calls.Add(("Restore", null));

    public void EnableLockdownIfConfigured(AppSettings? settings, ILogger? logger) =>
        Calls.Add(("EnableLockdownIfConfigured", settings));

    public void ReconcileLockdownForHealth(bool tunnelServing, AppSettings? settings, ILogger? logger)
    {
        Calls.Add(("ReconcileLockdownForHealth", settings));
        ReconcileCalls.Add((tunnelServing, settings));
    }
}
