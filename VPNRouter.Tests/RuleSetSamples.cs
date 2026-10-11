using System.IO.Compression;

namespace VPNRouter.Tests;

internal static class RuleSetSamples
{
    // geosite-tld-ru.srs as published by SagerNet/sing-geosite (branch rule-set) on 2026-10-11: 155 bytes, version 1.
    private const string UpstreamGeositeTldRuBase64 =
        "U1JTAXjaHMpBjoJAEIXhgiGZVSVzjJnFq9CZDRdho6uiUUFjQ2iUWnqe9jyeRwNf8vI2f0Y55UTZgzZfvw39FEVa1X+03jOl9FpX79LWfO/rvjme+inelv/W3xfVyxSHc6ca3dyJeH9wTkc15qhx9CXmdnAKVBC9tiWAwKWgCgFWMWAWBAEixrDALEGMjUWY3/QJAAD//92rMS8=";

    public static byte[] UpstreamGeositeTldRu() => Convert.FromBase64String(UpstreamGeositeTldRuBase64);

    // Passes the app's check (signature, version, intact zlib stream) but is not a rule-set the core could load:
    // a stand-in for the 50 KB geoip file where a test only needs "present and not damaged".
    public static byte[] IntactEnvelope(int minLength = 0)
    {
        var payload = new byte[Math.Max(64, minLength + 1024)];
        new Random(20261011).NextBytes(payload);

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
