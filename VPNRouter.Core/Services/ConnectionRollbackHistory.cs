#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using VPNRouter.Core.Json;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public enum RollbackAssistKind
{
    None = 0,
    NeedsLocalDiagnostics,
    OfferRollback,
    NeedsPriorVersionAttestation,
}

public enum RollbackConfidence
{
    None = 0,
    Medium,
    High,
}

/// <summary>
/// A client-side allowlist entry for a regression that is already known when the
/// build ships. It is never populated from model output, subscription data or a
/// remote release note.
/// </summary>
public sealed record KnownRegression(string AffectedBuildId, string TargetVersion, string ReasonCode);

public sealed record RollbackAssistDecision(
    RollbackAssistKind Kind,
    int ConsecutiveFailures,
    string CurrentVersion,
    string CurrentBuildId,
    string ConfigFingerprint,
    string? PreviousWorkingVersion,
    string? PreviousWorkingBuildId,
    string? PreviousInstalledVersion,
    RollbackConfidence Confidence,
    string ReasonCode,
    bool WasDismissed)
{
    public bool ShouldShow => !WasDismissed && Kind is
        RollbackAssistKind.NeedsLocalDiagnostics or
        RollbackAssistKind.OfferRollback or
        RollbackAssistKind.NeedsPriorVersionAttestation;
}

public sealed record RollbackHandoffResult(
    bool HadPending,
    bool Success,
    string TargetVersion,
    string ObservedVersion);

public static class RollbackBuildIdentity
{
    public static string Read(string version)
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly;
        return $"{version}:{assembly.ManifestModule.ModuleVersionId:N}";
    }
}

/// <summary>
/// Persistent history for the rollback assistant. The file stores only build,
/// configuration and attempt metadata; connection identifiers and raw errors are
/// never written.
/// </summary>
public sealed class ConnectionRollbackHistory
{
    public const int FailureThreshold = 3;
    public const string FileName = "connection-rollback.json";
    private const int VerifiedHistoryLimit = 8;
    private const int FailureHistoryLimit = 20;
    private static readonly TimeSpan RollbackChoiceLifetime = TimeSpan.FromDays(90);

    private readonly object _gate = new();
    private readonly string _path;
    private readonly Func<DateTimeOffset> _clock;

    public string CurrentVersion { get; }
    public string CurrentBuildId { get; }

    public ConnectionRollbackHistory(
        string? path = null,
        Func<DateTimeOffset>? clock = null,
        string? buildId = null,
        string? currentVersion = null)
    {
        CurrentVersion = string.IsNullOrWhiteSpace(currentVersion) ? AppVersion.Version : currentVersion;
        CurrentBuildId = string.IsNullOrWhiteSpace(buildId)
            ? RollbackBuildIdentity.Read(CurrentVersion)
            : buildId;
        _path = path ?? Path.Combine(AppPaths.DataDir, FileName);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public RollbackAssistDecision ObserveAttempt(
        string attemptId,
        string configFingerprint,
        ConnectionAttemptAssessment assessment,
        KnownRegression? knownRegression = null,
        string? signedRecoveryVersion = null,
        string? activeConfigIncompatibility = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(configFingerprint);

        lock (_gate)
        {
            var state = Load();
            var changed = EnsureCurrent(state, configFingerprint);
            var effective = assessment.CountsTowardRollback
                ? attemptId
                : string.Empty;
            if (changed)
                Save(state);

            if (!assessment.CountsTowardRollback)
                return Evaluate(state, configFingerprint, knownRegression, signedRecoveryVersion,
                    activeConfigIncompatibility);

            if (state.LastAttemptId == effective)
                return Evaluate(state, configFingerprint, knownRegression, signedRecoveryVersion,
                    activeConfigIncompatibility);

            state.LastAttemptId = effective;
            state.ConsecutiveFailures++;
            state.Failures.Add(new FailureRecord
            {
                AtUtc = _clock().ToString("O", CultureInfo.InvariantCulture),
                Category = assessment.Category.ToString(),
                DetailCode = Truncate(assessment.DetailCode, 64),
            });
            if (state.Failures.Count > FailureHistoryLimit)
                state.Failures.RemoveRange(0, state.Failures.Count - FailureHistoryLimit);

            Save(state);
            return Evaluate(state, configFingerprint, knownRegression, signedRecoveryVersion,
                activeConfigIncompatibility);
        }
    }

    public void ClearFailuresAfterConnection()
    {
        lock (_gate)
        {
            var state = Load();
            if (state.ConsecutiveFailures == 0 || state.BuildId != CurrentBuildId) return;
            state.ConsecutiveFailures = 0;
            state.Failures.Clear();
            state.LastAttemptId = string.Empty;
            Save(state);
        }
    }

    public RollbackAssistDecision ObserveSuccess(
        string configFingerprint,
        KnownRegression? knownRegression = null,
        string? signedRecoveryVersion = null,
        string? activeConfigIncompatibility = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configFingerprint);

        lock (_gate)
        {
            var state = Load();
            EnsureCurrent(state, configFingerprint);
            state.ConsecutiveFailures = 0;
            state.Failures.Clear();
            state.LastAttemptId = string.Empty;
            state.DismissedBuildId = string.Empty;
            state.DismissedConfigFingerprint = string.Empty;
            state.DismissedAtUtc = string.Empty;

            var existing = state.VerifiedBuilds.FirstOrDefault(item =>
                item.BuildId == CurrentBuildId &&
                item.ConfigFingerprint == configFingerprint);
            if (existing == null)
            {
                state.VerifiedBuilds.Add(new VerifiedBuild
                {
                    BuildId = CurrentBuildId,
                    Version = CurrentVersion,
                    ConfigFingerprint = configFingerprint,
                    VerifiedAtUtc = _clock().ToString("O", CultureInfo.InvariantCulture),
                });
            }
            else
            {
                existing.Version = CurrentVersion;
                existing.VerifiedAtUtc = _clock().ToString("O", CultureInfo.InvariantCulture);
            }

            state.VerifiedBuilds = state.VerifiedBuilds
                .OrderByDescending(item => ParseStamp(item.VerifiedAtUtc))
                .Take(VerifiedHistoryLimit)
                .ToList();

            Save(state);
            return Evaluate(state, configFingerprint, knownRegression, signedRecoveryVersion,
                activeConfigIncompatibility);
        }
    }

