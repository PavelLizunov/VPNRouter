// Phase 3E (2026-05-18) — ParseStage.
//
// Converts the raw vless:// URI strings collected by FetchStage into the
// FreeConfigEntry list. Drains the FetchStage.PendingFetches bucket (no
// shared mutable state on the orchestrator side). Failures inside a single
// URI never abort the whole stage — we count them and surface via the log
// + FailureReason field for telemetry.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VPNRouter.Core.Services.FreeConfigs.Stages;

/// <summary>
/// Stage 2 of the Free Configs pipeline. Converts raw vless:// URIs
/// (collected by <see cref="FetchStage"/> into PendingFetches) into the
/// <see cref="FreeConfigEntry"/> list. Skipped via the pool short-circuit
/// (pool.json comes pre-parsed).
/// </summary>
public sealed class ParseStage : IFreeConfigStage
{
    private readonly FetchStage _fetchStage;

    public ParseStage(FetchStage fetchStage)
    {
        _fetchStage = fetchStage ?? throw new ArgumentNullException(nameof(fetchStage));
    }

    /// <inheritdoc />
    public string Name => "parse";

    /// <inheritdoc />
    public bool Optional => false;

    /// <inheritdoc />
    public Task<StageResult> RunAsync(StageContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var sw = Stopwatch.StartNew();
        ctx.StageNotice?.Invoke("Parsing configs...");

        var output = new List<FreeConfigEntry>();
        var parseErrors = 0;
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (src, raws) in _fetchStage.PendingFetches)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var raw in raws)
            {
                try
                {
                    var vless = VlessUriParser.Parse(raw);
                    var id = BuildId(vless.Server, vless.Port, vless.Uuid);

                    // Intra-source duplicates are deduped here (cheap); the
                    // formal dedupe pass merges cross-source duplicates +
                    // any pre-existing entries from the seed list.
                    if (!seenIds.Add(id)) continue;

                    output.Add(new FreeConfigEntry
                    {
                        Id         = id,
                        SourceUrl  = src.Url,
                        RawUri     = raw,
                        Host       = vless.Server,
                        Port       = vless.Port,
                        Uuid       = vless.Uuid,
                        Name       = vless.Name ?? "",
                        Sni        = vless.Reality?.ServerName ?? vless.Tls?.ServerName ?? "",
                        Transport  = vless.Transport?.Type ?? "tcp",
                        Security   = vless.Security ?? "reality",
                    });
                }
                catch
                {
                    parseErrors++;
                }
            }
        }

        ctx.Logger.Information(
            "ParseStage: parsed {ok} unique entries ({err} parse errors) from {src} sources",
            output.Count, parseErrors, _fetchStage.PendingFetches.Count);

        // Drain the bucket — releasing memory and making re-runs safe.
        _fetchStage.PendingFetches.Clear();

        return Task.FromResult(new StageResult(
            Success: true,
            Output: output,
            FailureReason: parseErrors > 0 ? $"{parseErrors} parse errors" : null,
            Duration: sw.Elapsed));
    }

    /// <summary>
    /// Stable hash id for a config: 16 hex chars from SHA-1 of
    /// <c>host:port:uuid</c>. Lowercases host + uuid before hashing so
    /// equivalent entries with cased variations dedupe correctly.
    /// </summary>
    internal static string BuildId(string host, int port, string uuid)
    {
        using var sha = SHA1.Create();
        var key = $"{host.ToLowerInvariant()}:{port}:{uuid.ToLowerInvariant()}";
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash, 0, 8);
    }
}
