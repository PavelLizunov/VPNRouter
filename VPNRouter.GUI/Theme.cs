namespace VPNRouter.GUI;

/// <summary>
/// Theme data: all colors and fonts for a single theme (light or dark).
/// </summary>
internal sealed class ThemeData
{
    // ── Primary palette ──
    public Color Background { get; init; }
    public Color Surface { get; init; }
    public Color SurfaceBorder { get; init; }

    // ── Accent ──
    public Color Primary { get; init; }
    public Color PrimaryHover { get; init; }
    public Color PrimaryLight { get; init; }

    // ── Text ──
    public Color TextPrimary { get; init; }
    public Color TextSecondary { get; init; }
    public Color TextMuted { get; init; }
    public Color TextOnPrimary { get; init; }

    // ── Status ──
    public Color Success { get; init; }
    public Color SuccessLight { get; init; }
    public Color Danger { get; init; }
    public Color DangerLight { get; init; }

    // ── Controls ──
    public Color ButtonDefault { get; init; }
    public Color ButtonDefaultText { get; init; }
    public Color InputBackground { get; init; }
    public Color InputBorder { get; init; }

    // ── Update panel (amber variants) ──
    public Color UpdatePanelBg { get; init; }
    public Color UpdatePanelBorder { get; init; }
    public Color UpdatePanelText { get; init; }
    public Color AmberButton { get; init; }

    // ── Fonts (shared across themes) ──
    public Font HeaderFont { get; init; } = null!;
    public Font SubHeaderFont { get; init; } = null!;
    public Font BodyFont { get; init; } = null!;
    public Font ButtonFont { get; init; } = null!;
    public Font SmallFont { get; init; } = null!;
    public Font StartStopFont { get; init; } = null!;
    public Font BoldBodyFont { get; init; } = null!;
}

/// <summary>
/// Centralized theme management. Supports Light and Dark themes with runtime switching.
/// </summary>
internal static class Theme
{
    // ── Shared fonts (identical for both themes) ──
    private static readonly Font _headerFont    = new("Segoe UI", 16f, FontStyle.Bold);
    private static readonly Font _subHeaderFont = new("Segoe UI", 10f, FontStyle.Regular);
    private static readonly Font _bodyFont      = new("Segoe UI", 9.5f, FontStyle.Regular);
    private static readonly Font _buttonFont    = new("Segoe UI", 10f, FontStyle.Bold);
    private static readonly Font _smallFont     = new("Segoe UI", 8.5f, FontStyle.Regular);
    private static readonly Font _startStopFont = new("Segoe UI", 12f, FontStyle.Bold);
    private static readonly Font _boldBodyFont  = new("Segoe UI", 9.5f, FontStyle.Bold);

    public static readonly ThemeData Light = new()
    {
        Background       = Color.FromArgb(245, 248, 252),  // #F5F8FC
        Surface          = Color.FromArgb(255, 255, 255),  // #FFFFFF
        SurfaceBorder    = Color.FromArgb(218, 226, 237),  // #DAE2ED
        Primary          = Color.FromArgb(37, 99, 235),    // #2563EB Blue-600
        PrimaryHover     = Color.FromArgb(29, 78, 216),    // #1D4ED8 Blue-700
        PrimaryLight     = Color.FromArgb(219, 234, 254),  // #DBEAFE Blue-100
        TextPrimary      = Color.FromArgb(30, 41, 59),     // #1E293B Slate-800
        TextSecondary    = Color.FromArgb(100, 116, 139),  // #64748B Slate-500
        TextMuted        = Color.FromArgb(148, 163, 184),  // #94A3B8 Slate-400
        TextOnPrimary    = Color.White,
        Success          = Color.FromArgb(22, 163, 74),    // #16A34A Green-600
        SuccessLight     = Color.FromArgb(220, 252, 231),  // #DCFCE7 Green-100
        Danger           = Color.FromArgb(220, 38, 38),    // #DC2626 Red-600
        DangerLight      = Color.FromArgb(254, 226, 226),  // #FEE2E2 Red-100
        ButtonDefault    = Color.FromArgb(241, 245, 249),  // #F1F5F9 Slate-100
        ButtonDefaultText= Color.FromArgb(51, 65, 85),     // #334155 Slate-700
        InputBackground  = Color.White,
        InputBorder      = Color.FromArgb(203, 213, 225),  // #CBD5E1 Slate-300
        UpdatePanelBg    = Color.FromArgb(254, 243, 199),  // amber-100
        UpdatePanelBorder= Color.FromArgb(253, 186, 116),  // amber-300
        UpdatePanelText  = Color.FromArgb(146, 64, 14),    // amber-800
        AmberButton      = Color.FromArgb(245, 158, 11),   // amber-500
        HeaderFont       = _headerFont,
        SubHeaderFont    = _subHeaderFont,
        BodyFont         = _bodyFont,
        ButtonFont       = _buttonFont,
        SmallFont        = _smallFont,
        StartStopFont    = _startStopFont,
        BoldBodyFont     = _boldBodyFont,
    };

