using System.Text.Json;

namespace VPNRouter.Headless.Protocol;

public interface IProtocolHandler : IAsyncDisposable
{
    Task<object> ExecuteAsync(string method, JsonElement parameters, CancellationToken cancellationToken);
    event Action<object>? StateChanged;
    event Action<object>? ProgressChanged;
}
