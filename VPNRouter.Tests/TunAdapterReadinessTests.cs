using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class TunAdapterReadinessTests
{
    [Fact]
    public void DisableOrphanedAdapter_NonExistentAdapter_NoThrow()
    {
        var ex = Record.Exception(() =>
            TunAdapterDiagnostics.DisableOrphanedAdapter(
                logger: null,
                interfaceName: "VPNRouter-Test-DoesNotExist-" + Guid.NewGuid().ToString("N"),
                context: "test.nonexistent"));
        Assert.Null(ex);
    }

    [Fact]
    public async Task PreStartCleanupAsync_NonWindows_ReturnsZeroNoOp()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(),
            "Non-Windows no-op contract; never clean the dev machine's live TUN from tests.");

        var n = await TunAdapterDiagnostics.PreStartCleanupAsync(
            logger: null, context: "test.non-windows");

        Assert.Equal(0, n);
    }

    [Fact]
    public void TunDiag_PreStartCleanup_NoTunAdapters_ReturnsSuccessNoOp()
    {
        Assert.Empty(TunAdapterDiagnostics.ExtractStaleAdapterNames(string.Empty));

        var noTun = """
            Admin State    State          Type             Interface Name
            -------------------------------------------------------------------------
            Enabled        Connected      Dedicated        Ethernet
            Enabled        Connected      Dedicated        Wi-Fi
            Enabled        Disconnected   Loopback         Loopback Pseudo-Interface 1
            """;
        Assert.Empty(TunAdapterDiagnostics.ExtractStaleAdapterNames(noTun));
    }

    [Fact]
    public void TunDiag_PreStartCleanup_OneStaleTun_RemovesIt()
    {
        var output = """
            Admin State    State          Type             Interface Name
            -------------------------------------------------------------------------
            Enabled        Connected      Dedicated        Ethernet
            Enabled        Disconnected   Dedicated        VPNRouter-TUN
            """;
        var result = TunAdapterDiagnostics.ExtractStaleAdapterNames(output);
        Assert.Single(result);
        Assert.Equal("VPNRouter-TUN", result[0], ignoreCase: true);
    }

    [Fact]
    public void TunDiag_PreStartCleanup_SingBoxFallbackName_Detects()
    {
        var bare = TunAdapterDiagnostics.ExtractStaleAdapterNames("""
            Admin State    State          Type             Interface Name
            Enabled        Disconnected   Dedicated        sing-box-tun
            """);
        Assert.Single(bare);

        var suffixed = TunAdapterDiagnostics.ExtractStaleAdapterNames("""
            Admin State    State          Type             Interface Name
            Enabled        Disconnected   Dedicated        sing-box-tun-abc12345
            """);
        Assert.Single(suffixed);
        Assert.Equal("sing-box-tun-abc12345", suffixed[0], ignoreCase: true);
    }

    [Fact]
    public void TunDiag_PreStartCleanup_UnrelatedWintunAdapter_LeavesAlone()
    {
        var output = """
            Admin State    State          Type             Interface Name
            -------------------------------------------------------------------------
            Enabled        Connected      Dedicated        Ethernet
            Enabled        Connected      Dedicated        Wintun Userspace Tunnel
            Enabled        Connected      Dedicated        wg-AmneziaWG
            Enabled        Connected      Dedicated        TAP-Windows Adapter V9
            Enabled        Connected      Dedicated        OpenVPN Wintun
            """;
        var result = TunAdapterDiagnostics.ExtractStaleAdapterNames(output);
        Assert.Empty(result);
    }

    [Fact]
    public void TunDiag_PreStartCleanup_MixedAdapters_OnlyOursDetected()
    {
        var output = """
            Admin State    State          Type             Interface Name
            -------------------------------------------------------------------------
            Enabled        Connected      Dedicated        Ethernet
            Enabled        Disconnected   Dedicated        VPNRouter-TUN
            Enabled        Connected      Dedicated        Wintun Userspace Tunnel
            Enabled        Connected      Dedicated        wg0
            """;
        var result = TunAdapterDiagnostics.ExtractStaleAdapterNames(output);
        Assert.Single(result);
        Assert.Equal("VPNRouter-TUN", result[0], ignoreCase: true);
    }

    [Fact]
    public void TunDiag_PreStartCleanup_DuplicateRows_DedupedByName()
    {
        var output = """
            Admin State    State          Type             Interface Name
            -------------------------------------------------------------------------
            Enabled        Connected      Dedicated        VPNRouter-TUN
            Disabled       Disconnected   Dedicated        VPNRouter-TUN
            """;
        var result = TunAdapterDiagnostics.ExtractStaleAdapterNames(output);
        Assert.Single(result);
    }

    [Fact]
    public void SingBoxManager_DefaultTunInterfaceName_MatchesVpnRouterTun()
    {
        var field = typeof(SingBoxManager).GetField(
            "DefaultTunInterfaceName",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var value = (string?)field!.GetValue(null);
        Assert.Equal("VPNRouter-TUN", value);
    }

    [Fact]
    public void PreStartCleanup_AdapterMissing_NoOp_ParserBranch()
    {
        Assert.Empty(TunAdapterDiagnostics.ExtractStaleAdapterNames(string.Empty));

        var noTun = """
            Admin State    State          Type             Interface Name
            -------------------------------------------------------------------------
            Enabled        Connected      Dedicated        Ethernet
            Enabled        Connected      Dedicated        Wi-Fi
            Enabled        Disconnected   Loopback         Loopback Pseudo-Interface 1
            """;
        Assert.Empty(TunAdapterDiagnostics.ExtractStaleAdapterNames(noTun));
    }

    [Fact]
    public void PreStartCleanup_VPNRouterTunPresent_DetectedForRemoval()
    {
        var output = """
            Admin State    State          Type             Interface Name
            -------------------------------------------------------------------------
            Enabled        Connected      Dedicated        Ethernet
            Enabled        Disconnected   Dedicated        VPNRouter-TUN
            """;
        var result = TunAdapterDiagnostics.ExtractStaleAdapterNames(output);
        Assert.Single(result);
        Assert.Equal("VPNRouter-TUN", result[0], ignoreCase: true);
    }

    [Fact]
    public void PreStartCleanup_DisabledAdapter_StillDetectedForRemoval()
    {
        var output = """
            Admin State    State          Type             Interface Name
            -------------------------------------------------------------------------
            Disabled       Disconnected   Dedicated        VPNRouter-TUN
            """;
        var result = TunAdapterDiagnostics.ExtractStaleAdapterNames(output);
        Assert.Single(result);
        Assert.Equal("VPNRouter-TUN", result[0], ignoreCase: true);
    }

    [Fact]
    public void PreStartCleanup_MultipleAdapters_AllDetectedForRemoval()
    {
        var output = """
            Admin State    State          Type             Interface Name
            -------------------------------------------------------------------------
            Enabled        Connected      Dedicated        Ethernet
            Disabled       Disconnected   Dedicated        VPNRouter-TUN
            Disabled       Disconnected   Dedicated        sing-box-tun-1234
            """;
        var result = TunAdapterDiagnostics.ExtractStaleAdapterNames(output);
        Assert.Equal(2, result.Count);
        Assert.Contains(result, n => n.Equals("VPNRouter-TUN", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result, n => n.Equals("sing-box-tun-1234", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PreStartCleanup_WireGuardAdapterPresent_NotInRemovalSet()
    {
        var output = """
            Admin State    State          Type             Interface Name
            -------------------------------------------------------------------------
            Enabled        Connected      Dedicated        Ethernet
            Enabled        Connected      Dedicated        WireGuardTUN
            Enabled        Connected      Dedicated        AmneziaWG_NetTun_0
            Enabled        Connected      Dedicated        Wintun Userspace Tunnel
            Enabled        Connected      Dedicated        OpenVPN Wintun
            Enabled        Connected      Dedicated        TAP-Windows Adapter V9
            """;
        var result = TunAdapterDiagnostics.ExtractStaleAdapterNames(output);
        Assert.Empty(result);
    }

    [Fact]
    public void PreStartCleanup_LocalizedNetshOutput_RussianAndGerman_StillExtracts()
    {
        var ruOutput = """
            Состояние    Состояние      Тип              Имя интерфейса
            -------------------------------------------------------------------------
            Подключен    Подключен      Выделенный       Ethernet
            Отключен     Отключен       Выделенный       VPNRouter-TUN
            """;
        var ruResult = TunAdapterDiagnostics.ExtractStaleAdapterNames(ruOutput);
        Assert.Single(ruResult);
        Assert.Equal("VPNRouter-TUN", ruResult[0], ignoreCase: true);

        var deOutput = """
            Verwaltungsstatus  Status        Typ             Schnittstellenname
            -------------------------------------------------------------------------
            Aktiviert          Verbunden     Dediziert       Ethernet
            Deaktiviert        Getrennt      Dediziert       VPNRouter-TUN
            """;
        var deResult = TunAdapterDiagnostics.ExtractStaleAdapterNames(deOutput);
        Assert.Single(deResult);
        Assert.Equal("VPNRouter-TUN", deResult[0], ignoreCase: true);

        var esOutput = """
            Estado de admin.  Estado        Tipo            Nombre de la interfaz
            -------------------------------------------------------------------------
            Habilitado        Conectado     Dedicado        Ethernet
            Deshabilitado     Desconectado  Dedicado        VPNRouter-TUN
            """;
        var esResult = TunAdapterDiagnostics.ExtractStaleAdapterNames(esOutput);
        Assert.Single(esResult);
        Assert.Equal("VPNRouter-TUN", esResult[0], ignoreCase: true);
    }

    [Fact]
    public void ExtractStaleAdapterNames_VPNRouterTunExactFinalField()
    {
        Assert.Single(TunAdapterDiagnostics.ExtractStaleAdapterNames(
            "Admin State    State          Type             Interface Name\n" +
            "Disabled       Disconnected   Dedicated        VPNRouter-TUN"));
    }

    [Fact]
    public void ExtractStaleAdapterNames_SingBoxTunBareSuffix_BothSurfaced()
    {
        var bare = TunAdapterDiagnostics.ExtractStaleAdapterNames(
            "Disabled       Disconnected   Dedicated        sing-box-tun");
        Assert.Single(bare);
        Assert.Equal("sing-box-tun", bare[0], ignoreCase: true);

        var suffixed = TunAdapterDiagnostics.ExtractStaleAdapterNames(
            "Disabled       Disconnected   Dedicated        sing-box-tun-AB12");
        Assert.Single(suffixed);
        Assert.Equal("sing-box-tun-AB12", suffixed[0], ignoreCase: true);
    }

    [Fact]
    public void ExtractStaleAdapterNames_EmbeddedInLongerWordChar_NegativeTest()
    {
        var embeddedInWordChars = """
            Admin State    State          Type             Interface Name
            Enabled        Connected      Dedicated        MyVPNRouter-TUNExtra
            Enabled        Connected      Dedicated        XVPNRouter-TUNX
            """;
        var result = TunAdapterDiagnostics.ExtractStaleAdapterNames(embeddedInWordChars);
        Assert.Empty(result);
    }

    [Fact]
    public void ExtractStaleAdapterNames_EmbeddedOrNumberedNames_AreIgnored()
    {
        var otherNames = """
            Admin State    State          Type             Interface Name
            Enabled        Connected      Dedicated        Pre-VPNRouter-TUN-Suffix
            Enabled        Connected      Dedicated        My VPNRouter-TUN
            Enabled        Connected      Dedicated        My sing-box-tun-AB12
            Enabled        Connected      Dedicated        My  VPNRouter-TUN
            Disabled       Disconnected   Dedicated        VPNRouter-TUN 46
            Disabled       Disconnected   Dedicated        sing-box-tun-AB12 old
            """;
        var result = TunAdapterDiagnostics.ExtractStaleAdapterNames(otherNames);
        Assert.Empty(result);
    }

    [Fact]
    public void ExtractStaleAdapterNames_LowerCaseMatches_PinCurrentBehavior()
    {
        var lower = TunAdapterDiagnostics.ExtractStaleAdapterNames(
            "Enabled        Connected      Dedicated        vpnrouter-tun");
        Assert.Single(lower);
    }

    [Fact]
    public void LaunchProcess_UsesPreStartCleanupAsync_NotEnsureAdapterEnabledOrAbsent()
    {
        var src = LoadSingBoxManagerSource();
        if (src == null) return;

        var stripped = StripLineComments(src);

        var launchProcessRegion = ExtractRegion(stripped, "void LaunchProcess(", 200, 1200);

        Assert.Contains("PreStartCleanup", launchProcessRegion);

        Assert.DoesNotContain("EnsureAdapterEnabledOrAbsent", launchProcessRegion);
    }

    [Fact]
    public void OnProcessExited_SchedulesAdapterRemoval_NotOnlyDisable()
    {
        var src = LoadSingBoxManagerSource();
        if (src == null) return;

        var stripped = StripLineComments(src);
        var onExitedRegion = ExtractRegion(stripped, "void OnProcessExited(", 100, 5000);

        var hasRemoval =
            onExitedRegion.Contains("QueueTunAdapterRemoval") ||
            onExitedRegion.Contains("TryRemoveAdapterAsync") ||
            onExitedRegion.Contains("PreStartCleanupAsync") ||
            onExitedRegion.Contains("RemoveAdapterAsync") ||
            onExitedRegion.Contains("Remove-NetAdapter");

        Assert.True(hasRemoval,
            "OnProcessExited region must schedule adapter removal " +
            "(TryRemoveAdapterAsync / PreStartCleanupAsync / Remove-NetAdapter). " +
            "Pre-Wave-38 only called DisableOrphanedAdapter — the orphan record " +
            "survives and HealthMonitor's restart hits 'Cannot create a file'.");
    }

    [Fact]
    public void StopInternal_EarlyExitPath_SchedulesAdapterRemoval()
    {
        var src = LoadSingBoxManagerSource();
        if (src == null) return;

        var stripped = StripLineComments(src);

        var stopEarlyRegion = ExtractRegion(stripped,
            "StopInternal.early", 200, 1200);

        var hasRemoval =
            stopEarlyRegion.Contains("QueueTunAdapterRemoval") ||
            stopEarlyRegion.Contains("TryRemoveAdapterAsync") ||
            stopEarlyRegion.Contains("PreStartCleanupAsync") ||
            stopEarlyRegion.Contains("RemoveAdapterAsync") ||
            stopEarlyRegion.Contains("Remove-NetAdapter");

        Assert.True(hasRemoval,
            "StopInternal.early region must schedule adapter removal too. " +
            "Pre-Wave-38 only called DisableOrphanedAdapter — see Agent 1 brief §3.");
    }

    [Fact]
    public void AutoRestartLoop_FiveCrashes_ParserConsistentBetweenIterations()
    {
        var output = """
            Admin State    State          Type             Interface Name
            -------------------------------------------------------------------------
            Enabled        Connected      Dedicated        Ethernet
            Disabled       Disconnected   Dedicated        VPNRouter-TUN
            """;

        var firstRun = TunAdapterDiagnostics.ExtractStaleAdapterNames(output);
        Assert.Single(firstRun);

        for (var i = 0; i < 5; i++)
        {
            var nthRun = TunAdapterDiagnostics.ExtractStaleAdapterNames(output);
            Assert.Single(nthRun);
            Assert.Equal("VPNRouter-TUN", nthRun[0], ignoreCase: true);
        }
    }

    private static string? LoadSingBoxManagerSource()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(
                dir.FullName,
                "VPNRouter.Core", "Services", "SingBoxManager.cs");
            if (File.Exists(candidate)) return SingBoxSourceText.ReadAll(candidate);
        }
        return null;
    }

    private static string StripLineComments(string src)
    {
        return string.Join('\n',
            src.Split('\n').Select(l =>
                l.Contains("//") ? l[..l.IndexOf("//")] : l));
    }

    private static string ExtractRegion(string src, string marker,
        int beforeBytes, int afterBytes)
    {
        var idx = src.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return src;
        var start = Math.Max(0, idx - beforeBytes);
        var end = Math.Min(src.Length, idx + marker.Length + afterBytes);
        return src.Substring(start, end - start);
    }
}
