using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VPNRouter.Tools.UiMcp;

// Drives the real, installed app on the Windows test worker (tools/live/*.ps1 over ssh) and brings the evidence back:
// the step log with timings, the report and screenshots. The headless tools render the UI; these run the product.
public static class LiveTools
{
    private const string HostEnv = "VPNROUTER_LIVE_HOST";
    private const string ScriptsEnv = "VPNROUTER_LIVE_SCRIPTS";
    private const string DefaultHost = "tester@100.115.182.0";
    private const string RemoteDirScp = "C:/android-build";
    private const string RemoteDirWin = @"C:\android-build";

    internal static readonly string[] Scenarios = { "tabs", "cycles", "servers", "ping", "modes", "dump" };
    private static readonly string[] DumpTabs = { "servers", "subscribe", "settings", "apps", "tools", "public" };

    private static readonly Regex HostPattern = new(@"^[A-Za-z0-9_.-]+@[A-Za-z0-9_.:-]+$", RegexOptions.Compiled);
    private static readonly Regex RunPattern = new(@"^\d{8}-\d{6}-[a-z]+$", RegexOptions.Compiled);
    private static readonly Regex FilePattern = new(@"^[0-9A-Za-z._-]+\.(png|txt|json|log)$", RegexOptions.Compiled);

    internal static bool IsLiveTool(string name) => name is "live_run" or "live_runs" or "live_report";

    public static IEnumerable<JsonObject> Definitions()
    {
        yield return Tool("live_run",
            "Run a functional scenario against the REAL installed VPNRouter app on the Windows test worker (UI Automation over ssh) and return the step log with timings, log findings, crash events and optionally screenshots. " +
            "Scenarios: tabs (every tab and inner tab), cycles (N connect/disconnect cycles with phase timings and public IP), servers (connect to each subscription server and time it), " +
            "ping (ping/test-all timings with the tunnel down and up), modes (switch Selected apps / All traffic while connected), dump (write the controls of one tab). " +
            "Connect scenarios change the worker's real VPN state; the app must already be running there. Takes minutes.",
            Schema(new JsonObject
            {
                ["scenario"] = Str("Scenario to run", Scenarios),
                ["count"] = Int("Cycles or switches (default 3, 1..20)"),
                ["max_servers"] = Int("servers scenario: how many servers to try (default 20, 1..100)"),
                ["tab"] = Str("dump scenario: which tab", DumpTabs),
                ["timeout_sec"] = Int("Give up waiting after this many seconds (default 900, 60..3600)"),
                ["max_images"] = Int("Attach up to this many of the run's screenshots (default 0; file names are listed either way)"),
            }, "scenario"));

        yield return Tool("live_runs",
            "List the most recent live runs on the worker (name and the final LIVE-DONE line).",
            Schema(new JsonObject { ["limit"] = Int("How many (default 10)") }));

        yield return Tool("live_report",
            "Fetch the evidence of one live run: step log, report summary, and the named screenshots/files.",
            Schema(new JsonObject
            {
                ["run"] = Str("Run name from live_runs, e.g. 20261004-003056-cycles (default: the latest)"),
                ["files"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string" },
                    ["description"] = "File names in the run folder to attach (png as images, txt/json/log as text)",
                },
            }));
    }

    public static JsonArray Run(string name, JsonObject args) => name switch
    {
        "live_run" => RunScenario(args),
        "live_runs" => ListRuns(args),
        "live_report" => Report(args),
        _ => throw new ArgumentException("Unknown tool " + name),
    };

    // ---- tools -------------------------------------------------------------------------------------------------------------------------

    private static JsonArray RunScenario(JsonObject args)
    {
        var host = Host();
        var scenario = Opt(args, "scenario") ?? throw new ArgumentException("scenario is required");
        if (!Scenarios.Contains(scenario)) throw new ArgumentException("scenario must be one of " + string.Join(", ", Scenarios));
        var count = Clamp(args, "count", 3, 1, 20);
        var maxServers = Clamp(args, "max_servers", 20, 1, 100);
        var timeout = Clamp(args, "timeout_sec", 900, 60, 3600);
        var tab = Opt(args, "tab") ?? "settings";
        if (!DumpTabs.Contains(tab)) throw new ArgumentException("tab must be one of " + string.Join(", ", DumpTabs));
        var maxImages = Clamp(args, "max_images", 0, 0, 12);

        var scripts = FindScripts();
        Scp(host, new[] { Path.Combine(scripts, "live-harness.ps1"), Path.Combine(scripts, "live-run.ps1") }, RemoteDirScp + "/", 120);

        var remote = Ssh(host, timeout + 120,
            "powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", RemoteDirWin + @"\live-run.ps1",
            "-Scenario", scenario, "-Count", count.ToString(), "-MaxServers", maxServers.ToString(), "-Tab", tab,
            "-TimeoutSec", timeout.ToString());

        var runDir = remote.Stdout.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("RUN-DIR ", StringComparison.Ordinal));
        if (runDir == null)
            return Text("The scenario produced no run folder.\n" + Clip(remote.Stdout + remote.Stderr, 2500));
        var run = runDir[(runDir.LastIndexOf('\\') + 1)..];
        if (!RunPattern.IsMatch(run)) throw new InvalidOperationException("Unexpected run folder name: " + run);

