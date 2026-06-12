using System;
using System.IO;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

/// <summary>
/// slice 5 — copy-on-first-use bootstrap. The slipstream-client binary ships
/// BUNDLED in the installer (app/, like sing-box); on first Start it is promoted
/// to the canonical runtime path (SlipstreamExePath under %ProgramData%). These
/// pin SlipstreamManager.EnsureBinaryProvisioned: bundled→runtime copy when
/// absent, RE-PROMOTION when the runtime copy is a stale size (v2.42.0-r13 fix —
/// the app/-only auto-update never refreshes the ProgramData copy, so a new CLI
/// flag the updated DLL passes makes an old binary exit(2) → dns-tunnel dead),
/// an efficiency skip when sizes match, and fail-closed when nothing is available.
/// See plans/dns-tunnel-slipstream-integration-2026-06-10.md.
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
    public void Provision_RuntimeSameSize_NotReCopied()
    {
        // Same size as the bundle → assume identical → skip the (7 MB) re-copy so we
        // don't rewrite the runtime binary on every Start. A same-length sentinel
        // survives untouched.
        var bundled = WriteBundled("AAAA-BUNDLE!!");   // 13 bytes
        Directory.CreateDirectory(_binDir);
        File.WriteAllText(_target, "BBBB-RUNTIME!");    // 13 bytes (same length)

        var ok = SlipstreamManager.EnsureBinaryProvisioned(_target, bundled, _binDir, null);

        Assert.True(ok);
        Assert.Equal("BBBB-RUNTIME!", File.ReadAllText(_target)); // untouched (sizes match)
    }

    [Fact]
    public void Provision_RuntimeStaleDifferentSize_ReCopied()
    {
        // v2.42.0-r13 regression: an existing user's ProgramData copy is the OLD
        // slipstream-client.exe (a different size than the freshly-bundled one). Before
        // the fix the early-return-if-exists left it stale forever, so the r12 DLL
        // passing --path-stats made the old binary exit(2) → dns-tunnel dead. It MUST
        // now re-promote (overwrite) when the sizes differ.
        var bundled = WriteBundled("NEW-BUNDLED-BINARY-WITH-PATH-STATS"); // longer
        Directory.CreateDirectory(_binDir);
        File.WriteAllText(_target, "OLD-RUNTIME"); // shorter → stale

        var ok = SlipstreamManager.EnsureBinaryProvisioned(_target, bundled, _binDir, null);

        Assert.True(ok);
        Assert.Equal("NEW-BUNDLED-BINARY-WITH-PATH-STATS", File.ReadAllText(_target)); // re-promoted
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
