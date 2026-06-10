using System;
using System.IO;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

/// <summary>
/// slice 5 — copy-on-first-use bootstrap. The slipstream-client binary ships
/// BUNDLED in the installer (app/, like sing-box); on first Start it is promoted
/// to the canonical runtime path (SlipstreamExePath under %ProgramData%). These
/// pin SlipstreamManager.EnsureBinaryProvisioned: bundled→runtime copy, no
/// clobber of an existing (e.g. updater-written) runtime binary, and fail-closed
/// when nothing is available. See plans/dns-tunnel-slipstream-integration-2026-06-10.md.
/// </summary>
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
    public void Provision_RuntimeAlreadyPresent_DoesNotOverwrite()
    {
        // A newer updater-written binary must survive — overwrite:false.
        var bundled = WriteBundled("STALE-BUNDLED");
        Directory.CreateDirectory(_binDir);
        File.WriteAllText(_target, "NEWER-RUNTIME");

        var ok = SlipstreamManager.EnsureBinaryProvisioned(_target, bundled, _binDir, null);

        Assert.True(ok);
        Assert.Equal("NEWER-RUNTIME", File.ReadAllText(_target)); // untouched
    }

    [Fact]
    public void Provision_NeitherPresent_ReturnsFalse()
    {
        var missingBundled = Path.Combine(_bundleDir, "slipstream-client.exe"); // never written
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
