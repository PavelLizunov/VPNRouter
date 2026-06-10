using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

/// <summary>
/// v2.41.x — DNS-tunnel (slipstream) sidecar manager. Spawns slipstream-client
/// with the profile leaf PEM written to disk + passed via --cert; fail-closed
/// (an immediate exit throws so VpnEngine won't start sing-box over a dead local
/// port); optional fingerprint hard-reject. See
/// plans/dns-tunnel-slipstream-integration-2026-06-10.md.
/// </summary>
public class SlipstreamManagerTests
{
    // A PEM whose base64 body is real, so ComputeLeafSha256Hex() can hash it.
    private static readonly byte[] FakeDer =
        Encoding.ASCII.GetBytes("fake-der-bytes-for-slipstream-unit-test-0123456789");
    private static readonly string SamplePem =
        "-----BEGIN CERTIFICATE-----\n" + Convert.ToBase64String(FakeDer) + "\n-----END CERTIFICATE-----";
    private static readonly string SampleFingerprint =
        Convert.ToHexString(SHA256.HashData(FakeDer)).ToLowerInvariant();

    private static VlessServerEntry MakeEntry(string fingerprint = "")
        => new()
        {
            Protocol = "dns-tunnel",
            Name = "Emergency DNS",
            Server = "tunnel.example.org",
            DnsDomain = "tunnel.example.org",
            DnsResolvers = new List<string> { "195.208.4.1:53", "195.208.5.1:53" },
            DnsLeafCertPem = SamplePem,
            DnsLeafFingerprint = fingerprint,
            Uuid = "11111111-1111-1111-1111-111111111111",
        };

    private static void EnsureDummyBinary()
    {
        Directory.CreateDirectory(AppPaths.SlipstreamBinDir);
        File.WriteAllText(AppPaths.SlipstreamExePath, "dummy");
    }

    private static void CleanupFiles()
    {
        try { if (File.Exists(AppPaths.SlipstreamExePath)) File.Delete(AppPaths.SlipstreamExePath); } catch { }
        try { if (File.Exists(AppPaths.SlipstreamActiveCertPath)) File.Delete(AppPaths.SlipstreamActiveCertPath); } catch { }
    }

    private static (FakeProcessRunner fake, FakeProcessHandle handle) AliveRunner(int pid = 4242)
    {
        var fake = new FakeProcessRunner();
        var handle = new FakeProcessHandle(pid);
        fake.OnStart(r => r.ExecutablePath == AppPaths.SlipstreamExePath, _ => handle);
        return (fake, handle);
    }

    [Fact]
    public void Start_NonDnsTunnelEntry_ThrowsArgument()
    {
        var (fake, _) = AliveRunner();
        var mgr = new SlipstreamManager(runner: fake) { StartupProbeMs = 50 };
        var entry = MakeEntry();
        entry.Protocol = "vless";
        Assert.Throws<ArgumentException>(() => mgr.Start(entry));
    }

    [Fact]
    public void Start_MissingCert_Throws()
    {
        var (fake, _) = AliveRunner();
        var mgr = new SlipstreamManager(runner: fake) { StartupProbeMs = 50 };
        var entry = MakeEntry();
        entry.DnsLeafCertPem = "";
        var ex = Assert.Throws<SlipstreamException>(() => mgr.Start(entry));
        Assert.Contains("certificate", ex.Message);
    }

    [Fact]
    public void Start_MissingBinary_ThrowsClearError()
    {
        CleanupFiles(); // ensure no binary present
        var (fake, _) = AliveRunner();
        var mgr = new SlipstreamManager(runner: fake) { StartupProbeMs = 50 };
        var ex = Assert.Throws<SlipstreamException>(() => mgr.Start(MakeEntry()));
        Assert.Contains("slipstream-client not found", ex.Message);
        Assert.Empty(fake.StartCalls); // never reached the spawn
    }

