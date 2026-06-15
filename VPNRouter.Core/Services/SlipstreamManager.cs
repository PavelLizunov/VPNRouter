using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>Raised when the slipstream-client sidecar can't be started.</summary>
public class SlipstreamException : Exception
{
    public SlipstreamException(string message) : base(message) { }
    public SlipstreamException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The chosen local TCP port is already held by another process.</summary>
public sealed class SlipstreamPortConflictException : SlipstreamException
{
    public int Port { get; }
    public string? OwnerProcessHint { get; }
    public SlipstreamPortConflictException(int port, string? ownerHint)
        : base($"Local port {port} is already in use" +
               (ownerHint != null ? $" by {ownerHint}" : "") +
               " — slipstream-client can't bind it. Stop the other process or pick another port.")
    {
        Port = port;
        OwnerProcessHint = ownerHint;
    }
}

/// <summary>
/// Manages the slipstream-client sidecar (DNS-tunnel transport). Listens on a
/// local TCP port; the sing-box VLESS outbound is generated against it, so this
/// is a <b>transport dependency of the connection</b> (unlike the independent
/// TgProxy / Zapret sidecars). VpnEngine starts it BEFORE sing-box and stops it
/// after; a failed start throws so the engine fails closed (never starts sing-box
/// over a dead local port).
///
/// <para>The full leaf PEM travels in the dns-tunnel:// profile
/// (<see cref="VlessServerEntry.DnsLeafCertPem"/>); we write it to
/// <see cref="AppPaths.SlipstreamActiveCertPath"/> and pass <c>--cert</c>.
/// slipstream-client has no <c>--pin</c>, so the optional
/// <see cref="VlessServerEntry.DnsLeafFingerprint"/> is an integrity cross-check
/// (sha256 of the leaf DER) — hard-reject on mismatch.</para>
///
/// <para>See plans/dns-tunnel-slipstream-integration-2026-06-10.md.</para>
/// </summary>
public class SlipstreamManager : IDisposable
{
    /// <summary>Fixed local TCP port for the MVP (per the integration brief).
    /// Dynamic free-port selection is a future improvement.</summary>
    public const int DefaultLocalPort = 7001;

    private readonly ILogger _logger;
    private readonly IProcessRunner _runner;
    private IProcessHandle? _handle;
    private readonly StringBuilder _capturedStderr = new();
    private readonly object _stderrGate = new();
    private bool _disposed;

    /// <summary>Test-only seam — swap in a fake for the long-lived spawn.
    /// Production uses the default <see cref="ProcessRunner"/>.</summary>
    internal static IProcessRunner Runner { get; set; } = new ProcessRunner();

    /// <summary>Post-spawn fail-closed probe window. Production 2 s; tests lower it.</summary>
    internal int StartupProbeMs { get; set; } = 2000;

    public bool IsRunning => _handle != null && !_handle.HasExited;
    public int? Pid => IsRunning ? _handle?.Pid : null;
    public int LocalPort { get; private set; }

    public SlipstreamManager(ILogger? logger = null, IProcessRunner? runner = null)
    {
        _logger = logger ?? Log.Logger;
        _runner = runner ?? Runner;
    }

