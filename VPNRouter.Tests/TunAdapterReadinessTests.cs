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