    [Fact]
    public void Start_HappyPath_SpawnsWithCorrectArgv_AndWritesCert()
    {
        EnsureDummyBinary();
        try
        {
            var (fake, _) = AliveRunner();
            var mgr = new SlipstreamManager(runner: fake) { StartupProbeMs = 50 };

            mgr.Start(MakeEntry(), localPort: 7001);

            Assert.Single(fake.StartCalls);
            var argv = fake.StartCalls[0].Arguments.ToList();
            var expected = new List<string>
            {
                "--cert", AppPaths.SlipstreamActiveCertPath,
                "-d", "tunnel.example.org",
                "-l", "7001",
                "--tcp-listen-host", "127.0.0.1",
                "-r", "195.208.4.1:53",
                "-r", "195.208.5.1:53",
            };
            Assert.Equal(expected, argv);

            // The profile PEM was written to the active-cert path verbatim.
            Assert.True(File.Exists(AppPaths.SlipstreamActiveCertPath));
            Assert.Equal(SamplePem, File.ReadAllText(AppPaths.SlipstreamActiveCertPath));

            Assert.True(mgr.IsRunning);
            Assert.Equal(7001, mgr.LocalPort);
        }
        finally { CleanupFiles(); }
    }

    [Fact]
    public void Start_FingerprintMatch_Spawns()
    {
        EnsureDummyBinary();
        try
        {
            var (fake, _) = AliveRunner();
            var mgr = new SlipstreamManager(runner: fake) { StartupProbeMs = 50 };
            mgr.Start(MakeEntry(fingerprint: SampleFingerprint));
            Assert.Single(fake.StartCalls);
        }
        finally { CleanupFiles(); }
    }

    [Fact]
    public void Start_FingerprintMatchWithColonsAndUpper_Spawns()
    {
        EnsureDummyBinary();
        try
        {
            // Server fingerprints often arrive as AA:BB:CC… uppercase — must normalise.
            var colonUpper = string.Join(":",
                Enumerable.Range(0, SampleFingerprint.Length / 2)
                          .Select(i => SampleFingerprint.Substring(i * 2, 2).ToUpperInvariant()));
            var (fake, _) = AliveRunner();
            var mgr = new SlipstreamManager(runner: fake) { StartupProbeMs = 50 };
            mgr.Start(MakeEntry(fingerprint: colonUpper));
            Assert.Single(fake.StartCalls);
        }
        finally { CleanupFiles(); }
    }

    [Fact]
    public void Start_FingerprintMismatch_ThrowsAndNeverSpawns()
    {
        EnsureDummyBinary();
        try
        {
            var (fake, _) = AliveRunner();
            var mgr = new SlipstreamManager(runner: fake) { StartupProbeMs = 50 };
            var ex = Assert.Throws<SlipstreamException>(
                () => mgr.Start(MakeEntry(fingerprint: "00ff00ff00ff")));
            Assert.Contains("fingerprint mismatch", ex.Message);
            Assert.Empty(fake.StartCalls); // hard-reject before spawn
        }
        finally { CleanupFiles(); }
    }

    [Fact]
    public void Start_EarlyExit_ThrowsAndCleansUp()
    {
        EnsureDummyBinary();
        try
        {
            var fake = new FakeProcessRunner();
            // Factory returns an already-exited handle → the watchdog sees the
            // immediate exit and the manager must fail closed.
            fake.OnStart(r => r.ExecutablePath == AppPaths.SlipstreamExePath, _ =>
            {
                var h = new FakeProcessHandle();
                h.SignalExit(1);
                return h;
            });
            var mgr = new SlipstreamManager(runner: fake) { StartupProbeMs = 50 };

            var ex = Assert.Throws<SlipstreamException>(() => mgr.Start(MakeEntry()));
            Assert.Contains("exited immediately", ex.Message);
            Assert.False(mgr.IsRunning);
            // Stop() in the fail-closed path removed the active cert.
            Assert.False(File.Exists(AppPaths.SlipstreamActiveCertPath));
        }
        finally { CleanupFiles(); }
    }

    [Fact]
    public void Stop_KillsProcess_SuppressesExited_AndRemovesActiveCert()
    {
        EnsureDummyBinary();
        try
        {
            var (fake, handle) = AliveRunner();
            var mgr = new SlipstreamManager(runner: fake) { StartupProbeMs = 50 };
            mgr.Start(MakeEntry());
            Assert.True(File.Exists(AppPaths.SlipstreamActiveCertPath));

            mgr.Stop();

            Assert.True(handle.SuppressExitedEventCallCount > 0, "Exited must be suppressed before Kill");
            Assert.True(handle.KillCallCount > 0, "process must be killed");
            Assert.False(mgr.IsRunning);
            Assert.False(File.Exists(AppPaths.SlipstreamActiveCertPath), "active cert removed on Stop");
        }
        finally { CleanupFiles(); }
    }
}
