namespace VPNRouter.Android;

internal static class AndroidVerificationSession
{
    // Each callback belongs to the same Java session, even if native shutdown outlives this attempt.
    public static async Task<T> RunAsync<T>(Action awaitReady, Action cancel, Action close,
        Func<Task<T>> probe, CancellationToken token)
    {
        using var registration = token.Register(cancel);
        T result;
        try
        {
            try
            {
                token.ThrowIfCancellationRequested();
                awaitReady();
                token.ThrowIfCancellationRequested();
                result = await probe().ConfigureAwait(false);
            }
            finally { close(); }
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // Reflection reports the interrupted Java wait as an invocation exception.
            throw new OperationCanceledException(token);
        }
        token.ThrowIfCancellationRequested();
        return result;
    }
}
