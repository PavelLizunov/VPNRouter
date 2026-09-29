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

public class SlipstreamManagerTests
{
    private static readonly byte[] FakeDer =
        Encoding.ASCII.GetBytes("fake-der-bytes-for-slipstream-unit-test-0123456789");
    private static readonly string SamplePem =
        "-----BEGIN CERTIFICATE-----\n" + Convert.ToBase64String(FakeDer) + "\n-----END CERTIFICATE-----";
    private static readonly string SampleFingerprint =
        Convert.ToHexStringLower(SHA256.HashData(FakeDer));

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
    public void SelectResolvers_NoSystemFlag_ReturnsLinkLiterals()
    {
        var e = MakeEntry();
        var r = SlipstreamManager.SelectResolvers(e, new[] { "10.0.0.1:53" });
        Assert.Equal(new[] { "195.208.4.1:53", "195.208.5.1:53" }, r);
    }

    [Fact]
    public void SelectResolvers_SystemFlag_OsAvailable_PrefersOs()
    {
        var e = MakeEntry();
        e.DnsUseSystemResolver = true;
        var r = SlipstreamManager.SelectResolvers(e, new[] { "10.152.222.133:53", "10.152.222.140:53" });
        Assert.Equal(new[] { "10.152.222.133:53", "10.152.222.140:53" }, r);
    }

    [Fact]
    public void SelectResolvers_SystemFlag_OsEmpty_FallsBackToLiterals()
    {
        var e = MakeEntry();
        e.DnsUseSystemResolver = true;
        var r = SlipstreamManager.SelectResolvers(e, Array.Empty<string>());
        Assert.Equal(new[] { "195.208.4.1:53", "195.208.5.1:53" }, r);
    }

    [Fact]
    public void SelectResolvers_SystemFlag_OsEmpty_NoLiterals_ReturnsEmpty()
    {
        var e = MakeEntry();
        e.DnsUseSystemResolver = true;
        e.DnsResolvers = new List<string>();
        var r = SlipstreamManager.SelectResolvers(e, Array.Empty<string>());
        Assert.Empty(r);
    }

    [Fact]
    public void SelectResolvers_SystemFlag_DedupesOsList()
    {
        var e = MakeEntry();
        e.DnsUseSystemResolver = true;
        var r = SlipstreamManager.SelectResolvers(e, new[] { "10.0.0.1:53", "10.0.0.1:53", "10.0.0.2:53" });
        Assert.Equal(new[] { "10.0.0.1:53", "10.0.0.2:53" }, r);
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
        CleanupFiles();
        var (fake, _) = AliveRunner();
        var mgr = new SlipstreamManager(runner: fake) { StartupProbeMs = 50 };
        var ex = Assert.Throws<SlipstreamException>(() => mgr.Start(MakeEntry()));
        Assert.Contains("slipstream-client not found", ex.Message);
        Assert.Empty(fake.StartCalls);
    }

    [Fact]
    public void Start_SetsRustLogEnv_SoTransportDeathIsDiagnosable()
    {
        EnsureDummyBinary();
        try
        {
            var (fake, _) = AliveRunner();
            var mgr = new SlipstreamManager(runner: fake) { StartupProbeMs = 50 };

            mgr.Start(MakeEntry(), localPort: 7001);

            Assert.Single(fake.StartCalls);
            var env = fake.StartCalls[0].EnvironmentOverrides;
            Assert.NotNull(env);
            Assert.True(env!.TryGetValue("RUST_LOG", out var lvl));
            Assert.Equal("info", lvl);
        }
        finally { CleanupFiles(); }
    }

    [Fact]
    public void Start_WithAuthoritative_PassesAuthoritativeAlongsideResolvers()
    {
        EnsureDummyBinary();
        try
        {
            var (fake, _) = AliveRunner();
            var mgr = new SlipstreamManager(runner: fake) { StartupProbeMs = 50 };
            var entry = MakeEntry();
            entry.DnsAuthoritative = new System.Collections.Generic.List<string> { "213.155.15.93:53" };

            mgr.Start(entry, localPort: 7001);

            var argv = fake.StartCalls[0].Arguments.ToList();
            var ai = argv.IndexOf("--authoritative");
            Assert.True(ai >= 0 && ai + 1 < argv.Count, "expected --authoritative <ep> in argv");
            Assert.Equal("213.155.15.93:53", argv[ai + 1]);
            Assert.Contains("-r", argv);
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
            Assert.Empty(fake.StartCalls);
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
            Assert.False(File.Exists(AppPaths.SlipstreamActiveCertPath));
        }
        finally { CleanupFiles(); }
    }

    [Fact]
    public void IsPortListening_TrueWhenBound_FalseWhenClosed()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            Assert.True(SlipstreamManager.IsPortListening(port));
        }
        finally
        {
            listener.Stop();
        }
        Assert.False(SlipstreamManager.IsPortListening(port));
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
