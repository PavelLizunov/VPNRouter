using System.Text.Json;

namespace VPNRouter.Headless.Protocol;

public sealed class ProtocolRequest
{
    public int Version { get; }
    public string Id { get; }
    public string Method { get; }
    public JsonElement Params { get; }

    public ProtocolRequest(int version, string id, string method, JsonElement @params)
    {
        Version = version;
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Method = method ?? throw new ArgumentNullException(nameof(method));
        Params = @params;
    }

    public static bool IsValidId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > ProtocolConstants.MaxIdLength)
            return false;

        foreach (char c in id)
        {
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-'))
            {
                return false;
            }
        }

        return true;
    }
}
