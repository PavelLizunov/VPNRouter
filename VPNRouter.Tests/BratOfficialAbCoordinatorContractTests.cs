#nullable enable

namespace VPNRouter.Tests;

public sealed class BratOfficialAbCoordinatorContractTests
{
    [Fact]
    public void Coordinator_HasOnlyFourModesAndFixedVerifierSurface()
    {
        var source = ReadRepoFile("tools", "brat-official-ab.ps1");

        Assert.Contains("[ValidateSet('Preflight', 'Install', 'Run3', 'Cleanup')]", source, StringComparison.Ordinal);
        Assert.Contains("[ValidateSet('Control', 'Target')]", source, StringComparison.Ordinal);
        Assert.Contains("[string]$Profile = 'Target'", source, StringComparison.Ordinal);
        Assert.Contains("$EvidenceProfile = if ($Mode -in @('Preflight', 'Run3')) { $Profile } else { 'None' }", source, StringComparison.Ordinal);
        Assert.Contains("[ValidateSet('Preflight', 'Install', 'Cycle', 'Cleanup')]", source, StringComparison.Ordinal);
        Assert.Contains("-Action altclient -AltClient 'AmneziaWG' -AltOperation $Operation -AltProfile $RunProfile", source, StringComparison.Ordinal);
        Assert.Contains("-Action altclient -AltClient 'AmneziaWG' -AltOperation $Operation 2>&1", source, StringComparison.Ordinal);
        Assert.Contains("for ($cycle = 1; $cycle -le 3; $cycle++)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ProtocolOrdinal", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SubscriptionUrl", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ServerHost", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Coordinator_TargetRunRequiresThreePassingControlCyclesFirst()
    {
        var source = ReadRepoFile("tools", "brat-official-ab.ps1");

        var control = source.IndexOf("-Operation Cycle -Cycle $cycle -RunProfile Control", StringComparison.Ordinal);
        var targetGate = source.IndexOf("if ($Profile -eq 'Target' -and $controlPassed)", StringComparison.Ordinal);
        var target = source.IndexOf("-Operation Cycle -Cycle $cycle -RunProfile Target", StringComparison.Ordinal);

        Assert.True(control >= 0 && targetGate > control && target > targetGate);
        Assert.Equal(2, source.Split("for ($cycle = 1; $cycle -le 3; $cycle++)", StringSplitOptions.None).Length - 1);
        Assert.Contains("-Operation Preflight -RunProfile Control", source, StringComparison.Ordinal);
        Assert.Contains("-Operation Preflight -RunProfile Target", source, StringComparison.Ordinal);
        Assert.Contains("if ($result.Status -ne 'PASS') { $controlPassed = $false }", source, StringComparison.Ordinal);
        Assert.Contains("if ($result.Status -in @('BLOCKED', 'ABORTED')) { break }", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Coordinator_NeverImplementsRemoteClientOrReadsOpaqueInputs()
    {
        var source = ReadRepoFile("tools", "brat-official-ab.ps1");
        var forbidden = new[]
        {
            "New-PSSession", "Invoke-Command", "Start-Process", "Get-Content",
            "Import-Clixml", "Get-NetAdapter", "Find-NetRoute", "Get-Process",
            "Get-CimInstance", "Copy-Item", "Invoke-WebRequest", "FixturePath",
            "ConfigPath", "EndpointAddress", "PrivateKey", "Environment.GetEnvironmentVariable",
        };

        foreach (var token in forbidden)
            Assert.DoesNotContain(token, source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Coordinator_WritesOnlyAllowlistedAggregateEvidenceUnderIgnoredRoot()
    {
        var source = ReadRepoFile("tools", "brat-official-ab.ps1");
        var gitignore = ReadRepoFile(".gitignore");

        Assert.Contains("artifacts\\brat-stability\\official-ab", source, StringComparison.Ordinal);
        Assert.Contains("/artifacts/brat-stability/", gitignore, StringComparison.Ordinal);
        Assert.Contains("ConvertTo-SafeResult", source, StringComparison.Ordinal);
        Assert.Contains("'Ready', 'InstallerNotApproved', 'ClientNotInstalled', 'ClientBinaryInvalid'", source, StringComparison.Ordinal);
        Assert.Contains("FixtureAttestationMissing", source, StringComparison.Ordinal);
        Assert.Contains("'TransportLost', 'CleanupFailed', 'Cleaned'", source, StringComparison.Ordinal);
        Assert.Contains("PayloadIntegrityFailure", source, StringComparison.Ordinal);
        Assert.Contains("'Sent', 'Received', 'Loss', 'Duplicate', 'Reorder', 'Corruption', 'Unknown'", source, StringComparison.Ordinal);
        Assert.Contains("'RttP50Ms', 'RttP95Ms', 'RttP99Ms', 'MaxAcknowledgedGapMs'", source, StringComparison.Ordinal);
        Assert.Contains("[DateTimeOffset]::TryParseExact", source, StringComparison.Ordinal);
        Assert.Contains("$ended -lt $started", source, StringComparison.Ordinal);
        Assert.Contains("'ManagementRouteIntact', 'ExpectedAdapterRoute', 'AdapterByteCorrelation', 'CleanTeardown'", source, StringComparison.Ordinal);
        Assert.Contains("$property.Value -isnot [bool]", source, StringComparison.Ordinal);
        Assert.Contains("Official-client verifier omitted the fixed aggregate schema", source, StringComparison.Ordinal);
        Assert.Contains("fixed invariants", source, StringComparison.Ordinal);
        Assert.Contains("measured evidence without complete cycle proof", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RouteScope", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TunCorrelation", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FullTunnelProven", source, StringComparison.Ordinal);
        Assert.Contains("ErrorClass", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ErrorMessage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("$Result | ConvertTo-Json", source, StringComparison.Ordinal);
        Assert.DoesNotContain("$raw | ConvertTo-Json", source, StringComparison.Ordinal);
        Assert.DoesNotContain("$safeMetrics[$name] = $property.Value", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Coordinator_SerializesRunAndAlwaysCleansUpInFinally()
    {
        var source = ReadRepoFile("tools", "brat-official-ab.ps1");
        var finallyIndex = source.IndexOf("finally {", StringComparison.Ordinal);
        var cleanupIndex = source.IndexOf("Invoke-OfficialOperation -Operation Cleanup", finallyIndex, StringComparison.Ordinal);

        Assert.Contains("Local\\VPNRouterBratStability", source, StringComparison.Ordinal);
        Assert.Contains("$Mutex.WaitOne(0)", source, StringComparison.Ordinal);
        Assert.Contains("if ($MutexHeld -and $Mode -in @('Install', 'Run3'))", source, StringComparison.Ordinal);
        Assert.DoesNotContain("$Mode -ne 'Cleanup'", source, StringComparison.Ordinal);
        Assert.True(finallyIndex >= 0);
        Assert.True(cleanupIndex > finallyIndex);
        Assert.Contains("$Mutex.ReleaseMutex()", source, StringComparison.Ordinal);
        Assert.Contains("$Mutex.Dispose()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Coordinator_SeparatesMeasuredFailureFromBlockedAndAborted()
    {
        var source = ReadRepoFile("tools", "brat-official-ab.ps1");

        Assert.Contains("@('PASS', 'FAIL', 'BLOCKED', 'ABORTED')", source, StringComparison.Ordinal);
        Assert.Contains("[string]$Result.Client -ne 'AmneziaWG'", source, StringComparison.Ordinal);
        Assert.Contains("[string]$Result.Operation -ne $Operation", source, StringComparison.Ordinal);
        Assert.Contains("[string]$Result.Profile -ne $RunProfile", source, StringComparison.Ordinal);
        Assert.Contains("$status -eq 'FAIL' -and $Operation -ne 'Cycle'", source, StringComparison.Ordinal);
        Assert.Contains("$status -eq 'FAIL' -and $lifecycle -notin @('ReplyGap', 'CookieFailure', 'NetworkFailure')", source, StringComparison.Ordinal);
        Assert.Contains("($lifecycle -eq 'PayloadIntegrityFailure') -ne ($status -eq 'ABORTED')", source, StringComparison.Ordinal);
        Assert.Contains("$Operation -eq 'Cleanup' -and $status -notin @('PASS', 'ABORTED')", source, StringComparison.Ordinal);
        Assert.Contains("Preflight = 'Ready'", source, StringComparison.Ordinal);
        Assert.Contains("Cleanup = 'Cleaned'", source, StringComparison.Ordinal);
        Assert.Contains("if ($safe.Status -eq 'FAIL') { $script:NetworkFailures++ }", source, StringComparison.Ordinal);
        Assert.Contains("if ($Status -eq 'ABORTED')", source, StringComparison.Ordinal);
        Assert.Contains("if ($Status -eq 'BLOCKED')", source, StringComparison.Ordinal);
        Assert.Contains("$TerminalStatus = 'ABORTED'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("NetworkFailures++", source[..source.IndexOf("function Invoke-OfficialOperation", StringComparison.Ordinal)], StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VPNRouter.sln")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        var path = Path.Combine(new[] { directory!.FullName }.Concat(relativeParts).ToArray());
        Assert.True(File.Exists(path), $"Repository file not found: {path}");
        return File.ReadAllText(path);
    }
}
