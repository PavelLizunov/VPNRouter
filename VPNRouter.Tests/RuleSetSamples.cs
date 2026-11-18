using System.IO.Compression;
using VPNRouter.Core;

namespace VPNRouter.Tests;

internal static class RuleSetSamples
{
    // geosite-tld-ru.srs as published by SagerNet/sing-geosite (branch rule-set) on 2026-10-11: 155 bytes, version 1.
    private const string UpstreamGeositeTldRuBase64 =
        "U1JTAXjaHMpBjoJAEIXhgiGZVSVzjJnFq9CZDRdho6uiUUFjQ2iUWnqe9jyeRwNf8vI2f0Y55UTZgzZfvw39FEVa1X+03jOl9FpX79LWfO/rvjme+inelv/W3xfVyxSHc6ca3dyJeH9wTkc15qhx9CXmdnAKVBC9tiWAwKWgCgFWMWAWBAEixrDALEGMjUWY3/QJAAD//92rMS8=";

    public static byte[] UpstreamGeositeTldRu() => Convert.FromBase64String(UpstreamGeositeTldRuBase64);

    // v1: one default rule, one domain_keyword string "example", final item, invert=false.
    public static byte[] KeywordRule()
    {
        using var file = new MemoryStream();
        file.Write("SRS"u8);
        file.WriteByte(1);
        using (var zlib = new ZLibStream(file, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(new byte[] { 1, 0, 3, 1, 7 });
            zlib.Write("example"u8);
            zlib.Write(new byte[] { 255, 0 });
        }
        return file.ToArray();
    }

    // One v1 ip_cidr rule with 1280 distinct singleton ranges in the benchmarking network.
    // Uncompressed zlib blocks keep this valid fixture above the built-in GeoIP size floor.
    public static byte[] GeoIpRule()
    {
        using var file = new MemoryStream();
        file.Write("SRS"u8);
        file.WriteByte(1);
        using (var zlib = new ZLibStream(file, CompressionLevel.NoCompression, leaveOpen: true))
        {
            zlib.Write(new byte[] { 1, 0, 6, 1, 0, 0, 0, 0, 0, 0, 5, 0 });
            byte[] address = [4, 198, 18, 0, 0];
            for (int i = 0; i < 1280; i++)
            {
                address[3] = (byte)(i / 128);
                address[4] = (byte)((i % 128) * 2);
                zlib.Write(address); // first address in range
                zlib.Write(address); // last address in range
            }
            zlib.Write(new byte[] { 255, 0 });
        }
        return file.ToArray();
    }

    public static void WithBuiltInGeoFiles(Action test)
    {
        var previous = AppPaths.DataDir;
        var temp = Directory.CreateTempSubdirectory("vpnrouter-builtin-geo-");
        try
        {
            AppPaths.OverrideDataDir(temp.FullName);
            Directory.CreateDirectory(AppPaths.GeoDir);
            File.WriteAllBytes(AppPaths.GeoIpRuPath, GeoIpRule());
            File.WriteAllBytes(AppPaths.GeoSiteRuPath, UpstreamGeositeTldRu());
            test();
        }
        finally
        {
            AppPaths.OverrideDataDir(previous);
            temp.Delete(recursive: true);
        }
    }

    public static Task<bool> CheckWithRuntimeAsync(string path, CancellationToken ct, string runtime)
    {
        var process = new VPNRouter.Core.Services.ProcessRunner();
        var runner = new Fakes.FakeProcessRunner().OnRun(_ => true, request =>
            process.RunAsync(request with { ExecutablePath = Path.GetFullPath(runtime) }, ct));
        return VPNRouter.Core.Services.RuleSetFile.CheckContentsAsync(path, ct, runner);
    }

    // Exercise the real check-config invocation with a deterministic parser response.
    public static Task<bool> CheckFixtureContentsAsync(string path, CancellationToken ct)
    {
        var runner = new Fakes.FakeProcessRunner().OnRun(
            req => req.Arguments.SequenceEqual(new[] { "check", "-c", "stdin" }), async req =>
            {
                using var doc = System.Text.Json.JsonDocument.Parse(req.StdinInput!);
                var set = doc.RootElement.GetProperty("route").GetProperty("rule_set")[0];
                Assert.Equal("local", set.GetProperty("type").GetString());
                Assert.Equal("binary", set.GetProperty("format").GetString());
                Assert.Equal(Path.GetFullPath(path), set.GetProperty("path").GetString());
                var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                bool valid = bytes.SequenceEqual(UpstreamGeositeTldRu()) || bytes.SequenceEqual(KeywordRule()) || bytes.SequenceEqual(GeoIpRule());
                return new VPNRouter.Core.Services.ProcessResult(valid ? 0 : 1, "", "", TimeSpan.Zero, false);
            });
        return VPNRouter.Core.Services.RuleSetFile.CheckContentsAsync(path, ct, runner);
    }

    // The envelope is intact but the runtime cannot load its rule body.
    public static byte[] IntactEnvelope(int minLength = 0)
    {
        var payload = new byte[Math.Max(64, minLength + 1024)];
        new Random(20261011).NextBytes(payload);
        // A single default rule with an unsupported item. Keep the fixture bounded for native checks.
        payload[0] = 1;
        payload[1] = 0;
        payload[2] = 0xFE;

        using var file = new MemoryStream();
        file.Write("SRS"u8);
        file.WriteByte(1);
        using (var zlib = new ZLibStream(file, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(payload);
        return file.ToArray();
    }

    public static byte[] ZeroFilled(int length) => new byte[length];

    public static byte[] ValidHeaderZeroedBody(int length)
    {
        var bytes = new byte[length];
        "SRS"u8.CopyTo(bytes);
        bytes[3] = 1;
        bytes[4] = 0x78;
        bytes[5] = 0xDA;
        return bytes;
    }
}
