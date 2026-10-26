using System.Diagnostics;
using System.Net.Http;
using System.Text;
using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public enum SingBoxState { Stopped, Starting, Running, Restarting, Failed }

public partial class SingBoxManager : IDisposable
{
    private readonly SingBoxSettings _settings;
    private readonly ILogger _logger;

    private readonly IProcessRunner _runner;
    private IProcessHandle? _handle;
    private string _currentConfigPath = string.Empty;
    // int, not bool: Dispose uses Interlocked.CompareExchange for single execution.
    private int _disposed;
    private TunOwnershipLock _tunLock;

    private int _stopState;

    // Process-wide ordered queue: TUN removal can outlive the manager, and a reconnect must not race a prior pnputil removal.
    private static readonly object s_tunRemovalGate = new();
    private static Task<TunAdapterNotReadyException?> s_pendingTunRemoval =
        Task.FromResult<TunAdapterNotReadyException?>(null);

    internal static void ResetTunRemovalQueueForTests()
    {
        lock (s_tunRemovalGate)
        {
            s_pendingTunRemoval = Task.FromResult<TunAdapterNotReadyException?>(null);
        }
    }

    internal static IProcessRunner Runner { get; set; } = new ProcessRunner();

    private bool _linuxUsedPkexec;
    private bool _ownsTunLock;
    private SingBoxRuntimePolicy? _policy;

    // Sticky: production denial wins, the first fixture stays, a later fixture cannot replace it.
    private SingBoxRuntimePolicy? EffectivePolicy => SingBoxRuntimePolicy.Capture(ref _policy);
    private bool _exactStopUnconfirmed;
    private readonly object _lifecycleGate = new();

    private readonly IHttpClient _http;

    public SingBoxState State { get; private set; } = SingBoxState.Stopped;
    public int? Pid => _handle != null && !_handle.HasExited ? _handle.Pid : null;
    internal IProcessHandle? OwnedProcessHandle => _handle;
    public event EventHandler? Crashed;
    public event Action<int>? Started;

    private const int StderrBufferSize = 50;
    private readonly string[] _capturedStderr = new string[StderrBufferSize];
    private int _capturedStderrCount;
    private readonly object _capturedStderrLock = new();

    private volatile bool _restartInProgress;

    private volatile bool _stopInProgress;

    public bool LastCrashWasTunOrphan { get; private set; }

    internal bool LastCrashWasLinuxTunPermissionFailure { get; private set; }

    public SingBoxManager(SingBoxSettings settings, ILogger? logger = null, IHttpClient? http = null, IProcessRunner? runner = null)
    {
        _settings = settings;
        _logger = logger ?? Log.Logger;
        _http = http ?? PolicyHttpClient.Shared;
        _runner = runner ?? Runner;
        _tunLock = TunOwnershipLock.Instance(_logger);
        _policy = SingBoxRuntimePolicy.Current;

        AppDomain.CurrentDomain.ProcessExit += OnAppDomainProcessExit;
    }

    private void OnAppDomainProcessExit(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _disposed) == 0)
            _tunLock.Dispose();
    }

    private const long MaxLogSizeBytes = 10 * 1024 * 1024;

    public void ReloadConfigJson(string configJson, bool forceRestart = false) =>
        ReloadConfigJsonWithResult(configJson, forceRestart);

    internal bool ReloadConfigJsonWithResult(string configJson, bool forceRestart = false)
    {
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                _logger.Debug("[SingBoxManager] ReloadConfigJson ignored — manager already disposed");
                return false;
            }
            if (!_ownsTunLock)
            {
                _logger.Warning("[SingBoxManager] ReloadConfigJson ignored — manager does not own valid TUN lease");
                return false;
            }

            if (_exactStopUnconfirmed)
            {
                StopInternal(releaseLock: false);
                if (State != SingBoxState.Stopped)
                    return false;

                forceRestart = true;
            }

            _logger.Information("[SingBoxManager] Reloading config{Mode}",
                forceRestart ? " (force restart, no hot-reload attempt)" : "");
            _currentConfigPath = WriteJsonToDisk(configJson);

            if (!forceRestart && TryHotReload())
                return true;

            if (!forceRestart)
                _logger.Warning("[SingBoxManager] Hot-reload unavailable — restarting sing-box");

            return RestartCore();
        }
    }

    public bool TryReloadConfigJson(string configJson)
    {
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                _logger.Debug("[SingBoxManager] TryReloadConfigJson ignored — manager already disposed");
                return false;
            }
            if (!_ownsTunLock || _exactStopUnconfirmed)
            {
                _logger.Warning("[SingBoxManager] TryReloadConfigJson ignored — manager does not own valid TUN lease");
                return false;
            }

            _logger.Information("[SingBoxManager] Attempting hot-reload (no restart fallback)");
            _currentConfigPath = WriteJsonToDisk(configJson);
            return TryHotReload();
        }
    }

    private const string DefaultTunInterfaceName = "VPNRouter-TUN";

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
        AppDomain.CurrentDomain.ProcessExit -= OnAppDomainProcessExit;
        LastCrashWasTunOrphan = false;
        LastCrashWasLinuxTunPermissionFailure = false;
        Stop();
        if (!_ownsTunLock)
            _handle?.Dispose();
        if (_ownsTunLock)
        {
            _logger.Warning(
                "[SingBoxManager] Dispose preserved TUN ownership because exact stop was not confirmed (state={State})",
                State);
            AppDomain.CurrentDomain.ProcessExit += OnAppDomainProcessExit;
            Volatile.Write(ref _disposed, 0);
        }
    }
}

public class ProcessMetrics
{
    public long MemoryMb { get; init; }
    public TimeSpan CpuTime { get; init; }
    public DateTime? StartTime { get; init; }
}
