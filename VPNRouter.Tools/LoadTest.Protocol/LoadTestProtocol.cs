#nullable enable

using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;

namespace VPNRouter.Tools.LoadTest.Protocol;

public static class LoadTestContract
{
    public const string HostName = "loadtest.vpn.ninitux.com";
    public const int HttpPort = 443;
    public const int UdpPort = 19000;
    public const int BlobBytes = 64 * 1024;
    public const int MaxDatagramBytes = 1200;
    public const int CookieRequestBytes = 40;
    public const int CookieBytes = 34;
    public const int EchoHeaderBytes = 44;
    public const int MaxPayloadBytes = MaxDatagramBytes - EchoHeaderBytes;
    public const int CookieLifetimeSeconds = 30;
    public const int PerSourcePacketsPerSecond = 100;
    public const int GlobalPacketsPerSecond = 1000;
    public const int MaxReplayEntries = 4096;
}

public enum UdpPacketKind : byte
{
    CookieRequest = 1,
    Cookie = 2,
    EchoRequest = 3,
    EchoResponse = 4,
}

public enum UdpEchoDisposition
{
    Echo,
    Invalid,
    Expired,
    Replay,
    RateLimited,
}

public sealed class UdpCookieAuthenticator
{
    private const byte Version = 1;
    private const int ExpiryOffset = 2;
    private const int NonceOffset = 10;
    private const int TagOffset = 18;
    private const int TagBytes = 16;
    private readonly byte[] _secret;

    public UdpCookieAuthenticator(byte[] secret)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(secret.Length, 16);
        _secret = secret.ToArray();
    }

    public byte[] CreateCookie(IPAddress source, ReadOnlySpan<byte> nonce, DateTimeOffset now)
    {
        if (nonce.Length != 8) throw new ArgumentOutOfRangeException(nameof(nonce));

        var cookie = new byte[LoadTestContract.CookieBytes];
        cookie[0] = Version;
        cookie[1] = (byte)UdpPacketKind.Cookie;
        BinaryPrimitives.WriteInt64BigEndian(cookie.AsSpan(ExpiryOffset, 8), now.AddSeconds(LoadTestContract.CookieLifetimeSeconds).ToUnixTimeSeconds());
        nonce.CopyTo(cookie.AsSpan(NonceOffset, 8));
        ComputeTag(source, cookie.AsSpan(0, TagOffset), cookie.AsSpan(TagOffset, TagBytes));
        return cookie;
    }

    public bool IsValid(IPAddress source, ReadOnlySpan<byte> cookie, DateTimeOffset now, out UdpEchoDisposition failure)
    {
        failure = UdpEchoDisposition.Invalid;
        if (cookie.Length != LoadTestContract.CookieBytes || cookie[0] != Version || cookie[1] != (byte)UdpPacketKind.Cookie) return false;

        Span<byte> expected = stackalloc byte[TagBytes];
        ComputeTag(source, cookie[..TagOffset], expected);
        if (!CryptographicOperations.FixedTimeEquals(expected, cookie.Slice(TagOffset, TagBytes))) return false;

        if (BinaryPrimitives.ReadInt64BigEndian(cookie.Slice(ExpiryOffset, 8)) < now.ToUnixTimeSeconds())
        {
            failure = UdpEchoDisposition.Expired;
            return false;
        }

        return true;
    }

    public static DateTimeOffset GetExpiry(ReadOnlySpan<byte> cookie) =>
        DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadInt64BigEndian(cookie.Slice(ExpiryOffset, 8)));

    private void ComputeTag(IPAddress source, ReadOnlySpan<byte> header, Span<byte> destination)
    {
        Span<byte> sourceBytes = stackalloc byte[16];
        var written = source.TryWriteBytes(sourceBytes, out var count) ? count : 0;
        var data = new byte[written + header.Length];
        sourceBytes[..written].CopyTo(data);
        header.CopyTo(data.AsSpan(written));
        HMACSHA256.HashData(_secret, data).AsSpan(0, destination.Length).CopyTo(destination);
    }
}

public sealed class FixedRateLimiter
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (long Second, int Count)> _perSource = new(StringComparer.Ordinal);
    private long _globalSecond = long.MinValue;
    private int _globalCount;

    public bool TryTake(IPAddress source, DateTimeOffset now)
    {
        lock (_gate)
        {
            var second = now.ToUnixTimeSeconds();
            if (_globalSecond != second)
            {
                _globalSecond = second;
                _globalCount = 0;
                _perSource.Clear();
            }
            if (_globalCount >= LoadTestContract.GlobalPacketsPerSecond) return false;

            var key = source.ToString();
            var entry = _perSource.TryGetValue(key, out var prior) && prior.Second == second
                ? prior
                : (Second: second, Count: 0);
            if (entry.Count >= LoadTestContract.PerSourcePacketsPerSecond) return false;

            _perSource[key] = (second, entry.Count + 1);
            _globalCount++;
            return true;
        }
    }
}

public sealed class UdpEchoProcessor
{
    private readonly UdpCookieAuthenticator _cookies;
    private readonly FixedRateLimiter _rateLimiter = new();
    private readonly Dictionary<string, (long Sequence, DateTimeOffset Expiry)> _lastSequence = new(StringComparer.Ordinal);

    public UdpEchoProcessor(byte[] secret) => _cookies = new(secret);

    public int ReplayEntryCount => _lastSequence.Count;

    public UdpEchoDisposition Process(IPAddress source, ReadOnlySpan<byte> request, DateTimeOffset now, out byte[]? response)
    {
        response = null;
        if (request.Length > LoadTestContract.MaxDatagramBytes || request.Length < 2 || request[0] != 1) return UdpEchoDisposition.Invalid;
        if (!_rateLimiter.TryTake(source, now)) return UdpEchoDisposition.RateLimited;

        if (request[1] == (byte)UdpPacketKind.CookieRequest)
        {
            if (request.Length != LoadTestContract.CookieRequestBytes) return UdpEchoDisposition.Invalid;
            response = _cookies.CreateCookie(source, request.Slice(2, 8), now);
            return UdpEchoDisposition.Echo;
        }

        if (request[1] != (byte)UdpPacketKind.EchoRequest || request.Length < LoadTestContract.EchoHeaderBytes) return UdpEchoDisposition.Invalid;
        if (!_cookies.IsValid(source, request.Slice(2, LoadTestContract.CookieBytes), now, out var failure)) return failure;

        var expiry = UdpCookieAuthenticator.GetExpiry(request.Slice(2, LoadTestContract.CookieBytes));
        CleanupReplay(now);
        var sequence = BinaryPrimitives.ReadInt64BigEndian(request.Slice(36, 8));
        var key = source + ":" + Convert.ToHexString(request.Slice(12, 8));
        if (_lastSequence.TryGetValue(key, out var last) && sequence <= last.Sequence) return UdpEchoDisposition.Replay;
        if (!_lastSequence.ContainsKey(key) && _lastSequence.Count >= LoadTestContract.MaxReplayEntries) return UdpEchoDisposition.RateLimited;
        _lastSequence[key] = (sequence, expiry);

        response = new byte[request.Length - LoadTestContract.CookieBytes];
        response[0] = 1;
        response[1] = (byte)UdpPacketKind.EchoResponse;
        request.Slice(36).CopyTo(response.AsSpan(2));
        return UdpEchoDisposition.Echo;
    }

    private void CleanupReplay(DateTimeOffset now)
    {
        foreach (var key in _lastSequence.Where(entry => entry.Value.Expiry < now).Select(entry => entry.Key).ToArray())
            _lastSequence.Remove(key);
    }
}
