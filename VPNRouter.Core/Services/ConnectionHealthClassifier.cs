#nullable enable
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace VPNRouter.Core.Services;

public enum ConnHealthCategory
{
    Other = 0,

    RelayOpenAttempt,

    RelayOpenFail,

    LocalClose,

    ProxyStreamError,
}

public enum RelayFailKind
{
    Other = 0,
    Eof,
    DialTimeout,
    Reset,
}

public sealed record ConnLogEvent(
    ConnHealthCategory Category,
    string? ConnId,
    string? OutboundTag,
    string? Destination,
    string? DurationRaw,
    RelayFailKind? FailKind = null);

public static class ConnectionHealthClassifier
{
    private static class Markers
    {
        public const string Outbound = "outbound/";
        public const string ConnPrefix = "connection:";
        public const string RelayFail = "using outbound/";
        public const string RelayAttempt = ": outbound connection to ";
        public const string UploadClosed = "connection upload closed";
        public const string DownloadClosed = "connection download closed";
        public const string RawRead = "raw read";
        public const string RawReadHyphen = "raw-read";
        public const string Eof = ": EOF";
        public const string DialTimeout = "i/o timeout";
    }

    private static readonly Regex ConnTag =
        new(@"\[(\d+)\s+([^\]]+)\]", RegexOptions.Compiled);

    private static readonly Regex OutboundTagRx =
        new(@"outbound/[A-Za-z0-9_-]+\[([^\]]+)\]", RegexOptions.Compiled);

    public static ConnLogEvent? Classify(string payload, IReadOnlySet<string>? proxyEndpoints = null)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;

        bool hasOutbound = payload.Contains(Markers.Outbound, StringComparison.Ordinal);
        if (!hasOutbound && !payload.Contains(Markers.ConnPrefix, StringComparison.Ordinal))
            return null;

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
            return null;
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
        // Test raw-read before the endpoint match: a local raw-read close can name the proxy IP and must not count as a proxy error.
        if (payload.Contains(Markers.RawRead, StringComparison.Ordinal) ||
            payload.Contains(Markers.RawReadHyphen, StringComparison.Ordinal))
            return ConnHealthCategory.LocalClose;

        if (proxyEndpoints is { Count: > 0 } && ReferencesProxy(payload, proxyEndpoints))
            return ConnHealthCategory.ProxyStreamError;

        return ConnHealthCategory.Other;
    }

    private static RelayFailKind ClassifyFailKind(string payload)
    {
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
            end++;
            while (end < payload.Length && char.IsDigit(payload[end]))
                end++;
        }
        return end > start ? payload[start..end] : null;
    }
}
