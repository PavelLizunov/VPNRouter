// Phase 3E (2026-05-18) — GeoIpStage.
//
// MaxMind / ip-api.com lookup. Mutates only entries that have no
// CountryCode yet — entries inherited from cache (or pool.json) already
// carry country codes and skip the network round-trip. Skippable: if
// the lookup throws (offline, ip-api.com 503), the stage logs a Warning
// and passes Input through unchanged.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VPNRouter.Core.Services.FreeConfigs.Stages;

/// <summary>
/// Stage 4 of the Free Configs pipeline. Enriches FreeConfigEntry instances
/// with ISO-2 country codes via DNS + ip-api.com batch lookup. Optional —
/// failure (offline / API down) is non-fatal, the pipeline continues with
/// entries that still have <c>CountryCode == null</c> (UI renders them as
/// "—"). Skipped via the pool short-circuit (pool.json comes
/// pre-enriched).
/// </summary>
public sealed class GeoIpStage : IFreeConfigStage
{
    private readonly FreeConfigGeoIp _geoIp;

    public GeoIpStage(FreeConfigGeoIp geoIp)
    {
        _geoIp = geoIp ?? throw new ArgumentNullException(nameof(geoIp));
    }

    /// <inheritdoc />
    public string Name => "geoip";

    /// <summary>
    /// GeoIp is best-effort — see file-header comment. The orchestrator
    /// honours <see cref="Optional"/> by NOT aborting the pipeline on
    /// failure; instead the stage is logged as failed-but-passed-through.
    /// </summary>
    public bool Optional => true;

    /// <inheritdoc />
    public async Task<StageResult> RunAsync(StageContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var sw = Stopwatch.StartNew();

        var input = ctx.Input;
        var needGeo = input.Where(c => string.IsNullOrEmpty(c.CountryCode)).ToList();
        if (needGeo.Count == 0)
        {
            ctx.Logger.Debug("GeoIpStage: all entries already have country codes — no-op");
            return new StageResult(
                Success: true,
                Output: input,
                FailureReason: null,
                Duration: sw.Elapsed);
        }

        ctx.StageNotice?.Invoke($"Resolving country codes ({needGeo.Count} IPs)...");

        // Forward internal progress to the UI notice line so users see
        // batch progress.
        var notice = ctx.StageNotice;
        _geoIp.Progress = new Progress<(string stage, int done, int total)>(p =>
        {
            var label = p.stage == "dns"
                ? $"Resolving DNS: {p.done}/{p.total}"
                : $"Resolving country (batch {p.done}/{p.total})";
            notice?.Invoke(label);
        });

        string? failure = null;
        try
        {
            await _geoIp.EnrichAsync(needGeo, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // User cancel — propagate so the outer pipeline returns to the
            // cancel-handling path. Same convention as TestStage.
            throw;
        }
        catch (Exception ex)
        {
            ctx.Logger.Warning(
                "GeoIpStage: enrich failed: {err} — entries keep null CountryCode",
                ex.Message);
            failure = ex.Message;
        }
        finally
        {
            _geoIp.Progress = null;
        }

        return new StageResult(
            Success: failure is null,
            Output: input,
            FailureReason: failure,
            Duration: sw.Elapsed);
    }
}
