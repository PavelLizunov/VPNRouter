#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Models;

namespace VPNRouter.Headless;

/// <summary>
/// Authoritative session lifecycle contract for headless VPNRouter operation.
/// Defined in Omarchy Headless Protocol v1.
/// </summary>
public interface IRouterSession : IAsyncDisposable
{
    /// <summary>
    /// Current connection state: "disconnected", "connecting", "connected", "disconnecting", "error", or "unavailable".
    /// </summary>
    string State { get; }

    /// <summary>
    /// Sanitized error code when State is "error"; null otherwise.
    /// </summary>
    string? ErrorCode { get; }

    /// <summary>
    /// Whether the session is currently capable of initiating a connection.
    /// </summary>
    bool CanConnect { get; }

    /// <summary>
    /// Whether firewall kill-switch is truthfully supported and verified on this host.
    /// </summary>
    bool SupportsKillSwitch { get; }

    /// <summary>
    /// Whether DNS leak lockdown is truthfully supported and verified on this host.
    /// </summary>
    bool SupportsDnsLockdown { get; }

    /// <summary>
    /// Raised whenever State, ErrorCode, or capabilities transition.
    /// </summary>
    event Action? Changed;

    /// <summary>
    /// Connect using the specified settings. Transitions to connecting then connected on typed readiness.
    /// </summary>
    Task ConnectAsync(AppSettings settings, CancellationToken ct);

    /// <summary>
    /// Apply updated settings to an active connection.
    /// </summary>
    Task ApplyAsync(AppSettings settings, CancellationToken ct);

    /// <summary>
    /// Gracefully disconnect and clean up engine resources.
    /// </summary>
    Task DisconnectAsync(CancellationToken ct);
}