    /// <summary>
    /// Start slipstream-client for <paramref name="entry"/> (must be a
    /// dns-tunnel server) listening on 127.0.0.1:<paramref name="localPort"/>.
    /// Throws on any failure — the caller (VpnEngine) must fail closed.
    /// </summary>
    public void Start(VlessServerEntry entry, int localPort = DefaultLocalPort)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        if (!string.Equals(entry.Protocol, "dns-tunnel", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"SlipstreamManager.Start requires a dns-tunnel server (got protocol '{entry.Protocol}')",
                nameof(entry));

        if (IsRunning)
        {
            _logger.Warning("[Slipstream] Already running (PID {Pid}), stopping first", Pid);
            Stop();
        }

        // ── Profile field validation ──
        if (string.IsNullOrWhiteSpace(entry.DnsDomain))
            throw new SlipstreamException("dns-tunnel server has no domain");
        // ── Resolver selection ──
        //   System-resolver mode (link sentinel "system"): prefer the OS/operator
        //   default resolver(s) discovered now — before the TUN is up, so this reads
        //   the underlying physical NIC's DNS. On a strict RU mobile whitelist the
        //   operator resolver is the ONLY reachable DNS, so a link cannot hardcode
        //   НСДИ IPs and work for every operator. The link's literal IPs (if any)
        //   are the fallback when the OS resolver can't be discovered.
        List<string> resolvers;
        if (entry.DnsUseSystemResolver)
        {
            var osResolvers = ReadOsResolvers();
            resolvers = SelectResolvers(entry, osResolvers);
            _logger.Information(
                "[Slipstream] System-resolver mode: {OsCount} OS resolver(s) discovered; " +
                "using {Used} ({Source})",
                osResolvers.Count, resolvers.Count, osResolvers.Count > 0 ? "OS" : "link fallback");
        }
        else
        {
            resolvers = SelectResolvers(entry, Array.Empty<string>());
        }
        if (resolvers.Count == 0)
            throw new SlipstreamException(entry.DnsUseSystemResolver
                ? "dns-tunnel server requests the system resolver but none could be discovered and no fallback resolvers are configured"
                : "dns-tunnel server has no resolvers");
        if (string.IsNullOrWhiteSpace(entry.DnsLeafCertPem))
            throw new SlipstreamException("dns-tunnel server has no leaf certificate (PEM)");

        // ── Binary present? Promote the installer-bundled copy (app/) to the
        //    runtime path on first use, then fail closed if still absent. ──
        EnsureBinaryProvisioned(
            AppPaths.SlipstreamExePath, AppPaths.SlipstreamBundledExePath,
            AppPaths.SlipstreamBinDir, _logger);
        if (!File.Exists(AppPaths.SlipstreamExePath))
            throw new SlipstreamException(
                $"slipstream-client not found at {AppPaths.SlipstreamExePath}. " +
                "It ships bundled with the Windows installer; for a dev build, build it " +
                "from Mygod/slipstream-rust and place it there. (DNS-tunnel is Windows/Linux only.)");

        // ── Optional leaf fingerprint integrity cross-check (hard-reject) ──
        if (!string.IsNullOrWhiteSpace(entry.DnsLeafFingerprint))
        {
            var actual = ComputeLeafSha256Hex(entry.DnsLeafCertPem);
            var expected = NormalizeHex(entry.DnsLeafFingerprint);
            if (actual == null)
            {
                _logger.Warning(
                    "[Slipstream] Could not compute leaf fingerprint from PEM — skipping cross-check " +
                    "(slipstream-client --cert remains the authority)");
            }
            else if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new SlipstreamException(
                    $"dns-tunnel leaf fingerprint mismatch (link={expected}, cert={actual}) — " +
                    "refusing to connect (tampered or corrupt profile)");
            }
        }

        // ── Local port pre-flight ──
        if (!IsPortAvailable(localPort))
        {
            var ownerHint = TryResolvePortOwner(localPort);
            _logger.Warning("[Slipstream] Port {Port} pre-flight: BUSY (owner: {Owner})",
                localPort, ownerHint ?? "<unknown>");
            throw new SlipstreamPortConflictException(localPort, ownerHint);
        }

        // ── Write the profile leaf PEM to the active-cert path ──
        try
        {
            Directory.CreateDirectory(AppPaths.SlipstreamDir);
            File.WriteAllText(AppPaths.SlipstreamActiveCertPath, entry.DnsLeafCertPem);
        }
        catch (Exception ex)
        {
            throw new SlipstreamException("Failed to write the active leaf cert to disk", ex);
        }

        // ── Build argv ──
        //   slipstream-client --cert <pem> -d <domain> -l <port>
        //     --tcp-listen-host 127.0.0.1 -r <resolver> [-r <resolver> ...]
        var argv = new List<string>
        {
            "--cert", AppPaths.SlipstreamActiveCertPath,
            "-d", entry.DnsDomain.Trim(),
            "-l", localPort.ToString(),
            "--tcp-listen-host", "127.0.0.1",
        };
        foreach (var r in resolvers) { argv.Add("-r"); argv.Add(r); }

