using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using VPNRouter.Core.Json;

namespace VPNRouter.Core.Services;

/// <summary>
/// JSON-backed strikes counter that detects "App can't even reach UI"
/// loops and breaks them out automatically. v2.32.0.
///
/// <para>Mirrors Chrome's bad-flags-recovery / Office "we're sorry,
/// something went wrong" loop break: every <c>Program.Main</c> entry
/// increments a persisted counter; only when the UI is up enough to
/// render <c>MainWindow.Opened</c> do we call <see cref="MarkStable"/>
/// and zero the counter. Three consecutive failures trigger the first
/// recovery tier (<see cref="SelfRepairThreshold"/> →
/// <c>self-repair</c>); persistent loops escalate to config reset at
/// <see cref="ConfigResetThreshold"/> and a Safe-Mode prompt at
/// <see cref="SafeModePromptThreshold"/>.</para>
///
/// <para><b>Loop guard</b>: each tier records when it last triggered
/// (<see cref="State.LastSelfRepairUtc"/> et al.) and declines to
/// re-trigger within <see cref="CooldownMinutes"/>. Without this, a
/// SelfRepair that itself fails to install would re-fire on every
/// rapid relaunch within the same minute. Cooldown matches the existing
/// <see cref="SelfRepair"/> 10-minute window so the two layers
/// reinforce rather than fight.</para>
///
/// <para><b>File</b>:
/// <c>%ProgramData%\VPNRouter\launch-counter.json</c> (Windows). Schema
/// is camelCase JSON. All writes are best-effort — a counter that
/// can't persist (read-only fs, AV scrubbing) silently no-ops rather
/// than blocking app startup.</para>
/// </summary>
public static class LaunchFailureCounter
{
    private const string DefaultFileName = "launch-counter.json";

    /// <summary>3 strikes → trigger <see cref="SelfRepair"/> web reinstall.</summary>
    public const int SelfRepairThreshold = 3;

    /// <summary>5 strikes → ALSO move <c>config.yaml</c> aside and reset to defaults.</summary>
    public const int ConfigResetThreshold = 5;

    /// <summary>7 strikes → console-mode "Safe Mode" prompt pointing at <c>repair.cmd</c>.</summary>
    public const int SafeModePromptThreshold = 7;

    private static int _cooldownMinutes = 10;

    /// <summary>Per-tier cooldown window, in minutes (default 10).</summary>
    public static int CooldownMinutes => _cooldownMinutes;

    /// <summary>
    /// Override the cooldown window. Used by tests to drive
    /// rapid-relaunch scenarios without sleeping for real minutes.
    /// </summary>
    public static void ResetCooldown(int minutes) => _cooldownMinutes = Math.Max(0, minutes);

    private static string DefaultPath => Path.Combine(AppPaths.DataDir, DefaultFileName);

    /// <summary>Persisted state. Public so tests + diagnostics can inspect.</summary>
    // Phase 7 Wave 34 (2026-05-19): explicit [JsonPropertyName] camelCase
    // wire keys, pinning the same shape that the local JsonOptions'
    // PropertyNamingPolicy=JsonNamingPolicy.CamelCase produced pre-Wave-34.
    // The JsonTypeInfo<T> overload ties to AppJsonContext's options instead
    // (no naming policy set), so the explicit attributes are the
    // wire-stable equivalent. Existing user state files on disk continue
    // to read cleanly because PropertyNameCaseInsensitive=true is on the
    // context too (added Wave 25).
    public sealed class State
    {
        [JsonPropertyName("consecutiveFailures")]
        public int ConsecutiveFailures { get; set; }
        [JsonPropertyName("lastFailureUtc")]
        public string LastFailureUtc { get; set; } = string.Empty;
        [JsonPropertyName("lastFailureType")]
        public string LastFailureType { get; set; } = string.Empty;
        [JsonPropertyName("lastSuccessUtc")]
        public string LastSuccessUtc { get; set; } = string.Empty;
        [JsonPropertyName("lastSelfRepairUtc")]
        public string LastSelfRepairUtc { get; set; } = string.Empty;
        [JsonPropertyName("lastConfigResetUtc")]
        public string LastConfigResetUtc { get; set; } = string.Empty;
        [JsonPropertyName("lastSafeModePromptUtc")]
        public string LastSafeModePromptUtc { get; set; } = string.Empty;
    }

    /// <summary>
    /// Bump the failure counter on Main entry. Call BEFORE any other
    /// startup work so a crash later in Main still leaves the counter
    /// incremented — that is the whole point: we count "started but
    /// did not reach <see cref="MarkStable"/>" as a failure.
    /// </summary>
    /// <param name="failureType">
    /// Optional last-known exception type (free-form). Typically null
    /// at entry; populated by an unhandled-exception handler later.
    /// </param>
    /// <param name="path">Override file path (tests).</param>
    /// <returns>The new counter value after increment.</returns>
    public static int IncrementOnStartup(string? failureType = null, string? path = null)
    {
        var p = path ?? DefaultPath;
        var s = TryLoad(p);
        s.ConsecutiveFailures++;
        s.LastFailureUtc = DateTime.UtcNow.ToString("o");
        if (!string.IsNullOrEmpty(failureType))
            s.LastFailureType = failureType;
        TrySave(p, s);
        return s.ConsecutiveFailures;
    }

