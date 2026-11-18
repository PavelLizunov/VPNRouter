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
            _owner = command;
            return true;
        }
    }

    public void Close() => TakeResult();

    internal (string? Owner, string? Pending) ResolveResult(Func<string?, bool> isCurrent)
    {
        var (owner, pending) = TakeResult();
        return (isCurrent(owner) ? owner : null, isCurrent(pending) ? pending : null);
    }

    private (string? Owner, string? Pending) TakeResult()
    {
        lock (_sync)
        {
            var requests = (_owner, _pending);
            _owner = _pending = null;
            return requests;
        }
    }
}
