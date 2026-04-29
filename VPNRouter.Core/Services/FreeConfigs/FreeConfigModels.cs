using System.Text.Json.Serialization;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services.FreeConfigs;

/// <summary>
/// Status of a free config after testing.
/// </summary>
public enum FreeConfigStatus
{
    /// <summary>Never tested yet.</summary>
    Unknown = 0,
    /// <summary>TCP connect succeeded within reasonable latency.</summary>
    Ok = 1,
    /// <summary>Connection timed out (likely offline or ISP filtered).</summary>
    Timeout = 2,
    /// <summary>Connection reset / refused (endpoint unreachable).</summary>
    Unreachable = 3,
    /// <summary>Parsing failed.</summary>
    ParseError = 4,
    /// <summary>TCP established but very slow (probable throttling / DPI interference).</summary>
    Slow = 5,
    /// <summary>TCP connects but TLS handshake fails (dead endpoint, wrong SNI, expired cert).</summary>
    TlsFailed = 6,
    /// <summary>Latency under plausible threshold (&lt; 5 ms = likely local intercept / active VPN).</summary>
    Implausible = 7,
    /// <summary>Deep-verified: real HTTP request through sing-box succeeded. Gold standard.</summary>
    Verified = 8,
}

/// <summary>
/// A single VLESS config aggregated from a public source.
/// </summary>
public sealed class FreeConfigEntry
{
    /// <summary>Stable id: hash of (host:port:uuid).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Where this config came from (URL).</summary>
    public string SourceUrl { get; set; } = string.Empty;

    /// <summary>Original vless:// URI.</summary>
    public string RawUri { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Uuid { get; set; } = string.Empty;

    /// <summary>Display name from fragment (#...).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>SNI from query (&sni=...).</summary>
    public string Sni { get; set; } = string.Empty;

    /// <summary>Transport type (tcp / grpc / ws).</summary>
    public string Transport { get; set; } = "tcp";

    /// <summary>Security mode (reality / tls / none).</summary>
    public string Security { get; set; } = "reality";

    /// <summary>Resolved IPv4 (cached).</summary>
    public string? ResolvedIp { get; set; }

    /// <summary>ISO-2 country code (resolved via GeoIP).</summary>
    public string? CountryCode { get; set; }

    /// <summary>Status of last test.</summary>
    public FreeConfigStatus Status { get; set; } = FreeConfigStatus.Unknown;

    /// <summary>Median RTT in milliseconds (0 if never tested).</summary>
    public int LatencyMs { get; set; }

    /// <summary>When this config was last tested (UTC).</summary>
    public DateTime? LastTestedAt { get; set; }

    /// <summary>When this config was first observed (UTC).</summary>
    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;

    /// <summary>Optional short reason for last failure (e.g. "cert mismatch", "tls timeout"). For UI diagnostics.</summary>
    public string? LastError { get; set; }

    /// <summary>v2.14.3: measured download throughput in Mbps (during Deep Verify, optional).</summary>
    public int? MeasuredBandwidthMbps { get; set; }

    /// <summary>v2.14.3: when bandwidth was last measured (UTC).</summary>
    public DateTime? BandwidthTestedAt { get; set; }

    /// <summary>v2.28.6: timestamp of the last re-verify that failed on a
    /// previously-Verified entry. The recheck flow keeps the entry in the
    /// saved list (with <see cref="Status"/> still set to
    /// <see cref="FreeConfigStatus.Verified"/>) so historical numbers
    /// (LatencyMs, MeasuredBandwidthMbps) survive, while
    /// <see cref="LastVerifyFailedAt"/> &gt; <see cref="LastTestedAt"/> tells
    /// the UI to render the entry with a "failed last check" badge. Null on
    /// entries that have never failed a re-verify.</summary>
    public DateTime? LastVerifyFailedAt { get; set; }

    /// <summary>
    /// v2.29.0 Phase 3C: timestamp of the last successful Deep Verify pass
    /// (real HTTP round-trip through sing-box, not just TCP+TLS). Set by
    /// FreeConfigDeepVerifier on Verified result, cleared on subsequent
    /// non-Verified re-test.
    ///
    /// <para>Used by the batched search loop to skip re-verifying entries
    /// that were Deep-Verified within the last 6 hours — saves 5-15 s
    /// per entry that's already known-working. Especially valuable on the
    /// "first cached pass" of a search where many entries were Verified
    /// in the prior session and just need a TCP ping refresh.</para>
    ///
    /// <para>Distinct from <see cref="LastTestedAt"/> which covers ANY
    /// test type (TCP+TLS or Deep). Distinct from
    /// <see cref="BandwidthTestedAt"/> which only fires when bandwidth
    /// measurement was opt-in. Null on entries that have never been
    /// Deep-Verified successfully.</para>
    /// </summary>
    public DateTime? LastDeepVerifyAt { get; set; }

    /// <summary>
    /// Builds a VlessServerEntry from this free config, suitable for insertion into AppSettings.Vless.Servers.
    /// </summary>
    public VlessServerEntry ToVlessServerEntry()
    {
        // Re-parse the raw URI to get a fully populated entry with all fields (Reality keys, TLS, transport details).
        var entry = VlessUriParser.Parse(RawUri);
        // Override name so user can identify it as a free config.
        entry.Name = $"⚡ {BuildShortName()}";
        return entry;
    }

    /// <summary>
    /// Short display label: "[CC] host:port" or "host:port".
    /// </summary>
    public string BuildShortName()
    {
        var cc = string.IsNullOrEmpty(CountryCode) ? "" : $"[{CountryCode}] ";
        return $"{cc}{Host}:{Port}";
    }
}

/// <summary>
/// Definition of a public source of VLESS configs.
/// </summary>
public sealed class FreeConfigSource
{
    public string Name { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;

    /// <summary>Rough expected number of entries for logging.</summary>
    public int ExpectedCount { get; init; }
}