    public RollbackAssistDecision Evaluate(
        string configFingerprint,
        KnownRegression? knownRegression = null,
        string? signedRecoveryVersion = null,
        string? activeConfigIncompatibility = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configFingerprint);
        lock (_gate)
            return Evaluate(Load(), configFingerprint, knownRegression, signedRecoveryVersion,
                activeConfigIncompatibility);
    }

    public void Dismiss(string configFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configFingerprint);
        lock (_gate)
        {
            var state = Load();
            EnsureCurrent(state, configFingerprint);
            state.DismissedBuildId = CurrentBuildId;
            state.DismissedConfigFingerprint = configFingerprint;
            state.DismissedAtUtc = _clock().ToString("O", CultureInfo.InvariantCulture);
            Save(state);
        }
    }

    public void BeginRollbackHandoff(string targetVersion, string configFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(configFingerprint);
        lock (_gate)
        {
            var state = Load();
            EnsureCurrent(state, configFingerprint);
            var now = _clock().ToString("O", CultureInfo.InvariantCulture);
            state.PendingHandoff = new HandoffState
            {
                TargetVersion = NormalizeVersion(targetVersion),
                FromVersion = CurrentVersion,
                ConfigFingerprint = configFingerprint,
                StartedAtUtc = now,
            };
            state.RollbackChoice = new RollbackChoiceState
            {
                TargetVersion = NormalizeVersion(targetVersion),
                FromVersion = CurrentVersion,
                ConfigFingerprint = configFingerprint,
                AtUtc = now,
                SuppressNewer = true,
            };
            Save(state);
        }
    }

    public void FailRollbackHandoff(string targetVersion)
    {
        lock (_gate)
        {
            var state = Load();
            var target = state.PendingHandoff?.TargetVersion ?? NormalizeVersion(targetVersion);
            state.LastHandoff = new HandoffResultState
            {
                TargetVersion = target,
                ObservedVersion = CurrentVersion,
                Success = false,
                AtUtc = _clock().ToString("O", CultureInfo.InvariantCulture),
            };
            state.PendingHandoff = null;
            state.RollbackChoice = null;
            Save(state);
        }
    }

    public RollbackHandoffResult? CompleteHandoff(string observedVersion)
    {
        lock (_gate)
        {
            var state = Load();
            if (state.PendingHandoff is not { } pending)
                return null;

            var observed = NormalizeVersion(observedVersion);
            var success = VersionsEqual(observed, pending.TargetVersion);
            state.LastHandoff = new HandoffResultState
            {
                TargetVersion = pending.TargetVersion,
                ObservedVersion = observed,
                Success = success,
                AtUtc = _clock().ToString("O", CultureInfo.InvariantCulture),
            };
            state.PendingHandoff = null;
            if (!success)
                state.RollbackChoice = null;
            Save(state);
            return new RollbackHandoffResult(true, success, pending.TargetVersion, observed);
        }
    }

    public void AcknowledgeHandoffResult()
    {
        lock (_gate)
        {
            var state = Load();
            if (state.LastHandoff == null)
                return;
            state.LastHandoff = null;
            Save(state);
        }
    }

    // Builds that contain this code honor the choice. The published 2.50.5
    // build predates it: its startup check ignores AutoCheck and still shows a
    // dismissible banner, but it never installs without the explicit Update
    // command and cannot read this state.
    public bool ShouldSuppressAutomaticUpdates()
    {
        lock (_gate)
        {
            var state = Load();
            var choice = state.RollbackChoice;
            if (choice == null || !choice.SuppressNewer)
                return false;

            var now = _clock();
            var started = ParseStamp(choice.AtUtc);
            if (started == DateTimeOffset.MinValue || now - started > RollbackChoiceLifetime ||
                !VersionsEqual(CurrentVersion, choice.TargetVersion))
            {
                state.RollbackChoice = null;
                Save(state);
                return false;
            }

            return true;
        }
    }

    public void ClearRollbackChoice()
    {
        lock (_gate)
        {
            var state = Load();
            if (state.RollbackChoice == null)
                return;
            state.RollbackChoice = null;
            Save(state);
        }
    }

    internal bool HasRepeatedFailures()
    {
        lock (_gate)
        {
            var state = Load();
            return state.BuildId == CurrentBuildId && state.ConsecutiveFailures >= FailureThreshold;
        }
    }

    public static string? ComputeConfigFingerprint(AppSettings? settings)
    {
        if (settings?.App == null || settings.Vless == null) return null;
        try { return Fingerprint(settings); }
        catch (Exception)
        {
            // Recovery evidence is optional. An unreadable profile must not abort Connect.
            return null;
        }
    }

    private static string? Fingerprint(AppSettings settings)
    {
        // Project the submitted settings onto the inputs that affect the connection.
        // No profile values, paths, or credentials are written to the history file.
        var snapshot = SettingsLoader.Copy(settings);
        var app = snapshot.App;
        if (string.Equals(app.ConfigMode, "custom", StringComparison.OrdinalIgnoreCase))
        {
            var path = VpnEngine.ResolveCustomConfigPath(snapshot);
            if (!File.Exists(path)) return null;
            using var file = File.OpenRead(path);
            app.CustomConfig = Convert.ToHexString(SHA256.HashData(file));
            snapshot.Vless = new VlessConfig();
        }
        else
        {
            VlessServersResolver.Resolve(snapshot);
            snapshot.Vless = new VlessConfig
            {
                Servers = snapshot.Vless.GetActiveServers(),
                ActiveServer = snapshot.Vless.ActiveServer,
                AutoSelectBestServer = snapshot.Vless.AutoSelectBestServer,
            };
            app.CustomConfig = string.Empty;
        }
        app.CustomConfigs = [];
        app.SubscriptionUrl = app.Subscriptions?.FirstOrDefault(feed => feed.Enabled &&
            feed.Id == app.ActiveSubscriptionId)?.Url ?? string.Empty;
        app.Subscriptions = [];
        app.SubscriptionServers = [];
        app.Theme = app.UiMode = app.Language = string.Empty;
        snapshot.Update = new UpdateSettings();
        return SettingsLoader.GetSubmittedRevision(snapshot);
    }

    private RollbackAssistDecision Evaluate(
        ConnectionRollbackState state,
        string configFingerprint,
        KnownRegression? knownRegression,
        string? signedRecoveryVersion,
        string? activeConfigIncompatibility)
    {
        var currentState = state.BuildId == CurrentBuildId &&
                           state.ConfigFingerprint == configFingerprint;
        var failures = currentState ? state.ConsecutiveFailures : 0;
        var dismissed = currentState &&
                        state.DismissedBuildId == CurrentBuildId &&
                        state.DismissedConfigFingerprint == configFingerprint;
        var previousInstalled = string.IsNullOrWhiteSpace(state.PreviousInstalledVersion)
            ? null
            : NormalizeVersion(state.PreviousInstalledVersion);
        var trustedTarget = !string.IsNullOrWhiteSpace(signedRecoveryVersion) &&
                            IsOlder(signedRecoveryVersion, CurrentVersion)
            ? NormalizeVersion(signedRecoveryVersion)
            : null;

        RollbackAssistDecision Make(
            RollbackAssistKind kind,
            string reasonCode,
            string? previousWorkingVersion,
            string? previousWorkingBuildId,
            RollbackConfidence confidence) =>
            new(kind,
                failures,
                CurrentVersion,
                CurrentBuildId,
                configFingerprint,
                previousWorkingVersion,
                previousWorkingBuildId,
                previousInstalled,
                confidence,
                reasonCode,
                dismissed);

        if (failures < FailureThreshold)
            return Make(RollbackAssistKind.None, "failure-threshold-not-reached", null, null,
                RollbackConfidence.None);

        if (!string.IsNullOrWhiteSpace(activeConfigIncompatibility))
            return Make(RollbackAssistKind.NeedsLocalDiagnostics,
                "active-config-incompatible:" + activeConfigIncompatibility,
                null, null, RollbackConfidence.None);

        if (knownRegression != null &&
            string.Equals(knownRegression.AffectedBuildId, CurrentBuildId, StringComparison.Ordinal))
        {
            var target = NormalizeVersion(knownRegression.TargetVersion);
            if (trustedTarget == null || VersionsEqual(target, trustedTarget))
                return Make(RollbackAssistKind.OfferRollback, "known-regression", target, null,
                    RollbackConfidence.High);
            return Make(RollbackAssistKind.NeedsLocalDiagnostics, "known-regression-untrusted-target",
                null, null, RollbackConfidence.None);
        }

        var previous = state.VerifiedBuilds
            .Where(item => item.BuildId != CurrentBuildId &&
                           item.ConfigFingerprint == configFingerprint &&
                           IsOlder(item.Version, CurrentVersion))
            .OrderByDescending(item => ParseStamp(item.VerifiedAtUtc))
            .FirstOrDefault();

        if (trustedTarget == null)
        {
            if (previous == null)
                return Make(RollbackAssistKind.NeedsLocalDiagnostics, "insufficient-history",
                    null, null, RollbackConfidence.None);
            return Make(RollbackAssistKind.OfferRollback, "previous-build-verified",
                NormalizeVersion(previous.Version), previous.BuildId,
                ConfidenceFor(previous, failures));
        }

        var attestation = state.Attestations.FirstOrDefault(item =>
            item.ConfigFingerprint == configFingerprint &&
            VersionsEqual(item.TargetVersion, trustedTarget));
        if (attestation != null && !string.Equals(attestation.Answer, "worked", StringComparison.Ordinal))
            return Make(RollbackAssistKind.NeedsLocalDiagnostics, "attestation-negative",
                null, null, RollbackConfidence.None);

        if (previous != null && VersionsEqual(previous.Version, trustedTarget))
            return Make(RollbackAssistKind.OfferRollback, "previous-build-verified",
                trustedTarget, previous.BuildId, ConfidenceFor(previous, failures));

        if (attestation != null)
            return Make(RollbackAssistKind.OfferRollback, "attested-previous-install",
                trustedTarget, null, RollbackConfidence.Medium);

        if (previousInstalled != null && VersionsEqual(previousInstalled, trustedTarget))
            return Make(RollbackAssistKind.NeedsPriorVersionAttestation, "attestation-required",
                trustedTarget, null, RollbackConfidence.None);

        return Make(RollbackAssistKind.NeedsLocalDiagnostics, "insufficient-history",
            null, null, RollbackConfidence.None);
    }

    private RollbackConfidence ConfidenceFor(VerifiedBuild previous, int failures)
    {
        var verifiedAt = ParseStamp(previous.VerifiedAtUtc);
        var age = verifiedAt == DateTimeOffset.MinValue ? TimeSpan.MaxValue : _clock() - verifiedAt;
        return failures >= FailureThreshold + 1 || age <= TimeSpan.FromDays(30)
            ? RollbackConfidence.High
            : RollbackConfidence.Medium;
    }

    public string? PreviousInstalledVersion
    {
        get
        {
            lock (_gate)
            {
                var value = Load().PreviousInstalledVersion;
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
    }

    public void RememberPreviousInstalledVersion(string version, string source)
    {
        if (string.IsNullOrWhiteSpace(version) ||
            !UpdateChecker.TryParseSemVer(version, out var previous) ||
            !UpdateChecker.TryParseSemVer(CurrentVersion, out var current) ||
            previous.CompareTo(current) >= 0)
            return;

        var normalized = NormalizeVersion(version);
        lock (_gate)
        {
            var state = Load();
            if (string.Equals(state.PreviousInstalledVersion, normalized, StringComparison.OrdinalIgnoreCase))
                return;
            state.PreviousInstalledVersion = normalized;
            state.PreviousInstalledAtUtc = _clock().ToString("O", CultureInfo.InvariantCulture);
            state.PreviousInstalledSource = Truncate(source, 64);
            Save(state);
        }
    }

    public static void CapturePreviousInstalledVersion(string version, string source, string? path = null)
    {
        if (!RollbackArtifactPolicy.IsSupportedHost())
            return;
        try
        {
            new ConnectionRollbackHistory(path).RememberPreviousInstalledVersion(version, source);
        }
        catch
        {
        }
    }

    public void RecordPriorVersionAttestation(
        string targetVersion,
        string configFingerprint,
        bool? worked)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(configFingerprint);

        var target = NormalizeVersion(targetVersion);
        lock (_gate)
        {
            var state = Load();
            state.Attestations.RemoveAll(item =>
                item.ConfigFingerprint == configFingerprint &&
                VersionsEqual(item.TargetVersion, target));
            state.Attestations.Add(new AttestationRecord
            {
                TargetVersion = target,
                ConfigFingerprint = configFingerprint,
                Answer = worked switch
                {
                    true => "worked",
                    false => "not-worked",
                    _ => "unknown",
                },
                AtUtc = _clock().ToString("O", CultureInfo.InvariantCulture),
            });
            if (state.Attestations.Count > VerifiedHistoryLimit)
                state.Attestations.RemoveRange(0, state.Attestations.Count - VerifiedHistoryLimit);
            Save(state);
        }
    }

    private bool EnsureCurrent(ConnectionRollbackState state, string configFingerprint)
    {
        if (state.BuildId == CurrentBuildId && state.ConfigFingerprint == configFingerprint)
            return false;

        state.BuildId = CurrentBuildId;
        state.ConfigFingerprint = configFingerprint;
        state.ConsecutiveFailures = 0;
        state.Failures.Clear();
        state.LastAttemptId = string.Empty;
        state.DismissedBuildId = string.Empty;
        state.DismissedConfigFingerprint = string.Empty;
        state.DismissedAtUtc = string.Empty;
        return true;
    }

    private ConnectionRollbackState Load()
    {
        try
        {
            if (!File.Exists(_path))
                return new ConnectionRollbackState();
            var json = File.ReadAllText(_path);
            var state = JsonSerializer.Deserialize(json, AppJsonContext.Default.ConnectionRollbackState);
            if (state == null || state.SchemaVersion != 1)
                return new ConnectionRollbackState();
            state.Failures ??= [];
            state.VerifiedBuilds ??= [];
            state.Attestations ??= [];
            return state;
        }
        catch
        {
            return new ConnectionRollbackState();
        }
    }

    private void Save(ConnectionRollbackState state)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            var temp = _path + ".tmp";
            AppPaths.WritePrivateText(temp, JsonSerializer.Serialize(
                state, AppJsonContext.Default.ConnectionRollbackState));
            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
        }
    }

    private static bool IsOlder(string candidate, string current) =>
        UpdateChecker.TryParseSemVer(candidate, out var target) &&
        UpdateChecker.TryParseSemVer(current, out var running) &&
        target.CompareTo(running) < 0;

    private static bool VersionsEqual(string left, string right) =>
        UpdateChecker.TryParseSemVer(left, out var a) &&
        UpdateChecker.TryParseSemVer(right, out var b) &&
        a.CompareTo(b) == 0;

    private static string NormalizeVersion(string version)
    {
        var trimmed = version.Trim();
        return trimmed.StartsWith('v') || trimmed.StartsWith('V') ? trimmed[1..] : trimmed;
    }

    private static string Truncate(string value, int length) =>
        string.IsNullOrEmpty(value) ? string.Empty :
        value.Length <= length ? value : value[..length];

    private static DateTimeOffset ParseStamp(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

}

