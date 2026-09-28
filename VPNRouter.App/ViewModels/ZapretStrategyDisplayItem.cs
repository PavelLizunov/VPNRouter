#nullable enable
namespace VPNRouter.App.ViewModels;

public sealed class ZapretStrategyDisplayItem
{
    public string Glyph { get; init; } = string.Empty;

    public string NameText { get; init; } = string.Empty;

    public ZapretStrategyDisplayKind Kind { get; init; }

    public bool IsSuccess => Kind == ZapretStrategyDisplayKind.Success;
    public bool IsWarning => Kind == ZapretStrategyDisplayKind.Warning;
    public bool IsDanger  => Kind == ZapretStrategyDisplayKind.Danger;
    public bool IsMuted   => Kind == ZapretStrategyDisplayKind.Muted;
    public bool IsStale   => Kind == ZapretStrategyDisplayKind.Stale;

    public override string ToString() => $"{Glyph}  {NameText}";
}

public enum ZapretStrategyDisplayKind
{
    Muted = 0,
    Success,
    Warning,
    Danger,
    Stale,
}
