#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VPNRouter.Core.Services;

namespace VPNRouter.Headless.Lifecycle;

/// <summary>
/// Enforces bounded execution for engine stop and resource disposal without leaking secrets.
/// </summary>
public static class BoundedTeardown
{
    public static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Bounded stop execution. Guarantees completion within the allocated timeout,
    /// catches and scrubs any secret-bearing exception messages.
    /// Returns true if the engine stopped cleanly and no process remains active; false otherwise.
    /// </summary>
    public static async Task<bool> StopBoundedAsync(ILifecycleEngine engine, TimeSpan? timeout = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var budget = timeout ?? DefaultStopTimeout;

        try
        {
            var stopTask = Task.Run(() => engine.Stop());
            var completed = await Task.WhenAny(stopTask, Task.Delay(budget)).ConfigureAwait(false);

            if (completed != stopTask)
            {
                logger?.Warning("[BoundedTeardown] Engine stop exceeded timeout of {Budget}s; process remains active.", budget.TotalSeconds);
                return false;
            }

            await stopTask.ConfigureAwait(false);

            if (engine.IsRunning || engine.SingBoxPid != null)
            {
                logger?.Warning("[BoundedTeardown] Engine stop finished but engine reports active state; process remains.");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            var scrubbed = SanitizeExceptionMessage(ex);
            logger?.Warning("[BoundedTeardown] Exception during engine stop: {Message}", scrubbed);
            return false;
        }
    }

    /// <summary>
    /// Scrub potential secret information (URIs, credentials, UUIDs) from exception messages.
    /// </summary>
    public static string SanitizeExceptionMessage(Exception? ex)
    {
        if (ex == null) return string.Empty;
        var message = ex.Message;
        try
        {
            return CrashReporter.ScrubSecrets(message);
        }
        catch
        {
            return "An internal error occurred during teardown.";
        }
    }
}
