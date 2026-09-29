using Serilog;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VPNRouter.Core.Services;

public static class ResilientStarter
{
    public static readonly int[] DefaultBackoffSeconds = { 5, 10, 20, 40 };

    public static async Task<bool> StartWithBackoffAsync(
        string componentName,
        Func<CancellationToken, Task> startFn,
        Func<Exception, bool>? isRetriable = null,
        int[]? backoffSeconds = null,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        backoffSeconds ??= DefaultBackoffSeconds;
        isRetriable ??= DefaultIsRetriable;

        var maxAttempts = backoffSeconds.Length + 1;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var preDelay = attempt == 1 ? 0 : backoffSeconds[attempt - 2];
            logger?.Information(
                "[Resilient] {Component}: attempt {Attempt}/{Max}, delay={Delay}s",
                componentName, attempt, maxAttempts, preDelay);

            try
            {
                await startFn(ct);
                logger?.Information(
                    "[Resilient] {Component}: attempt {Attempt}/{Max} succeeded",
                    componentName, attempt, maxAttempts);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (!isRetriable(ex))
            {
                logger?.Error(ex,
                    "[ResilientStarter] {Component} failed with non-retriable error: {Error}",
                    componentName, ex.Message);
                throw;
            }
            catch (Exception ex)
            {
                logger?.Warning(
                    "[Resilient] {Component}: attempt {Attempt}/{Max} failed-with-{Error}",
                    componentName, attempt, maxAttempts, ex.Message);

                if (attempt == maxAttempts)
                {
                    logger?.Error(ex,
                        "[ResilientStarter] {Component} failed after {Max} attempts: {Error}",
                        componentName, maxAttempts, ex.Message);
                    return false;
                }

                var delaySeconds = backoffSeconds[attempt - 1];
                logger?.Warning(
                    "[ResilientStarter] {Component} attempt {Attempt}/{Max} failed: {Error}. Retrying in {Delay}s",
                    componentName, attempt, maxAttempts, ex.Message, delaySeconds);

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
            }
        }

        return false;
    }

    public static Task<bool> StartWithBackoffAsync(
        string componentName,
        Action startFn,
        Func<Exception, bool>? isRetriable = null,
        int[]? backoffSeconds = null,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        return StartWithBackoffAsync(
            componentName,
            _ =>
            {
                startFn();
                return Task.CompletedTask;
            },
            isRetriable,
            backoffSeconds,
            logger,
            ct);
    }

    private static bool DefaultIsRetriable(Exception ex)
    {
        if (ex is FileNotFoundException) return false;
        if (ex is OperationCanceledException) return false;

        if (ex.GetType().Name == "TunOwnershipException") return false;

        return true;
    }
}
