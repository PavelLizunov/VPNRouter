using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Serilog;

namespace VPNRouter.Core.Services;

public class ZapretManager : IDisposable
{
    private readonly ILogger _logger;
    private readonly IProcessRunner _runner;
    private readonly object _processGate = new();
    private IProcessHandle? _handle;
    private Action? _restartStrategy;
    private bool _disposed;
    private Task? _serviceStop;
    internal TimeSpan ServiceStopRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    internal static IProcessRunner Runner { get; set; } = new ProcessRunner();

    internal Func<bool> HasDriverConflict { get; init; } = ZapretActions.HasDriverConflict;

    internal bool HasOwnedProcess
    {
        get { lock (_processGate) return _handle != null && _handle.QueryHasExited() != true; }
    }

    public bool IsRunning => Pid.HasValue;
    public int? Pid
    {
        get { lock (_processGate) return _handle is { } handle && handle.QueryHasExited() == false ? handle.Pid : null; }
    }

    public static bool IsWinwsRunning() => WinwsPid.HasValue;

    public static int? WinwsPid
    {
        get
        {
            if (!OperatingSystem.IsWindows() || !ZapretUpdater.IsInstalled()) return null;
            var expected = Path.Combine(ZapretUpdater.PackageDisplayPath, "bin", "winws.exe");
            var processes = Process.GetProcessesByName("winws");
            try
            {
                foreach (var process in processes)
                    if (ProcessOwnership.IsSamePath(ProcessOwnership.ImagePathOf(process), expected)) return process.Id;
                return null;
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }
    }

    public event Action? ImmediateExitDetected;

    public static readonly TimeSpan ImmediateExitWindow = TimeSpan.FromSeconds(2);

    public ZapretManager(ILogger? logger = null, IProcessRunner? runner = null)
    {
        _logger = logger ?? Log.Logger;
        _runner = runner ?? Runner;
    }

    public void StartFromBat(string batPath, string parsedArgs) => StartInstalledStrategy(batPath);

    private void StartInstalledStrategy(string batPath) =>
        ZapretRuntimeDirectory.WithPackage(_ => StartWinws(ResolveInstalledStrategyArguments(batPath),
            () => StartInstalledStrategy(batPath)));

    public static string[] ResolveInstalledStrategyArguments(string batPath)
    {
        // Read the strategy from the installed package even when the caller has cached its arguments.
        var fullPath = Path.GetFullPath(batPath);
        var installed = ZapretUpdater.ParseStrategies().FirstOrDefault(strategy =>
            string.Equals(strategy.BatPath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (installed is null)
            throw new ArgumentException("Select a strategy from the installed Zapret package.", nameof(batPath));

        return ExpandArguments(installed.Arguments, fromPackage: true);
    }

    public void Start(string args) =>
        ZapretRuntimeDirectory.WithPackage(_ => StartWinws(ExpandArguments(args, fromPackage: false), () => Start(args)));

    internal static string[] ExpandArguments(string args, bool fromPackage)
    {
        var tokens = FirewallManager.SplitShellArgs(args);
        if (tokens.Length == 0) throw new ArgumentException("Select a Zapret strategy or enter its options.", nameof(args));
        var root = ZapretUpdater.ZapretDir;
        var bin = Path.Combine(root, "bin") + Path.DirectorySeparatorChar;
        var lists = Path.Combine(root, "lists") + Path.DirectorySeparatorChar;
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            var split = token.IndexOf('=');
            var option = split < 0 ? token : token[..split];
            var value = split < 0 ? string.Empty : token[(split + 1)..];
            if (!fromPackage)
            {
                bool supported = option is "--new" or "--wf-tcp" or "--wf-udp" or "--wf-l3"
                    or "--filter-tcp" or "--filter-udp" or "--filter-l3" or "--filter-l7"
                    or "--hostlist" or "--hostlist-exclude" or "--hostlist-domains" or "--hostlist-exclude-domains"
                    or "--ip-id" or "--ipset" or "--ipset-exclude"
                    or "--dpi-desync" || option.StartsWith("--dpi-desync-", StringComparison.Ordinal);
                var file = value.StartsWith("%BIN%", StringComparison.OrdinalIgnoreCase) ? value[5..]
                    : value.StartsWith("%LISTS%", StringComparison.OrdinalIgnoreCase) ? value[7..] : value;
                if (!supported || file is "." or ".." || !file.All(c =>
                        char.IsAsciiLetterOrDigit(c) || c is ',' or '.' or '-' or '_' or '+' or '='))
                    throw new ArgumentException("This custom winws option or file path is unsupported. Select an installed strategy or use a file from BIN or LISTS.", nameof(args));
            }
            tokens[index] = token
                .Replace("%BIN%", bin, StringComparison.OrdinalIgnoreCase)
                .Replace("%LISTS%", lists, StringComparison.OrdinalIgnoreCase);
        }
        return tokens;
    }

    private void StartWinws(string[] arguments, Action restartStrategy)
    {
        var exe = ZapretUpdater.WinwsExePath;
        if (!File.Exists(exe))
            throw new FileNotFoundException("Download Zapret in VPNRouter to install the protected runtime.", exe);
        lock (_processGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (HasDriverConflict())
                throw new InvalidOperationException(Localization.Strings.ZapretServiceOutcome(ZapretActions.ServiceApply.Conflict));
            if (!StopOwnedProcess())
                throw new InvalidOperationException(Localization.Strings.ZapretStopPending);

            _handle = _runner.Start(new ProcessRequest(exe, arguments, ZapretUpdater.BinDir,
                EnvironmentOverrides: OperatingSystem.IsWindows() ? ZapretRuntimeDirectory.ProcessEnvironment() : null,
                CaptureStdout: false, CaptureStderr: false) { ClearInheritedEnvironment = OperatingSystem.IsWindows() });
            _restartStrategy = restartStrategy;
            var startedAt = DateTime.UtcNow;
            _handle.Exited += (_, code) => DetectImmediateExit(DateTime.UtcNow - startedAt, code);
            _logger.Information("[Zapret] Started winws (PID {Pid})", _handle.Pid);
        }
    }

    private void DetectImmediateExit(TimeSpan runtime, int? exitCode)
    {
        if (runtime >= ImmediateExitWindow) return;
        if (exitCode == 0) return;

        _logger.Warning(
            "[Zapret] Immediate exit detected (code={Code}, runtime={Ms}ms) — surfaced AV whitelist hint",
            exitCode, (int)runtime.TotalMilliseconds);
        try { ImmediateExitDetected?.Invoke(); }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[Zapret] ImmediateExitDetected handler threw");
        }
    }

    public static string BuildLegacyArgs(string strategy, int targetPort = 443)
    {
        return strategy switch
        {
            "multisplit" =>
                $"--wf-tcp={targetPort},8443 --wf-l3=ipv4 " +
                $"--dpi-desync=multisplit --dpi-desync-split-seqovl=2 --dpi-desync-split-pos=2",

            "fake+multisplit" =>
                $"--wf-tcp={targetPort},8443 --wf-l3=ipv4 " +
                $"--dpi-desync=fake,multisplit --dpi-desync-ttl=2 " +
                $"--dpi-desync-split-seqovl=2 --dpi-desync-split-pos=2 " +
                $"--dpi-desync-fake-tls=0x00000000000000000000",

            _ => throw new ArgumentException($"Unknown legacy strategy: {strategy}")
        };
    }

    // Keep only the application strategy selection. A retry revalidates the installed package and driver.
    internal Action? SuspendOwnedStrategy()
    {
        lock (_processGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_handle == null) return null;
            var exited = _handle.QueryHasExited() ?? throw new InvalidOperationException(Localization.Strings.ZapretStopPending);
            var restart = exited ? null : _restartStrategy;
            if (!StopOwnedProcess()) throw new InvalidOperationException(Localization.Strings.ZapretStopPending);
            return restart;
        }
    }

