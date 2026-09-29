namespace VPNRouter.Core.Services;

public enum VpnConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Error,
}

/// <summary>
/// The connection state as the VPN service last wrote it. The service is the only writer; the app screen, the
/// quick-settings tile and the notification only read it through <see cref="VpnStateResolver"/>.
/// </summary>
public sealed record VpnStateSnapshot(
    VpnConnectionState State,
    string? Reason,
    int OwnerPid,
    DateTimeOffset UpdatedAt);

public static class VpnStateCodec
{
    public const string Disconnected = "disconnected";
    public const string Connecting = "connecting";
    public const string Connected = "connected";
    public const string Error = "error";

    public static string Encode(VpnConnectionState state) => state switch
    {
        VpnConnectionState.Connecting => Connecting,
        VpnConnectionState.Connected => Connected,
        VpnConnectionState.Error => Error,
        _ => Disconnected,
    };

    public static VpnStateSnapshot? TryParse(string? state, string? reason, int ownerPid, long updatedAtUnixMs)
    {
        VpnConnectionState? parsed = state?.Trim().ToLowerInvariant() switch
        {
            Disconnected => VpnConnectionState.Disconnected,
            Connecting => VpnConnectionState.Connecting,
            Connected => VpnConnectionState.Connected,
            Error => VpnConnectionState.Error,
            _ => null,
        };
        if (parsed is null || updatedAtUnixMs <= 0)
            return null;
        return new VpnStateSnapshot(
            parsed.Value,
            string.IsNullOrWhiteSpace(reason) ? null : reason,
            ownerPid,
            DateTimeOffset.FromUnixTimeMilliseconds(updatedAtUnixMs));
    }
}

/// <summary>
/// Turns the stored state into the state that is true now: a tunnel that belonged to a dead process is gone, and a
/// connect attempt never stays "connecting" forever.
/// </summary>
public static class VpnStateResolver
{
    public static readonly TimeSpan ConnectingTimeout = TimeSpan.FromSeconds(90);
    public const string ReasonInterrupted = "interrupted";
    public const string ReasonConnectTimeout = "connect-timeout";

    public static VpnStateSnapshot Resolve(VpnStateSnapshot? stored, int currentPid, DateTimeOffset now)
    {
        if (stored is null)
            return new VpnStateSnapshot(VpnConnectionState.Disconnected, null, currentPid, now);

        if (stored.State is VpnConnectionState.Connecting or VpnConnectionState.Connected
            && stored.OwnerPid != currentPid)
        {
            return stored with { State = VpnConnectionState.Disconnected, Reason = ReasonInterrupted };
        }

        if (stored.State == VpnConnectionState.Connecting)
        {
            var age = now - stored.UpdatedAt;
            if (age > ConnectingTimeout || age < -ConnectingTimeout)
                return stored with { State = VpnConnectionState.Error, Reason = ReasonConnectTimeout };
        }

        return stored;
    }
}

public enum TileClickAction
{
    Ignore,
    StartVpn,
    StopVpn,
    OpenAppForPermission,
    OpenAppForSetup,
}

public static class TileClickPlanner
{
    /// <summary>
    /// What a tap on the tile does. A connect attempt in progress swallows taps (no parallel connections); stopping
    /// needs nothing; starting needs Android's VPN consent and a configuration the service can restore.
    /// </summary>
    public static TileClickAction Plan(VpnConnectionState state, bool vpnPermissionGranted, bool hasSavedConfig) =>
        state switch
        {
            VpnConnectionState.Connecting => TileClickAction.Ignore,
            VpnConnectionState.Connected => TileClickAction.StopVpn,
            _ when !vpnPermissionGranted => TileClickAction.OpenAppForPermission,
            _ when !hasSavedConfig => TileClickAction.OpenAppForSetup,
            _ => TileClickAction.StartVpn,
        };
}

public enum TileVisual
{
    Off,
    Busy,
    On,
    Error,
}

public sealed record TileAppearance(TileVisual Visual, string Subtitle);

public static class TileAppearanceFactory
{
    public const string ReasonForegroundStartBlocked = "foreground-start-blocked";
    public const string ReasonNoPermission = "no-permission";
    public const string ReasonNoConfig = "no-config";
    public const string ReasonNoNetwork = "no-network";

    public static TileAppearance For(VpnStateSnapshot resolved, bool russian) => resolved.State switch
    {
        VpnConnectionState.Connecting => new(TileVisual.Busy, russian ? "Подключается…" : "Connecting…"),
        VpnConnectionState.Connected => new(TileVisual.On, russian ? "Подключён" : "Connected"),
        VpnConnectionState.Error => new(TileVisual.Error, ReasonText(resolved.Reason, russian)),
        _ => new(TileVisual.Off, russian ? "Выключен" : "Off"),
    };

    /// <summary>A short, human reason for a failed connect; unknown reasons stay generic so no raw error text leaks.</summary>
    public static string ReasonText(string? reason, bool russian) => reason switch
    {
        ReasonForegroundStartBlocked => russian ? "Откройте приложение" : "Open the app",
        VpnStateResolver.ReasonConnectTimeout => russian ? "Нет ответа" : "Timed out",
        ReasonNoPermission => russian ? "Нужно разрешение" : "Permission needed",
        ReasonNoConfig => russian ? "Нет настройки" : "Not set up",
        ReasonNoNetwork => russian ? "Нет сети" : "No network",
        _ => russian ? "Ошибка" : "Error",
    };
}
