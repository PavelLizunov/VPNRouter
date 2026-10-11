using System.IO.Compression;

namespace VPNRouter.Core.Services;

public static class RuleSetFile
{
    // sing-box exits at startup on a local rule-set it cannot parse, so a damaged file must never reach the config.
    public static bool IsValidBinary(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return IsValidBinary(stream);
    }

    public static bool IsValidBinary(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = new byte[4];
        if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
            return false;
        if (header[0] != (byte)'S' || header[1] != (byte)'R' || header[2] != (byte)'S' || header[3] == 0)
            return false;

        try
        {
            using var zlib = new ZLibStream(stream, CompressionMode.Decompress, leaveOpen: true);
            var buffer = new byte[16 * 1024];
            long inflated = 0;
            int read;
            while ((read = zlib.Read(buffer, 0, buffer.Length)) > 0)
                inflated += read;
            return inflated > 0;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
