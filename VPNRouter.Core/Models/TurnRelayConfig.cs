using System.Net;
using System.Net.Sockets;
using YamlDotNet.Serialization;

namespace VPNRouter.Core.Models;

/// <summary>
/// A sing-box-vpnctl <c>turnrelay</c> entry. The relay dials the TURN server and
/// carries a VLESS exit through it, so a VPNRouter row with this profile is a
/// chain upstream only; it is never selected as a standalone proxy.
/// </summary>
public sealed class TurnRelayConfig
{
    public const string DefaultProvider = "static";
    public const string DefaultServerType = "exit";
    public const string DefaultMode = "srtp";
    public const int DefaultConnections = 2;

    [YamlMember(Alias = "provider")]
    public string Provider { get; set; } = DefaultProvider;

    [YamlMember(Alias = "server_type")]
    public string ServerType { get; set; } = DefaultServerType;

    [YamlMember(Alias = "turn_server")]
    public string TurnServer { get; set; } = string.Empty;

    [YamlMember(Alias = "turn_username")]
    public string TurnUsername { get; set; } = string.Empty;

    [YamlMember(Alias = "turn_password")]
    public string TurnPassword { get; set; } = string.Empty;

    [YamlMember(Alias = "password")]
    public string Password { get; set; } = string.Empty;

    [YamlMember(Alias = "server_fingerprint")]
    public string ServerFingerprint { get; set; } = string.Empty;

    [YamlMember(Alias = "connections")]
    public int Connections { get; set; } = DefaultConnections;

    [YamlMember(Alias = "mode")]
    public string Mode { get; set; } = DefaultMode;

    [YamlMember(Alias = "udp")]
    public bool Udp { get; set; } = true;

    internal bool IsComplete() =>
        TurnRelayUriParserRules.IsValidHostPort(TurnServer) &&
        !string.IsNullOrWhiteSpace(TurnUsername) &&
        !string.IsNullOrWhiteSpace(TurnPassword) &&
        !string.IsNullOrWhiteSpace(Password) &&
        IsFingerprint(ServerFingerprint) &&
        Connections is >= 1 and <= 60 &&
        Mode is "srtp" or "dtls" &&
        Provider is "" or DefaultProvider &&
        ServerType is "" or DefaultServerType;

    internal static bool IsLiteralIp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('%') ||
            !IPAddress.TryParse(value, out var address))
            return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.None);
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return !address.Equals(IPAddress.IPv6Any) && !address.Equals(IPAddress.IPv6None) &&
                address.ScopeId == 0;
        return false;
    }

    internal static bool IsFingerprint(string? value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}

/// <summary>Small shared checks used by the URI parser and the model.</summary>
internal static class TurnRelayUriParserRules
{
    internal static bool IsValidHostPort(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || value.Any(char.IsWhiteSpace))
            return false;

        string host;
        string portText;
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            if (close <= 1) return false;
            host = value[1..close];
            if (close + 1 >= value.Length || value[close + 1] != ':') return false;
            portText = value[(close + 2)..];
            if (!IPAddress.TryParse(host, out var ip) ||
                ip.AddressFamily != AddressFamily.InterNetworkV6)
                return false;
        }
        else
        {
            var colon = value.LastIndexOf(':');
            if (colon <= 0 || colon == value.Length - 1) return false;
            host = value[..colon];
            portText = value[(colon + 1)..];
            if (host.Contains(':')) return false;
            if (IPAddress.TryParse(host, out var ip) &&
                ip.AddressFamily != AddressFamily.InterNetwork)
                return false;
            if (host.Any(char.IsControl) || host.Any(char.IsWhiteSpace)) return false;
        }

        return int.TryParse(portText, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535;
    }
}
