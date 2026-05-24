namespace VPNRouter.Core.Services;

/// <summary>
/// Thrown by <see cref="TgProxyManager.Start"/> when the configured
/// listen port is already bound by another process. Pre-fix the spawn
/// would proceed and Python would exit silently within the 2s watchdog
/// window with a generic "Process exited" warning — no port-conflict
/// breadcrumb. The typed exception lets the App-layer catch the cause
/// and surface a port-specific toast ("Port 1443 is busy. Close the
/// other app or change the port in settings.").
///
/// <para>The optional <see cref="OwnerProcessHint"/> field is populated
/// best-effort on Windows via a quick <c>netstat -ano</c> probe — null
/// if the probe fails or we couldn't identify the owner.</para>
///
/// <para>Sibling shape to <see cref="WgturnDownloadException"/> + the
/// other Core typed exceptions — UI layer reads
/// <see cref="System.Exception.Message"/> directly without re-wrapping.</para>
/// </summary>
public sealed class TgProxyPortConflictException : System.Exception
{
    /// <summary>The TCP port that's already in use.</summary>
    public int Port { get; }

    /// <summary>Best-effort process owner hint, e.g. "python.exe (PID 1234)"
    /// or null when the owner couldn't be identified (probe failed, race
    /// between port test + netstat, OS without netstat).</summary>
    public string? OwnerProcessHint { get; }

    public TgProxyPortConflictException(int port, string? ownerProcessHint = null)
        : base(BuildMessage(port, ownerProcessHint))
    {
        Port = port;
        OwnerProcessHint = ownerProcessHint;
    }

    private static string BuildMessage(int port, string? hint)
    {
        // English-only here — UI layer localizes its own toast via
        // Localization.Strings.TgProxyPortBusy. This message is the
        // diagnostic that lands in logs.
        return hint is null
            ? $"TgProxy port {port} is already in use."
            : $"TgProxy port {port} is already in use (owner: {hint}).";
    }
}
