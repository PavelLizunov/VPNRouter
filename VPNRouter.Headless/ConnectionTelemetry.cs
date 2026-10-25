using System.Globalization;
using System.Text.Json.Serialization;

namespace VPNRouter.Headless;

public sealed record ConnectionTelemetry(
    [property: JsonPropertyName("connectedSinceUnixMs")] long ConnectedSinceUnixMs,
    [property: JsonPropertyName("downloadBytes")] long? DownloadBytes,
    [property: JsonPropertyName("uploadBytes")] long? UploadBytes)
{
    internal static (long? Download, long? Upload) ReadTunCounters(string? statisticsDirectory = null)
    {
        if (!OperatingSystem.IsLinux()) return (null, null);
        var directory = statisticsDirectory ?? "/sys/class/net/VPNRouter-TUN/statistics";
        return (ReadCounter(Path.Combine(directory, "rx_bytes")), ReadCounter(Path.Combine(directory, "tx_bytes")));
    }

    private static long? ReadCounter(string path)
    {
        try
        {
            using var reader = File.OpenText(path);
            Span<char> buffer = stackalloc char[32];
            int count = reader.ReadBlock(buffer);
            return count < buffer.Length && long.TryParse(buffer[..count].Trim(), NumberStyles.None,
                CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
