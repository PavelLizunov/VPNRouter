using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VPNRouter.Tools.UiProbe;

namespace VPNRouter.Tools.UiMcp;

public static class Tools
{
    private static readonly string[] Themes = { "light", "dark" };
    private static readonly string[] Languages = { "en", "ru" };

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

    private static JsonObject Num(string description) => new() { ["type"] = "integer", ["description"] = description };

    private static JsonObject Bool(string description) => new() { ["type"] = "boolean", ["description"] = description };

    private static JsonObject StrList(string description) => new()
    {
        ["type"] = "array",
        ["items"] = new JsonObject { ["type"] = "string" },
        ["description"] = description,
    };

    private static JsonObject Common() => new()
    {
        ["surface"] = Str("Page or window name from ui_catalog, for example apps, servers, window-advanced"),
        ["scenario"] = Str("Preset state from ui_catalog (default, connected, connecting, many-servers)"),
        ["theme"] = Str("light or dark", Themes),
        ["language"] = Str("en or ru", Languages),
        ["width"] = Num("Window width in px (default 520; the app is narrow, try 360 and 720)"),
        ["height"] = Num("Window height in px (default 900; use 1400+ for long pages)"),
        ["state"] = new JsonObject
        {
            ["type"] = "object",
            ["description"] = "View model properties to set by name before rendering, e.g. {\"SelectedSettingsIndex\":5,\"IsConnected\":true}. See ui_state_properties.",
        },
        ["steps"] = StrList("Clicks made before rendering, to reach inner tabs and expanders: the visible label of each control, in order (\"Label#2\" = second match), e.g. [\"Settings\",\"Autostart\"]. Labels come from ui_tree."),
    };

    public static JsonArray Definitions()
    {
        var sweep = Common();
        sweep["mode"] = Str("tour: every control once in tree order; monkey: random controls, seeded", "tour", "monkey");
        sweep["speed_ms"] = Num("Pause after every action while the UI keeps running (0 = back to back, the fast-clicking case). Default 100.");
        sweep["max_steps"] = Num("Maximum actions (default 200)");
        sweep["seed"] = Num("Random seed for monkey mode (default 1)");
        sweep["render_every"] = Num("Render a frame after every Nth action (default 1; with speed_ms 0 a large value skips frames between clicks)");
        sweep["resize"] = Bool("Monkey mode only: change the window width every 10 steps");
        sweep["include_unbound"] = Bool("Also click plain buttons that have no command (code-behind handlers; unknown side effects)");
        sweep["allow_side_effects"] = Bool("Also click controls whose text or command suggests install/start/stop/delete/update. Only in a sandbox.");
        sweep["stop_on_failure"] = Bool("Stop at the first failure (default true)");
        sweep["max_seconds"] = Num("Time budget (default 90)");
        sweep["return_image"] = Bool("Attach the final (or failing) frame as an image (default true)");

        var matrix = new JsonObject
        {
            ["surfaces"] = StrList("Surfaces to render"),
            ["scenarios"] = StrList("Scenarios (default: default)"),
            ["themes"] = StrList("light, dark (default: light)"),
            ["languages"] = StrList("en, ru (default: en)"),
            ["widths"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "integer" },
                ["description"] = "Widths in px (default: 520)",
            },
            ["height"] = Num("Window height (default 900)"),
            ["columns"] = Num("Columns on the sheet (default 3)"),
            ["state"] = new JsonObject { ["type"] = "object", ["description"] = "View model properties to set, applied to every cell" },
        };

