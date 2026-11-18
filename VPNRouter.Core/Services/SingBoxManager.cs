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
    public int? Pid => _handle is { HasExited: false } handle ? handle.Pid : null;
    internal bool HasActiveTunnel => Pid.HasValue ||
        (_ownsTunLock && (OperatingSystem.IsMacOS() || _linuxUsedPkexec) &&
            ProcessOwnership.FindOwnedSingBox(ProcessOwnership.ConfiguredExePath) != null) ||
        IsRunning();
    internal IProcessHandle? OwnedProcessHandle => _handle;
    internal bool ExactStopUnconfirmed => _exactStopUnconfirmed;
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

    internal SingBoxManager(SingBoxSettings settings, (Uri Endpoint, string? Secret) controller,
        ILogger? logger = null, IHttpClient? http = null) : this(settings, logger, http) =>
        SetClashController(controller);

    public SingBoxManager(SingBoxSettings settings, ILogger? logger = null, IHttpClient? http = null, IProcessRunner? runner = null)
    {
        _settings = settings;
        _logger = logger ?? Log.Logger;
        _http = http ?? LocalControllerHttp;
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

    private string RuntimeExecutablePath => Path.GetFullPath(
        EffectivePolicy?.SelectedExecutablePath ?? AppPaths.DeployedSingBoxExePath(_settings));

    internal Task<bool> CheckRuleSetAsync(string path, CancellationToken ct) =>
        CheckCandidateAsync(RuleSetFile.BuildCheckConfig(path), ct);

    internal async Task<bool> CheckCandidateAsync(string json, CancellationToken ct, SingBoxSettings? nextDeployment = null)
    {
        EffectivePolicy?.Authorize(SingBoxRuntimeOperation.Verify);
        var executable = nextDeployment == null ? RuntimeExecutablePath :
            EffectivePolicy?.SelectedExecutablePath ?? AppPaths.SingBoxDeploymentSource(
                AppPaths.BundledSingBoxExePath, AppPaths.DeployedSingBoxExePath(nextDeployment));
        var check = new ProcessRequest(Path.GetFullPath(executable), ["check", "-c", "stdin"], StdinInput: json,
            Timeout: TimeSpan.FromSeconds(15));
        var result = await _runner.RunAsync(check, ct).ConfigureAwait(false);
        return result.ExitCode == 0 && !result.TimedOut;
    }

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
            if (forceRestart) return RestartWithConfig(configJson);

            _currentConfigPath = WriteJsonToDisk(configJson);
            if (TryHotReload()) return true;

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

    public void Dispose() => DisposeCore(requireConfirmedStop: false);

    internal bool TryRetireForReplacement() => DisposeCore(requireConfirmedStop: true);

    internal bool TryRetireForReplacement(IProcessHandle? expectedChild, CancellationToken ct) =>
        DisposeCore(requireConfirmedStop: true, expectedChild, ct);

    private bool DisposeCore(bool requireConfirmedStop, IProcessHandle? expectedChild = null, CancellationToken ct = default)
    {
        lock (_lifecycleGate)
        {
            if (ct.IsCancellationRequested || (expectedChild != null &&
                (!ReferenceEquals(expectedChild, _handle) || expectedChild.QueryHasExited() != false))) return false;
            if (Volatile.Read(ref _disposed) != 0) return true;

            StopInternal(releaseLock: true);
            if (_ownsTunLock || (requireConfirmedStop && (_exactStopUnconfirmed ||
                (_handle is { } handle && handle.QueryHasExited() != true))))
            {
                _logger.Warning("[SingBoxManager] Retaining manager because exact stop was not confirmed (state={State})", State);
                return false;
            }

            Volatile.Write(ref _disposed, 1);
            AppDomain.CurrentDomain.ProcessExit -= OnAppDomainProcessExit;
            LastCrashWasTunOrphan = false;
            LastCrashWasLinuxTunPermissionFailure = false;
            _handle?.Dispose();
            _handle = null;
            return true;
        }
    }
}

public class ProcessMetrics
{
    public long MemoryMb { get; init; }
    public TimeSpan CpuTime { get; init; }
    public DateTime? StartTime { get; init; }
}
