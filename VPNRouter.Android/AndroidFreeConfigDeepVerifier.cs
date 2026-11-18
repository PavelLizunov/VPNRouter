using Android.App;
using Serilog;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.FreeConfigs;
using Exception = System.Exception;

namespace VPNRouter.Android;

internal sealed class AndroidFreeConfigDeepVerifier(ILogger logger)
{
    private const string SecondaryProbeUrl = "https://1.1.1.1/cdn-cgi/trace";

    public async Task VerifyOneAsync(FreeConfigEntry cfg, CancellationToken ct = default)
    {
        if (cfg is null) return;
        ct.ThrowIfCancellationRequested();
        cfg.BeginDeepRecheck();
        try
        {
            var entry = ServerUriParser.Parse(cfg.RawUri);
            if (!string.IsNullOrEmpty(entry.DetourVia))
            {
                cfg.LastError = DeepVerifyConstants.DetourNote;
                return;
            }
            var primary = await RunProbeAsync(entry, DeepVerifyConstants.ProbeUrl, ct).ConfigureAwait(false);
            if (!primary.ok)
            {
                cfg.LastError = primary.err ?? "deep verify failed";
                return;
            }

            var secondary = await RunProbeAsync(entry, SecondaryProbeUrl, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!secondary.ok)
            {
                cfg.LastError = $"primary ok, secondary {secondary.err ?? "failed"}";
                return;
            }

            cfg.Status = FreeConfigStatus.Verified;
            cfg.LastDeepVerifyAt = DateTime.UtcNow;
            cfg.LastError = null;
            cfg.LastVerifyFailedAt = null;
            if (cfg.LatencyMs <= 0) cfg.LatencyMs = primary.latencyMs;
            logger.Information("[Android.DV] Both verification probes succeeded");
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (OperationCanceledException)
        {
            cfg.LastError = "deep verify timeout";
        }
        catch (Exception ex)
        {
            cfg.LastError = $"deep verify failed ({ex.GetType().Name})";
            logger.Warning("[Android.DV] Verification failed ({Type})", ex.GetType().Name);
        }
    }

    private static Task<(bool ok, int latencyMs, string? err)> RunProbeAsync(
        VlessServerEntry entry, string url, CancellationToken ct) => Task.Run(async () =>
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DeepVerifyConstants.OverallTimeout);
        var proxy = new VerificationProxy(NetPortUtil.FindFreePort());
        var config = VlessDeepVerifier.BuildSingleOutboundConfig(entry, proxy, embedded: true);

        // Reflection is required because the gomobile AAR is included without C# bindings.
        var loader = Application.Context.ClassLoader ?? throw new InvalidOperationException("Missing app class loader");
        using var bridge = loader.LoadClass("com.ninitux.vpnrouter.AndroidDeepVerifyBox")!;
        using var contextType = loader.LoadClass("android.content.Context")!;
        using var stringType = loader.LoadClass("java.lang.String")!;
        using var begin = bridge.GetMethod("begin", contextType, stringType)!;
        using var ready = bridge.GetMethod("awaitReady")!;
        using var cancel = bridge.GetMethod("cancel")!;
        using var close = bridge.GetMethod("close")!;
        using var json = new Java.Lang.String(config);
        timeout.Token.ThrowIfCancellationRequested();
        using var session = begin.Invoke(null, Application.Context, json)
            ?? throw new InvalidOperationException("Missing verification session");
        return await AndroidVerificationSession.RunAsync(
            () => ready.Invoke(session), () => cancel.Invoke(session), () => close.Invoke(session),
            () => DeepVerifyProbe.ProbeViaProxyAsync(proxy, TimeSpan.FromSeconds(8), timeout.Token, url),
            timeout.Token).ConfigureAwait(false);
    }, ct);
}