        // OPTIONAL authoritative endpoint(s) — query the tunnel server's NS
        // directly, bypassing the rate-limiting recursive resolver (the recursive
        // НСДИ resolvers drop the covert query stream after ~1.5-3 min → QUIC
        // idle-timeout 0x433). Passed ALONGSIDE -r so slipstream multipaths:
        // authoritative when the network allows direct UDP to it, recursive as the
        // censorship-resilient fallback. Absent for servers that publish none.
        var authoritative = (entry.DnsAuthoritative ?? new List<string>())
            .Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList();
        foreach (var a in authoritative) { argv.Add("--authoritative"); argv.Add(a); }

        // r7 (stability): honor the configured congestion control — bbr paces sends
        // smoother than slipstream's default, so a covert DNS flood trips the
        // recursive НСДИ resolver's rate-limit less. Only bbr/dcubic are valid for
        // slipstream-client; anything else falls through to its default.
        var cc = (entry.CongestionControl ?? "").Trim().ToLowerInvariant();
        if (cc == "bbr" || cc == "dcubic") { argv.Add("-c"); argv.Add(cc); }
        // r7 (stability): gentler keep-alive (2s vs the 400ms default) — fewer idle
        // queries to the rate-limiting resolver, and during a throttle it probes
        // lightly instead of hammering, giving the resolver room to recover within
        // the (now 180s) idle window. 2s is far under that, so liveness is unaffected.
        argv.Add("-t"); argv.Add("2000");

        // r12 (Codex measure-first): emit SAFE per-resolver throughput counters at INFO
        // once a second — payload vs empty DNS responses, polls, pending/inflight, and
        // last-payload-age. This is the safe replacement for the reverted r9 --debug-poll:
        // pure Rust counters on the existing 1s report path, with NO per-poll FFI
        // path-quality reads (the r9 0xC0000005 suspect). Lines land in slipstream.log
        // (RUST_LOG=info) so the recursive-vs-authoritative download collapse is finally
        // measurable: watch "payload+=" fall toward 0 and "last_payload_ms" climb while a
        // resolver still has stream tx → the download-throttle Codex hypothesized. The
        // per-resolver "mode=Recursive/Authoritative" label separates the two paths.
        argv.Add("--path-stats");

        // r10: the r9 diagnostic flags (--debug-poll / --debug-streams) are REMOVED.
        // They answered the question — picoquic multipath already shifts ~all traffic
        // to the authoritative path (213.155.15.93) and keeps it healthy (flow_blocked
        // never true), so the recursive-resolver rate-limit is NOT the bottleneck when
        // authoritative is reachable. BUT the r9 build segfaulted (0xC0000005) at ~6.8
        // min: the per-poll FFI reads into picoquic that --debug-poll performs (~40k in
        // 41s) are the prime suspect for a use-after-free on a path being torn down.
        // Removing them returns the client to the stable r8 transport surface; if it
        // STILL crashes at ~6-7 min the fault is in picoquic multipath itself and needs
        // a source-level fix + rebuild. Same r7 binary (fec9d314) — no rebuild.

        var request = new ProcessRequest(
            ExecutablePath: AppPaths.SlipstreamExePath,
            Arguments: argv,
            WorkingDirectory: AppPaths.SlipstreamBinDir,
            EnvironmentOverrides: new Dictionary<string, string>
            {
                // slipstream-client logs via tracing_subscriber, whose EnvFilter
                // reads RUST_LOG (default "info"). Pin it so the connection-
                // lifecycle WARN lines are emitted — these are the ONLY signal
                // for why a live DNS-tunnel drops:
                //   "Connection closed … local_error=0x433"  (QUIC idle timeout)
                //   "Connection closed; reconnecting in Nms" (backoff 250ms→5s)
                //   "Path for resolver … became unavailable" (resolver went silent)
                // Without capturing these a "worked then died" report is
                // un-rootcause-able. RUST_BACKTRACE surfaces any client panic.
                //
                // r10: reverted r9's debug level back to "info". The per-resolver cc
                // firehose did its job (authoritative path proven healthy) but the
                // verbose FFI polling it enabled is the prime suspect for the r9
                // 0xC0000005 segfault. info still emits every lifecycle WARN — the
                // signal needed to see a crash and its context — without the per-poll
                // hot loop. RUST_BACKTRACE stays on to surface any Rust-side panic.
                ["RUST_LOG"] = "info",
                ["RUST_BACKTRACE"] = "1",
            },
            CaptureStdout: true,
            CaptureStderr: true);

