using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Platform;

namespace VPNRouter.App.ViewModels.Internals;

internal static class BundledServerIpCountry
{
    private const int MaxTableBytes = 32 * 1024 * 1024;
    private static Lazy<byte[]> _ipv4 = new(() => Load("ipv4"));
    private static Lazy<byte[]> _ipv6 = new(() => Load("ipv6"));

    internal static void ReleaseCachedTables()
    {
        Interlocked.Exchange(ref _ipv4, new Lazy<byte[]>(() => Load("ipv4")));
        Interlocked.Exchange(ref _ipv6, new Lazy<byte[]>(() => Load("ipv6")));
    }

    // Each sorted record is an inclusive first/last address in network byte order,
    // followed by a two-byte ASCII country code.
    internal static string Code(string endpoint)
    {
        if (!IPAddress.TryParse(endpoint, out var ip)) return string.Empty;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        var ipv4 = ip.AddressFamily == AddressFamily.InterNetwork;
        var bytes = ipv4 ? 4 : 16;
        var table = (ipv4 ? Volatile.Read(ref _ipv4) : Volatile.Read(ref _ipv6)).Value;
        Span<byte> address = stackalloc byte[bytes];
        if (!ip.TryWriteBytes(address, out var size) || size != bytes) return string.Empty;

        var stride = bytes * 2 + 2;
        if (table.Length % stride != 0) return string.Empty;
        var lower = 0;
        var upper = table.Length / stride - 1;
        while (lower <= upper)
        {
            var middle = lower + (upper - lower) / 2;
            var record = table.AsSpan(middle * stride, stride);
            if (address.SequenceCompareTo(record[..bytes]) < 0)
                upper = middle - 1;
            else if (address.SequenceCompareTo(record.Slice(bytes, bytes)) > 0)
                lower = middle + 1;
            else
                return Encoding.ASCII.GetString(record[(2 * bytes)..]);
        }

        return string.Empty;
    }

    private static byte[] Load(string family)
    {
        var uri = new Uri($"avares://VPNRouter.App/Assets/CountryData/{family}.ranges.gz");
        if (!AssetLoader.Exists(uri)) return [];
        using var source = AssetLoader.Open(uri);

        if (source.CanSeek && source.Length >= 4)
        {
            // The embedded tables are single-member gzip files. ISIZE gives the exact array size.
            source.Seek(-4, SeekOrigin.End);
            Span<byte> sizeBytes = stackalloc byte[4];
            source.ReadExactly(sizeBytes);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(sizeBytes);
            if (size > MaxTableBytes) return [];

            source.Position = 0;
            using var tableStream = new GZipStream(source, CompressionMode.Decompress);
            var table = new byte[(int)size];
            tableStream.ReadExactly(table);
            return tableStream.ReadByte() == -1 ? table : [];
        }

        using var decompressed = new GZipStream(source, CompressionMode.Decompress);
        using var destination = new MemoryStream();
        decompressed.CopyTo(destination);
        return destination.ToArray();
    }
}
