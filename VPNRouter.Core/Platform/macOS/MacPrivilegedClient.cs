using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;
using VPNRouter.Core.Services;

namespace VPNRouter.Core.Platform.macOS;

public sealed record MacPrivilegeState(
    bool Ready,
    bool Legacy,
    bool LegacyProcess,
    int Policy,
    int Uid,
    string? Reason,
    string? HelperSha256,
    string? SingBoxSha256,
    string? AppVersion,
    string? LegacyState)
{
    public static MacPrivilegeState Unavailable(string reason) =>
        new(false, false, false, 0, 0, reason, null, null, null, null);
}

internal readonly record struct MacFirewallSession(int Pid, long StartTime);

/// <summary>
/// Thin client for the one authorized privileged helper. Every operation is a
/// typed JSON request on stdin; the helper path is the only sudoers command and
/// never receives arguments.
/// </summary>
public sealed class MacPrivilegedClient
{
    private readonly IProcessRunner _runner;
    private readonly ILogger? _logger;

    public MacPrivilegedClient(IProcessRunner? runner = null, ILogger? logger = null)
    {
        _runner = runner ?? new ProcessRunner();
        _logger = logger;
    }

    public static bool IsSupportedPlatform => OperatingSystem.IsMacOS();

    public MacPrivilegeState Probe()
    {
        var result = Run(new JsonObject { ["version"] = JsonValue.Create(1), ["op"] = "probe" },
            TimeSpan.FromSeconds(15));
        if (result.ExitCode != 0)
        {
            _logger?.Debug("[MacPrivilege] probe exit={Exit} stderr={Err}",
                result.ExitCode, result.Stderr?.Trim());
            return MacPrivilegeState.Unavailable("probe_failed");
        }

        try
        {
            using var document = JsonDocument.Parse(result.Stdout);
            var root = document.RootElement;
            var ready = root.TryGetProperty("ready", out var readyElement) && readyElement.GetBoolean();
            var legacy = root.TryGetProperty("legacy", out var legacyElement) && legacyElement.GetBoolean();
            var legacyProcess = root.TryGetProperty("legacy_process", out var legacyProcessElement) &&
                                legacyProcessElement.GetBoolean();
            var policy = 0;
            if (root.TryGetProperty("policy", out var policyElement))
                policyElement.TryGetInt32(out policy);
            var uid = 0;
            if (root.TryGetProperty("uid", out var uidElement))
                uidElement.TryGetInt32(out uid);
            return new MacPrivilegeState(
                Ready: ready,
                Legacy: legacy,
                LegacyProcess: legacyProcess,
                Policy: policy,
                Uid: uid,
                Reason: root.TryGetProperty("reason", out var reason) ? reason.GetString() : null,
                HelperSha256: root.TryGetProperty("helper_sha256", out var helper) ? helper.GetString() : null,
                SingBoxSha256: root.TryGetProperty("singbox_sha256", out var singbox) ? singbox.GetString() : null,
                AppVersion: root.TryGetProperty("app_version", out var version) ? version.GetString() : null,
                LegacyState: root.TryGetProperty("legacy_state", out var legacyState) ? legacyState.GetString() : null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            _logger?.Debug(ex, "[MacPrivilege] probe returned malformed JSON");
            return MacPrivilegeState.Unavailable("probe_malformed");
        }
    }

    public ProcessRequest CreateStartRequest(string configPath, string launchId)
    {
        var hash = ComputeFileSha256(configPath);
        var request = new JsonObject
        {
            ["version"] = JsonValue.Create(1),
            ["op"] = "start",
            ["config_sha256"] = hash,
            ["launch_id"] = launchId,
        };
        return new ProcessRequest(
            MacPrivilegePaths.SudoPath,
            new[] { "-n", MacPrivilegePaths.HelperPath },
            CaptureStdout: true,
            CaptureStderr: true,
            StdinInput: request.ToJsonString());
    }

    public bool Stop(int pid, long startTimeUnixSeconds)
    {
        if (pid <= 1 || startTimeUnixSeconds <= 0)
            return false;

        var request = new JsonObject
        {
            ["version"] = JsonValue.Create(1),
            ["op"] = "stop",
            ["pid"] = JsonValue.Create(pid),
            ["start_time"] = JsonValue.Create(startTimeUnixSeconds),
        };
        return RunOk(request, TimeSpan.FromSeconds(10));
    }

    internal bool StopLaunch(string launchId, bool launcherExited)
    {
        var result = Run(new JsonObject
        {
            ["version"] = JsonValue.Create(1),
            ["op"] = "stop_launch",
            ["launch_id"] = launchId,
        }, TimeSpan.FromSeconds(10));
        var outcome = result.Stdout.Trim();
        var unregistered = outcome == "not_registered";
        var stopped = result.ExitCode == 0 && !result.TimedOut &&
            (outcome == "stopped" || (unregistered && launcherExited));
        if (!stopped)
            _logger?.Debug("[MacPrivilege] Launch stop unconfirmed exit={Exit} timedOut={TimedOut} unregistered={Unregistered} launcherExited={LauncherExited}",
                result.ExitCode, result.TimedOut, unregistered, launcherExited);
        return stopped;
    }

    public bool DnsApply(string service, string server)
    {
        var request = new JsonObject
        {
            ["version"] = JsonValue.Create(1),
            ["op"] = "dns_apply",
            ["service"] = service,
            ["server"] = server,
        };
        return RunOk(request, TimeSpan.FromSeconds(15));
    }

    public bool DnsRestore() =>
        RunOk(new JsonObject { ["version"] = JsonValue.Create(1), ["op"] = "dns_restore" },
            TimeSpan.FromSeconds(15));

    public bool DnsFlush()
    {
        return RunOk(new JsonObject { ["version"] = JsonValue.Create(1), ["op"] = "dns_flush" },
            TimeSpan.FromSeconds(15));
    }

    internal MacFirewallSession? ReadPfSession(string launchId)
    {
        var result = Run(new JsonObject
        {
            ["version"] = JsonValue.Create(1),
            ["op"] = "pf_session",
            ["launch_id"] = launchId,
        }, TimeSpan.FromSeconds(10));
        if (result.ExitCode != 0 || result.TimedOut)
            return null;

        try
        {
            using var document = JsonDocument.Parse(result.Stdout);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("pid", out var pid) && pid.TryGetInt32(out var processId) &&
                root.TryGetProperty("start_time", out var start) && start.TryGetInt64(out var startTime) &&
                processId > 1 && startTime > 0)
                return new MacFirewallSession(processId, startTime);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            _logger?.Debug(ex, "[MacPrivilege] Invalid firewall session identity");
        }
        return null;
    }

