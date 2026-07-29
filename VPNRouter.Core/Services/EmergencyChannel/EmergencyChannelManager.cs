using System.Diagnostics;
using System.Text;
using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services.EmergencyChannel;

/// <summary>
/// r9 Phase 2 — process lifecycle for <c>wgturn-cli.exe</c>. Mirrors the
/// shape of <see cref="SingBoxManager"/>: spawn / kill / observe-exit
/// with the same intentional-stop pattern (set
/// <c>EnableRaisingEvents = false</c> BEFORE <c>Kill()</c> so the
/// <see cref="Process.Exited"/> callback doesn't fire as a false crash).
///
/// <para>StdOut / StdErr are tee'd to a dedicated log file
/// (<c>%ProgramData%\VPNRouter\logs\wgturn-cli.log</c>) so wgturn-cli's
/// own structured log lines stay isolated from the main vpnrouter.log.
/// On crash, the <see cref="Crashed"/> event fires and the engine
/// transitions to <see cref="EmergencyChannelState.Failed"/>.</para>
///
/// <para>Phase-2 is desktop-only — Linux / macOS variants are placeholders
/// (the same exe path resolver works cross-platform but the binary
/// itself isn't shipped on non-Windows builds yet; that's Phase 1+
/// for those platforms).</para>
/// </summary>
public class EmergencyChannelManager : IDisposable
{
    private readonly ILogger _logger;
    private readonly string _exePath;
    private readonly string _logPath;

    private Process? _process;
    private StreamWriter? _logWriter;
    private readonly object _logLock = new();
    private bool _disposed;

    /// <summary>State updated by <see cref="LaunchProcess"/> /
    /// <see cref="Stop"/> / <see cref="OnProcessExited"/>. Read-only
    /// from the outside; the Engine consumes <see cref="Started"/> +
    /// <see cref="Crashed"/> events instead.</summary>
    public EmergencyChannelState State { get; private set; } = EmergencyChannelState.Disconnected;

    /// <summary>PID of the running wgturn-cli, or null when not running.</summary>
    public int? Pid
    {
        get
        {
            // Single Volatile snapshot: a concurrent Stop/DisposeProcess
            // Exchange must not null the field between the HasExited and Id
            // reads, and we must never observe a successor process.
            var p = Volatile.Read(ref _process);
            return p is { HasExited: false } ? p.Id : null;
        }
    }

    /// <summary>Fires after a successful spawn. Carries the PID so the
    /// engine can persist it for status display / debugging.</summary>
    public event Action<int>? Started;

    /// <summary>Fires when the process exits *unintentionally* (i.e.
    /// not via <see cref="Stop"/>). The engine maps this to
    /// <see cref="EmergencyChannelState.Failed"/>.</summary>
    public event EventHandler<int?>? Crashed;

    public EmergencyChannelManager(ILogger? logger = null)
        : this(AppPaths.WgturnCliExePath, AppPaths.WgturnCliLogPath, logger) { }

    /// <summary>Internal ctor used by tests to point at a stub binary
    /// + log path. Public callers should use the parameterless overload.</summary>
    internal EmergencyChannelManager(string exePath, string logPath, ILogger? logger = null)
    {
        _exePath = exePath;
        _logPath = logPath;
        _logger = logger ?? Log.Logger;
    }

    /// <summary>Test seam — lets unit tests substitute a custom args
    /// string so they can spawn a stub binary (cmd.exe ping /
    /// /usr/bin/sleep) without the production
    /// <c>connect-url ... --vk-link ...</c> shape causing argument
    /// errors. Production callers leave this null and the default
    /// shape is used.</summary>
    internal Func<EmergencyChannelConfig, string>? ArgsBuilderOverride { get; set; }

