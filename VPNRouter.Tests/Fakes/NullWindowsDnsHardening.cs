// Phase 4 (Task #36-A, 2026-05-21) — capture-only test double for
// IWindowsDnsHardening.
//
// Why: the static WindowsDnsHardening facade writes to HKLM (DNSClient
// policy + Dnscache parameters) on Apply, plus shells out via netsh +
// installs firewall rules. Running the lifecycle happy-path tests
// (Task #36-C) against the real impl would silently mutate the dev / CI
// machine's machine-wide DNS resolution behaviour. NullWindowsDnsHardening
// records every invocation so tests can assert "the pipeline called this
// exactly once during ColdStart" without touching real OS state.
//
// Mirrors the FakeHttpClient / FakeProcessRunner capture pattern: store
// invocations in a list keyed by op-name + payload, expose for assertion.
//
// Brief: plans/phase4-iwindowsdnshardening-2026-05-21.md.

#nullable enable

using System.Collections.Generic;
using Serilog;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IWindowsDnsHardening"/> double for unit tests.
/// Records every <see cref="Apply"/> / <see cref="Restore"/> /
/// <see cref="EnableLockdownIfConfigured"/> invocation in
/// <see cref="Calls"/> so tests can pin "the pipeline called this exactly
/// once on cold start" or "Stop drove Restore".
///
/// <para>No filesystem, no netsh, no HKLM. Each method is a no-op beyond
/// the capture, matching the swallow-all contract documented on
/// <see cref="IWindowsDnsHardening"/>.</para>
///
/// <para>Thread-safety: the underlying <see cref="List{T}"/> is NOT
/// locked. Tests that drive the pipeline through the engine are sequential
/// (xUnit cases run on one thread by default for this project — see
/// VPNRouter.Tests.csproj's xunit.runner.json that disables
/// parallelization). If a future test drives concurrent Apply calls from
/// multiple threads, swap the list for a <see cref="System.Collections.Concurrent.ConcurrentQueue{T}"/>
/// — keeping the simpler list shape here matches the existing
/// <see cref="FakeProcessRunner.RunCalls"/> convention.</para>
/// </summary>
public sealed class NullWindowsDnsHardening : IWindowsDnsHardening
{
    /// <summary>
    /// Ordered log of every method invocation. Tests assert on Op name +
    /// captured <see cref="AppSettings"/> reference (null for Restore;
    /// the same instance the caller passed otherwise).
    /// </summary>
    public List<(string Op, AppSettings? Settings)> Calls { get; } = new();

    /// <summary>
    /// Per-op counts for tests that just need "did this fire" rather than
    /// the full invocation list.
    /// </summary>
    public int ApplyCount => Calls.Count(c => c.Op == "Apply");

    /// <summary>How many times <see cref="Restore"/> has been called.</summary>
    public int RestoreCount => Calls.Count(c => c.Op == "Restore");

    /// <summary>
    /// How many times <see cref="EnableLockdownIfConfigured"/> has been
    /// called.
    /// </summary>
    public int EnableLockdownCount =>
        Calls.Count(c => c.Op == "EnableLockdownIfConfigured");

    /// <inheritdoc />
    public void Apply(AppSettings? settings, ILogger? logger) =>
        Calls.Add(("Apply", settings));

    /// <inheritdoc />
    public void Restore(ILogger? logger) =>
        Calls.Add(("Restore", null));

    /// <inheritdoc />
    public void EnableLockdownIfConfigured(AppSettings? settings, ILogger? logger) =>
        Calls.Add(("EnableLockdownIfConfigured", settings));
}
