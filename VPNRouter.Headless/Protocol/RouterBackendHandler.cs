using System.Text.Json;

namespace VPNRouter.Headless.Protocol;

/// <summary>
/// Adapts RouterBackend to the transport-independent IProtocolHandler interface.
/// </summary>
public sealed class RouterBackendHandler : IProtocolHandler
{
    private readonly RouterBackend _backend;

    public RouterBackendHandler(RouterBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _backend.StateChanged += OnStateChanged;
        _backend.ProgressChanged += OnProgressChanged;
    }

    private void OnStateChanged(object state) => StateChanged?.Invoke(state);
    private void OnProgressChanged(object progress) => ProgressChanged?.Invoke(progress);

    public Task<object> ExecuteAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
        => _backend.ExecuteAsync(method, parameters, cancellationToken);

    public event Action<object>? StateChanged;
    public event Action<object>? ProgressChanged;

    public async ValueTask DisposeAsync()
    {
        _backend.StateChanged -= OnStateChanged;
        _backend.ProgressChanged -= OnProgressChanged;
        await _backend.DisposeAsync().ConfigureAwait(false);
    }
}