        _logger.Information(
            "[Slipstream] Spawn: {Exe} -d {Domain} -l {Port} (resolvers: {N}, authoritative: {M})",
            request.ExecutablePath, entry.DnsDomain, localPort, resolvers.Count, authoritative.Count);

        // r9 (DIAGNOSTIC): rotate the transport log so THIS session's debug-verbose
        // output lands in a clean file. Without this, a slipstream.log already at the
        // size cap from accumulated r4-r8 sessions would silently swallow the new
        // session (AppendTransportLog returns early when over-cap). Previous session
        // is preserved as .prev (also picked up by the diagnostics export).
        RotateTransportLog();

        // r9 (DIAGNOSTIC): log the COMPLETE argv so the transport log self-documents
        // exactly which flags were in effect for this session — confirms -c/-t/
        // --authoritative/--debug-* all reached the binary when reading back a
        // user-supplied slipstream.log out of context.
        var argvLine = "[Slipstream] argv: " + string.Join(" ", argv);
        _logger.Information(argvLine);
        AppendTransportLog(argvLine);

        lock (_stderrGate) _capturedStderr.Clear();

        try
        {
            _handle = _runner.Start(request);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[Slipstream] Failed to start process");
            throw new SlipstreamException("Failed to start slipstream-client", ex);
        }

        LocalPort = localPort;
        var startedHandle = _handle;
        startedHandle.Exited += (_, code) =>
        {
            _logger.Warning("[Slipstream] Process exited (exit code: {Code})", code);
            AppendTransportLog($"--- process exited (code {code}) ---");
        };
        // slipstream-client logs to STDOUT (tracing fmt default), so OutputLine
        // carries the connection-lifecycle WARN/ERROR lines. Persist BOTH streams
        // to a dedicated slipstream.log (AppPaths.SlipstreamLogPath) so a live
        // tunnel drop is diagnosable after the fact — and the redaction-safe
        // diagnostics export picks it up. ErrorLine still feeds the 16 KB
        // early-exit buffer (unchanged).
        AppendTransportLog(
            $"=== spawn PID {startedHandle.Pid} -d {entry.DnsDomain} -l {localPort} " +
            $"resolvers={resolvers.Count} ===");
        startedHandle.OutputLine += OnOutputLineHandler;
        startedHandle.ErrorLine += OnErrorLineHandler;
        _logger.Information("[Slipstream] Spawned PID {Pid} on 127.0.0.1:{Port}",
            startedHandle.Pid, localPort);

        // ── Fail-closed post-spawn watchdog ──
        // slipstream-client is a transport dependency, so an immediate exit must
        // surface — VpnEngine refuses to start sing-box over a dead local port.
        using var probeCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(StartupProbeMs));
        int? earlyExitCode = null;
        try
        {
            earlyExitCode = startedHandle.WaitForExitAsync(probeCts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            _logger.Information("[Slipstream] Alive after {Ms}ms probe (PID {Pid})",
                StartupProbeMs, startedHandle.Pid);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[Slipstream] Post-spawn probe raised");
        }

        if (earlyExitCode.HasValue)
        {
            string stderrTail;
            lock (_stderrGate) stderrTail = _capturedStderr.ToString();
            _logger.Error(
                "[Slipstream] Exited within {Ms}ms of spawn (code {Code}) — startup failure. Stderr: {Stderr}",
                StartupProbeMs, earlyExitCode, stderrTail.Trim());
            Stop();
            throw new SlipstreamException(
                $"slipstream-client exited immediately (code {earlyExitCode}). {Truncate(stderrTail.Trim(), 200)}");
        }
    }

    private void OnErrorLineHandler(object? sender, string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        AppendTransportLog(line);
        const int MaxStderrBuffer = 16 * 1024;
        lock (_stderrGate)
            if (_capturedStderr.Length < MaxStderrBuffer)
                _capturedStderr.AppendLine(line);
    }