    /// <summary>
    /// Annotate the last-failure-type without changing the counter.
    /// Wire from <c>AppDomain.UnhandledException</c> so the next
    /// launch can surface what crashed last time.
    /// </summary>
    public static void RecordFailureType(string failureType, string? path = null)
    {
        if (string.IsNullOrEmpty(failureType)) return;
        var p = path ?? DefaultPath;
        var s = TryLoad(p);
        s.LastFailureType = failureType;
        TrySave(p, s);
    }

    /// <summary>
    /// Reset the counter — call from <c>MainWindow.Opened</c> (or any
    /// equivalent "the UI made it past the danger zone" milestone).
    /// </summary>
    public static void MarkStable(string? path = null)
    {
        var p = path ?? DefaultPath;
        var s = TryLoad(p);
        s.ConsecutiveFailures = 0;
        s.LastSuccessUtc = DateTime.UtcNow.ToString("o");
        TrySave(p, s);
    }

    /// <summary>
    /// Recommend the next recovery action given the current strike
    /// count and per-tier cooldowns.
    /// <para><b>Side effect</b>: when returning a non-<c>"none"</c>
    /// action, stamps that tier's cooldown so a rapid relaunch
    /// doesn't re-trigger the same recovery within
    /// <see cref="CooldownMinutes"/>. The caller is expected to
    /// dispatch the action immediately.</para>
    /// </summary>
    /// <returns>
    /// One of: <c>"none"</c>, <c>"self-repair"</c>,
    /// <c>"config-reset"</c>, <c>"safe-mode-prompt"</c>.
    /// </returns>
    public static string RecommendAction(string? path = null)
    {
        var p = path ?? DefaultPath;
        var s = TryLoad(p);
        var now = DateTime.UtcNow;
        var cooldown = TimeSpan.FromMinutes(_cooldownMinutes);

        // Highest tier wins. The tiers are additive in spirit (3 →
        // self-repair, 5 → ALSO config-reset, 7 → ALSO safe-mode-prompt)
        // but we only return ONE action per launch — the one that
        // hasn't been used in the last cooldown window. This produces
        // the expected escalation: self-repair fires first; if that
        // didn't break the loop and the counter keeps climbing,
        // config-reset takes over; finally the user-facing prompt.
        if (s.ConsecutiveFailures >= SafeModePromptThreshold &&
            !WithinCooldown(s.LastSafeModePromptUtc, now, cooldown))
        {
            s.LastSafeModePromptUtc = now.ToString("o");
            TrySave(p, s);
            return "safe-mode-prompt";
        }

        if (s.ConsecutiveFailures >= ConfigResetThreshold &&
            !WithinCooldown(s.LastConfigResetUtc, now, cooldown))
        {
            s.LastConfigResetUtc = now.ToString("o");
            TrySave(p, s);
            return "config-reset";
        }

        if (s.ConsecutiveFailures >= SelfRepairThreshold &&
            !WithinCooldown(s.LastSelfRepairUtc, now, cooldown))
        {
            s.LastSelfRepairUtc = now.ToString("o");
            TrySave(p, s);
            return "self-repair";
        }

        return "none";
    }

    /// <summary>Read the current state without mutation. Tests use this.</summary>
    public static State Read(string? path = null) => TryLoad(path ?? DefaultPath);

    /// <summary>Wipe the state file. Tests use this.</summary>
    public static void Reset(string? path = null)
    {
        var p = path ?? DefaultPath;
        try { if (File.Exists(p)) File.Delete(p); } catch { /* best-effort */ }
    }

    private static bool WithinCooldown(string isoStamp, DateTime now, TimeSpan window)
    {
        if (string.IsNullOrEmpty(isoStamp)) return false;
        if (!DateTime.TryParse(isoStamp, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var stamp))
            return false;
        return (now - stamp.ToUniversalTime()) < window;
    }

    private static State TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return new State();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, Json.AppJsonContext.Default.State) ?? new State();
        }
        catch
        {
            // Corrupted / unreadable file — start fresh. Don't loop on
            // a parse error.
            return new State();
        }
    }

    private static void TrySave(string path, State state)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, Json.AppJsonContext.Default.State));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
        catch
        {
            // Counter is advisory; never block startup if it can't be
            // persisted (read-only fs, AV interference, locked file).
        }
    }

    // Phase 7 Wave 34 (2026-05-19): retired the local JsonOptions field.
    // Both Read/Save now use the JsonTypeInfo<State> overload directly
    // against AppJsonContext.Default. CamelCase wire format preserved
    // via [JsonPropertyName] attributes on each State property (see
    // the class declaration above) — independent of the context's
    // (default-null) PropertyNamingPolicy. DefaultIgnoreCondition.Never
    // dropped because State's properties have no nullable defaults that
    // would otherwise be elided.
}