        return Collect(host, run, Array.Empty<string>(), maxImages, header: $"scenario {scenario} on {host}, run {run}");
    }

    private static JsonArray ListRuns(JsonObject args)
    {
        var host = Host();
        var limit = Clamp(args, "limit", 10, 1, 50);
        var names = RunNames(host).TakeLast(limit).ToList();
        var sb = new StringBuilder();
        foreach (var run in names)
        {
            var done = Ssh(host, 30, "cmd", "/c", "type", $@"{RemoteDirWin}\live\{run}\done.txt").Stdout.Trim();
            sb.AppendLine($"{run}  {(done.Length > 0 ? done : "(no done.txt - still running or aborted)")}");
        }
        return Text(sb.Length == 0 ? "No live runs on the worker yet." : sb.ToString());
    }

    private static JsonArray Report(JsonObject args)
    {
        var host = Host();
        var run = Opt(args, "run") ?? RunNames(host).LastOrDefault() ?? throw new InvalidOperationException("No live runs on the worker yet.");
        if (!RunPattern.IsMatch(run)) throw new ArgumentException("run must look like 20261004-003056-cycles");
        var files = args["files"] is JsonArray a ? a.Select(n => n?.GetValue<string>() ?? string.Empty).Where(s => s.Length > 0).ToArray() : Array.Empty<string>();
        foreach (var f in files)
            if (!FilePattern.IsMatch(f)) throw new ArgumentException("Bad file name: " + f);
        return Collect(host, run, files, maxImages: 0, header: $"run {run} on {host}");
    }

    // ---- evidence ----------------------------------------------------------------------------------------------------------------------

    private static JsonArray Collect(string host, string run, string[] files, int maxImages, string header)
    {
        var local = Path.Combine(Path.GetTempPath(), "vpnrouter-live-" + run);
        Directory.CreateDirectory(local);
        var remoteDir = $"{RemoteDirScp}/live/{run}";

        var listing = Ssh(host, 30, "cmd", "/c", "dir", "/b", $@"{RemoteDirWin}\live\{run}").Stdout
            .Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && FilePattern.IsMatch(l)).ToList();

        var wanted = new List<string> { "report.json", "steps.log", "done.txt" };
        var images = listing.Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).ToList();
        wanted.AddRange(files.Where(f => listing.Contains(f)));
        wanted.AddRange(images.Take(maxImages));
        foreach (var file in wanted.Where(f => listing.Contains(f)).Distinct())
        {
            var scp = Exec("scp", new[] { "-q", "-o", "BatchMode=yes", $"{host}:{remoteDir}/{file}", local }, 120);
            if (scp.ExitCode != 0) Debug.WriteLine("scp " + file + ": " + scp.Stderr);
        }

        var text = new StringBuilder();
        text.AppendLine(header);
        text.AppendLine(Read(local, "done.txt").Trim());
        text.AppendLine();
        text.AppendLine("Steps:");
        text.AppendLine(Clip(Read(local, "steps.log"), 9000));
        text.AppendLine(Summary(Read(local, "report.json")));
        var others = listing.Except(new[] { "report.json", "steps.log", "done.txt" }).ToList();
        if (others.Count > 0) text.AppendLine("Files in the run folder: " + string.Join(", ", others));
        text.AppendLine("Local copy: " + local);

        var items = new JsonArray(TextItem(text.ToString()));
        foreach (var file in wanted.Distinct().Where(f => listing.Contains(f)))
        {
            var path = Path.Combine(local, file);
            if (!File.Exists(path)) continue;
            if (file.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                items.Add(new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(File.ReadAllBytes(path)), ["mimeType"] = "image/png" });
            else if (files.Contains(file))
                items.Add(TextItem($"--- {file} ---\n{Clip(File.ReadAllText(path), 12000)}"));
        }
        return items;
    }

    private static string Summary(string reportJson)
    {
        if (string.IsNullOrWhiteSpace(reportJson)) return "(no report.json)";
        try
        {
            var report = JsonNode.Parse(reportJson.TrimStart('\uFEFF'));
            var sb = new StringBuilder();
            foreach (var key in new[] { "log_findings", "crash_events" })
            {
                if (report?[key] is not JsonArray list) continue;
                sb.AppendLine($"{key}: {list.Count}");
                foreach (var item in list.Take(25)) sb.AppendLine("  " + Clip(item?.ToJsonString() ?? string.Empty, 300));
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return "(report.json unreadable: " + ex.Message + ")";
        }
    }

    private static List<string> RunNames(string host)
    {
        var listing = Ssh(host, 30, "cmd", "/c", "dir", "/b", "/ad", $@"{RemoteDirWin}\live").Stdout;
        return listing.Split('\n').Select(l => l.Trim()).Where(l => RunPattern.IsMatch(l)).OrderBy(l => l, StringComparer.Ordinal).ToList();
    }

    // ---- process helpers ---------------------------------------------------------------------------------------------------------------

    private sealed record Result(int ExitCode, string Stdout, string Stderr);

    private static string Host()
    {
        var host = Environment.GetEnvironmentVariable(HostEnv);
        if (string.IsNullOrWhiteSpace(host)) host = DefaultHost;
        if (!HostPattern.IsMatch(host)) throw new InvalidOperationException($"{HostEnv} must look like user@host");
        return host;
    }

    private static string FindScripts()
    {
        var explicitDir = Environment.GetEnvironmentVariable(ScriptsEnv);
        if (!string.IsNullOrWhiteSpace(explicitDir))
        {
            if (File.Exists(Path.Combine(explicitDir, "live-harness.ps1"))) return explicitDir;
            throw new InvalidOperationException($"{ScriptsEnv} does not contain live-harness.ps1: {explicitDir}");
        }
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "tools", "live");
                if (File.Exists(Path.Combine(candidate, "live-harness.ps1"))) return candidate;
            }
        throw new InvalidOperationException($"tools/live/live-harness.ps1 not found from the working directory; set {ScriptsEnv} to the folder that holds it.");
    }

    private static Result Ssh(string host, int timeoutSec, params string[] remoteCommand)
    {
        var args = new List<string> { "-o", "BatchMode=yes", "-o", "ConnectTimeout=15", host };
        args.AddRange(remoteCommand);
        return Exec("ssh", args, timeoutSec);
    }

    private static void Scp(string host, IEnumerable<string> files, string remoteDir, int timeoutSec)
    {
        var args = new List<string> { "-q", "-o", "BatchMode=yes", "-o", "ConnectTimeout=15" };
        args.AddRange(files);
        args.Add($"{host}:{remoteDir}");
        var result = Exec("scp", args, timeoutSec);
        if (result.ExitCode != 0) throw new InvalidOperationException("scp to the worker failed: " + Clip(result.Stderr, 500));
    }

    private static Result Exec(string file, IEnumerable<string> arguments, int timeoutSec)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start " + file);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(timeoutSec)))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"{file} did not finish in {timeoutSec} s");
        }
        process.WaitForExit();
        return new Result(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    // ---- small helpers -----------------------------------------------------------------------------------------------------------------

    private static string Read(string dir, string file)
    {
        var path = Path.Combine(dir, file);
        return File.Exists(path) ? File.ReadAllText(path).TrimStart('\uFEFF') : string.Empty;
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + $"\n... ({text.Length - max} more characters)";

    private static string? Opt(JsonObject args, string key) =>
        args[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static int Clamp(JsonObject args, string key, int fallback, int min, int max) =>
        Math.Clamp(args[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : fallback, min, max);

    private static JsonArray Text(string text) => new(TextItem(text));

    private static JsonObject TextItem(string text) => new() { ["type"] = "text", ["text"] = text };

    private static JsonObject Tool(string name, string description, JsonObject schema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = schema,
    };

    private static JsonObject Schema(JsonObject properties, params string[] required)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Length > 0) schema["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray());
        return schema;
    }

    private static JsonObject Str(string description, params string[] choices)
    {
        var o = new JsonObject { ["type"] = "string", ["description"] = description };
        if (choices.Length > 0) o["enum"] = new JsonArray(choices.Select(c => (JsonNode)c).ToArray());
        return o;
    }

    private static JsonObject Int(string description) => new() { ["type"] = "integer", ["description"] = description };
}
