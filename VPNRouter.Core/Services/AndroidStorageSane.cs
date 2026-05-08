using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// v2.32.0 (AND-SR-1, plans/vpnrouter-platform-current-diff.md §16.3) —
/// hooks <see cref="AppSettingsSane.EnsureSane"/> + per-key enum
/// normalisation onto Android's <c>SharedPreferences</c> storage so the
/// platform inherits desktop's central self-repair contract instead of
/// drifting behind inline per-key guards.
///
/// <para>Desktop runs <see cref="SettingsLoader.LoadCore"/> →
/// deserialise → <see cref="AppSettingsSane.EnsureSane"/> →
/// <see cref="SettingsValidator.Validate"/>. On Android the keys live as
/// scalar SharedPreferences entries and never form an in-memory
/// <see cref="AppSettings"/> tree — so this helper bridges the two:
/// build a transient AppSettings, run the shared null-safety sweep
/// (forward-compat hook), normalise documented enum keys against their
/// allowed sets, and write back any field that needed repair. Call once
/// on app launch (from <c>AndroidApp.OnFrameworkInitializationCompleted</c>)
/// before any consumer reads.</para>
///
/// <para>Backend-agnostic on purpose: takes <paramref name="get"/> /
/// <paramref name="set"/> delegates so VPNRouter.Tests can drive it
/// against an in-memory <see cref="System.Collections.Generic.Dictionary{TKey, TValue}"/>
/// without an Android device.</para>
/// </summary>
public static class AndroidStorageSane
{
    /// <summary>
    /// SR-1 contract for one persisted enum-shaped key. Stored value must
    /// fall in <see cref="Allowed"/>; any other non-empty raw value is
    /// quarantined and reset to <see cref="DefaultValue"/>.
    /// </summary>
    public sealed record EnumKeySpec(
        string Key,
        IReadOnlyCollection<string> Allowed,
        string DefaultValue);

    /// <summary>
    /// Outcome of one <see cref="RepairAllOnLoad"/> pass. <see cref="Changes"/>
    /// is one human-readable line per repaired key (matches the desktop
    /// <see cref="SettingsLoader.LastRecoveryNotice"/> tone). Empty when
    /// the store was already clean.
    /// </summary>
    public sealed record RepairResult(IReadOnlyList<string> Changes);

    /// <summary>
    /// Walks the store via <paramref name="get"/>, repairs any enum key
    /// in <paramref name="enumKeys"/> whose stored value is unknown, and
    /// writes the canonical default through <paramref name="set"/>.
    /// Optionally calls <paramref name="quarantine"/> to stash the bad
    /// payload for forensics before overwriting (mirrors AndroidStorage's
    /// <c>{key}__corrupt_*</c> sibling pattern).
    ///
    /// <para>Idempotent: a clean store produces an empty result; a second
    /// pass after the first repaired everything also produces an empty
    /// result. Safe to call any number of times.</para>
    /// </summary>
    public static RepairResult RepairAllOnLoad(
        Func<string, string?> get,
        Action<string, string?> set,
        IEnumerable<EnumKeySpec> enumKeys,
        Action<string, string?>? quarantine = null)
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(enumKeys);

        // ── Phase 1 ── transient AppSettings + EnsureSane.
        // Today this is structurally a no-op: Android persists scalar
        // keys, not an AppSettings tree, so a freshly-constructed
        // instance has no nulls for EnsureSane to repair. The call is
        // wired in deliberately so any forward-compat scalar default
        // added to EnsureSane in Core is picked up by every caller that
        // already runs RepairAllOnLoad — no Android-specific change
        // needed when invariants grow.
        new AppSettings().EnsureSane();

        // ── Phase 2 ── per-key SR-1 enum normalisation.
        // Empty/missing keys are LEFT alone — that's the first-run path,
        // and consumers' inline getters substitute defaults at read time.
        // Writing a default eagerly here would burn a SharedPreferences
        // commit on every fresh install for no semantic gain.
        var changes = new List<string>();

        foreach (var spec in enumKeys)
        {
            string? raw;
            try { raw = get(spec.Key); }
            catch { continue; }

            if (string.IsNullOrWhiteSpace(raw)) continue;

            var match = spec.Allowed.FirstOrDefault(v =>
                string.Equals(v, raw, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                // Value valid; normalise casing if it differs from the
                // canonical entry. Idempotent — no recovery notice.
                if (!string.Equals(match, raw, StringComparison.Ordinal))
                {
                    try { set(spec.Key, match); } catch { /* best-effort */ }
                }
                continue;
            }

            // Unknown value — quarantine + reset.
            try { quarantine?.Invoke(spec.Key, raw); } catch { /* best-effort */ }
            try { set(spec.Key, spec.DefaultValue); } catch { /* best-effort */ }
            changes.Add(
                $"setting '{spec.Key}' had unknown value '{raw}'; reset to '{spec.DefaultValue}'");
        }

        return new RepairResult(changes);
    }
}
