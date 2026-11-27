using System.Web;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

internal static class NativeTransportUriParser
{
    internal const string TrustTunnelPrefix = "trusttunnel://";
    internal const string MieruPrefix = "mieru://";

    internal static bool HasScheme(ReadOnlySpan<char> value) =>
        value.StartsWith(TrustTunnelPrefix, StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith(MieruPrefix, StringComparison.OrdinalIgnoreCase);

    internal static bool IsSupported(ReadOnlySpan<char> value) =>
        value.StartsWith(TrustTunnelPrefix, StringComparison.OrdinalIgnoreCase)
            ? SingBoxFeatures.TrustTunnelAvailable
            : SingBoxFeatures.MieruAvailable;

    internal static VlessServerEntry Parse(string raw)
    {
        if (!IsSupported(raw))
            throw new FormatException("This protocol requires a compatible sing-box core.");
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6) ||
            uri.Port is < 1 or > 65535 || uri.AbsolutePath is not ("" or "/"))
            throw new FormatException("Invalid native transport endpoint.");

        var userInfo = uri.UserInfo.Split(':', 2);
        if (userInfo.Length != 2)
            throw new FormatException("Native transport requires username and password.");
        var username = Uri.UnescapeDataString(userInfo[0]);
        var password = Uri.UnescapeDataString(userInfo[1]);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password) ||
            username.Any(char.IsControl) || password.Any(char.IsControl) ||
            (uri.Scheme == "trusttunnel" && username.Contains(':')))
            throw new FormatException("Invalid native transport credentials.");

        var host = uri.IdnHost.Trim('[', ']');
        var name = Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
        if (name.Any(char.IsControl))
            throw new FormatException("Invalid native transport profile name.");
        var entry = new VlessServerEntry
        {
            Protocol = uri.Scheme,
            Name = string.IsNullOrWhiteSpace(name) ? host : name,
            Server = host,
            Port = uri.Port,
            Username = username,
            Password = password,
        };
        var query = HttpUtility.ParseQueryString(uri.Query);
        foreach (var key in query.AllKeys)
        {
            var allowed = uri.Scheme == "trusttunnel"
                ? key is "sni" or "quic" or "udp" or "pk_sha256"
                : key is "transport" or "multiplexing" or "handshake";
            if (!allowed || query.GetValues(key)?.Length != 1)
                throw new FormatException("Invalid native transport option.");
        }

        if (uri.Scheme == "trusttunnel")
        {
            var sni = query["sni"] ?? host;
            if (Uri.CheckHostName(sni) is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
                throw new FormatException("Invalid TrustTunnel TLS server name.");
            var hexPin = query["pk_sha256"];
            if (hexPin != null && (hexPin.Length != 64 || !hexPin.All(Uri.IsHexDigit)))
                throw new FormatException("Invalid TrustTunnel public-key pin.");
            entry.Security = "tls";
            entry.Tls = new VlessTlsConfig { Enabled = true, ServerName = sni };
            entry.TrustTunnel = new TrustTunnelProfile
            {
                Quic = ReadSwitch(query["quic"], false),
                Udp = ReadSwitch(query["udp"], true),
                CertificatePublicKeySha256 = hexPin == null
                    ? null : Convert.ToBase64String(Convert.FromHexString(hexPin)),
            };
        }
        else
        {
            entry.Security = "none";
            entry.Mieru = new MieruProfile
            {
                Transport = query["transport"] ?? "tcp",
                Multiplexing = query["multiplexing"] ?? "default",
                HandshakeMode = query["handshake"] ?? "default",
            };
            if (!entry.Mieru.IsValid())
                throw new FormatException("Invalid Mieru transport options.");
        }

        return entry;
    }

    private static bool ReadSwitch(string? value, bool defaultValue) => value switch
    {
        null => defaultValue,
        "0" => false,
        "1" => true,
        _ => throw new FormatException("Invalid TrustTunnel switch."),
    };
}
