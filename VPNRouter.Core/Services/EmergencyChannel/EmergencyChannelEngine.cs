using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services.EmergencyChannel;

/// <summary>
/// Lifecycle states for the emergency channel. Modelled after
/// <see cref="SingBoxState"/> but with names that fit the
/// "user-facing connection" mental model (rather than process-state).
/// </summary>
public enum EmergencyChannelState
{
    /// <summary>No wgturn-cli running.</summary>
    Disconnected,
    /// <summary>Spawn invoked, waiting for tunnel to come up.</summary>
    Connecting,
    /// <summary>wgturn-cli is running and reporting healthy.</summary>
    Connected,
    /// <summary>wgturn-cli exited unexpectedly. User must reconnect.</summary>
    Failed
}

/// <summary>
/// r9 Phase 2 — high-level lifecycle service for the wgturn-core
/// emergency fallback channel. Mirrors the shape of
/// <see cref="VpnEngine"/>: <see cref="StartAsync"/> spawns
/// <c>wgturn-cli.exe</c> via <see cref="EmergencyChannelManager"/>,
/// <see cref="Stop"/> tears it down, <see cref="Restart"/> = Stop +
/// Start.
///
/// <para>Phase-2 scope is intentionally minimal: there is NO UI binding
/// (Phase 3) and NO mutex policy with the main sing-box VPN (still TBD
/// with user — both can theoretically run on separate ports but they
/// share the TUN adapter so will conflict at TUN-creation time; the
/// <c>ConflictingVpnDetector</c> from the Bug-r9-E chip should also
/// flag this once that lands).</para>
///
/// <para>Events:
/// <list type="bullet">
/// <item><see cref="StateChanged"/> — fires on every state transition
/// so the future Phase-3 UI can drive a status badge.</item>
/// <item><see cref="ErrorOccurred"/> — fires when StartAsync throws
/// (config validation / binary missing / spawn failure) or wgturn-cli
/// crashes after a successful start. Carries the error message.</item>
/// </list>
/// </para>
/// </summary>
public class EmergencyChannelEngine : IDisposable
{
    private readonly ILogger? _logger;
    private readonly Func<EmergencyChannelManager> _managerFactory;

    private EmergencyChannelManager? _manager;
    private EmergencyChannelConfig? _activeConfig;
    private EmergencyChannelState _state = EmergencyChannelState.Disconnected;
    private bool _disposed;

