#nullable enable
namespace VPNRouter.App.ViewModels;

/// <summary>
/// v2.37.0-r46 — one row in the Hero quick-strategy ComboBox.
///
/// <para>Carries the glyph (e.g. "✓ 5/5" or "◌") + strategy name + a
/// classification kind. The ComboBox ItemTemplate in DpiBypassPage uses
/// Kind-flag computed properties (IsSuccess/IsWarning/IsDanger/IsMuted/IsStale)
/// to drive style selectors that paint the glyph green/yellow/red/gray/orange
/// via DynamicResource brushes. Name stays default text colour for
/// readability.</para>
///
/// <para>Theme-safe: style selectors resolve brushes via DynamicResource so
/// theme switch (light ↔ dark) re-paints without reload.</para>
/// </summary>
public sealed class ZapretStrategyDisplayItem
{
    /// <summary>The colored prefix glyph + optional score, e.g. "✓ 5/5" or "◌".</summary>
    public string Glyph { get; init; } = string.Empty;

    /// <summary>The raw strategy name shown in default text colour, e.g. "general (ALT3)".</summary>
    public string NameText { get; init; } = string.Empty;

    /// <summary>Classification of this strategy's probe result. Drives ItemTemplate styling.</summary>
    public ZapretStrategyDisplayKind Kind { get; init; }

    // Bool projections of Kind — Avalonia style selectors bind to these via
    // Classes.success="{Binding IsSuccess}" etc. Computed from Kind so the
    // serialization surface stays a single enum.
    public bool IsSuccess => Kind == ZapretStrategyDisplayKind.Success;
    public bool IsWarning => Kind == ZapretStrategyDisplayKind.Warning;
    public bool IsDanger  => Kind == ZapretStrategyDisplayKind.Danger;
    public bool IsMuted   => Kind == ZapretStrategyDisplayKind.Muted;
    public bool IsStale   => Kind == ZapretStrategyDisplayKind.Stale;

    /// <summary>
    /// Fallback ToString used when ItemTemplate hasn't applied (rare — e.g.
    /// ComboBox showing the selected item summary in some Avalonia versions
    /// without ContentTemplate). Includes the glyph + name so the user still
    /// gets the information even if colour is lost.
    /// </summary>
    public override string ToString() => $"{Glyph}  {NameText}";
}

public enum ZapretStrategyDisplayKind
{
    /// <summary>Untested or no data (◌ muted gray).</summary>
    Muted = 0,
    /// <summary>Strategy passed verification (✓ green).</summary>
    Success,
    /// <summary>Strategy partially passed (⚠ yellow/warning).</summary>
    Warning,
    /// <summary>Strategy failed verification (✗ red/danger).</summary>
    Danger,
    /// <summary>Winner data is stale, &gt;7 days old (⏱ orange — warning-ish).</summary>
    Stale,
}
