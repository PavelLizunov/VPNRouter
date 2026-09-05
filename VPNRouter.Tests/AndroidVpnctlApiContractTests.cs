#nullable enable
using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Source regression contracts for Android vpnctl API bindings across VpnRouterService
/// and AndroidDeepVerifyBox without requiring Java runtime or external dependencies.
/// </summary>
public sealed class AndroidVpnctlApiContractTests
{
    private const string MainBridge = "VpnRouterService.java";
    private const string DeepVerifyBridge = "AndroidDeepVerifyBox.java";

    [Theory]
    [InlineData(MainBridge)]
    [InlineData(DeepVerifyBridge)]
    public void BothBridges_NoBoxServiceOrFixAndroidStack_CallCommandServer(string bridgeFile)
    {
        var source = ReadJava(bridgeFile);

        Assert.DoesNotContain("import io.nekohasekai.libbox.BoxService;", source);
        Assert.DoesNotContain("Libbox.newService", source);
        Assert.DoesNotContain("setFixAndroidStack", source);

        Assert.Contains("Libbox.newCommandServer", source);
        Assert.Contains("startOrReloadService", source);
        Assert.Contains("new OverrideOptions()", source);
    }

    [Theory]
    [InlineData(MainBridge)]
    [InlineData(DeepVerifyBridge)]
    public void BothBridges_ConstructorRegion_NoStartOnCommandServerListener(string bridgeFile)
    {
        var source = ReadJava(bridgeFile);
        var codeWithoutComments = source;

        Assert.False(
            Regex.IsMatch(codeWithoutComments, @"\b(boxService|commandServer)\.start\s*\("),
            $"{bridgeFile} must not invoke .start() on CommandServer listener.");
    }

    [Theory]
    [InlineData(MainBridge)]
    [InlineData(DeepVerifyBridge)]
    public void BothBridges_TeardownAndFinally_CloseServiceBeforeClose(string bridgeFile)
    {
        var source = ReadJava(bridgeFile);
        var idxCloseService = source.IndexOf("closeService()", StringComparison.Ordinal);
        var idxClose = source.IndexOf("close()", idxCloseService, StringComparison.Ordinal);

        Assert.True(idxCloseService >= 0, $"{bridgeFile} must call closeService().");
        Assert.True(idxClose > idxCloseService, $"{bridgeFile} must call closeService() before close().");
    }

    [Fact]
    public void PlatformConnectionOwner_FindMethod_ActualUid_PackageNames_NoFabricatedUnknownId()
    {
        var source = ReadJava(MainBridge);

        Assert.Contains("public ConnectionOwner findConnectionOwner(", source);
        Assert.Contains("cm.getConnectionOwnerUid(", source);
        Assert.Contains("owner.setUserId(uid);", source);
        Assert.Contains("owner.setAndroidPackageNames(", source);
        Assert.Contains("throw new Exception(\"unknown connection owner\");", source);
        Assert.DoesNotContain("owner.setUserId(-1)", source);
    }

    [Fact]
    public void PermissionsProtectAndOpenTun_BaselineUnchangedSourceGuard()
    {
        var source = ReadJava(MainBridge);

        Assert.Contains("public void autoDetectInterfaceControl(int fd)", source);
        Assert.Contains("service.protect(fd)", source);
        Assert.Contains("public int openTun(TunOptions options)", source);
        Assert.Contains("service.openTun(options)", source);
        Assert.Contains("int openTun(TunOptions options) throws Exception", source);
        Assert.Contains("builder.establish()", source);
        Assert.Contains("return pfd.getFd();", source);
    }

    [Theory]
    [InlineData(MainBridge)]
    [InlineData(DeepVerifyBridge)]
    public void DebugHandlers_WriteDebugMessage_ScopedBodyNoLogCallsWithoutPayload(string bridgeFile)
    {
        var source = ReadJava(bridgeFile);

        Assert.Contains("public void writeDebugMessage(String message)", source);
        Assert.Matches(@"writeDebugMessage\(String message\)\s*\{\s*\}", source);
    }

    [Fact]
    public void DebugHandlers_VpnRouterService_NoRawPayload_NoWriteLog_AndGetSystemProxyStatusBothFalse()
    {
        var source = ReadJava(MainBridge);

        Assert.Matches(@"writeDebugMessage\(String message\)\s*\{\s*\}", source);
        Assert.DoesNotContain("public void writeLog(String message)", source);

        Assert.Contains("public SystemProxyStatus getSystemProxyStatus()", source);
        Assert.Contains("status.setAvailable(false);", source);
        Assert.Contains("status.setEnabled(false);", source);
    }

    [Fact]
    public void DebugHandlers_DeepVerifyBridge_GetSystemProxyStatus_Unsupported()
    {
        var source = ReadJava(DeepVerifyBridge);

        Assert.Contains("public SystemProxyStatus getSystemProxyStatus()", source);
        Assert.Contains("status.setAvailable(false);", source);
        Assert.Contains("status.setEnabled(false);", source);
    }

    private static string ReadJava(string filename) =>
        StripComments(File.ReadAllText(Path.Combine(FindRoot(), "VPNRouter.Android", filename)));

    private static string StripComments(string source) =>
        Regex.Replace(
            source,
            @"(""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*')|//.*$|/\*[\s\S]*?\*/",
            m => m.Groups[1].Success ? m.Groups[1].Value : "",
            RegexOptions.Multiline);

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VPNRouter.sln")))
                return dir.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
