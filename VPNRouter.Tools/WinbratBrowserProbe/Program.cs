#nullable enable

using System.Text.Json;
using System.Text.Json.Serialization;

namespace VPNRouter.Tools.WinbratBrowserProbe;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var result = await BrowserProbe.RunAsync(args, CancellationToken.None);
        Console.WriteLine(BrowserProbeJson.Serialize(result));
        return result.Lifecycle == BrowserProbeLifecycle.Completed ? 0 : 1;
    }
}

internal static class BrowserProbeJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(BrowserProbeResult result) => JsonSerializer.Serialize(result, Options);
}
