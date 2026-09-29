using System;
using System.IO;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public class SlipstreamManagerProvisioningTests : IDisposable
{
    private readonly string _root;
    private readonly string _bundleDir;
    private readonly string _binDir;
    private readonly string _target;

    public SlipstreamManagerProvisioningTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vpnr-slip-prov-" + Guid.NewGuid().ToString("N"));
        _bundleDir = Path.Combine(_root, "app");
        _binDir = Path.Combine(_root, "data", "slipstream", "bin");
        _target = Path.Combine(_binDir, "slipstream-client.exe");
        Directory.CreateDirectory(_bundleDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private string WriteBundled(string content)
    {
        var p = Path.Combine(_bundleDir, "slipstream-client.exe");
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void Provision_NeitherPresent_ReturnsFalse()
    {
        var missingBundled = Path.Combine(_bundleDir, "slipstream-client.exe");
        var ok = SlipstreamManager.EnsureBinaryProvisioned(_target, missingBundled, _binDir, null);

        Assert.False(ok);
        Assert.False(File.Exists(_target));
    }
}