    public EmergencyChannelState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            StateChanged?.Invoke(value);
        }
    }

    public int? Pid => _manager?.Pid;
    public string? ActiveLabel => _activeConfig?.Label;

    /// <summary>Fires on every state transition. Phase-3 UI uses this
    /// to drive the connection-status badge.</summary>
    public event Action<EmergencyChannelState>? StateChanged;

    /// <summary>Fires when StartAsync throws or the underlying
    /// wgturn-cli exits unexpectedly. Carries a human-readable error
    /// message suitable for surfacing via toast / banner.</summary>
    public event Action<string>? ErrorOccurred;

    public EmergencyChannelEngine(ILogger? logger = null)
        : this(() => new EmergencyChannelManager(logger), logger) { }

    /// <summary>Internal ctor used by tests to inject a stub manager
    /// factory. Public callers should use the parameterless overload.</summary>
    internal EmergencyChannelEngine(Func<EmergencyChannelManager> managerFactory, ILogger? logger = null)
    {
        _managerFactory = managerFactory;
        _logger = logger;
    }

    /// <summary>
    /// Bring the emergency channel up. Spawns a fresh
    /// <see cref="EmergencyChannelManager"/> if needed, then calls its
    /// <c>Start</c> with the parsed config. Throws on configuration or
    /// spawn failure (caller should surface the message via toast).
    /// </summary>
    public Task StartAsync(EmergencyChannelConfig config, CancellationToken ct = default)
    {
        if (config == null) throw new ArgumentNullException(nameof(config));

        ct.ThrowIfCancellationRequested();

        if (State == EmergencyChannelState.Connecting || State == EmergencyChannelState.Connected)
        {
            _logger?.Warning(
                "[EmergencyChannelEngine] StartAsync called while already running (state {State}) — stopping first",
                State);
            Stop();
        }

        State = EmergencyChannelState.Connecting;

        try
        {
            var manager = _managerFactory();
            Interlocked.Exchange(ref _manager, manager);
            manager.Started += OnManagerStarted;
            manager.Crashed += OnManagerCrashed;
            manager.Start(config);
            _activeConfig = config;
            State = EmergencyChannelState.Connected;
            _logger?.Information(
                "[EmergencyChannelEngine] Connected (PID {Pid}, label {Label})",
                manager.Pid, config.Label ?? "(none)");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "[EmergencyChannelEngine] StartAsync failed");
            var failed = Interlocked.Exchange(ref _manager, null);
            try { failed?.Dispose(); } catch { }
            _activeConfig = null;
            State = EmergencyChannelState.Failed;
            ErrorOccurred?.Invoke(ex.Message);
            throw;
        }
    }

    /// <summary>Tear down the emergency channel. Idempotent.</summary>
    public void Stop()
    {
        // Atomic claim — exactly one of Stop/OnManagerCrashed disposes.
        var manager = Interlocked.Exchange(ref _manager, null);
        if (manager == null)
        {
            State = EmergencyChannelState.Disconnected;
            return;
        }

        try
        {
            manager.Started -= OnManagerStarted;
            manager.Crashed -= OnManagerCrashed;
            manager.Stop();
        }
        catch (Exception ex)
        {
            _logger?.Warning(ex, "[EmergencyChannelEngine] Stop encountered error");
        }
        finally
        {
            try { manager.Dispose(); } catch { }
            _activeConfig = null;
            State = EmergencyChannelState.Disconnected;
            _logger?.Information("[EmergencyChannelEngine] Disconnected");
        }
    }

    /// <summary>Stop + Start with the previously-active config. Throws
    /// <see cref="InvalidOperationException"/> if there is no active
    /// config (i.e. <see cref="StartAsync"/> was never called).</summary>
    public async Task RestartAsync(CancellationToken ct = default)
    {
        var saved = _activeConfig
            ?? throw new InvalidOperationException(
                "RestartAsync called before any successful StartAsync — no config to restart with.");
        Stop();
        await StartAsync(saved, ct).ConfigureAwait(false);
    }

    private void OnManagerStarted(int pid)
    {
        // Reserved for future telemetry hook — for Phase 2 the
        // engine-level state transitions happen synchronously inside
        // StartAsync because Manager.Start is synchronous.
        _logger?.Debug("[EmergencyChannelEngine] Manager reported PID {Pid}", pid);
    }

    private void OnManagerCrashed(object? sender, int? exitCode)
    {
        // Exact-owner claim is decisive — mirrors Manager.OnProcessExited.
        // A stale callback from a manager that Stop()/reconnect already
        // claimed or replaced loses the CompareExchange and must not log,
        // flip state, raise ErrorOccurred, or dispose.
        if (sender is not EmergencyChannelManager crashed ||
            !ReferenceEquals(Interlocked.CompareExchange(ref _manager, null, crashed), crashed))
            return;

        crashed.Started -= OnManagerStarted;
        crashed.Crashed -= OnManagerCrashed;
        try { crashed.Dispose(); } catch { }

        _logger?.Warning("[EmergencyChannelEngine] wgturn-cli crashed (exit {Code})",
            exitCode?.ToString() ?? "?");
        State = EmergencyChannelState.Failed;
        ErrorOccurred?.Invoke($"wgturn-cli exited unexpectedly (exit code: {exitCode?.ToString() ?? "?"})");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { Stop(); } catch { }
        GC.SuppressFinalize(this);
    }
}
