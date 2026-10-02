using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace VPNRouter.Tools.UiProbe;

public sealed class SweepOptions
{
    public ProbeOptions Probe { get; set; } = new();

    // tour: every interactive control once, in tree order. monkey: random controls, repeated, seeded.
    public string Mode { get; set; } = "tour";
    public int SpeedMs { get; set; } = 100;
    public int MaxSteps { get; set; } = 200;
    public int Seed { get; set; } = 1;

    // A frame is rendered after every Nth action. 0-speed with a large value is the "fast clicking" case.
    public int RenderEvery { get; set; } = 1;
    public bool IncludeUnbound { get; set; }
    public bool AllowSideEffects { get; set; }
    public bool StopOnFailure { get; set; } = true;
    public bool Resize { get; set; }
    public int MaxSeconds { get; set; } = 90;
}

public sealed record SweepStep(int Index, string Element, string Action, bool Ok, string? Error, long Ms);

public sealed class SweepResult
{
    public int StepsRun { get; set; }
    public int CandidatesSeen { get; set; }
    public bool Truncated { get; set; }
    public List<SweepStep> Trail { get; } = new();
    public List<SweepStep> Failures { get; } = new();
    public List<string> Skipped { get; } = new();
    public List<string> Notes { get; } = new();
    public byte[]? FailurePng { get; set; }
    public byte[]? FinalPng { get; set; }
}

// Drives the real window with real pointer input and renders frames, watching for exceptions. Not a fuzzer of the VPN: controls whose
// commands start, stop, install, delete or otherwise act on the machine are skipped unless the caller opts in.
public static class Sweeper
{
    private static readonly Regex SafeCommand = new(
        @"^(Select|SetLanguage|SetTheme|Toggle|Show|Hide|Close|Dismiss|Expand|Collapse|Back|Next|Previous|Copy|ClearSearch|ClearFilter|Filter|Sort|Switch|Go|Navigate|OpenSettings|OpenTab|OpenPage|OpenAdvanced|OpenSimple)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Dangerous = new(
        @"(delete|remove|reset|uninstall|install|update|connect|disconnect|start|stop|restart|export|import|apply|kill|repair|elevate|admin|run|launch|download|upload|fetch|refresh)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private sealed record Candidate(Control Control, string Key, string Label, Point Center);

    public static SweepResult Run(SweepOptions o)
    {
        var result = new SweepResult();
        var errors = new List<string>();

        void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            errors.Add(e.Exception.GetType().Name + ": " + e.Exception.Message + FirstFrames(e.Exception, 5));
            e.Handled = true;
        }

        using var session = ProbeSession.Open(o.Probe);
        result.Notes.AddRange(session.Notes);
        Dispatcher.UIThread.UnhandledException += OnUnhandled;
        try
        {
            Loop(session, o, result, errors);
        }
        finally
        {
            Dispatcher.UIThread.UnhandledException -= OnUnhandled;
        }

        try { result.FinalPng = session.CapturePng(); }
        catch (Exception ex) { result.Notes.Add("final frame: " + ex.GetType().Name + ": " + ex.Message); }
        return result;
    }

