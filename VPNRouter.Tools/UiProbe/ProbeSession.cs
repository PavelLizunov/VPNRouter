using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VPNRouter.App.ViewModels;

namespace VPNRouter.Tools.UiProbe;

public sealed class ProbeOptions
{
    public string Surface { get; set; } = "simple";
    public string Scenario { get; set; } = "default";
    public Dictionary<string, JsonElement>? State { get; set; }

    // Clicks made after the surface is laid out and before it is captured, to reach states a view model property cannot (inner tabs, expanders).
    // Each step is the visible label (or control name) of an interactive control; "Label#2" picks the second match.
    public List<string>? Steps { get; set; }
    public string Theme { get; set; } = "light";
    public string Language { get; set; } = "en";
    public int Width { get; set; } = 520;
    public int Height { get; set; } = 900;

    public ProbeOptions Clone() => (ProbeOptions)MemberwiseClone();

    public string Label => $"{Surface}/{Scenario}/{Theme}/{Language}/{Width}x{Height}" +
                           (Steps is { Count: > 0 } ? " [" + string.Join(" > ", Steps) + "]" : string.Empty);
}

// One view model and one window for the length of an operation. Everything here must run on the Avalonia UI thread
// of an initialized headless application (the MCP host starts one; the tests already have one).
public sealed class ProbeSession : IDisposable
{
    private readonly ThemeVariant? _previousTheme;
    private readonly string _previousLanguage;

    public MainWindowViewModel Vm { get; }
    public Window Window { get; }
    public ProbeOptions Options { get; }
    public List<string> Notes { get; } = new();

    private ProbeSession(ProbeOptions options, MainWindowViewModel vm, Window window, ThemeVariant? theme, string language)
    {
        Options = options;
        Vm = vm;
        Window = window;
        _previousTheme = theme;
        _previousLanguage = language;
    }

    public static ProbeSession Open(ProbeOptions o)
    {
        var surface = Surfaces.Find(o.Surface)
            ?? throw new ArgumentException($"Unknown surface '{o.Surface}'. Known: {string.Join(", ", Surfaces.All.Select(s => s.Name))}");
        var scenario = Scenarios.Find(o.Scenario)
            ?? throw new ArgumentException($"Unknown scenario '{o.Scenario}'. Known: {string.Join(", ", Scenarios.All.Select(s => s.Name))}");

        var app = Application.Current ?? throw new InvalidOperationException("No Avalonia application is running.");
        var previousTheme = app.RequestedThemeVariant;
        var previousLanguage = VPNRouter.App.Localization.Strings.Lang;

        var vm = new MainWindowViewModel(new ProbeSettingsStore());
        try
        {
            if (string.Equals(o.Language, "ru", StringComparison.OrdinalIgnoreCase))
                vm.SetLanguageRussianCommand.Execute(null);
            else
                vm.SetLanguageEnglishCommand.Execute(null);

            // Switch the theme the way the app does (through the view model): it also swaps the mascot for the dark theme and refreshes the theme-bound brushes.
            // Setting only the application theme variant left the dark-theme header with the light-theme mascot, black line art on a dark tile.
            if (string.Equals(o.Theme, "dark", StringComparison.OrdinalIgnoreCase))
                vm.SetThemeDarkCommand.Execute(null);
            else
                vm.SetThemeLightCommand.Execute(null);

            scenario.Apply(vm);
            var problems = Scenarios.ApplyState(vm, o.State);

            var window = surface.Create(vm);
            window.Width = o.Width;
            window.Height = o.Height;
            var session = new ProbeSession(o, vm, window, previousTheme, previousLanguage);
            session.Notes.AddRange(problems.Select(p => "state: " + p));
            window.Show();
            session.Layout();
            foreach (var step in o.Steps ?? new List<string>())
                session.ClickByLabel(step);
            return session;
        }
        catch
        {
            app.RequestedThemeVariant = previousTheme;
            VPNRouter.App.Localization.Strings.Lang = previousLanguage;
            vm.Dispose();
            throw;
        }
    }

    public void Layout()
    {
        Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
        Window.UpdateLayout();
    }

    public bool ClickByLabel(string step)
    {
        var index = 1;
        var label = step;
        var hash = step.LastIndexOf('#');
        if (hash > 0 && int.TryParse(step[(hash + 1)..], out var n) && n > 0)
        {
            label = step[..hash];
            index = n;
        }

        var matches = Window.GetVisualDescendants()
            .OfType<Control>()
            .Where(c => Describe.IsInteractive(c) && c.IsEffectivelyVisible && c.IsEffectivelyEnabled && c.Bounds.Width > 0 &&
                        (string.Equals(Describe.Label(c), label, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(Describe.TextOf(c)?.Trim(), label, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(c.Name, label, StringComparison.Ordinal)))
            .ToList();
        if (matches.Count < index)
        {
            Notes.Add($"step '{step}': no visible interactive control with that label ({matches.Count} found)");
            return false;
        }

        var target = matches[index - 1];
        try { target.BringIntoView(); } catch { }
        Layout();
        var centre = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), Window);
        if (!centre.HasValue)
        {
            Notes.Add($"step '{step}': the control has no position");
            return false;
        }

        Window.MouseMove(centre.Value, RawInputModifiers.None);
        Window.MouseDown(centre.Value, MouseButton.Left, RawInputModifiers.None);
        Window.MouseUp(centre.Value, MouseButton.Left, RawInputModifiers.None);
        Pump(150);
        Layout();
        return true;
    }

    // Lets timers, bindings and animations run for roughly this long, one render tick at a time.
    public static void Pump(int milliseconds)
    {
        var end = DateTime.UtcNow.AddMilliseconds(Math.Max(0, milliseconds));
        do
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
            if (milliseconds > 0) Thread.Sleep(Math.Min(10, milliseconds));
        }
        while (DateTime.UtcNow < end);
    }

    public byte[] CapturePng()
    {
        Layout();
        var bitmap = Window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("CaptureRenderedFrame returned null (headless Skia drawing is not enabled).");
        var path = Path.Combine(Path.GetTempPath(), "uiprobe-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            bitmap.Save(path, PngBitmapEncoderOptions.Default);
            return File.ReadAllBytes(path);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    public void Dispose()
    {
        try { Window.Close(); } catch { }
        try { Vm.Dispose(); } catch { }
        var app = Application.Current;
        if (app != null) app.RequestedThemeVariant = _previousTheme;
        VPNRouter.App.Localization.Strings.Lang = _previousLanguage;
    }
}
