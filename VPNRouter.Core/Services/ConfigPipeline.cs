using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// Canonical config-generation pipeline (Phase 2F, 2026-05-17).
/// Single source of truth for the server-resolution + sing-box-JSON
/// generation chain in <em>generated / subscribe</em> ConfigMode. Called
/// from <see cref="VpnEngine.StartAsync"/> (initial connect) AND from
/// <see cref="HealthMonitor"/>'s auto-restart / hot-reload path.
///
/// <para><strong>Why this exists.</strong> Pre-2.32.x these two callers
/// had separate hand-rolled pipelines. The v2.28.2 silent leak slipped
/// through because <see cref="VlessServersResolver.Resolve"/> was added
/// to <c>VpnEngine.StartAsync</c> in one commit but the <c>VpnEngine.Apply</c>
/// hot-reload path was missed. Same class of drift could happen again
/// the next time a new step is bolted on. This helper makes that
/// impossible: anything added here propagates to every caller for free.</para>
///
/// <para><strong>What's <em>not</em> extracted.</strong> Three sibling
/// guards live OUTSIDE this helper because they are tightly coupled to
/// caller-specific orchestration:</para>
/// <list type="bullet">
///   <item><see cref="LeakProtection.ValidateAppSettings"/> (F-12
///   pre-gen invariant check) — only fires from <c>VpnEngine.StartAsync</c>
///   and <c>VpnEngine.Apply</c>. <c>HealthMonitor</c> intentionally
///   skips it because the AppSettings model has already been validated
///   at start time; rebuild paths trust the model.</item>
///   <item><see cref="ConfigSanityCheck.CheckBeforeStart"/> (F-E dead-
///   config detection) — only fires from <c>StartAsync</c> because it
///   triggers <see cref="AutoFailoverEngine"/>, which calls back into
///   <c>StartAsync</c> recursively. Running this from <c>HealthMonitor</c>
///   would cause restart-loop tangles.</item>
///   <item>Custom-mode dispatch (<see cref="CustomConfigInjector.Inject"/>)
///   — callers select between custom and generated branches before
///   calling this helper. ConfigPipeline only handles the
///   generated / subscribe branch.</item>
/// </list>
///
/// <para><strong>Behaviour contract.</strong> Idempotent. Mutates
/// <c>settings.Vless.Servers</c> in-place via
/// <see cref="VlessServersResolver"/> — callers MUST NOT persist
/// settings between this call and the next save without first reloading
/// from disk (same constraint <c>VpnEngine</c> has always had; see the
/// "v2.30.0-r8: do NOT persist <c>settings</c> directly" comment block).</para>
/// </summary>
internal static class ConfigPipeline
{
    /// <summary>
    /// Walks: resolve subscription servers → fold legacy <c>vless.servers</c> →
    /// build sing-box config → validate for leaks → serialize.
    /// </summary>
    /// <param name="profile">Active profile (sources process_name rules /
    /// DNS mode / block_on_vpn_fail). Pass an empty / full-tunnel profile
    /// for full-tunnel routing.</param>
    /// <param name="resolvedProcessNames">Already-scanned process names
    /// (output of <see cref="IProcessScanner.ScanForProfile"/>). This
    /// helper does NOT scan — callers stage the scan because process
    /// resolution is platform-specific and slow (WMI on Windows).</param>
    /// <param name="settings">AppSettings (mutated in-place by
    /// <see cref="VlessServersResolver.Resolve"/>; see contract notes).</param>
    /// <param name="validationMode">Whether a <see cref="LeakProtection"/>
    /// failure throws (initial-connect path) or logs+continues (recovery
    /// path). See <see cref="ValidationMode"/>.</param>
    /// <param name="warningSink">Optional sink for non-fatal warnings
    /// from <see cref="LeakProtection.ValidateConfig"/>. Initial-connect
    /// callers route these to the UI via <c>VpnEngine.Warning</c>; recovery
    /// callers can pass null (warnings still go to the logger).</param>
    /// <param name="logger">Serilog logger for trace + warn emission.</param>
    /// <returns>The generated sing-box JSON string, ready to feed
    /// <see cref="SingBoxManager.StartWithJson"/> or
    /// <see cref="SingBoxManager.ReloadConfigJson"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when:
    /// <list type="bullet">
    ///   <item>VlessServersResolver returns 0 servers (msg from
    ///   <see cref="VlessServersResolver.DescribeEmptyReason"/>).</item>
    ///   <item><see cref="LeakProtection.ValidateConfig"/> reports
    ///   <see cref="ValidationResult.IsValid"/>=false AND
    ///   <paramref name="validationMode"/>=<see cref="ValidationMode.Strict"/>.</item>
    /// </list>
    /// </exception>
    public static string Generate(
        Profile profile,
        IEnumerable<string> resolvedProcessNames,
        AppSettings settings,
        ValidationMode validationMode = ValidationMode.Strict,
        Action<string>? warningSink = null,
        ILogger? logger = null,
        bool? strictDnsOverride = null)
    {
        // ── Step 1: Resolve servers ───────────────────────────────────────
        // VlessServersResolver mutates settings.Vless.Servers in-place
        // with the effective server list. Single source of truth — both
        // VpnEngine.StartAsync (line ~240) and HealthMonitor.GenerateConfigJson
        // (line ~536) used to call this; now centralized here.
        var resolved = VlessServersResolver.Resolve(settings, logger);

        // ── Step 2: Empty guard ──────────────────────────────────────────
        // Pre-2F StartAsync had this check; HealthMonitor did not (its
        // ConfigGenerator.Generate would throw "no active VLESS servers"
        // with a less actionable message). Unifying here gives every
        // caller the same descriptive reason from DescribeEmptyReason.
        if (resolved.Count == 0)
        {
            var why = VlessServersResolver.DescribeEmptyReason(settings)
                      ?? "VLESS server not configured.";
            // Match StartAsync's exception type so call sites that catch
            // InvalidOperationException for the empty-servers case continue
            // to work unchanged.
            throw new InvalidOperationException(why);
        }

        // ── Step 3: Build sing-box config ────────────────────────────────
        // strictDnsOverride (v2.42.0): HealthMonitor passes false to suppress
        // "all DNS via tunnel" when the proxy is unreachable (StrictDns
        // failover); null = honour the persisted setting. Only the generated
        // path consumes it — custom mode keeps its own StrictDns handling.
        var sbConfig = ConfigGenerator.Generate(profile, resolvedProcessNames, settings, strictDnsOverride);

        // ── Step 4: Validate for leaks ───────────────────────────────────
        // Bug-r9-F-DEFENSIVE: settings passed so outbound IPs are cross-
        // checked against the user's known server list. Same call shape
        // as VpnEngine + HealthMonitor used pre-2F.
        try
        {
            var validation = LeakProtection.ValidateConfig(sbConfig, settings);

            foreach (var warn in validation.Warnings)
            {
                logger?.Warning("[ConfigPipeline] {Warn}", warn);
                warningSink?.Invoke(warn);
            }

            if (!validation.IsValid)
            {
                var errors = string.Join("; ", validation.Errors);
                if (validationMode == ValidationMode.Strict)
                {
                    // StartAsync semantics: hard-fail the start.
                    throw new InvalidOperationException(
                        $"Config validation failed: {errors}");
                }
                else
                {
                    // HealthMonitor semantics: validation is advisory on restart so a
                    // TRANSIENT invariant glitch (empty server list mid-refresh,
                    // outbound-scope warning) doesn't block recovery. BUT a STATIC,
                    // deterministic IPv6-leak invariant — dns.strategy must be ipv4_only
                    // on the v4-only TUN — is not transient: shipping it re-enables AAAA
                    // and leaks IPv6 past the tunnel on EVERY restart/rescan
                    // (v2.40.0-r10 #7 core-audit). Strict mode already throws on it; make
                    // Advisory fatal for this specific invariant too, so a crash-restart
                    // can't silently re-ship a leaking config. Fail-closed: the block
                    // rules stay enabled and recovery aborts rather than leaking.
                    if (validation.Errors.Any(e =>
                            e.Contains("ipv4_only", StringComparison.OrdinalIgnoreCase)
                            || e.Contains("dns.strategy", StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new InvalidOperationException(
                            $"Config validation failed (non-transient IPv6-leak invariant): {errors}");
                    }
                    // We still log loudly so the failure surfaces in vpnrouter.log.
                    logger?.Warning(
                        "[ConfigPipeline] LeakProtection flagged restart config: errors=[{Errors}] warnings=[{Warnings}]",
                        string.Join(" | ", validation.Errors),
                        string.Join(" | ", validation.Warnings));
                }
            }
            else if (validation.Warnings.Count > 0
                     && validationMode == ValidationMode.Advisory)
            {
                logger?.Information(
                    "[ConfigPipeline] LeakProtection restart-config warnings: {Warnings}",
                    string.Join(" | ", validation.Warnings));
            }
        }
        catch (InvalidOperationException)
        {
            // Strict-mode validation failure — rethrow as-is so the caller's
            // try/catch can surface the message to the user.
            throw;
        }
        catch (Exception ex)
        {
            // Validation must NEVER block recovery — advisory mode swallows
            // the throw so a buggy validator doesn't break HealthMonitor
            // restart paths. Strict mode (StartAsync) treats this as
            // "validator threw, abort connect" via the rethrow above —
            // but ValidateConfig is well-defined enough that the catch
            // here primarily exists for the advisory branch.
            if (validationMode == ValidationMode.Strict) throw;
            logger?.Warning(ex,
                "[ConfigPipeline] LeakProtection.ValidateConfig threw (non-fatal in advisory mode)");
        }

        // ── Step 5: Serialize ────────────────────────────────────────────
        return ConfigGenerator.Serialize(sbConfig);
    }

    /// <summary>
    /// Controls how <see cref="ConfigPipeline.Generate"/> reacts to a
    /// <see cref="LeakProtection"/> failure.
    /// </summary>
    public enum ValidationMode
    {
        /// <summary>
        /// Initial-connect semantics. Validation failure throws
        /// <see cref="InvalidOperationException"/> so the caller aborts
        /// the start and surfaces the error to the user. Used by
        /// <see cref="VpnEngine.StartAsync"/> + <see cref="VpnEngine"/>'s
        /// Apply path.
        /// </summary>
        Strict,

        /// <summary>
        /// Recovery semantics. Validation failure is logged at WARN
        /// level but the (potentially leaky) config is still returned.
        /// Used by <see cref="HealthMonitor"/>'s auto-restart path — see
        /// r5 comment block: "Validation must NEVER block recovery — it's
        /// an advisory." Pre-r5 HealthMonitor didn't validate at all;
        /// r5 added the call as a warning-only chokepoint.
        /// </summary>
        Advisory,
    }
}