    private static void Loop(ProbeSession session, SweepOptions o, SweepResult result, List<string> errors)
    {
        var window = session.Window;
        var rng = new Random(o.Seed);
        var visited = new HashSet<string>();
        var clock = Stopwatch.StartNew();

        for (var step = 1; step <= o.MaxSteps; step++)
        {
            if (clock.Elapsed.TotalSeconds > o.MaxSeconds)
            {
                result.Truncated = true;
                result.Notes.Add($"stopped after {o.MaxSeconds} s");
                break;
            }

            var skipped = new List<string>();
            var candidates = Collect(window, o, skipped);
            foreach (var s in skipped.Distinct()) if (!result.Skipped.Contains(s) && result.Skipped.Count < 60) result.Skipped.Add(s);
            result.CandidatesSeen = Math.Max(result.CandidatesSeen, candidates.Count);

            Candidate? pick;
            if (string.Equals(o.Mode, "monkey", StringComparison.OrdinalIgnoreCase))
            {
                pick = candidates.Count == 0 ? null : candidates[rng.Next(candidates.Count)];
                if (o.Resize && step % 10 == 0)
                {
                    window.Width = rng.Next(320, 720);
                    session.Layout();
                    result.Notes.Add($"step {step}: resized to {window.Width:0}");
                }
            }
            else
            {
                pick = candidates.FirstOrDefault(c => !visited.Contains(c.Key));
            }

            if (pick == null) break;
            visited.Add(pick.Key);

            errors.Clear();
            string? error = null;
            var watch = Stopwatch.StartNew();
            try
            {
                Act(session, pick);
                if (o.SpeedMs > 0) ProbeSession.Pump(o.SpeedMs);
                else Dispatcher.UIThread.RunJobs();
                if (o.RenderEvery > 0 && step % o.RenderEvery == 0) RenderFrame(session);
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message + FirstFrames(ex, 6);
            }
            if (error == null && errors.Count > 0) error = string.Join(" | ", errors);

            var record = new SweepStep(step, pick.Key, pick.Label, error == null, error, watch.ElapsedMilliseconds);
            result.StepsRun = step;
            result.Trail.Add(record);
            if (result.Trail.Count > 40) result.Trail.RemoveAt(0);
            if (error != null)
            {
                result.Failures.Add(record);
                if (result.FailurePng == null)
                {
                    try { result.FailurePng = session.CapturePng(); }
                    catch (Exception ex) { result.Notes.Add("failure frame: " + ex.GetType().Name + ": " + ex.Message); }
                }
                if (o.StopOnFailure) break;
            }
        }
    }

    private static void RenderFrame(ProbeSession session)
    {
        session.Layout();
        _ = session.Window.CaptureRenderedFrame();
    }

    private static List<Candidate> Collect(Window window, SweepOptions o, List<string> skipped)
    {
        var list = new List<Candidate>();
        var width = window.ClientSize.Width;
        var height = window.ClientSize.Height;
        foreach (var visual in window.GetVisualDescendants())
        {
            if (visual is not Control c || !Describe.IsInteractive(c)) continue;
            if (!c.IsEffectivelyVisible || !c.IsEffectivelyEnabled || !c.IsHitTestVisible) continue;
            if (c.Bounds.Width <= 0 || c.Bounds.Height <= 0) continue;

            var key = Describe.Path(c);
            var reason = Refuse(c, o);
            if (reason != null)
            {
                skipped.Add($"{key} ({reason})");
                continue;
            }
            var centre = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), window);
            if (!centre.HasValue) continue;
            if (centre.Value.X < 0 || centre.Value.Y < 0 || centre.Value.X > width || centre.Value.Y > height)
            {
                skipped.Add($"{key} (off screen at {centre.Value.X:0},{centre.Value.Y:0}; use a taller window)");
                continue;
            }
            list.Add(new Candidate(c, key, $"{c.GetType().Name} click", centre.Value));
        }
        return list;
    }

    // Why a control must not be clicked, or null when it may be.
    private static string? Refuse(Control c, SweepOptions o)
    {
        var text = Describe.TextOf(c) ?? string.Empty;
        var command = Describe.CommandName(c);

        if (OperatingSystem.IsWindows() && !o.AllowSideEffects && c is not (TabItem or ListBoxItem or Expander))
            return "windows host: only navigation is clicked without allow_side_effects";

        var navigation = c is TabItem or ListBoxItem or Expander;
        if (!o.AllowSideEffects && !navigation && (Dangerous.IsMatch(text) || (command != null && Dangerous.IsMatch(command))))
            return "acts on the machine";

        if (c is Button && c is not ToggleButton)
        {
            if (command == null) return o.IncludeUnbound ? null : "plain button without a command (code-behind handler)";
            if (!SafeCommand.IsMatch(command.Replace("Command", string.Empty)) && !o.AllowSideEffects)
                return $"command {command} is not in the safe list";
        }
        return null;
    }

    private static void Act(ProbeSession session, Candidate pick)
    {
        var window = session.Window;
        var point = pick.Center;
        window.MouseMove(point, RawInputModifiers.None);
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);

        if (pick.Control is TextBox)
            window.KeyTextInput("probe 123 <&> \"q\"");

        if (pick.Control is ComboBox { IsDropDownOpen: true })
        {
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        }
    }

    private static string FirstFrames(Exception ex, int count)
    {
        var frames = (ex.StackTrace ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Take(count)
            .Select(l => l.Trim());
        var inner = ex.InnerException != null ? $" (inner {ex.InnerException.GetType().Name}: {ex.InnerException.Message})" : string.Empty;
        return inner + "\n  " + string.Join("\n  ", frames);
    }
}
