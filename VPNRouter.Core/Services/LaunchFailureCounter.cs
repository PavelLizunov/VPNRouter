using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using VPNRouter.Core.Json;

namespace VPNRouter.Core.Services;

public static class LaunchFailureCounter
{
    private const string DefaultFileName = "launch-counter.json";

    public const int SelfRepairThreshold = 3;

    public const int ConfigResetThreshold = 5;

    public const int SafeModePromptThreshold = 7;

    private static int _cooldownMinutes = 10;

    public static int CooldownMinutes => _cooldownMinutes;

    public static void ResetCooldown(int minutes) => _cooldownMinutes = Math.Max(0, minutes);

    private static string DefaultPath => Path.Combine(AppPaths.DataDir, DefaultFileName);

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

    public static void RecordFailureType(string failureType, string? path = null)
    {
        if (string.IsNullOrEmpty(failureType)) return;
        var p = path ?? DefaultPath;
        var s = TryLoad(p);
        s.LastFailureType = failureType;
        TrySave(p, s);
    }

    public static void MarkStable(string? path = null)
    {
        var p = path ?? DefaultPath;
        var s = TryLoad(p);
        s.ConsecutiveFailures = 0;
        s.LastSuccessUtc = DateTime.UtcNow.ToString("o");
        TrySave(p, s);
    }

    public static string RecommendAction(string? path = null)
    {
        var p = path ?? DefaultPath;
        var s = TryLoad(p);
        var now = DateTime.UtcNow;
        var cooldown = TimeSpan.FromMinutes(_cooldownMinutes);

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

    public static State Read(string? path = null) => TryLoad(path ?? DefaultPath);

    public static void Reset(string? path = null)
    {
        var p = path ?? DefaultPath;
        try { if (File.Exists(p)) File.Delete(p); } catch { }
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
        }
    }

}
