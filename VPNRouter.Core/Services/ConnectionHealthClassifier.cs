#nullable enable
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace VPNRouter.Core.Services;

/// <summary>
/// Category a sing-box connection log line falls into for proxy-health scoring.
/// </summary>
public enum ConnHealthCategory
{
    /// <summary>Recognized connection line we don't score (e.g. a teardown of a
    /// non-proxy remote socket).</summary>
    Other = 0,

    /// <summary>"outbound/&lt;tag&gt;: outbound connection to &lt;dest&gt;" — a relay-open
    /// <em>attempt</em>. The denominator: every dial logs it, success or fail.</summary>
    RelayOpenAttempt,

    /// <summary>"connection: open connection to &lt;dest&gt; using outbound/&lt;tag&gt;: &lt;err&gt;"
    /// (or the UDP "listen packet connection ... using outbound/...") — the relay open
    /// FAILED. The error subtype is in <see cref="ConnLogEvent.FailKind"/>. The token
    /// "using outbound/" only ever appears on a failure line (a success logs
    /// "outbound/&lt;tag&gt;: outbound connection to"), so it is the reliable marker.</summary>
    RelayOpenFail,

    /// <summary>"connection upload/download closed: raw read | raw-read tcp4
    /// 172.19.0.x-&gt;172.19.0.x ..." — the local TUN/app read side closed. Benign: the
    /// application abandoned its own connection. The bulk of the "forcibly closed"
    /// messages a naive (EOF+RST)/min signal mis-counts as proxy failures.</summary>
    LocalClose,

    /// <summary>"connection upload/download closed: read tcp &lt;local&gt;-&gt;&lt;proxy&gt;: ..." —
    /// an already-established connection to the proxy node broke mid-stream (RST or
    /// timeout). Distinct from a relay-open failure; a secondary proxy-health signal.</summary>
    ProxyStreamError,
}

/// <summary>Error subtype of a <see cref="ConnHealthCategory.RelayOpenFail"/>.</summary>
public enum RelayFailKind
{
    Other = 0,
    /// <summary>": EOF" — relay opened then the upstream silently closed.</summary>
    Eof,
    /// <summary>"dial tcp &lt;node&gt;: i/o timeout" — TCP connect to the node never completed.</summary>
    DialTimeout,
    /// <summary>"read tcp ...: wsarecv/forcibly closed/connection reset" — socket reset.</summary>
    Reset,
}

/// <summary>One classified connection log line.</summary>
public sealed record ConnLogEvent(
    ConnHealthCategory Category,
    string? ConnId,
    string? OutboundTag,
    string? Destination,
    string? DurationRaw,
    RelayFailKind? FailKind = null);

/// <summary>
/// Pure classifier for sing-box connection log lines (Clash <c>/logs</c> payloads
/// or <c>singbox.log</c> lines).
///
/// <para>Its whole reason to exist is the precise classification the independent
/// review (<c>plans/independent-review-server-health-mtu-2026-06-19.md</c> §A2/E1/F1)
/// requires: a naive <c>(EOF+RST)/min</c> signal mis-counts local closes as proxy
/// failures. In bundle 214717 the 737 "forcibly closed" messages are 733 local
/// upload-side closes + a handful of real proxy-socket breaks — not 737 resets of
/// Reality connections. Conversely, a pure EOF count <em>under</em>-counts: ~224
/// relay opens in that bundle fail with "dial tcp &lt;node&gt;: i/o timeout", which an
/// EOF-only grep misses. This classifier captures the full relay-open failure set
/// (<see cref="ConnHealthCategory.RelayOpenFail"/>, any <see cref="RelayFailKind"/>)
/// and keeps benign <see cref="ConnHealthCategory.LocalClose"/> out of it.</para>
///
/// <para><strong>Observe-only.</strong> Output feeds <see cref="ConnectionHealthState"/>
/// for calibration; nothing here triggers a toast or failover.</para>
///
/// <para><strong>Prefix-agnostic.</strong> Matches by substring, so it works whether
/// the payload still carries the <c>"+TZ date time LEVEL [id dur]"</c> prefix (raw
/// <c>singbox.log</c>) or is the bare message sing-box emits over Clash <c>/logs</c>.</para>
/// </summary>
public static class ConnectionHealthClassifier
{
    /// <summary>The substring markers that form the contract with sing-box's emitter.
    /// Hoisted + named because they are the whole parsing surface — a sing-box wording
    /// change (cf. the 1.13.9→1.13.10 process_name regression) would land here.</summary>
    private static class Markers
    {
        public const string Outbound = "outbound/";
        public const string ConnPrefix = "connection:";
        // Appears ONLY on relay-open failures; a success logs "outbound/<tag>: outbound connection to".
        public const string RelayFail = "using outbound/";
        // Leading ": " is significant — anchors on the outbound tag's separator.
        public const string RelayAttempt = ": outbound connection to ";
        public const string UploadClosed = "connection upload closed";
        public const string DownloadClosed = "connection download closed";
        public const string RawRead = "raw read";
        public const string RawReadHyphen = "raw-read";
        public const string Eof = ": EOF";
        public const string DialTimeout = "i/o timeout";
    }

    // "[810041638 108ms]" / "[2130031130 26m27s]" -> id, raw duration.
    // Requires digits-then-space so "outbound/vless[proxy]" never matches.
    private static readonly Regex ConnTag =
        new(@"\[(\d+)\s+([^\]]+)\]", RegexOptions.Compiled);

    // "outbound/vless[proxy]" / "outbound/vless-udp[proxy-udp]" -> tag in brackets.
    private static readonly Regex OutboundTagRx =
        new(@"outbound/[A-Za-z0-9_-]+\[([^\]]+)\]", RegexOptions.Compiled);