    public static readonly ThemeData Dark = new()
    {
        Background       = Color.FromArgb(24, 24, 27),     // #18181B zinc-900
        Surface          = Color.FromArgb(39, 39, 42),      // #27272A zinc-800
        SurfaceBorder    = Color.FromArgb(63, 63, 70),      // #3F3F46 zinc-700
        Primary          = Color.FromArgb(59, 130, 246),    // #3B82F6 blue-500
        PrimaryHover     = Color.FromArgb(37, 99, 235),     // #2563EB blue-600
        PrimaryLight     = Color.FromArgb(30, 58, 138),     // #1E3A8A blue-900
        TextPrimary      = Color.FromArgb(244, 244, 245),   // #F4F4F5 zinc-100
        TextSecondary    = Color.FromArgb(161, 161, 170),   // #A1A1AA zinc-400
        TextMuted        = Color.FromArgb(113, 113, 122),   // #71717A zinc-500
        TextOnPrimary    = Color.White,
        Success          = Color.FromArgb(34, 197, 94),     // #22C55E green-500
        SuccessLight     = Color.FromArgb(20, 83, 45),      // #14532D green-900
        Danger           = Color.FromArgb(239, 68, 68),     // #EF4444 red-500
        DangerLight      = Color.FromArgb(127, 29, 29),     // #7F1D1D red-900
        ButtonDefault    = Color.FromArgb(52, 52, 56),      // zinc-750
        ButtonDefaultText= Color.FromArgb(212, 212, 216),   // #D4D4D8 zinc-300
        InputBackground  = Color.FromArgb(39, 39, 42),      // #27272A zinc-800
        InputBorder      = Color.FromArgb(82, 82, 91),      // #52525B zinc-600
        UpdatePanelBg    = Color.FromArgb(69, 26, 3),       // amber-950
        UpdatePanelBorder= Color.FromArgb(146, 64, 14),     // amber-800
        UpdatePanelText  = Color.FromArgb(253, 230, 138),   // amber-200
        AmberButton      = Color.FromArgb(245, 158, 11),    // amber-500
        HeaderFont       = _headerFont,
        SubHeaderFont    = _subHeaderFont,
        BodyFont         = _bodyFont,
        ButtonFont       = _buttonFont,
        SmallFont        = _smallFont,
        StartStopFont    = _startStopFont,
        BoldBodyFont     = _boldBodyFont,
    };

    public static ThemeData Current { get; private set; } = Light;
    public static bool IsDark => Current == Dark;

    public static event Action? ThemeChanged;

    public static void SetTheme(bool dark)
    {
        Current = dark ? Dark : Light;
        ThemeChanged?.Invoke();
    }

    /// <summary>Apply flat-blue styling to a primary action button.</summary>
    public static void ApplyPrimary(Button btn)
    {
        btn.BackColor = Current.Primary;
        btn.ForeColor = Current.TextOnPrimary;
        btn.FlatStyle = FlatStyle.Flat;
        btn.FlatAppearance.BorderSize = 0;
        btn.Font = Current.ButtonFont;
        btn.Cursor = Cursors.Hand;
    }

    /// <summary>Apply flat styling to a secondary/default button.</summary>
    public static void ApplySecondary(Button btn)
    {
        btn.BackColor = Current.ButtonDefault;
        btn.ForeColor = Current.ButtonDefaultText;
        btn.FlatStyle = FlatStyle.Flat;
        btn.FlatAppearance.BorderColor = Current.SurfaceBorder;
        btn.FlatAppearance.BorderSize = 1;
        btn.Font = Current.BodyFont;
        btn.Cursor = Cursors.Hand;
    }
}