    public void Stop() => TryStop();

    internal bool TryStop()
    {
        lock (_processGate) return StopOwnedProcess();
    }

    private bool StopOwnedProcess()
    {
        var handle = _handle;
        if (handle == null) return true;
        var exited = handle.QueryHasExited();
        if (exited == null) return false;
        if (exited == false)
        {
            try
            {
                handle.SuppressExitedEvent();
                handle.Kill(entireProcessTree: false);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                handle.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[Zapret] Could not confirm winws exit");
            }
            if (handle.QueryHasExited() != true)
            {
                _logger.Warning("[Zapret] winws is still running (PID {Pid})", handle.Pid);
                return false;
            }
        }
        try { handle.DisposeAfterExit(); }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[Zapret] Could not release the owned winws handle");
            return false;
        }
        _handle = null;
        _restartStrategy = null;
        return true;
    }

    internal Task StopForServiceAsync()
    {
        lock (_processGate)
        {
            _disposed = true; // A queued startup must not replace the process being drained.
            if (_serviceStop is { IsCompleted: false })
            {
                // A second managed stop can retry a refusal while the first waits for exit.
                if (_handle?.QueryHasExited() == false)
                {
                    try { _handle.Kill(entireProcessTree: false); }
                    catch (Exception ex) { _logger.Warning(ex, "[Zapret] Service stop retry failed"); }
                }
                return _serviceStop;
            }
            return _serviceStop = DrainServiceProcessAsync();
        }
    }

    private async Task DrainServiceProcessAsync()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (TryDispose()) return;
            if (attempt < 2) await Task.Delay(ServiceStopRetryDelay).ConfigureAwait(false);
        }

        IProcessHandle? remaining;
        lock (_processGate) remaining = _handle;
        if (remaining == null) return;
        _logger.Warning("[Zapret] Service stop is pending until its winws process exits");
        // A host deadline is not evidence of exit. Normally the single native wait stays asleep.
        // If Windows refuses the wait itself, retain the handle and pace recovery attempts.
        while (true)
        {
            try
            {
                await remaining.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                break;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[Zapret] Could not wait for the owned winws process");
                await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                if (TryDispose()) return;
            }
        }
        lock (_processGate)
        {
            if (!ReferenceEquals(_handle, remaining)) return;
            remaining.DisposeAfterExit();
            _handle = null;
            _restartStrategy = null;
        }
    }

    internal bool TryDispose()
    {
        lock (_processGate)
        {
            if (!StopOwnedProcess()) return false;
            _disposed = true;
            return true;
        }
    }

    public void Dispose()
    {
        if (!TryDispose()) throw new InvalidOperationException(Localization.Strings.ZapretStopPending);
    }
}
