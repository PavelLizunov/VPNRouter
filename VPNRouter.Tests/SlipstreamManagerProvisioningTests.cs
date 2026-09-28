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
    public void Provision_BundledPresent_RuntimeAbsent_Copies()
    {
        var bundled = WriteBundled("BUNDLED-BINARY");
        Assert.False(File.Exists(_target));

        var ok = SlipstreamManager.EnsureBinaryProvisioned(_target, bundled, _binDir, null);

        Assert.True(ok);
        Assert.True(File.Exists(_target));
        Assert.Equal("BUNDLED-BINARY", File.ReadAllText(_target));
    }

    [Fact]
    public void Provision_RuntimeSameSize_NotReCopied()
    {
        var bundled = WriteBundled("AAAA-BUNDLE!!");
        Directory.CreateDirectory(_binDir);
        File.WriteAllText(_target, "BBBB-RUNTIME!");

        var ok = SlipstreamManager.EnsureBinaryProvisioned(_target, bundled, _binDir, null);

        Assert.True(ok);
        Assert.Equal("BBBB-RUNTIME!", File.ReadAllText(_target));
    }

    [Fact]
    public void Provision_RuntimeStaleDifferentSize_ReCopied()
    {
        var bundled = WriteBundled("NEW-BUNDLED-BINARY-WITH-PATH-STATS");
        Directory.CreateDirectory(_binDir);
        File.WriteAllText(_target, "OLD-RUNTIME");

        var ok = SlipstreamManager.EnsureBinaryProvisioned(_target, bundled, _binDir, null);

        Assert.True(ok);
        Assert.Equal("NEW-BUNDLED-BINARY-WITH-PATH-STATS", File.ReadAllText(_target));
    }

    [Fact]
    public void Provision_NeitherPresent_ReturnsFalse()
    {
        var missingBundled = Path.Combine(_bundleDir, "slipstream-client.exe");
        var ok = SlipstreamManager.EnsureBinaryProvisioned(_target, missingBundled, _binDir, null);

        Assert.False(ok);
        Assert.False(File.Exists(_target));
    }

    [Fact]
    public void Provision_BundledNull_ReturnsFalse()
    {
        var ok = SlipstreamManager.EnsureBinaryProvisioned(_target, null, _binDir, null);

        Assert.False(ok);
        Assert.False(File.Exists(_target));
    }

    [Fact]
    public void Provision_CreatesBinDir_WhenMissing()
    {
        var bundled = WriteBundled("X");
        Assert.False(Directory.Exists(_binDir));

        SlipstreamManager.EnsureBinaryProvisioned(_target, bundled, _binDir, null);

        Assert.True(Directory.Exists(_binDir));
        Assert.True(File.Exists(_target));
    }
}