    /// <summary>
    /// Classify one log payload. Returns <c>null</c> for lines that aren't
    /// connection-relevant (so the caller records only scored events).
    /// </summary>
    /// <param name="payload">The Clash <c>/logs</c> message or a singbox.log line.</param>
    /// <param name="proxyEndpoints">Active proxy socket endpoints as they appear in
    /// the log ("ip:port", e.g. "104.194.156.93:443"). Needed to tell a mid-stream
    /// proxy break from a non-proxy one. May be null/empty — then a stream break that
    /// would be <see cref="ConnHealthCategory.ProxyStreamError"/> degrades to
    /// <see cref="ConnHealthCategory.Other"/>.</param>
    public static ConnLogEvent? Classify(string payload, IReadOnlySet<string>? proxyEndpoints = null)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;

        bool hasOutbound = payload.Contains(Markers.Outbound, StringComparison.Ordinal);
        if (!hasOutbound && !payload.Contains(Markers.ConnPrefix, StringComparison.Ordinal))
            return null;

        // Decide the category from cheap substring markers FIRST, so the null-return
        // path (recognized context, nothing to score) never pays for the regex field
        // extraction below. Order: attempt is by far the most frequent line, then the
        // relay-open failure marker, then teardown.
        ConnHealthCategory category;
        RelayFailKind? failKind = null;

        if (payload.Contains(Markers.RelayAttempt, StringComparison.Ordinal))
        {
            category = ConnHealthCategory.RelayOpenAttempt;
        }
        else if (payload.Contains(Markers.RelayFail, StringComparison.Ordinal))
        {
            category = ConnHealthCategory.RelayOpenFail;
            failKind = ClassifyFailKind(payload);
        }
        else if (IsTeardown(payload))
        {
            category = ClassifyTeardown(payload, proxyEndpoints);
        }
        else
        {
            return null; // recognized context, nothing we score
        }

        var (connId, durationRaw) = ExtractConnTag(payload);
        return new ConnLogEvent(category, connId, ExtractOutboundTag(payload),
            ExtractDestination(payload), durationRaw, failKind);
    }

    private static bool IsTeardown(string payload) =>
        payload.Contains(Markers.UploadClosed, StringComparison.Ordinal) ||
        payload.Contains(Markers.DownloadClosed, StringComparison.Ordinal);

    private static ConnHealthCategory ClassifyTeardown(string payload, IReadOnlySet<string>? proxyEndpoints)
    {
        // Precedence is load-bearing: a local "raw read"/"raw-read" close can itself
        // name the proxy ip, so raw-read MUST be tested before the endpoint match —
        // else a benign local close is miscounted as a proxy stream error, the exact
        // false-positive B0 exists to prevent.
        if (payload.Contains(Markers.RawRead, StringComparison.Ordinal) ||
            payload.Contains(Markers.RawReadHyphen, StringComparison.Ordinal))
            return ConnHealthCategory.LocalClose;

        if (proxyEndpoints is { Count: > 0 } && ReferencesProxy(payload, proxyEndpoints))
            return ConnHealthCategory.ProxyStreamError;

        return ConnHealthCategory.Other; // teardown of some other remote socket
    }

    private static RelayFailKind ClassifyFailKind(string payload)
    {
        // EOF is the terminal error token ("...using outbound/<tag>: EOF"); the other
        // causes carry trailing socket detail, hence EndsWith here vs Contains below.
        if (payload.AsSpan().TrimEnd().EndsWith(Markers.Eof))
            return RelayFailKind.Eof;
        if (payload.Contains(Markers.DialTimeout, StringComparison.Ordinal))
            return RelayFailKind.DialTimeout;
        if (payload.Contains("forcibly closed", StringComparison.Ordinal) ||
            payload.Contains("connection reset", StringComparison.Ordinal) ||
            payload.Contains("wsarecv", StringComparison.Ordinal) ||
            payload.Contains("wsasend", StringComparison.Ordinal) ||
            payload.Contains("broken pipe", StringComparison.Ordinal))
            return RelayFailKind.Reset;
        return RelayFailKind.Other;
    }

    private static bool ReferencesProxy(string payload, IReadOnlySet<string> proxyEndpoints)
    {
        foreach (var endpoint in proxyEndpoints)
            if (!string.IsNullOrEmpty(endpoint) &&
                payload.Contains(endpoint, StringComparison.Ordinal))
                return true;
        return false;
    }

    private static (string? ConnId, string? DurationRaw) ExtractConnTag(string payload)
    {
        var m = ConnTag.Match(payload);
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : (null, null);
    }

    private static string? ExtractOutboundTag(string payload)
    {
        var m = OutboundTagRx.Match(payload);
        return m.Success ? m.Groups[1].Value : null;
    }

    // "open connection to 83.97.108.34:21115 using ..."  -> "83.97.108.34:21115"
    // "outbound/vless[proxy]: outbound connection to 1.2.3.4:443" -> "1.2.3.4:443"
    // Diagnostic metadata only (no category depends on it). IPv6 "[::1]:443" forms are
    // not parsed — log destinations in practice are IPv4 ip:port.
    private static string? ExtractDestination(string payload)
    {
        const string marker = "connection to ";
        int i = payload.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0)
            return null;

        int start = i + marker.Length;
        int end = start;
        while (end < payload.Length && payload[end] != ' ' && payload[end] != ':')
            end++;
        if (end < payload.Length && payload[end] == ':')
        {
            end++; // include ":port"
            while (end < payload.Length && char.IsDigit(payload[end]))
                end++;
        }
        return end > start ? payload[start..end] : null;
    }
}
