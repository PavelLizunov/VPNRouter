namespace VPNRouter.Core.Services;

public sealed class TgProxyPortConflictException : System.Exception
{
    public int Port { get; }

    public string? OwnerProcessHint { get; }

    public TgProxyPortConflictException(int port, string? ownerProcessHint = null)
        : base(BuildMessage(port, ownerProcessHint))
    {
        Port = port;
        OwnerProcessHint = ownerProcessHint;
    }

    private static string BuildMessage(int port, string? hint)
    {
        return hint is null
            ? $"TgProxy port {port} is already in use."
            : $"TgProxy port {port} is already in use (owner: {hint}).";
    }
}
