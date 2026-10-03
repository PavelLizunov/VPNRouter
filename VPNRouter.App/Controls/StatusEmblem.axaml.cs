using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Markup.Xaml;

namespace VPNRouter.App.Controls;

public partial class StatusEmblem : UserControl
{
    public static readonly StyledProperty<bool> IsOnProperty = AvaloniaProperty.Register<StatusEmblem, bool>(nameof(IsOn));
    public static readonly StyledProperty<bool> IsBusyProperty = AvaloniaProperty.Register<StatusEmblem, bool>(nameof(IsBusy));
    public static readonly StyledProperty<bool> IsErrorProperty = AvaloniaProperty.Register<StatusEmblem, bool>(nameof(IsError));
    public static readonly StyledProperty<bool> IsWarnProperty = AvaloniaProperty.Register<StatusEmblem, bool>(nameof(IsWarn));
    public static readonly StyledProperty<string?> IconIdProperty =
        AvaloniaProperty.Register<StatusEmblem, string?>(nameof(IconId));

    public bool IsOn { get => GetValue(IsOnProperty); set => SetValue(IsOnProperty, value); }
    public bool IsBusy { get => GetValue(IsBusyProperty); set => SetValue(IsBusyProperty, value); }
    public bool IsError { get => GetValue(IsErrorProperty); set => SetValue(IsErrorProperty, value); }
    public bool IsWarn { get => GetValue(IsWarnProperty); set => SetValue(IsWarnProperty, value); }

    /// <summary>off, on, busy, error or warn; an error wins over busy, busy over warn, warn over on.</summary>
    public string State => IsError ? "error" : IsBusy ? "busy" : IsWarn ? "warn" : IsOn ? "on" : "off";

    /// <summary>The Pictogram id shown in the disc (the page's own icon).</summary>
    public string? IconId { get => GetValue(IconIdProperty); set => SetValue(IconIdProperty, value); }

    private readonly Grid _root;
    private readonly Arc _spinner;
    private readonly Pictogram _glyph;
    private readonly Pictogram _badge;

    public StatusEmblem()
    {
        AvaloniaXamlLoader.Load(this);
        _root = this.FindControl<Grid>("Root")!;
        _spinner = this.FindControl<Arc>("Spinner")!;
        _glyph = this.FindControl<Pictogram>("Glyph")!;
        _badge = this.FindControl<Pictogram>("BadgeGlyph")!;
        // The connecting arc spins only when the system allows animations; with reduced motion it stays a still accent arc.
        _root.Classes.Set("calm", PrefersReducedMotion());
        Apply();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsOnProperty || change.Property == IsBusyProperty || change.Property == IsErrorProperty ||
            change.Property == IsWarnProperty || change.Property == IconIdProperty) Apply();
    }

    private void Apply()
    {
        if (_root is null) return;
        var state = State;
        _root.Classes.Set("on", state == "on");
        _root.Classes.Set("busy", state == "busy");
        _root.Classes.Set("error", state == "error");
        _root.Classes.Set("warn", state == "warn");
        _spinner.IsVisible = state == "busy";
        _spinner.Classes.Set("spin", state == "busy");
        _glyph.Id = IconId;
        _badge.Id = state switch
        {
            "on" => "shield-check",
            "error" or "warn" => "shield-alert",
            "busy" => "shield",
            _ => "power",
        };
    }

    // Windows: "Show animations in Windows" (SPI_GETCLIENTAREAANIMATION). Other systems expose no setting Avalonia can read.
    private static bool PrefersReducedMotion()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            return SystemParametersInfo(0x1042, 0, out var enabled, 0) && !enabled;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, out bool value, uint winIni);
}