public sealed class ConnectionRollbackState
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("build_id")]
    public string BuildId { get; set; } = string.Empty;

    [JsonPropertyName("config_fingerprint")]
    public string ConfigFingerprint { get; set; } = string.Empty;

    [JsonPropertyName("consecutive_failures")]
    public int ConsecutiveFailures { get; set; }

    [JsonPropertyName("last_attempt_id")]
    public string LastAttemptId { get; set; } = string.Empty;

    [JsonPropertyName("failures")]
    public List<FailureRecord> Failures { get; set; } = [];

    [JsonPropertyName("verified_builds")]
    public List<VerifiedBuild> VerifiedBuilds { get; set; } = [];

    [JsonPropertyName("previous_installed_version")]
    public string PreviousInstalledVersion { get; set; } = string.Empty;

    [JsonPropertyName("previous_installed_at_utc")]
    public string PreviousInstalledAtUtc { get; set; } = string.Empty;

    [JsonPropertyName("previous_installed_source")]
    public string PreviousInstalledSource { get; set; } = string.Empty;

    [JsonPropertyName("attestations")]
    public List<AttestationRecord> Attestations { get; set; } = [];

    [JsonPropertyName("dismissed_build_id")]
    public string DismissedBuildId { get; set; } = string.Empty;

    [JsonPropertyName("dismissed_config_fingerprint")]
    public string DismissedConfigFingerprint { get; set; } = string.Empty;

    [JsonPropertyName("dismissed_at_utc")]
    public string DismissedAtUtc { get; set; } = string.Empty;

    [JsonPropertyName("pending_handoff")]
    public HandoffState? PendingHandoff { get; set; }

    [JsonPropertyName("rollback_choice")]
    public RollbackChoiceState? RollbackChoice { get; set; }

    [JsonPropertyName("last_handoff")]
    public HandoffResultState? LastHandoff { get; set; }
}

