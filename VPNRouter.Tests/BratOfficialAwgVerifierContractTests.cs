#nullable enable

namespace VPNRouter.Tests;

public sealed class BratOfficialAwgVerifierContractTests
{
    [Fact]
    public void Verifier_ExposesOnlyFixedOfficialClientOperations()
    {
        var verifier = ReadRepoFile("tools", "brat-verify.ps1");
        var action = Slice(verifier, "    'altclient' {", "    'lifecycle' {");

        Assert.Contains("'altclient'", verifier, StringComparison.Ordinal);
        Assert.Contains("[ValidateSet('AmneziaWG')]", verifier, StringComparison.Ordinal);
        Assert.Contains("[ValidateSet('Preflight', 'Install', 'Cycle', 'Cleanup')]", verifier, StringComparison.Ordinal);
        Assert.Contains("[ValidateSet('Control', 'Target')]", verifier, StringComparison.Ordinal);
        Assert.Contains("Test-ApprovedOfficialAwgInstaller", action, StringComparison.Ordinal);
        Assert.Contains("Invoke-Command -Session $s -FilePath $OfficialAwgRemoteHelper", action, StringComparison.Ordinal);
        Assert.DoesNotContain("[string]$AltExecutable", verifier, StringComparison.Ordinal);
        Assert.DoesNotContain("[string]$Fixture", verifier, StringComparison.Ordinal);
        Assert.DoesNotContain("[string]$Endpoint", verifier, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteHelper_PinsPackageBinaryAndPayload()
    {
        var helper = ReadRepoFile("tools", "brat-official-awg-remote.ps1");

        Assert.Contains("1b7308d0c74685193dee5d30fd30f370b5a2748a7f648869cd16f25286efc784", helper, StringComparison.Ordinal);
        Assert.Contains("dcd5ace18c26a58dd632b337f769673be14a288cfc04ba37f69587884d3806be", helper, StringComparison.Ordinal);
        Assert.Contains("141D90A1BA8F61863FBEDDF7DD1D66C1D1E0B128", helper, StringComparison.Ordinal);
        Assert.Contains("5855167c4c89efa5c5adbd0933ee4269382785bb35d6b04f7a5fd27d80f72934", helper, StringComparison.Ordinal);
        Assert.Contains("Get-AuthenticodeSignature -LiteralPath $ClientExe", helper, StringComparison.Ordinal);
        Assert.Contains("DO_NOT_LAUNCH=1", helper, StringComparison.Ordinal);
        Assert.Contains("[Environment]::MachineName -ine 'WINBRAT'", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteHelper_NeverReadsOrCopiesOpaqueFixtureContents()
    {
        var helper = ReadRepoFile("tools", "brat-official-awg-remote.ps1");

        Assert.Contains("VPNRouter-AB-AWG-Control.conf.dpapi", helper, StringComparison.Ordinal);
        Assert.Contains("VPNRouter-AB-AWG.conf.dpapi", helper, StringComparison.Ordinal);
        Assert.Contains("VPNRouter-AB-AWG-Control.tailscale-safe", helper, StringComparison.Ordinal);
        Assert.Contains("VPNRouter-AB-AWG.tailscale-safe", helper, StringComparison.Ordinal);
        Assert.Contains("Test-ProtectedAcl", helper, StringComparison.Ordinal);
        Assert.Contains("AreAccessRulesProtected", helper, StringComparison.Ordinal);
        Assert.Contains("AccessControlType]::Deny", helper, StringComparison.Ordinal);
        Assert.Contains("FileSystemRights]::FullControl", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-Content -LiteralPath $Selected.Fixture", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-FileHash -LiteralPath $Selected.Fixture", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("Copy-Item", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("PrivateKey", helper, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/dumplog", helper, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemoteHelper_ArmsWatchdogBeforeTunnelAndPreservesManagement()
    {
        var helper = ReadRepoFile("tools", "brat-official-awg-remote.ps1");
        var cycle = Slice(helper, "function Invoke-Cycle {", "    $SemaphoreHeld = $Semaphore.WaitOne(0)");

        Assert.Contains("Get-Service -Name 'Tailscale'", helper, StringComparison.Ordinal);
        Assert.Contains("Get-NetTCPConnection -State Established", helper, StringComparison.Ordinal);
        Assert.Contains("$_.LocalPort -in @(5985, 5986)", helper, StringComparison.Ordinal);
        Assert.Contains("Find-NetRoute -RemoteIPAddress $address.IPAddressToString", helper, StringComparison.Ordinal);
        Assert.Contains("$adapters.InterfaceIndex -contains $route.InterfaceIndex", helper, StringComparison.Ordinal);
        Assert.Contains("New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(10)", helper, StringComparison.Ordinal);
        Assert.True(cycle.IndexOf("Start-FixedWatchdog", StringComparison.Ordinal) <
                    cycle.IndexOf("Invoke-OfficialClientCommand -Command Install", StringComparison.Ordinal));
        Assert.Contains("Test-NoCgnatAddress", cycle, StringComparison.Ordinal);
        Assert.Contains("AmneziaWGTunnel$VPNRouter-AB-AWG-Control", helper, StringComparison.Ordinal);
        Assert.Contains("AmneziaWGTunnel$VPNRouter-AB-AWG", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("Disable-NetAdapter", helper, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Get-Service *", helper, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemoteHelper_ReturnsOnlyAggregateAttributionAndCleansExactly()
    {
        var helper = ReadRepoFile("tools", "brat-official-awg-remote.ps1");

        Assert.Contains("StartedUtc", helper, StringComparison.Ordinal);
        Assert.Contains("EndedUtc", helper, StringComparison.Ordinal);
        Assert.Contains("ManagementRouteIntact", helper, StringComparison.Ordinal);
        Assert.Contains("ExpectedAdapterRoute", helper, StringComparison.Ordinal);
        Assert.Contains("AdapterByteCorrelation", helper, StringComparison.Ordinal);
        Assert.Contains("CleanTeardown", helper, StringComparison.Ordinal);
        Assert.Contains("$loadDelta -gt 0 -and $loadDelta -gt $quietDelta", helper, StringComparison.Ordinal);
        Assert.Contains("Invoke-FixedCleanup", helper, StringComparison.Ordinal);
        var cleanup = Slice(helper, "function Invoke-FixedCleanup {", "function Invoke-Install {");
        Assert.True(cleanup.IndexOf("Stop-FixedTunnel", StringComparison.Ordinal) <
                    cleanup.IndexOf("Stop-FixedWatchdog", StringComparison.Ordinal));
        Assert.True(cleanup.IndexOf("Stop-FixedWatchdog", StringComparison.Ordinal) <
                    cleanup.IndexOf("Remove-FixedWorkRoot", StringComparison.Ordinal));
        Assert.Contains("Stop-ScheduledTask -TaskName $WatchdogTask", helper, StringComparison.Ordinal);
        Assert.Contains("$task.State -eq 'Running'", helper, StringComparison.Ordinal);
        Assert.Contains("System.IO.FileAttributes]::ReparsePoint", helper, StringComparison.Ordinal);
        Assert.Contains("System.Collections.Generic.Queue[string]", helper, StringComparison.Ordinal);
        Assert.Contains("$resolved -ine 'C:\\r4review\\official-ab\\current'", helper, StringComparison.Ordinal);
        Assert.Contains("$null -eq $tun", helper, StringComparison.Ordinal);
        Assert.Contains("RedirectStandardOutput = $true", helper, StringComparison.Ordinal);
        Assert.Contains("ReadToEndAsync()", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("client-output.txt", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("client-error.txt", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void Verifier_FailsClosedOnTransportAndSanitizesEveryField()
    {
        var verifier = ReadRepoFile("tools", "brat-verify.ps1");
        var action = Slice(verifier, "    'altclient' {", "    'lifecycle' {");

        Assert.Contains("AddMinutes(12)", action, StringComparison.Ordinal);
        Assert.Contains("'WatchdogFired'", action, StringComparison.Ordinal);
        Assert.Contains("'TransportLost'", action, StringComparison.Ordinal);
        Assert.Contains("if ($observedWatchdogFired -isnot [bool])", action, StringComparison.Ordinal);
        Assert.Contains("if ($observedWatchdogFired) { $watchdogFired = $true }", action, StringComparison.Ordinal);
        Assert.Contains("CleanTeardown", action, StringComparison.Ordinal);
        Assert.Contains("Official-client network result lost route, byte or teardown attribution", action, StringComparison.Ordinal);
        Assert.Contains("Official-client helper returned an unapproved enum", action, StringComparison.Ordinal);
        Assert.Contains("Get-RequiredOfficialBoolean", action, StringComparison.Ordinal);
        Assert.Contains("$property.Value -isnot [bool]", action, StringComparison.Ordinal);
        Assert.Contains("Official-client helper omitted the fixed aggregate schema", action, StringComparison.Ordinal);
        Assert.Contains("Official-client metrics violate fixed aggregate invariants", action, StringComparison.Ordinal);
        Assert.Contains("PayloadIntegrityFailure", action, StringComparison.Ordinal);
        Assert.DoesNotContain("Write-Output $text", action, StringComparison.Ordinal);
        Assert.DoesNotContain("Write-Output $raw", action, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception.Message", action, StringComparison.Ordinal);
    }

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        var endIndex = source.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        Assert.True(startIndex >= 0 && endIndex > startIndex);
        return source[startIndex..endIndex];
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