    private void OnOutputLineHandler(object? sender, string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        AppendTransportLog(line);
        // Surface the connection-lifecycle WARN/ERROR lines to the MAIN app log
        // too, so the primary diagnostic log shows a flapping/dying tunnel
        // without needing to open slipstream.log. Normal operation is near-silent
        // at info level (only the initial "Listening …"), so this does not flood.
        if (line.IndexOf("WARN", StringComparison.OrdinalIgnoreCase) >= 0 ||
            line.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0)
            _logger.Warning("[Slipstream] {Line}", StripAnsi(line));
    }

    // ── Dedicated transport log (AppPaths.SlipstreamLogPath) ──
    // Both slipstream streams + lifecycle markers land here so a post-mortem
    // (or the diagnostics export) can root-cause a dropped tunnel. Best-effort,
    // size-capped, ANSI-stripped. Static + locked so concurrent OutputLine/
    // ErrorLine callbacks (separate reader threads) don't interleave a line.
    // 8 MB cap (r9's 32 MB bump reverted in r10 with the debug firehose). With
    // per-session rotation (RotateTransportLog) and info-level output this easily
    // holds a full multi-minute session's lifecycle lines without tripping the
    // head-keep cap.
    private const long TransportLogMaxBytes = 8 * 1024 * 1024;
    private static readonly object _transportLogGate = new();

    // r9 (DIAGNOSTIC): roll the current transport log to .prev at the start of each
    // session so a fresh, uncapped file captures THIS connection's debug output.
    // Guards against an already-full slipstream.log (from accumulated r4-r8 sessions)
    // silently swallowing the new session because AppendTransportLog short-circuits
    // when the file is already over TransportLogMaxBytes.
    private static void RotateTransportLog()
    {
        try
        {
            lock (_transportLogGate)
            {
                var path = AppPaths.SlipstreamLogPath;
                if (!File.Exists(path)) return;
                var prev = path + ".prev";
                try { if (File.Exists(prev)) File.Delete(prev); } catch { /* best-effort */ }
                File.Move(path, prev);
            }
        }
        catch { /* best-effort diagnostics — never throw from the start path */ }
    }

