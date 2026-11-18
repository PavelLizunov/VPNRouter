namespace VPNRouter.Core.Services;

/// <summary>
/// Lets only one system VPN consent dialog be open at a time. A second Connect request while the dialog is still
/// showing (a double tap, or a tile tap followed by a button tap) must not open a second dialog: Android answers the
/// first with "cancelled", which the app reads as a refusal and turns the connection intent off.
/// </summary>
public sealed class ConnectConsentGate
{
    private readonly Lock _sync = new();
    private string? _owner;
    private string? _pending;

    public ConnectConsentGate() { }

    internal ConnectConsentGate(string? owner, string? pending)
    {
        _owner = owner;
        _pending = pending;
    }

    internal (string? Owner, string? Pending) Requests
    {
        get { lock (_sync) return (_owner, _pending); }
    }

    public bool IsOpen => Requests.Owner != null;
    public bool TryOpen() => OpenOrQueue(string.Empty);

    internal bool OpenOrQueue(string command)
    {
        lock (_sync)
        {
            if (_owner != null)
            {
                if (command.Length > 0 && command != _owner) _pending = command;
                return false;
            }
            _pending = null;
            _owner = command;
            return true;
        }
    }

    public void Close()
    {
        lock (_sync) _owner = _pending = null;
    }

    internal string? ResolveResult(Func<string?, bool> isCurrent)
    {
        lock (_sync)
        {
            if (_owner == null) return null;
            var owner = _owner;
            _owner = null;
            if (!isCurrent(_pending)) _pending = null;
            return isCurrent(owner) ? owner : null;
        }
    }

    internal string? ClaimPending(bool currentActivity, Func<string?, bool> isCurrent)
    {
        lock (_sync)
        {
            if (!currentActivity || _owner != null || _pending == null) return null;
            var pending = _pending;
            _pending = null;
            return isCurrent(pending) ? pending : null;
        }
    }
}
