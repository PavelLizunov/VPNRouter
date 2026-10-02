using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using VPNRouter.App.ViewModels;

namespace VPNRouter.Tools.UiProbe;

public sealed class ProbeOptions
{
    public string Surface { get; set; } = "simple";
    public string Scenario { get; set; } = "default";
    public Dictionary<string, JsonElement>? State { get; set; }
    public string Theme { get; set; } = "light";
    public string Language { get; set; } = "en";
    public int Width { get; set; } = 520;
    public int Height { get; set; } = 900;

    public ProbeOptions Clone() => (ProbeOptions)MemberwiseClone();

    public string Label => $"{Surface}/{Scenario}/{Theme}/{Language}/{Width}x{Height}";
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

            // The view model applies the saved theme when it is built, so the theme is set after it.
            app.RequestedThemeVariant = string.Equals(o.Theme, "dark", StringComparison.OrdinalIgnoreCase)
                ? ThemeVariant.Dark
                : ThemeVariant.Light;

            scenario.Apply(vm);
            var problems = Scenarios.ApplyState(vm, o.State);

            var window = surface.Create(vm);
            window.Width = o.Width;
            window.Height = o.Height;
            var session = new ProbeSession(o, vm, window, previousTheme, previousLanguage);
            session.Notes.AddRange(problems.Select(p => "state: " + p));
            window.Show();
            session.Layout();
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
