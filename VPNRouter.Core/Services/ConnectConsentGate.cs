namespace VPNRouter.Core.Services;

/// <summary>
/// Lets only one system VPN consent dialog be open at a time. A second Connect request while the dialog is still
/// showing (a double tap, or a tile tap followed by a button tap) must not open a second dialog: Android answers the
/// first with "cancelled", which the app reads as a refusal and turns the connection intent off.
/// </summary>
public sealed class ConnectConsentGate
{
    private int _open;

    public bool IsOpen => Volatile.Read(ref _open) == 1;

    /// <summary>True when the caller may open the dialog; false when one is already open.</summary>
    public bool TryOpen() => Interlocked.CompareExchange(ref _open, 1, 0) == 0;

    /// <summary>Call when the dialog returned its result, whatever the result was.</summary>
    public void Close() => Volatile.Write(ref _open, 0);
}
