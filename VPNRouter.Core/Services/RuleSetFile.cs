using System.Buffers.Binary;
using System.IO.Compression;

namespace VPNRouter.Core.Services;

public static class RuleSetFile
{
    // RuleSetVersionCurrent of the pinned sing-box core; that core exits at startup on a newer file.
    private const byte MaxVersion = 5;
    private const int HeaderLength = 4;
    private const int ChecksumLength = 4;
    private const long MaxFileBytes = 64L * 1024 * 1024;
    private const long MaxInflatedBytes = 256L * 1024 * 1024;

    // sing-box exits at startup on a local rule-set it cannot parse, so a damaged file must never reach the config.
    public static bool IsValidBinary(string path)
    {
        if (new FileInfo(path).Length > MaxFileBytes)
            return false;
        return IsValidBinary(File.ReadAllBytes(path));
    }

    public static bool IsValidBinary(byte[] file)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (file.Length <= HeaderLength + ChecksumLength)
            return false;
        if (file[0] != (byte)'S' || file[1] != (byte)'R' || file[2] != (byte)'S')
            return false;
        if (file[3] is 0 or > MaxVersion)
            return false;

        uint checksum = 1;
        long inflated = 0;
        try
        {
            using var body = new MemoryStream(file, HeaderLength, file.Length - HeaderLength, writable: false);
            using var zlib = new ZLibStream(body, CompressionMode.Decompress);
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = zlib.Read(buffer, 0, buffer.Length)) > 0)
            {
                inflated += read;
                if (inflated > MaxInflatedBytes)
                    return false;
                checksum = Adler32(checksum, buffer.AsSpan(0, read));
            }
        }
        catch (InvalidDataException)
        {
            return false;
        }

        // .NET does not report a zlib stream that was cut short, so the trailing Adler-32 is compared here.
        var expected = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(file.Length - ChecksumLength));
        return inflated > 0 && checksum == expected;
    }

    private static uint Adler32(uint checksum, ReadOnlySpan<byte> data)
    {
        const uint Modulus = 65521;
        uint low = checksum & 0xFFFF;
        uint high = checksum >> 16;
        foreach (var value in data)
        {
            low = (low + value) % Modulus;
            high = (high + low) % Modulus;
        }
        return (high << 16) | low;
    }
}
