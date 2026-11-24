#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;

namespace VPNRouter.Core.Services;

public enum ConnectionFailureCategory
{
    Technical = 0,
    NetworkUnavailable,
    ProviderRejected,
    AuthenticationFailed,
    UserCancelled,
    LocalEnvironment,
    Background,
}

public sealed record ConnectionAttemptAssessment(
    ConnectionFailureCategory Category,
    string DetailCode)
{
    public bool CountsTowardRollback => Category == ConnectionFailureCategory.Technical;
}

/// <summary>
/// Classifies a finished manual connection attempt. Only technical failures are
/// comparable evidence of a client regression; network, provider, authentication,
/// cancellation and local-environment outcomes are deliberately excluded.
/// </summary>
public static class ConnectionAttemptClassifier
{
    private static readonly string[] AuthenticationMarkers =
    [
        "authentication failed",
        "auth failed",
        "invalid credentials",
        "unauthorized",
        "forbidden",
        "tls handshake",
        "certificate",
    ];

    private static readonly string[] ProviderMarkers =
    [
        "subscription expired",
        "subscription revoked",
        "access expired",
        "access revoked",
        "traffic limit",
        "device limit",
        "quota exceeded",
        "http 401",
        "http 403",
        "http 429",
    ];

    private static readonly string[] NetworkMarkers =
    [
        "network is unreachable",
        "no internet",
        "no network",
        "name or service not known",
        "nodename nor servname",
        "temporary failure in name resolution",
        "host not found",
        "connection refused",
        "connection reset",
        "broken pipe",
    ];

    private static readonly string[] LocalMarkers =
    [
        "permission denied",
        "access is denied",
        "requires administrator",
        "run as administrator",
        "tun adapter busy",
        "adapter owned by another",
        "conflicting vpn",
    ];

    public static ConnectionAttemptAssessment Classify(
        Exception? exception,
        bool userCancelled = false,
        bool background = false)
    {
        if (background)
            return new ConnectionAttemptAssessment(ConnectionFailureCategory.Background, "background");

        if (userCancelled || exception is OperationCanceledException)
            return new ConnectionAttemptAssessment(ConnectionFailureCategory.UserCancelled, "cancelled");

        if (exception is TunOwnershipException or ConflictingVpnException)
            return new ConnectionAttemptAssessment(ConnectionFailureCategory.LocalEnvironment, "local-environment");

        var chain = Flatten(exception);
        if (chain.Any(item => item is OperationCanceledException))
            return new ConnectionAttemptAssessment(ConnectionFailureCategory.UserCancelled, "cancelled");
        if (chain.Any(item => item is TunnelExitedDuringStartupException))
            return new ConnectionAttemptAssessment(ConnectionFailureCategory.Technical, "engine-stopped");
        if (chain.Any(item => item is UnauthorizedAccessException or ConflictingVpnException or TunOwnershipException))
            return new ConnectionAttemptAssessment(ConnectionFailureCategory.LocalEnvironment, "local-environment");
        var messages = chain
            .Select(item => item.Message ?? string.Empty)
            .Where(item => item.Length > 0)
            .ToArray();

        if (chain.Any(item => item is AuthenticationException) || Matches(messages, AuthenticationMarkers))
            return new ConnectionAttemptAssessment(ConnectionFailureCategory.AuthenticationFailed, "authentication");

        if (Matches(messages, LocalMarkers))
            return new ConnectionAttemptAssessment(ConnectionFailureCategory.LocalEnvironment, "local-environment");

        if (Matches(messages, ProviderMarkers))
            return new ConnectionAttemptAssessment(ConnectionFailureCategory.ProviderRejected, "provider");

        foreach (var item in chain)
        {
            if (item is HttpRequestException { StatusCode: { } status })
            {
                var code = (int)status;
                return code is 401 or 403 or 407 or 429
                    ? new ConnectionAttemptAssessment(ConnectionFailureCategory.AuthenticationFailed, "http-auth")
                    : new ConnectionAttemptAssessment(ConnectionFailureCategory.ProviderRejected, "http-provider");
            }

            if (item is SocketException socket)
            {
                return socket.SocketErrorCode switch
                {
                    SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain
                        or SocketError.NetworkUnreachable or SocketError.NetworkDown
                        or SocketError.HostUnreachable or SocketError.TimedOut or SocketError.ConnectionRefused
                        or SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.NetworkReset
                        => new ConnectionAttemptAssessment(ConnectionFailureCategory.NetworkUnavailable, "network"),
                    _ => new ConnectionAttemptAssessment(ConnectionFailureCategory.Technical, "socket"),
                };
            }
        }

        if (chain.Any(item => item is HttpRequestException or TimeoutException) ||
            Matches(messages, NetworkMarkers))
            return new ConnectionAttemptAssessment(ConnectionFailureCategory.NetworkUnavailable, "network");

        return new ConnectionAttemptAssessment(
            ConnectionFailureCategory.Technical,
            exception?.GetType().Name ?? "unknown");
    }

    private static bool Matches(IEnumerable<string> messages, IEnumerable<string> markers) =>
        messages.Any(message => markers.Any(marker =>
            message.Contains(marker, StringComparison.OrdinalIgnoreCase)));

    private static IReadOnlyList<Exception> Flatten(Exception? exception)
    {
        var result = new List<Exception>();
        var seen = new HashSet<Exception>();
        for (var current = exception; current != null && seen.Add(current); current = current.InnerException)
            result.Add(current);
        return result;
    }
}
