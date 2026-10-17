using System.Diagnostics;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text;
using Serilog;
using VPNRouter.Core.Localization;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public partial class SingBoxManager
{
    public void Start(SingBoxConfig config) =>
        StartWithJson(ConfigGenerator.Serialize(config));

    public void StartWithJson(string configJson)
    {
        var policy = EffectivePolicy;
        using var _ = SingBoxRuntimePolicy.EnterScope(policy);
        policy?.Authorize(SingBoxRuntimeOperation.Start);

        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                _logger.Debug("[SingBoxManager] Start ignored — manager already disposed");
                return;
            }
            StartWithJsonCore(configJson);
        }
    }

    private void StartWithJsonCore(string configJson)
    {
        if (State == SingBoxState.Starting || _handle is { HasExited: false })
        {
            _logger.Warning(
                "[SingBoxManager] StartWithJson ignored - sing-box already {State} (PID {Pid}); use Restart/ReloadConfigJson for reconfigure",
                State,
                Pid);
            return;
        }

        if (State == SingBoxState.Running)
        {
            _logger.Warning("[SingBoxManager] Running state without live handle before StartWithJson; cleaning up stale state first");
            Stop();
        }

        if (!TryAcquireTunOwnership())
        {
            throw new TunOwnershipException(
                "Another VPNRouter instance already owns the TUN adapter. " +
                "Stop the other instance (e.g. disable Windows Service autostart) and try again.");
        }

        var exePath = EffectivePolicy?.SelectedExecutablePath
            ?? (OperatingSystem.IsWindows()
                ? Environment.ExpandEnvironmentVariables(_settings.ExecutablePath)
                : AppPaths.SingBoxExePath);

        ProcessOwnership.ConfiguredExePath = exePath;

        if (!File.Exists(exePath))
        {
            ReleaseTunOwnership();
            throw new FileNotFoundException($"sing-box not found at: {exePath}");
        }

        RotateSingBoxLog();
        _currentConfigPath = WriteJsonToDisk(configJson);

        _logger.Information("[SingBoxManager] Starting sing-box with config: {Config}", _currentConfigPath);

        State = SingBoxState.Starting;
        try
        {
            LaunchProcess(exePath);
            if (_handle is null)
            {
                State = SingBoxState.Failed;
                ReleaseTunOwnership();
            }
        }
        catch
        {
            KillLaunchedProcessBestEffort();
            State = SingBoxState.Failed;
            ReleaseTunOwnership();
            throw;
        }
    }

    // A start failure after the process was spawned (for example a Started subscriber that throws) must not
    // release the TUN lease while sing-box is still running, or another instance could start a second one.
    private void KillLaunchedProcessBestEffort()
    {
        var handle = _handle;
        if (handle is null) return;
        try
        {
            if (!handle.HasExited)
            {
                handle.SuppressExitedEvent();
                handle.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[SingBoxManager] Could not stop the process launched before a start failure");
        }
    }

    private bool TryAcquireTunOwnership()
    {
        if (_ownsTunLock) return !_exactStopUnconfirmed;
        if (!_tunLock.TryAcquireExclusive()) return false;
        _ownsTunLock = true;
        return true;
    }

    private void ReleaseTunOwnership()
    {
        if (!_ownsTunLock) return;
        _tunLock.Release();
        _ownsTunLock = false;
        _exactStopUnconfirmed = false;
    }

    public void Stop()
    {
        lock (_lifecycleGate)
            StopCore();
    }

    private void StopCore()
    {
        LastCrashWasTunOrphan = false;
        LastCrashWasLinuxTunPermissionFailure = false;
        StopInternal(releaseLock: true);
    }

    public void KillWedgedForRecovery()
    {
        lock (_lifecycleGate)
            StopInternal(releaseLock: false);
    }

    private void StopInternal(bool releaseLock)
    {
        // Only one thread runs StopInternal at a time; _stopState resets in finally.
        if (Interlocked.CompareExchange(ref _stopState, 1, 0) != 0)
        {
            _logger.Debug("[SingBoxManager] StopInternal: concurrent call detected (releaseLock={Release}), skipping", releaseLock);
            return;
        }
        // Set before any Kill so a late Exited callback is recognised as part of the intentional stop.
        _stopInProgress = true;
        try
        {
            _logger.Information(
                "[SingBoxManager] Stopping sing-box (state={State}, releaseLock={ReleaseLock})",
                State,
                releaseLock);

            if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            {
                StopUnix(releaseLock);
                return;
            }

            StopWindows(releaseLock);
        }
        finally
        {
            _stopInProgress = false;
            Volatile.Write(ref _stopState, 0);
        }
    }

    private void StopUnix(bool releaseLock)
    {
        if (!_ownsTunLock)
        {
            State = _handle is null ? SingBoxState.Stopped : SingBoxState.Failed;
            return;
        }

        if (OperatingSystem.IsLinux() && !_linuxUsedPkexec)
        {
            StopLinuxCapabilityMode(releaseLock);
            return;
        }

        StopUnixEscalated(releaseLock);
    }

    private void StopLinuxCapabilityMode(bool releaseLock)
    {
        var targetHandle = _handle;
        var capabilityStopped = false;
        try
        {
            if (targetHandle != null)
            {
                if (!targetHandle.HasExited)
                {
                    targetHandle.SuppressExitedEvent();
                    targetHandle.Kill(entireProcessTree: true);
                    try
                    {
                        using var killCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        targetHandle.WaitForExitAsync(killCts.Token).GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                capabilityStopped = targetHandle.HasExited;
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[SingBoxManager] Linux capability-mode exact Stop failed");
            capabilityStopped = false;
        }
        finally
        {
            if (!ReferenceEquals(_handle, targetHandle))
            {
                _logger.Error("[SingBoxManager] Capability stop lost exact-handle ownership");
                capabilityStopped = false;
            }
            else if (capabilityStopped)
            {
                targetHandle?.Dispose();
                _handle = null;
            }
            _exactStopUnconfirmed = !capabilityStopped;
            State = capabilityStopped ? SingBoxState.Stopped : SingBoxState.Failed;
            if (releaseLock && capabilityStopped) ReleaseTunOwnership();
            _logger.Information(
                "[SingBoxManager] Linux capability-mode stop completed={Stopped}",
                capabilityStopped);
        }
    }

    private void StopUnixEscalated(bool releaseLock)
    {
        var unixStopped = false;
        try
        {
            unixStopped = LinuxStopEscalationChain();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[SingBoxManager] Error stopping sing-box");
        }
        finally
        {
            _handle?.Dispose();
            _handle = null;
            _exactStopUnconfirmed = !unixStopped;
            State = unixStopped ? SingBoxState.Stopped : SingBoxState.Failed;
            if (releaseLock && unixStopped) ReleaseTunOwnership();
            _logger.Information(
                "[SingBoxManager] Unix sing-box stop completed={Stopped}",
                unixStopped);
        }
        
    }

    private void StopWindows(bool releaseLock)
    {
        var winTargetHandle = _handle;
        if (winTargetHandle == null)
        {
            _logger.Information(
                "[SingBoxManager] Stop called but sing-box already exited (process=null) — running cleanup-only path");
            FinishCleanupOnlyStop(releaseLock);
            return;
        }

        var alreadyExited = false;
        var probeFailed = false;
        try
        {
            alreadyExited = winTargetHandle.HasExited;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[SingBoxManager] Error probing sing-box process exit state");
            probeFailed = true;
        }

        if (probeFailed)
        {
            _exactStopUnconfirmed = true;
            State = SingBoxState.Failed;
            _logger.Warning(
                "[SingBoxManager] Stop unconfirmed: failed to probe process exit state");
            return;
        }

        if (alreadyExited)
        {
            _logger.Information(
                "[SingBoxManager] Stop called but sing-box already exited (process=HasExited) — running cleanup-only path");
            winTargetHandle.Dispose();
            if (ReferenceEquals(_handle, winTargetHandle))
                _handle = null;
            FinishCleanupOnlyStop(releaseLock);
            return;
        }

        KillWindowsProcess(winTargetHandle, releaseLock);
    }

    private void FinishCleanupOnlyStop(bool releaseLock)
    {
        _exactStopUnconfirmed = false;
        State = SingBoxState.Stopped;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                QueueTunAdapterRemoval("SingBoxManager.StopInternal.early.async");
                if (releaseLock)
                    WaitForQueuedTunAdapterRemoval();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[SingBoxManager] Orphan adapter cleanup failed (non-fatal)");
            }
        }
        if (releaseLock) ReleaseTunOwnership();
    }

    private void KillWindowsProcess(IProcessHandle winTargetHandle, bool releaseLock)
    {
        var winStopped = false;
        try
        {
            try
            {
                winTargetHandle.SuppressExitedEvent();
                winTargetHandle.Kill(entireProcessTree: true);
                try
                {
                    using var killCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    winTargetHandle.WaitForExitAsync(killCts.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[SingBoxManager] Error while stopping process");
            }

            try
            {
                winStopped = winTargetHandle.HasExited;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[SingBoxManager] Error inspecting process exit status after stop");
                winStopped = false;
            }
        }
        finally
        {
            if (!ReferenceEquals(_handle, winTargetHandle))
            {
                _logger.Error("[SingBoxManager] Windows stop lost exact-handle ownership");
                winStopped = false;
            }
            else if (winStopped)
            {
                winTargetHandle.Dispose();
                _handle = null;
            }

            _exactStopUnconfirmed = !winStopped;
            State = winStopped ? SingBoxState.Stopped : SingBoxState.Failed;

            if (winStopped)
            {
                _logger.Information("[SingBoxManager] sing-box stopped");

                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        QueueTunAdapterRemoval("SingBoxManager.StopInternal.killed.async");
                        if (releaseLock)
                            WaitForQueuedTunAdapterRemoval();
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "[SingBoxManager] Orphan adapter cleanup failed (non-fatal)");
                    }
                }

                if (releaseLock) ReleaseTunOwnership();
            }
            else
            {
                _logger.Warning(
                    "[SingBoxManager] Windows exact stop was not confirmed — preserving handle and TUN lease");
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private void QueueTunAdapterRemoval(string context)
    {
        lock (s_tunRemovalGate)
        {
            var previous = s_pendingTunRemoval;
            s_pendingTunRemoval = Task.Run(async () =>
            {
                var previousFailure = await previous.ConfigureAwait(false);
                try
                {
                    await TunAdapterDiagnostics.TryRemoveAdapterAsync(
                        _logger, DefaultTunInterfaceName, context).ConfigureAwait(false);
                    return previousFailure;
                }
                catch (TunAdapterNotReadyException ex)
                {
                    _logger.Warning(ex,
                        "[SingBoxManager] Queued orphan adapter removal did not settle");
                    return previousFailure ?? ex;
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex,
                        "[SingBoxManager] Queued orphan adapter removal failed");
                    return previousFailure;
                }
            });
        }
    }

    [SupportedOSPlatform("windows")]
    private void WaitForQueuedTunAdapterRemoval()
    {
        while (true)
        {
            Task<TunAdapterNotReadyException?> pending;
            lock (s_tunRemovalGate)
                pending = s_pendingTunRemoval;

            var failure = pending.GetAwaiter().GetResult();

            if (failure != null)
            {
                if (string.IsNullOrWhiteSpace(failure.InstanceId))
                    throw failure;

                TunAdapterDiagnostics.WaitForExactPnpRemovalSettledAsync(
                        _logger, failure.InstanceId, DefaultTunInterfaceName,
                        "SingBoxManager.LaunchProcess.queued")
                    .GetAwaiter().GetResult();
            }

            lock (s_tunRemovalGate)
            {
                // A cleanup may have been queued while awaiting; join the new tail too so launch never races a pnputil removal.
                if (!ReferenceEquals(s_pendingTunRemoval, pending))
                    continue;

                if (failure != null)
                    s_pendingTunRemoval = Task.FromResult<TunAdapterNotReadyException?>(null);
                return;
            }
        }
    }

    public void Restart()
    {
        var policy = EffectivePolicy;
        using var _ = SingBoxRuntimePolicy.EnterScope(policy);
        policy?.Authorize(SingBoxRuntimeOperation.Restart);

        lock (_lifecycleGate)
            RestartCore();
    }

    private bool RestartCore()
    {
        // A stale HealthMonitor restart continuation can arrive after Dispose: never relaunch a disposed manager.
        if (Volatile.Read(ref _disposed) != 0)
        {
            _logger.Debug("[SingBoxManager] Restart ignored — manager already disposed");
            return false;
        }
        if (!_ownsTunLock)
        {
            _logger.Warning("[SingBoxManager] Restart ignored — manager does not own the TUN lease");
            return false;
        }
        _logger.Information("[SingBoxManager] Restarting sing-box");
        State = SingBoxState.Restarting;

        // Set before StopInternal so a late Exited event during Restart is not reported as a crash.
        _restartInProgress = true;
        var oldHandle = _handle;
        try
        {
            StopInternal(releaseLock: false);
            if (State != SingBoxState.Stopped || _exactStopUnconfirmed)
            {
                _logger.Error(
                    "[SingBoxManager] Restart aborted: exact stop was not confirmed (state={State})",
                    State);
                return false;
            }
            State = SingBoxState.Restarting;

            if (OperatingSystem.IsWindows())
            {
                try { Thread.Sleep(750); } catch { }
            }

            var exePath = EffectivePolicy?.SelectedExecutablePath
                ?? (OperatingSystem.IsWindows()
                    ? Environment.ExpandEnvironmentVariables(_settings.ExecutablePath)
                    : AppPaths.SingBoxExePath);
            LaunchProcess(exePath);
            if (_handle is null)
            {
                State = SingBoxState.Failed;
                ReleaseTunOwnership();
                return false;
            }

            var newHandle = _handle;
            return newHandle != null
                && !ReferenceEquals(newHandle, oldHandle)
                && State == SingBoxState.Running
                && !newHandle.HasExited;
        }
        catch
        {
            KillLaunchedProcessBestEffort();
            State = SingBoxState.Failed;
            ReleaseTunOwnership();
            throw;
        }
        finally
        {
            _restartInProgress = false;
        }
    }

    private void RotateSingBoxLog()
    {
        try
        {
            var logPath = AppPaths.SingBoxLogPath;

            if (!File.Exists(logPath))
                return;

            var fileInfo = new FileInfo(logPath);
            if (fileInfo.Length <= MaxLogSizeBytes)
                return;

            var oldPath = Path.ChangeExtension(logPath, ".old.log");
            if (File.Exists(oldPath))
                File.Delete(oldPath);

            File.Move(logPath, oldPath);
            _logger.Information("[SingBoxManager] Rotated singbox.log ({Size:F1} MB → singbox.old.log)",
                fileInfo.Length / 1024.0 / 1024.0);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[SingBoxManager] Failed to rotate singbox.log");
        }
    }

    private bool HasNetCapability(string exePath)
    {
        if (!OperatingSystem.IsLinux()) return false;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return false;

        try
        {
            var psi = new ProcessStartInfo("getcap")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add(exePath);
            using var p = Process.Start(psi);
            if (p == null) return false;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            if (!p.HasExited || p.ExitCode != 0) return false;

            if (string.IsNullOrWhiteSpace(output)) return false;
            if (output.IndexOf("cap_net_admin", StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (output.IndexOf("cap_net_bind_service", StringComparison.OrdinalIgnoreCase) < 0) return false;

            return true;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[SingBoxManager] getcap probe failed — falling back to pkexec path");
            return false;
        }
    }

    internal static bool TryColocateCronet(string singBoxExePath, string bundledDir, ILogger? logger)
    {
        var libName = OperatingSystem.IsWindows() ? "libcronet.dll"
                    : OperatingSystem.IsLinux()   ? "libcronet.so"
                    : null;
        if (libName == null) return false;
        try
        {
            var src = Path.Combine(bundledDir, libName);
            if (!File.Exists(src)) return false;
            var destDir = Path.GetDirectoryName(singBoxExePath);
            if (string.IsNullOrEmpty(destDir)) return false;
            var dest = Path.Combine(destDir, libName);
            if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                return true;
            if (File.Exists(dest) && new FileInfo(dest).Length == new FileInfo(src).Length)
                return true;
            File.Copy(src, dest, overwrite: true);
            logger?.Information("[SingBoxManager] Co-located {Lib} next to sing-box at {Dest}", libName, dest);
            return true;
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[SingBoxManager] Could not co-locate {Lib} next to sing-box — NaiveProxy may fail to start", libName);
            return false;
        }
    }

    private void LaunchProcess(string exePath)
    {
        LastCrashWasTunOrphan = false;
        LastCrashWasLinuxTunPermissionFailure = false;
        lock (_capturedStderrLock)
        {
            _capturedStderrCount = 0;
            Array.Clear(_capturedStderr, 0, _capturedStderr.Length);
        }

        TryColocateCronet(exePath, AppContext.BaseDirectory, _logger);

        if (OperatingSystem.IsWindows())
        {
            WaitForQueuedTunAdapterRemoval();
            TunAdapterDiagnostics
                .PreStartCleanupAsync(_logger, "SingBoxManager.LaunchProcess")
                .GetAwaiter().GetResult();
        }

        string spawnExe;
        IReadOnlyList<string> spawnArgs;

        if (OperatingSystem.IsMacOS())
        {
            spawnExe = "/usr/bin/sudo";
            spawnArgs = new[] { exePath, "run", "-c", _currentConfigPath };
        }
        else if (OperatingSystem.IsLinux())
        {
            var blocker = LinuxRuntimeEnvironment.GetTunPrivilegeBlocker();
            if (blocker != null)
            {
                _linuxUsedPkexec = false;
                throw new InvalidOperationException(
                    $"{Strings.LinuxTunSandboxUnsupported} ({blocker})");
            }

            if (HasNetCapability(exePath))
            {
                _logger.Information("[SingBoxManager] Linux: launching as user (CAP_NET_ADMIN present, no pkexec needed)");
                _linuxUsedPkexec = false;
                spawnExe = exePath;
                spawnArgs = new[] { "run", "-c", _currentConfigPath };
            }
            else if (EffectivePolicy != null)
            {
                // A selected runtime is never escalated. Missing capability fails closed.
                _linuxUsedPkexec = false;
                throw new SingBoxRuntimePolicyException(
                    SingBoxRuntimeOperation.Start,
                    SingBoxRuntimeFailure.PrerequisiteUnavailable);
            }
            else
            {
                _logger.Information("[SingBoxManager] Linux: falling back to pkexec (sing-box lacks CAP_NET_ADMIN — install via .deb or run 'sudo setcap cap_net_admin,cap_net_bind_service=+eip {Exe}' once)",
                    exePath);
                _linuxUsedPkexec = true;
                spawnExe = LinuxRuntimeEnvironment.ResolvePkexec()
                    ?? throw new InvalidOperationException(Strings.LinuxPkexecUnavailable);
                spawnArgs = new[] { exePath, "run", "-c", _currentConfigPath };
            }
        }
        else
        {
            spawnExe = exePath;
            spawnArgs = new[] { "run", "-c", _currentConfigPath };
        }

        var request = new ProcessRequest(
            ExecutablePath: spawnExe,
            Arguments: spawnArgs,
            CaptureStdout: true,
            CaptureStderr: true);

        // Last-moment disposal re-check at the spawn point: Dispose can race past the entry guard.
        if (Volatile.Read(ref _disposed) != 0)
        {
            _logger.Debug("[SingBoxManager] LaunchProcess aborted — manager disposed before spawn");
            return;
        }

        _handle = _runner.Start(request);

        var startedHandle = _handle;
        startedHandle.OutputLine += (_, line) =>
        {
            if (!string.IsNullOrEmpty(line))
                _logger.Debug("[sing-box] {Line}", line);
        };
        startedHandle.ErrorLine += (_, line) =>
        {
            if (!string.IsNullOrEmpty(line))
            {
                _logger.Warning("[sing-box] {Line}", line);
                lock (_capturedStderrLock)
                {
                    _capturedStderr[_capturedStderrCount % StderrBufferSize] = line;
                    _capturedStderrCount++;
                }
            }
        };
        startedHandle.Exited += (_, code) => OnProcessExited(code);

        State = SingBoxState.Running;
        _logger.Information("[SingBoxManager] sing-box started (PID {Pid})", startedHandle.Pid);
        Started?.Invoke(startedHandle.Pid);
    }

    private static string WriteJsonToDisk(string json)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            AppPaths.EnsurePrivateUnixDirectory(AppPaths.DataDir);
            AppPaths.EnsurePrivateUnixDirectory(AppPaths.ConfigDir);
        }
        else
            Directory.CreateDirectory(AppPaths.ConfigDir);

        var path = AppPaths.CurrentConfigPath;
        AppPaths.WritePrivateText(path, json);
        return path;
    }

}