    internal bool PfApplyAnchor(MacFirewallSession session, string rules) =>
        PfApply(session, "anchor", rules);

    internal bool PfApplyLegacy(MacFirewallSession session, string rules) =>
        PfApply(session, "legacy", rules);

    internal bool PfApplyMain(MacFirewallSession session) => PfApply(session, "main");

    internal bool PfEnable(MacFirewallSession session) =>
        RunOk(PfRequest("pf_enable", session), TimeSpan.FromSeconds(10));

    internal bool PfRelease(MacFirewallSession session) =>
        RunOk(PfRequest("pf_release", session), TimeSpan.FromSeconds(15));

    internal bool PfRecover() =>
        RunOk(new JsonObject { ["version"] = JsonValue.Create(1), ["op"] = "pf_recover" },
            TimeSpan.FromSeconds(15));

    public bool TryGetPfStatus(out string output)
    {
        var result = Run(new JsonObject { ["version"] = JsonValue.Create(1), ["op"] = "pf_status" },
            TimeSpan.FromSeconds(10));
        output = result.Stdout ?? string.Empty;
        return result.ExitCode == 0 && !result.TimedOut;
    }

    public ProcessResult Run(JsonObject request, TimeSpan? timeout = null)
    {
        var json = request.ToJsonString();
        try
        {
            return _runner.RunAsync(new ProcessRequest(
                    MacPrivilegePaths.SudoPath,
                    new[] { "-n", MacPrivilegePaths.HelperPath },
                    CaptureStdout: true,
                    CaptureStderr: true,
                    StdinInput: json,
                    Timeout: timeout ?? TimeSpan.FromSeconds(30)))
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            _logger?.Debug(ex, "[MacPrivilege] {Op} could not start the privileged helper",
                request["op"]?.GetValue<string>());
            return new ProcessResult(-1, string.Empty, ex.Message, TimeSpan.Zero, false);
        }
    }

    private bool PfApply(MacFirewallSession session, string mode, string? rules = null)
    {
        var request = PfRequest("pf_apply", session);
        request["mode"] = mode;
        if (rules != null)
            request["rules"] = rules;
        return RunOk(request, TimeSpan.FromSeconds(15));
    }

    private static JsonObject PfRequest(string operation, MacFirewallSession session) => new()
    {
        ["version"] = JsonValue.Create(1),
        ["op"] = operation,
        ["pid"] = JsonValue.Create(session.Pid),
        ["start_time"] = JsonValue.Create(session.StartTime),
    };

    private bool RunOk(JsonObject request, TimeSpan timeout)
    {
        var result = Run(request, timeout);
        if (result.ExitCode == 0 && !result.TimedOut) return true;
        _logger?.Debug("[MacPrivilege] {Op} failed exit={Exit} timedOut={TimedOut} stderr={Err}",
            request["op"]?.GetValue<string>(), result.ExitCode, result.TimedOut,
            result.Stderr?.Trim());
        return false;
    }

    public static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    internal static string RequestJson(JsonObject request) => request.ToJsonString();
}
