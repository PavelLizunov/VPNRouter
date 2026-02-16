namespace VPNRouter.GUI;

/// <summary>
/// Centralized color palette and font definitions for the Virtual Penguin Network UI.
/// Light theme with blue accent (Tailwind Blue/Slate inspired).
/// </summary>
internal static class Theme
{
    // ── Primary palette ──
    public static readonly Color Background       = Color.FromArgb(245, 248, 252);  // #F5F8FC light blue-gray
    public static readonly Color Surface           = Color.FromArgb(255, 255, 255);  // #FFFFFF white panels
    public static readonly Color SurfaceBorder     = Color.FromArgb(218, 226, 237);  // #DAE2ED borders

    // ── Accent ──
    public static readonly Color Primary           = Color.FromArgb(37, 99, 235);    // #2563EB Blue-600
    public static readonly Color PrimaryHover      = Color.FromArgb(29, 78, 216);    // #1D4ED8 Blue-700
    public static readonly Color PrimaryLight      = Color.FromArgb(219, 234, 254);  // #DBEAFE Blue-100

    // ── Text ──
    public static readonly Color TextPrimary       = Color.FromArgb(30, 41, 59);     // #1E293B Slate-800
    public static readonly Color TextSecondary     = Color.FromArgb(100, 116, 139);  // #64748B Slate-500
    public static readonly Color TextMuted         = Color.FromArgb(148, 163, 184);  // #94A3B8 Slate-400
    public static readonly Color TextOnPrimary     = Color.White;

    // ── Status ──
    public static readonly Color Success           = Color.FromArgb(22, 163, 74);    // #16A34A Green-600
    public static readonly Color SuccessLight      = Color.FromArgb(220, 252, 231);  // #DCFCE7 Green-100
    public static readonly Color Danger            = Color.FromArgb(220, 38, 38);    // #DC2626 Red-600
    public static readonly Color DangerLight       = Color.FromArgb(254, 226, 226);  // #FEE2E2 Red-100

    // ── Controls ──
    public static readonly Color ButtonDefault     = Color.FromArgb(241, 245, 249);  // #F1F5F9 Slate-100
    public static readonly Color ButtonDefaultText = Color.FromArgb(51, 65, 85);     // #334155 Slate-700
    public static readonly Color InputBackground   = Color.White;
    public static readonly Color InputBorder       = Color.FromArgb(203, 213, 225);  // #CBD5E1 Slate-300

    // ── Fonts ──
    public static readonly Font HeaderFont     = new("Segoe UI", 16f, FontStyle.Bold);
    public static readonly Font SubHeaderFont  = new("Segoe UI", 10f, FontStyle.Regular);
    public static readonly Font BodyFont       = new("Segoe UI", 9.5f, FontStyle.Regular);
    public static readonly Font ButtonFont     = new("Segoe UI", 10f, FontStyle.Bold);
    public static readonly Font SmallFont      = new("Segoe UI", 8.5f, FontStyle.Regular);
    public static readonly Font StartStopFont  = new("Segoe UI", 12f, FontStyle.Bold);

    /// <summary>Apply flat-blue styling to a primary action button.</summary>
    public static void ApplyPrimary(Button btn)
    {
        btn.BackColor = Primary;
        btn.ForeColor = TextOnPrimary;
        btn.FlatStyle = FlatStyle.Flat;
        btn.FlatAppearance.BorderSize = 0;
        btn.Font = ButtonFont;
        btn.Cursor = Cursors.Hand;
    }

    /// <summary>Apply flat styling to a secondary/default button.</summary>
    public static void ApplySecondary(Button btn)
    {
        btn.BackColor = ButtonDefault;
        btn.ForeColor = ButtonDefaultText;
        btn.FlatStyle = FlatStyle.Flat;
        btn.FlatAppearance.BorderColor = SurfaceBorder;
        btn.FlatAppearance.BorderSize = 1;
        btn.Font = BodyFont;
        btn.Cursor = Cursors.Hand;
    }
}
