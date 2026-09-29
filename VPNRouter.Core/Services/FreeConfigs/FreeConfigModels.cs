using System.Text.Json.Serialization;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services.FreeConfigs;

public enum FreeConfigStatus
{
    Unknown = 0,
    Ok = 1,
    Timeout = 2,
    Unreachable = 3,
    ParseError = 4,
    Slow = 5,
    TlsFailed = 6,
    Implausible = 7,
    Verified = 8,
}

public sealed class FreeConfigEntry
{
    public string Id { get; set; } = string.Empty;

    public string SourceUrl { get; set; } = string.Empty;

    public string RawUri { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Uuid { get; set; } = string.Empty;

    public string Protocol { get; set; } = "vless";

    public string? Path { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Sni { get; set; } = string.Empty;

    public string Transport { get; set; } = "tcp";

    public string Security { get; set; } = "reality";

    public string? ResolvedIp { get; set; }

    public string? CountryCode { get; set; }

    public FreeConfigStatus Status { get; set; } = FreeConfigStatus.Unknown;

    public int LatencyMs { get; set; }

    public DateTime? LastTestedAt { get; set; }

    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;

    public string? LastError { get; set; }

    public int? MeasuredBandwidthMbps { get; set; }

    public DateTime? BandwidthTestedAt { get; set; }

    public DateTime? LastVerifyFailedAt { get; set; }

    public DateTime? LastDeepVerifyAt { get; set; }

    public VlessServerEntry ToVlessServerEntry()
    {
        var entry = ServerUriParser.Parse(RawUri);
        entry.Name = $"⚡ {BuildShortName()}";
        return entry;
    }

    public string BuildShortName()
    {
        var cc = string.IsNullOrEmpty(CountryCode) ? "" : $"[{CountryCode}] ";
        return $"{cc}{Host}:{Port}";
    }
}

public sealed class FreeConfigSource
{
    public string Name { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;

    public int ExpectedCount { get; init; }
}