        return new JsonArray(
            Tool("ui_catalog", "List the pages and windows, preset scenarios, themes, languages and sizes this server can render.", Schema(new JsonObject())),
            Tool("ui_state_properties", "List writable view model properties (name: type) that can be passed as `state`.",
                Schema(new JsonObject { ["filter"] = Str("Substring of the property name") })),
            Tool("ui_render", "Render one surface to an image and run the layout lint (clipped text, text symbols instead of icons, low contrast, overflow).",
                Schema(Common(), "surface")),
            Tool($"ui_matrix", $"Render up to {Probe.MaxMatrixCells} combinations of surface x scenario x theme x language x width on one labelled sheet, with lint findings per cell.",
                Schema(matrix, "surfaces")),
            Tool("ui_tree", "Outline of the visible controls of a surface: type, name, text, window bounds, state, bound command. Controls marked * are what ui_sweep clicks.",
                Schema(AddTo(Common(), "max_lines", Num("Maximum lines (default 600)")), "surface")),
            Tool("ui_sweep", "Click controls of a surface with real pointer input at a chosen speed, rendering frames, and report exceptions with the element and the action trail. Skips controls that act on the machine unless allowed.",
                Schema(sweep, "surface")));
    }

    private static JsonObject AddTo(JsonObject o, string key, JsonNode value)
    {
        o[key] = value;
        return o;
    }

    private static JsonObject Tool(string name, string description, JsonObject schema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = schema,
    };

    // Runs on the UI thread.
    public static JsonArray Run(string name, JsonObject args) => name switch
    {
        "ui_catalog" => Text(Catalog()),
        "ui_state_properties" => Text(string.Join("\n", Scenarios.SettableProperties(Opt(args, "filter")))),
        "ui_render" => RenderTool(args),
        "ui_matrix" => MatrixTool(args),
        "ui_tree" => Text(Probe.Tree(Options(args), Int(args, "max_lines", 600))),
        "ui_sweep" => SweepTool(args),
        _ => throw new ArgumentException("Unknown tool " + name),
    };

    private static string Catalog()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Surfaces:");
        foreach (var s in Surfaces.All) sb.AppendLine($"  {s.Name} ({s.Kind}) - {s.Description}");
        sb.AppendLine("Scenarios:");
        foreach (var s in Scenarios.All) sb.AppendLine($"  {s.Name} - {s.Description}");
        sb.AppendLine("Themes: light, dark");
        sb.AppendLine("Languages: en, ru");
        sb.AppendLine("Useful widths: 360 (phone-like), 520 (default), 720; heights 900..1600 for long pages.");
        sb.AppendLine("Any other state: pass `state` (see ui_state_properties).");
        return sb.ToString();
    }

    private static JsonArray RenderTool(JsonObject args)
    {
        var result = Probe.Render(Options(args));
        var text = new StringBuilder();
        text.AppendLine(result.Label);
        foreach (var note in result.Notes) text.AppendLine("note: " + note);
        text.AppendLine(result.Findings.Count == 0 ? "lint: no findings" : $"lint: {result.Findings.Count} finding(s)");
        text.Append(LayoutLint.Format(result.Findings));
        return new JsonArray(TextItem(text.ToString()), ImageItem(result.Png));
    }

    private static JsonArray MatrixTool(JsonObject args)
    {
        var template = new ProbeOptions
        {
            Height = Int(args, "height", 900),
            State = StateOf(args),
        };
        var matrix = Probe.Matrix(
            template,
            List(args, "surfaces", Array.Empty<string>()),
            List(args, "scenarios", new[] { "default" }),
            List(args, "themes", new[] { "light" }),
            List(args, "languages", new[] { "en" }),
            IntList(args, "widths", new[] { 520 }),
            Int(args, "columns", 3));

        var text = new StringBuilder();
        foreach (var cell in matrix.Cells)
        {
            text.AppendLine($"{cell.Label}: {cell.Findings.Count} finding(s)");
            foreach (var f in cell.Findings.Take(12)) text.AppendLine($"    [{f.Severity}] {f.Rule}: {f.Element} - {f.Message}");
            if (cell.Findings.Count > 12) text.AppendLine($"    ... {cell.Findings.Count - 12} more; render the cell alone for the full list");
            foreach (var note in cell.Notes) text.AppendLine("    note: " + note);
        }
        return new JsonArray(TextItem(text.ToString()), ImageItem(matrix.Sheet));
    }

    private static JsonArray SweepTool(JsonObject args)
    {
        var options = new SweepOptions
        {
            Probe = Options(args),
            Mode = Opt(args, "mode") ?? "tour",
            SpeedMs = Int(args, "speed_ms", 100),
            MaxSteps = Int(args, "max_steps", 200),
            Seed = Int(args, "seed", 1),
            RenderEvery = Int(args, "render_every", 1),
            Resize = Flag(args, "resize", false),
            IncludeUnbound = Flag(args, "include_unbound", false),
            AllowSideEffects = Flag(args, "allow_side_effects", false),
            StopOnFailure = Flag(args, "stop_on_failure", true),
            MaxSeconds = Int(args, "max_seconds", 90),
        };
        var result = Sweeper.Run(options);

        var text = new StringBuilder();
        text.AppendLine($"{options.Probe.Label} mode={options.Mode} speed={options.SpeedMs}ms");
        text.AppendLine($"steps run: {result.StepsRun}, controls seen at once: {result.CandidatesSeen}, failures: {result.Failures.Count}{(result.Truncated ? ", TRUNCATED" : string.Empty)}");
        foreach (var note in result.Notes) text.AppendLine("note: " + note);
        foreach (var f in result.Failures)
        {
            text.AppendLine($"FAILURE at step {f.Index}: {f.Element}");
            text.AppendLine("  " + (f.Error ?? string.Empty).Replace("\n", "\n  "));
        }
        if (result.Failures.Count > 0)
        {
            text.AppendLine("Trail (last actions):");
            foreach (var t in result.Trail.TakeLast(15)) text.AppendLine($"  {t.Index}. {(t.Ok ? "ok  " : "FAIL")} {t.Element}");
        }
        else
        {
            text.AppendLine("Clicked:");
            foreach (var t in result.Trail.TakeLast(25)) text.AppendLine($"  {t.Index}. {t.Element}");
        }
        if (result.Skipped.Count > 0)
        {
            text.AppendLine($"Skipped ({result.Skipped.Count}):");
            foreach (var s in result.Skipped.Take(25)) text.AppendLine("  " + s);
        }

        var items = new JsonArray(TextItem(text.ToString()));
        if (Flag(args, "return_image", true))
        {
            var png = result.FailurePng ?? result.FinalPng;
            if (png != null) items.Add(ImageItem(png));
        }
        return items;
    }

    private static ProbeOptions Options(JsonObject args) => new()
    {
        Surface = Opt(args, "surface") ?? throw new ArgumentException("surface is required"),
        Scenario = Opt(args, "scenario") ?? "default",
        Theme = Opt(args, "theme") ?? "light",
        Language = Opt(args, "language") ?? "en",
        Width = Int(args, "width", 520),
        Height = Int(args, "height", 900),
        State = StateOf(args),
        Steps = args["steps"] is JsonArray steps
            ? steps.Select(n => n?.GetValue<string>() ?? string.Empty).Where(x => x.Length > 0).ToList()
            : null,
    };

    private static Dictionary<string, JsonElement>? StateOf(JsonObject args)
    {
        if (args["state"] is not JsonObject state || state.Count == 0) return null;
        var result = new Dictionary<string, JsonElement>();
        foreach (var (key, value) in state)
        {
            using var doc = JsonDocument.Parse(value?.ToJsonString() ?? "null");
            result[key] = doc.RootElement.Clone();
        }
        return result;
    }

    private static string? Opt(JsonObject args, string key) =>
        args[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static int Int(JsonObject args, string key, int fallback) =>
        args[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : fallback;

    private static bool Flag(JsonObject args, string key, bool fallback) =>
        args[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    private static List<string> List(JsonObject args, string key, string[] fallback) =>
        args[key] is JsonArray a ? a.Select(n => n?.GetValue<string>() ?? string.Empty).Where(s => s.Length > 0).ToList() : fallback.ToList();

    private static List<int> IntList(JsonObject args, string key, int[] fallback) =>
        args[key] is JsonArray a ? a.Select(n => n!.GetValue<int>()).ToList() : fallback.ToList();

    private static JsonArray Text(string text) => new(TextItem(text));

    private static JsonObject TextItem(string text) => new() { ["type"] = "text", ["text"] = text };

    private static JsonObject ImageItem(byte[] png) => new()
    {
        ["type"] = "image",
        ["data"] = Convert.ToBase64String(png),
        ["mimeType"] = "image/png",
    };
}