public sealed class AttestationRecord
{
    [JsonPropertyName("target_version")]
    public string TargetVersion { get; set; } = string.Empty;

    [JsonPropertyName("config_fingerprint")]
    public string ConfigFingerprint { get; set; } = string.Empty;

    [JsonPropertyName("answer")]
    public string Answer { get; set; } = string.Empty;

    [JsonPropertyName("at_utc")]
    public string AtUtc { get; set; } = string.Empty;
}

public sealed class FailureRecord
{
    [JsonPropertyName("at_utc")]
    public string AtUtc { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("detail_code")]
    public string DetailCode { get; set; } = string.Empty;
}

public sealed class VerifiedBuild
{
    [JsonPropertyName("build_id")]
    public string BuildId { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("config_fingerprint")]
    public string ConfigFingerprint { get; set; } = string.Empty;

    [JsonPropertyName("verified_at_utc")]
    public string VerifiedAtUtc { get; set; } = string.Empty;
}

public sealed class HandoffState
{
    [JsonPropertyName("target_version")]
    public string TargetVersion { get; set; } = string.Empty;

    [JsonPropertyName("from_version")]
    public string FromVersion { get; set; } = string.Empty;

    [JsonPropertyName("config_fingerprint")]
    public string ConfigFingerprint { get; set; } = string.Empty;

    [JsonPropertyName("started_at_utc")]
    public string StartedAtUtc { get; set; } = string.Empty;
}

public sealed class RollbackChoiceState
{
    [JsonPropertyName("target_version")]
    public string TargetVersion { get; set; } = string.Empty;

    [JsonPropertyName("from_version")]
    public string FromVersion { get; set; } = string.Empty;

    [JsonPropertyName("config_fingerprint")]
    public string ConfigFingerprint { get; set; } = string.Empty;

    [JsonPropertyName("at_utc")]
    public string AtUtc { get; set; } = string.Empty;

    [JsonPropertyName("suppress_newer")]
    public bool SuppressNewer { get; set; } = true;
}

public sealed class HandoffResultState
{
    [JsonPropertyName("target_version")]
    public string TargetVersion { get; set; } = string.Empty;

    [JsonPropertyName("observed_version")]
    public string ObservedVersion { get; set; } = string.Empty;

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("at_utc")]
    public string AtUtc { get; set; } = string.Empty;
}