    /// <summary>Spawn <c>wgturn-cli.exe connect-url &lt;url&gt; --vk-link &lt;link&gt;</c>.
    /// Throws <see cref="FileNotFoundException"/> if the binary isn't
    /// installed (Phase 1 chip dropped it during install) or
    /// <see cref="InvalidOperationException"/> if a previous process
    /// is still alive.</summary>
    public void Start(EmergencyChannelConfig config)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));
        if (string.IsNullOrWhiteSpace(config.WgturnUrl))
            throw new ArgumentException("WgturnUrl is required", nameof(config));
        if (string.IsNullOrWhiteSpace(config.VkLink))
            throw new ArgumentException("VkLink is required at start time", nameof(config));

        if (State == EmergencyChannelState.Connecting || State == EmergencyChannelState.Connected)
        {
            _logger.Warning("[EmergencyChannelManager] Start called while already running (state {State}, PID {Pid}) — stopping first",
                State, Pid);
            Stop();
        }

        // Mark intent BEFORE the binary check so a missing-binary throw
        // lands in the Failed state — callers (Engine + UI) read state
        // to decide whether to surface a "couldn't connect" badge.
        State = EmergencyChannelState.Connecting;
        try
        {
            if (!File.Exists(_exePath))
                throw new FileNotFoundException(
                    $"wgturn-cli not found at: {_exePath}. " +
                    "Install Phase 1 chip (wgturn-cli.exe in build.ps1) drops this binary at install time. " +
                    "For local testing, place wgturn-cli.exe at the path manually.",
                    _exePath);

            OpenLogWriter();
            LaunchProcess(config);
        }
        catch
        {
            CloseLogWriter();
            State = EmergencyChannelState.Failed;
            throw;
        }
    }

    /// <summary>
    /// Intentional stop. Sets <c>EnableRaisingEvents = false</c> before
    /// <c>Kill()</c> so the <see cref="Process.Exited"/> callback never
    /// fires as a false crash event — same pattern as
    /// <see cref="SingBoxManager.Stop"/>. Idempotent: safe to call when
    /// the process is already dead or never started.
    /// </summary>
    public void Stop()
    {
        _logger.Information("[EmergencyChannelManager] Stopping wgturn-cli (PID {Pid})", Pid);

        // Atomic claim — exactly one of Stop/OnProcessExited/Dispose disposes.
        var p = Interlocked.Exchange(ref _process, null);
        if (p == null || p.HasExited)
        {
            try { p?.Dispose(); } catch { }
            State = EmergencyChannelState.Disconnected;
            CloseLogWriter();
            return;
        }

        p.EnableRaisingEvents = false;

        try
        {
            p.Kill(entireProcessTree: true);
            p.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[EmergencyChannelManager] Error while stopping wgturn-cli");
        }
        finally
        {
            try { p.Dispose(); } catch { }
            State = EmergencyChannelState.Disconnected;
            CloseLogWriter();
            _logger.Information("[EmergencyChannelManager] wgturn-cli stopped");
        }
    }

    private void LaunchProcess(EmergencyChannelConfig config)
    {
        var psi = BuildProcessStartInfo(config);

        DisposeProcess();

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += OnStdLine;
        process.ErrorDataReceived += OnStdLine;
        // Bind to the exact instance so a stale callback can't touch a successor.
        process.Exited += (_, _) => OnProcessExited(process);
        _process = process;

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        State = EmergencyChannelState.Connected;
        _logger.Information("[EmergencyChannelManager] wgturn-cli started (PID {Pid})", process.Id);
        Started?.Invoke(process.Id);
    }

    /// <summary>Atomically claim and dispose <see cref="_process"/> (exactly-once).</summary>
    private void DisposeProcess()
    {
        var p = Interlocked.Exchange(ref _process, null);
        try { p?.Dispose(); } catch { }
    }

    // wgturn-cli arg shape (cmd/wgturn-cli/connect_url.go):
    //   wgturn-cli connect-url -url <wgturn://...> -vk-link <https://vk.com/call/join/...>
    //
    // Both forms are accepted by Go's flag package:
    //   connect-url -url <URL> -vk-link <LINK>     ← we use this (explicit, safest)
    //   connect-url <URL> -vk-link <LINK>          ← AVOID: Go's flag.Parse stops
    //                                                 at first non-flag positional,
    //                                                 so -vk-link gets eaten as a
    //                                                 second positional and the
    //                                                 URL/link pair is lost.
    // Verified the explicit-flag form works in live test
    // (plans/r9-actionable-without-stas.md Phase 2 verify step).
    //
    // SEC-3 (audit R06): ArgumentList so a literal double-quote in
    // WgturnUrl/VkLink stays one argv element (no flag injection).
    // ArgsBuilderOverride keeps the plain-string form for stub binaries.
    internal ProcessStartInfo BuildProcessStartInfo(EmergencyChannelConfig config)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (ArgsBuilderOverride is { } override_)
        {
            psi.Arguments = override_(config);
        }
        else
        {
            psi.ArgumentList.Add("connect-url");
            psi.ArgumentList.Add("-url");
            psi.ArgumentList.Add(config.WgturnUrl);
            psi.ArgumentList.Add("-vk-link");
            psi.ArgumentList.Add(config.VkLink);
        }

        return psi;
    }

    private void OnStdLine(object _, DataReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Data)) return;
        WriteLogLine(e.Data);
    }

    private void OnProcessExited(Process process)
    {
        // Exact-process claim is decisive. If a concurrent Stop() or a
        // successor LaunchProcess already claimed _process, this callback
        // lost the race — return without logging, state change, or event,
        // so a stopped manager can never flip back to Failed on a stale
        // exit. Only the winning callback captures/disposes/reports.
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _process, null, process), process))
            return;

        int? exitCode = null;
        try
        {
            if (process.HasExited)
                exitCode = process.ExitCode;
        }
        catch { /* race with handle teardown — tolerate */ }

        try { process.Dispose(); } catch { }

        _logger.Warning(
            "[EmergencyChannelManager] wgturn-cli exited unexpectedly (exit code: {Code})",
            exitCode?.ToString() ?? "?");

        State = EmergencyChannelState.Failed;
        Crashed?.Invoke(this, exitCode);

        CloseLogWriter();
    }

    private void OpenLogWriter()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[EmergencyChannelManager] Failed to create log directory {Dir}",
                Path.GetDirectoryName(_logPath));
        }

        lock (_logLock)
        {
            try
            {
                _logWriter?.Dispose();
                var fs = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.Read);
                _logWriter = new StreamWriter(fs, Encoding.UTF8) { AutoFlush = true };
                _logWriter.WriteLine($"--- wgturn-cli session start {DateTime.UtcNow:O} ---");
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[EmergencyChannelManager] Failed to open wgturn-cli log {Path}", _logPath);
                _logWriter = null;
            }
        }
    }

    private void WriteLogLine(string line)
    {
        lock (_logLock)
        {
            try
            {
                _logWriter?.WriteLine(line);
            }
            catch
            {
                // Swallow — logging failure must never propagate into
                // the process-output pump callbacks.
            }
        }
    }

    private void CloseLogWriter()
    {
        lock (_logLock)
        {
            try { _logWriter?.Dispose(); } catch { }
            _logWriter = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { Stop(); } catch { }
        DisposeProcess();
        CloseLogWriter();
        GC.SuppressFinalize(this);
    }
}
