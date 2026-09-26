#nullable enable
using System;

namespace VPNRouter.Headless.Lifecycle;

/// <summary>
/// Canonical state strings defined by Omarchy Headless Protocol v1.
/// </summary>
public static class SessionStates
{
    public const string Disconnected = "disconnected";
    public const string Connecting = "connecting";
    public const string Connected = "connected";
    public const string Disconnecting = "disconnecting";
    public const string Error = "error";
    public const string Unavailable = "unavailable";

    public static bool IsValid(string state) => state switch
    {
        Disconnected => true,
        Connecting => true,
        Connected => true,
        Disconnecting => true,
        Error => true,
        Unavailable => true,
        _ => false
    };
}
