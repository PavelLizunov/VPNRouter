using Serilog;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VPNRouter.Core.Services;

/// <summary>
/// Retry helper for component startup with exponential backoff.
/// Used by VPNRouter.Service to make autostart resilient against transient
/// failures (network stack not ready, TUN adapter init delay, WinDivert driver
/// load race, Process.Start returning null, etc.)
///
/// Design:
///   - Non-retriable errors (binary missing, TUN owned by another process,
///     user cancellation) propagate immediately — caller decides what to do.
///   - Transient errors are retried up to N times with growing delay.
///   - When retries are exhausted, returns false (does NOT throw) so caller
///     can decide whether a failed component should abort the whole startup.
/// </summary>
public static class ResilientStarter
{
    /// <summary>
    /// Default delays between attempts, in seconds: 5, 10, 20, 40.
    /// Total wait time before giving up: 75 seconds across 5 attempts.
    /// </summary>
    public static readonly int[] DefaultBackoffSeconds = { 5, 10, 20, 40 };

    /// <summary>
    /// Retry the async start function up to (backoffSeconds.Length + 1) times
    /// with exponential backoff between attempts.
    /// </summary>
    /// <param name="componentName">Used in log messages, e.g. "VPN", "Zapret".</param>
    /// <param name="startFn">Start function. Should throw on failure.</param>
    /// <param name="isRetriable">
    /// Optional predicate. Given an exception, return true to retry or false
    /// to rethrow immediately. Default: retry everything except
    /// FileNotFoundException and TunOwnershipException.
    /// </param>
    /// <param name="backoffSeconds">
    /// Delays between attempts (array length determines max attempts - 1).
    /// Default: { 5, 10, 20, 40 }.
    /// </param>
    /// <param name="logger">Optional Serilog logger for per-attempt diagnostics.</param>
    /// <param name="ct">Cancellation token. Propagated to startFn and Task.Delay.</param>
    /// <returns>true if any attempt succeeded; false if all attempts exhausted.</returns>
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

            try
            {
                await startFn(ct);
                if (attempt > 1)
                {
                    logger?.Information(
                        "[ResilientStarter] {Component} started on attempt {Attempt}/{Max}",
                        componentName, attempt, maxAttempts);
                }
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

    /// <summary>
    /// Overload for synchronous Start() methods (ZapretManager, TgProxyManager).
    /// Wraps the synchronous call in a Task.
    /// </summary>
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

    /// <summary>
    /// Default retriable predicate:
    ///   - FileNotFoundException  → non-retriable (binary missing, user config issue)
    ///   - TunOwnershipException  → non-retriable (handled by outer loop in Service)
    ///   - OperationCanceledException → non-retriable (stop requested)
    ///   - anything else → retriable (transient network/process/driver issue)
    /// </summary>
    private static bool DefaultIsRetriable(Exception ex)
    {
        if (ex is FileNotFoundException) return false;
        if (ex is OperationCanceledException) return false;

        // Name-based check so we don't need a using for TunOwnershipException
        // (it lives in the same namespace but we keep the helper standalone).
        if (ex.GetType().Name == "TunOwnershipException") return false;

        return true;
    }
}