    private static void AppendTransportLog(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        try
        {
            lock (_transportLogGate)
            {
                var path = AppPaths.SlipstreamLogPath;
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > TransportLogMaxBytes) return; // capped — keep the head
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {StripAnsi(line)}{Environment.NewLine}");
            }
        }
        catch { /* best-effort diagnostics — never throw from a log callback */ }
    }

    // slipstream's tracing fmt emits ANSI colour codes (\e[33m …); strip them so
    // the file + main log are plain text.
    private static string StripAnsi(string s)
        => System.Text.RegularExpressions.Regex.Replace(s, "\\[[0-9;]*m", "");

    public void Stop()
    {
        var handle = _handle;
        if (handle == null) { CleanActiveCert(); return; }

        if (handle.HasExited)
        {
            try { handle.Dispose(); } catch { /* defensive */ }
            _handle = null;
            CleanActiveCert();
            return;
        }

        _logger.Information("[Slipstream] Stopping (PID {Pid})", handle.Pid);
        try
        {
            // Suppress the Exited event BEFORE Kill so the intentional stop
            // doesn't log a false crash (sibling of the SingBoxManager pattern).
            handle.SuppressExitedEvent();
            handle.Kill(entireProcessTree: true);
            using var stopCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(3000));
            try { handle.WaitForExitAsync(stopCts.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException)
            {
                _logger.Debug("[Slipstream] WaitForExitAsync timeout (3s) — proceeding to dispose");
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[Slipstream] Error stopping");
        }
        finally
        {
            try { handle.Dispose(); } catch { /* defensive */ }
            _handle = null;
            CleanActiveCert();
            LocalPort = 0;
            _logger.Information("[Slipstream] Stopped");
        }
    }

    private void CleanActiveCert()
    {
        try
        {
            if (File.Exists(AppPaths.SlipstreamActiveCertPath))
                File.Delete(AppPaths.SlipstreamActiveCertPath);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[Slipstream] active cert cleanup failed");
        }
    }

    /// <summary>
    /// Copy the installer-bundled slipstream-client (app/) to the runtime path
    /// (<paramref name="targetExePath"/>), keeping the runtime copy in sync with the
    /// freshly-bundled binary. Returns true if the runtime binary is present after.
    /// Best-effort: a copy failure (e.g. non-elevated, unwritable ProgramData, or the
    /// target locked by a live instance) is logged and swallowed — the caller's
    /// File.Exists check is the real gate.
    /// <para>VERSION-AWARE (v2.42.0-r13): the runtime copy lives in ProgramData and is
    /// NOT refreshed by the app/-only auto-update. The original <c>overwrite:false</c> +
    /// early-return-if-exists meant an existing user kept a stale slipstream-client.exe
    /// forever — so every transport change (new CLI flags, the recursive poll-budget, …)
    /// silently never reached them, and a flag the updated DLL passes that the OLD binary
    /// doesn't know (e.g. <c>--path-stats</c> in r12) makes it exit(2) → dns-tunnel dead.
    /// We now re-promote (overwrite) when the runtime copy is missing or a different size
    /// — a real slipstream rebuild always shifts the binary length, so size is a reliable
    /// "the bundle changed" signal without re-copying 7 MB on every Start.</para>
    /// </summary>
    internal static bool EnsureBinaryProvisioned(
        string targetExePath, string? bundledExePath, string targetBinDir, ILogger? logger)
    {
        var haveBundle = !string.IsNullOrEmpty(bundledExePath) && File.Exists(bundledExePath);
        var haveTarget = File.Exists(targetExePath);
        // Nothing to promote from: keep whatever runtime copy already exists (dev builds
        // with a hand-placed binary and no app/ bundle still work).
        if (!haveBundle) return haveTarget;

        var needCopy = !haveTarget;
        if (!needCopy)
        {
            try
            {
                // A real slipstream rebuild always shifts the file length, so a size
                // mismatch reliably flags "the app/ bundle was updated but this stale
                // ProgramData copy wasn't" — exactly the r12 --path-stats skew. Equal
                // length → assume identical → skip the 7 MB re-copy every Start.
                needCopy = new FileInfo(bundledExePath!).Length != new FileInfo(targetExePath).Length;
            }
            catch (Exception ex)
            {
                // Unreadable runtime/bundle metadata — safest to re-promote.
                logger?.Warning(ex, "[Slipstream] Could not compare runtime vs bundled binary — re-promoting");
                needCopy = true;
            }
        }
        if (!needCopy) return true;

        try
        {
            Directory.CreateDirectory(targetBinDir);
            File.Copy(bundledExePath!, targetExePath, overwrite: true);
            logger?.Information("[Slipstream] Promoted bundled binary {Src} -> {Dst} ({Len} bytes, app {Ver})",
                bundledExePath, targetExePath, new FileInfo(targetExePath).Length, AppVersion.Version);
        }
        catch (Exception ex)
        {
            // Most likely the target is locked by a live slipstream-client. Keep the
            // existing binary; the next Start (once the lock clears) re-promotes.
            logger?.Warning(ex, "[Slipstream] Could not promote bundled binary to {Dst}", targetExePath);
        }
        return File.Exists(targetExePath); // a concurrent Start() may have won the copy
    }

    /// <summary>
    /// Pick the effective covert resolvers for <paramref name="entry"/>. Pure +
    /// testable: when <see cref="VlessServerEntry.DnsUseSystemResolver"/> is set the
    /// OS-discovered <paramref name="systemResolvers"/> win (the operator-agnostic
    /// WL-BYPASS path); the link's literal <see cref="VlessServerEntry.DnsResolvers"/>
    /// are the fallback when the OS list is empty. Otherwise the literals are used
    /// verbatim. May return an empty list (the caller fails closed).
    /// </summary>
    internal static List<string> SelectResolvers(VlessServerEntry entry, IReadOnlyList<string> systemResolvers)
    {
        var literals = (entry.DnsResolvers ?? new List<string>())
            .Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).ToList();
        if (!entry.DnsUseSystemResolver)
            return literals;
        var sys = (systemResolvers ?? Array.Empty<string>())
            .Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return sys.Count > 0 ? sys : literals; // OS resolver preferred; literals are the fallback
    }

    /// <summary>
    /// Discover the OS default DNS resolver(s) of the active physical NIC(s) as
    /// <c>ip:53</c> strings (IPv4, deduped; loopback / link-local / tunnel adapters
    /// skipped). Best-effort: returns empty on any failure so the caller falls back
    /// to the link's literal resolvers. Called BEFORE the TUN is up, so it reads the
    /// underlying network's DNS (e.g. the mobile operator resolver) — the only DNS
    /// reachable on a strict whitelist.
    /// </summary>
    internal static List<string> ReadOsResolvers()
    {
        var result = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue; // skip TUN / VPN adapters
                IPInterfaceProperties props;
                try { props = ni.GetIPProperties(); } catch { continue; }
                foreach (var dns in props.DnsAddresses)
                {
                    if (dns.AddressFamily != AddressFamily.InterNetwork) continue; // IPv4 covert path
                    if (IPAddress.IsLoopback(dns)) continue;
                    var ip = dns.ToString();
                    if (ip.StartsWith("169.254", StringComparison.Ordinal)) continue; // link-local
                    var ep = ip + ":53";
                    if (!result.Contains(ep)) result.Add(ep);
                }
            }
        }
        catch { /* best-effort; empty -> caller falls back to the link's literals */ }
        return result;
    }

    /// <summary>sha256 of the leaf DER (the standard cert fingerprint), hex
    /// lowercase. Extracts the base64 body between the BEGIN/END markers and
    /// hashes the DER bytes — no full X.509 parse, so it works even on an unusual
    /// PEM. Returns null when the body can't be decoded (caller skips the check).</summary>
    internal static string? ComputeLeafSha256Hex(string pem)
    {
        try
        {
            const string begin = "-----BEGIN CERTIFICATE-----";
            const string end = "-----END CERTIFICATE-----";
            var bi = pem.IndexOf(begin, StringComparison.Ordinal);
            var ei = pem.IndexOf(end, StringComparison.Ordinal);
            if (bi < 0 || ei < 0 || ei <= bi) return null;
            var body = pem.Substring(bi + begin.Length, ei - bi - begin.Length);
            body = new string(body.Where(c => !char.IsWhiteSpace(c)).ToArray());
            if (body.Length == 0) return null;
            var der = Convert.FromBase64String(body);
            return Convert.ToHexString(SHA256.HashData(der)).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Strip non-hex chars (colons, whitespace) and lowercase — server
    /// fingerprints often arrive as <c>AA:BB:CC...</c>.</summary>
    internal static string NormalizeHex(string s)
        => new string((s ?? string.Empty).Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();

    /// <summary>Loopback TCP bind probe — true if <paramref name="port"/> is free.</summary>
    public static bool IsPortAvailable(int port)
    {
        if (port <= 0 || port > 65535) return false;
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { listener?.Stop(); } catch { }
        }
    }

    /// <summary>True if something accepts a loopback TCP connection on
    /// <paramref name="port"/> right now (i.e. it's bound + listening).</summary>
    internal static bool IsPortListening(int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(IPAddress.Loopback, port).Wait(300) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Poll until the local port is accepting connections (slipstream-client is
    /// up AND bound), or the timeout elapses / the process dies. VpnEngine uses
    /// this to fail closed before sing-box dials the local front.
    /// </summary>
    public bool WaitForPortListening(int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (_handle == null || _handle.HasExited) return false; // process died
            if (IsPortListening(LocalPort)) return true;
            Thread.Sleep(100);
        }
        return false;
    }

    /// <summary>Best-effort owner-process hint for a busy port (Windows netstat).</summary>
    internal static string? TryResolvePortOwner(int port)
    {
        if (!OperatingSystem.IsWindows() || port <= 0) return null;
        try
        {
            var psi = new ProcessStartInfo("netstat", "-ano")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);
            foreach (var line in stdout.Split('\n'))
            {
                if (!line.Contains("LISTENING")) continue;
                if (!line.Contains($":{port} ")) continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5 || !int.TryParse(parts[^1], out var pid)) continue;
                try { using var p = Process.GetProcessById(pid); return $"{p.ProcessName} (PID {pid})"; }
                catch { return $"PID {pid}"; }
            }
        }
        catch { /* best-effort */ }
        return null;
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
