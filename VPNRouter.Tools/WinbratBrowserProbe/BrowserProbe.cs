#nullable enable

using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace VPNRouter.Tools.WinbratBrowserProbe;

internal enum BrowserProbeLifecycle
{
    Completed,
    InputRejected,
    AlreadyRunning,
    PlatformUnsupported,
    BrowserMissing,
    EdgeLaunchFailed,
    BrowserExited,
    DevToolsUnavailable,
    PageUnavailable,
    PagePollingFailure,
    DevToolsFailure,
    InvalidPageState,
    TimedOut,
    InternalFailure,
    CleanupFailure,
}

internal sealed record BrowserProbeResult(
    BrowserProbeLifecycle Lifecycle,
    int FetchOk,
    int FetchFail,
    int WsOk,
    int WsFail,
    bool Done,
    long MaxFetchNoProgressMs,
    long MaxWsNoProgressMs);

internal sealed record BrowserCandidate(string Path, string Vendor);

internal readonly record struct BrowserPageState(int FetchOk, int FetchFail, int WsOk, int WsFail, bool Done)
{
    public static bool TryParse(string json, out BrowserPageState state)
    {
        state = default;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryReadCounter(root, "fetchOk", out var fetchOk) ||
                !TryReadCounter(root, "fetchFail", out var fetchFail) ||
                !TryReadCounter(root, "wsOk", out var wsOk) ||
                !TryReadCounter(root, "wsFail", out var wsFail) ||
                !root.TryGetProperty("done", out var doneElement) ||
                doneElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return false;

            state = new(fetchOk, fetchFail, wsOk, wsFail, doneElement.GetBoolean());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadCounter(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var element) &&
               element.TryGetInt32(out value) &&
               value >= 0;
    }
}

internal sealed class BrowserProgressTracker
{
    private readonly TimeSpan _started;
    private BrowserPageState _previous;
    private TimeSpan _lastFetchProgress;
    private TimeSpan _lastWsProgress;

    public BrowserProgressTracker(TimeSpan started) =>
        (_started, _lastFetchProgress, _lastWsProgress) = (started, started, started);

    public long MaxFetchNoProgressMs { get; private set; }
    public long MaxWsNoProgressMs { get; private set; }

    public bool Observe(BrowserPageState current, TimeSpan elapsed)
    {
        if (elapsed < _started ||
            current.FetchOk < _previous.FetchOk || current.FetchFail < _previous.FetchFail ||
            current.WsOk < _previous.WsOk || current.WsFail < _previous.WsFail ||
            (_previous.Done && !current.Done))
            return false;

        MaxFetchNoProgressMs = Math.Max(MaxFetchNoProgressMs, Milliseconds(elapsed - _lastFetchProgress));
        MaxWsNoProgressMs = Math.Max(MaxWsNoProgressMs, Milliseconds(elapsed - _lastWsProgress));

        if (current.FetchOk > _previous.FetchOk)
            _lastFetchProgress = elapsed;
        if (current.WsOk > _previous.WsOk)
            _lastWsProgress = elapsed;

        _previous = current;
        return true;
    }

    private static long Milliseconds(TimeSpan duration) => Math.Max(0, (long)Math.Round(duration.TotalMilliseconds));
}

internal static class BrowserProbe
{
    internal static readonly BrowserCandidate[] BrowserCandidates =
    [
        new(Path.Combine(AppContext.BaseDirectory, "chrome-win64", "chrome.exe"), "PinnedArchive"),
        new(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", "Microsoft"),
        new(@"C:\Program Files\Microsoft\Edge\Application\msedge.exe", "Microsoft"),
        new(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "Google"),
        new(@"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe", "Google"),
    ];
    internal const string FixedPage = "https://loadtest.vpn.ninitux.com/browser";
    internal static readonly string FixedProfileRoot = Path.Combine(AppContext.BaseDirectory, "browser-profile");
    internal const string FixedExpression = "JSON.stringify({fetchOk:state.fetchOk,fetchFail:state.fetchFail,wsOk:state.wsOk,wsFail:state.wsFail,done:state.done})";

    private static readonly TimeSpan StartupLimit = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollLimit = TimeSpan.FromMinutes(11);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    public static async Task<BrowserProbeResult> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length != 0) return Empty(BrowserProbeLifecycle.InputRejected);
        if (!OperatingSystem.IsWindows()) return Empty(BrowserProbeLifecycle.PlatformUnsupported);
        var browser = FindBrowser();
        if (browser is null) return Empty(BrowserProbeLifecycle.BrowserMissing);

        try
        {
            using var singleRun = new Semaphore(1, 1, @"Global\VPNRouterFixedBrowserProbe");
            var guardHeld = singleRun.WaitOne(0);
            if (!guardHeld) return Empty(BrowserProbeLifecycle.AlreadyRunning);
            try { return await RunSingleAsync(browser.Path, cancellationToken); }
            finally
            {
                if (guardHeld) singleRun.Release();
            }
        }
        catch
        {
            return Empty(BrowserProbeLifecycle.InternalFailure);
        }
    }

    internal static BrowserCandidate? FindBrowser() =>
        BrowserCandidates.FirstOrDefault(candidate => File.Exists(candidate.Path));

    private static async Task<BrowserProbeResult> RunSingleAsync(string browserPath, CancellationToken cancellationToken)
    {

        Process? edge = null;
        var profile = Path.Combine(FixedProfileRoot, "run-" + Guid.NewGuid().ToString("N"));
        var result = Empty(BrowserProbeLifecycle.InternalFailure);
        var cleanupFailed = false;
        var stage = BrowserProbeLifecycle.EdgeLaunchFailed;

        try
        {
            ValidateProfilePath(profile);
            Directory.CreateDirectory(profile);
            edge = Process.Start(CreateStartInfo(profile, browserPath));
            if (edge is null) throw new ProbeFailure(BrowserProbeLifecycle.EdgeLaunchFailed);
            edge.OutputDataReceived += static (_, _) => { };
            edge.ErrorDataReceived += static (_, _) => { };
            edge.BeginOutputReadLine();
            edge.BeginErrorReadLine();

            stage = BrowserProbeLifecycle.DevToolsUnavailable;
            var port = await ReadDevToolsPortAsync(edge, profile, cancellationToken);
            stage = BrowserProbeLifecycle.PageUnavailable;
            var pageSocket = await FindExactPageAsync(edge, port, cancellationToken);
            stage = BrowserProbeLifecycle.PagePollingFailure;
            result = await PollPageAsync(edge, pageSocket, cancellationToken);
        }
        catch (ProbeFailure failure)
        {
            result = Empty(failure.Lifecycle);
        }
        catch (OperationCanceledException)
        {
            result = Empty(BrowserProbeLifecycle.TimedOut);
        }
        catch
        {
            result = Empty(stage);
        }
        finally
        {
            if (edge is not null)
            {
                try
                {
                    if (!edge.HasExited) edge.Kill(entireProcessTree: true);
                    if (!edge.WaitForExit(5000)) cleanupFailed = true;
                    else edge.WaitForExit();
                }
                catch
                {
                    cleanupFailed = true;
                }
                try { edge.Dispose(); }
                catch { cleanupFailed = true; }
            }

            cleanupFailed |= !await DeleteProfileAsync(profile);
        }

        return cleanupFailed ? result with { Lifecycle = BrowserProbeLifecycle.CleanupFailure } : result;
    }

    internal static ProcessStartInfo CreateStartInfo(string profile, string browserPath)
    {
        ValidateProfilePath(profile);
        if (!BrowserCandidates.Any(candidate => string.Equals(candidate.Path, browserPath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Invalid fixed browser executable.");
        var start = new ProcessStartInfo(browserPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("--headless=new");
        start.ArgumentList.Add("--disable-gpu");
        start.ArgumentList.Add("--disable-extensions");
        start.ArgumentList.Add("--disable-background-networking");
        start.ArgumentList.Add("--disable-component-update");
        start.ArgumentList.Add("--disable-sync");
        start.ArgumentList.Add("--no-first-run");
        start.ArgumentList.Add("--no-default-browser-check");
        start.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
        start.ArgumentList.Add("--remote-debugging-port=0");
        start.ArgumentList.Add("--user-data-dir=" + profile);
        start.ArgumentList.Add(FixedPage);
        return start;
    }

    internal static void ValidateProfilePath(string profile)
    {
        var root = Path.GetFullPath(FixedProfileRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(profile);
        var relative = Path.GetRelativePath(root, candidate);
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            relative.Contains(Path.DirectorySeparatorChar) ||
            !relative.StartsWith("run-", StringComparison.Ordinal) ||
            relative.Length != 36)
            throw new InvalidOperationException("Invalid fixed browser profile directory.");
    }

    private static async Task<int> ReadDevToolsPortAsync(Process edge, string profile, CancellationToken cancellationToken)
    {
        var activePort = Path.Combine(profile, "DevToolsActivePort");
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < StartupLimit)
        {
            ThrowIfExited(edge);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var firstLine = (await File.ReadAllLinesAsync(activePort, cancellationToken)).FirstOrDefault();
                if (int.TryParse(firstLine, out var port) && port is > 0 and <= 65535) return port;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            await Task.Delay(100, cancellationToken);
        }
        throw new ProbeFailure(BrowserProbeLifecycle.DevToolsUnavailable);
    }

    private static async Task<Uri> FindExactPageAsync(Process edge, int port, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var endpoint = new Uri($"http://127.0.0.1:{port}/json/list");
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < StartupLimit)
        {
            ThrowIfExited(edge);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var json = await client.GetStringAsync(endpoint, cancellationToken);
                using var document = JsonDocument.Parse(json);
                var matches = document.RootElement.EnumerateArray()
                    .Where(item => item.TryGetProperty("type", out var type) && type.GetString() == "page" &&
                                   item.TryGetProperty("url", out var url) && url.GetString() == FixedPage)
                    .ToArray();
                if (matches.Length == 1 &&
                    matches[0].TryGetProperty("webSocketDebuggerUrl", out var socketElement) &&
                    Uri.TryCreate(socketElement.GetString(), UriKind.Absolute, out var socket) &&
                    socket.Scheme == "ws" && socket.IsLoopback && socket.Port == port)
                    return socket;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (JsonException) { }
            await Task.Delay(100, cancellationToken);
        }
        throw new ProbeFailure(BrowserProbeLifecycle.PageUnavailable);
    }

    private static async Task<BrowserProbeResult> PollPageAsync(Process edge, Uri pageSocket, CancellationToken cancellationToken)
    {
        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        pollCts.CancelAfter(PollLimit);
        using var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(pageSocket, pollCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProbeFailure(BrowserProbeLifecycle.TimedOut);
        }
        catch (WebSocketException)
        {
            throw new ProbeFailure(BrowserProbeLifecycle.DevToolsFailure);
        }

        var clock = Stopwatch.StartNew();
        var tracker = new BrowserProgressTracker(TimeSpan.Zero);
        var state = default(BrowserPageState);
        var initialStateObserved = false;
        long id = 0;
        try
        {
            while (true)
            {
                ThrowIfExited(edge);
                try
                {
                    state = await EvaluateFixedStateAsync(socket, ++id, pollCts.Token);
                }
                catch (ProbeFailure failure) when (
                    failure.Lifecycle == BrowserProbeLifecycle.InvalidPageState &&
                    !initialStateObserved &&
                    clock.Elapsed < StartupLimit)
                {
                    await Task.Delay(100, pollCts.Token);
                    continue;
                }
                if (!tracker.Observe(state, clock.Elapsed))
                    throw new ProbeFailure(BrowserProbeLifecycle.InvalidPageState);
                initialStateObserved = true;
                if (state.Done)
                    return Result(BrowserProbeLifecycle.Completed, state, tracker);
                await Task.Delay(PollInterval, pollCts.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            tracker.Observe(state, clock.Elapsed);
            return Result(BrowserProbeLifecycle.TimedOut, state, tracker);
        }
        catch (WebSocketException)
        {
            throw new ProbeFailure(BrowserProbeLifecycle.DevToolsFailure);
        }
    }

    private static async Task<BrowserPageState> EvaluateFixedStateAsync(ClientWebSocket socket, long id, CancellationToken cancellationToken)
    {
        var command = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id,
            method = "Runtime.evaluate",
            @params = new { expression = FixedExpression, returnByValue = true },
        });
        await socket.SendAsync(command, WebSocketMessageType.Text, true, cancellationToken);

        while (true)
        {
            var message = await ReceiveTextAsync(socket, cancellationToken);
            using var response = JsonDocument.Parse(message);
            var root = response.RootElement;
            if (!root.TryGetProperty("id", out var responseId) || responseId.GetInt64() != id) continue;
            if (!root.TryGetProperty("result", out var result) ||
                result.TryGetProperty("exceptionDetails", out _) ||
                !result.TryGetProperty("result", out var valueResult) ||
                !valueResult.TryGetProperty("value", out var value) ||
                value.ValueKind != JsonValueKind.String ||
                !BrowserPageState.TryParse(value.GetString()!, out var state))
                throw new ProbeFailure(BrowserProbeLifecycle.InvalidPageState);
            return state;
        }
    }

    private static async Task<string> ReceiveTextAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        const int limit = 64 * 1024;
        var buffer = new byte[4096];
        using var message = new MemoryStream();
        WebSocketReceiveResult part;
        do
        {
            part = await socket.ReceiveAsync(buffer, cancellationToken);
            if (part.MessageType != WebSocketMessageType.Text || message.Length + part.Count > limit)
                throw new ProbeFailure(BrowserProbeLifecycle.DevToolsFailure);
            message.Write(buffer, 0, part.Count);
        } while (!part.EndOfMessage);
        return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
    }

    private static async Task<bool> DeleteProfileAsync(string profile)
    {
        try
        {
            ValidateProfilePath(profile);
            for (var attempt = 0; attempt < 5; attempt++)
            {
                if (!Directory.Exists(profile)) return true;
                try
                {
                    Directory.Delete(profile, recursive: true);
                    return true;
                }
                catch (IOException) when (attempt < 4) { }
                catch (UnauthorizedAccessException) when (attempt < 4) { }
                await Task.Delay(200);
            }
        }
        catch { }
        return !Directory.Exists(profile);
    }

    private static BrowserProbeResult Empty(BrowserProbeLifecycle lifecycle) =>
        new(lifecycle, 0, 0, 0, 0, false, 0, 0);

    private static BrowserProbeResult Result(BrowserProbeLifecycle lifecycle, BrowserPageState state, BrowserProgressTracker tracker) =>
        new(lifecycle, state.FetchOk, state.FetchFail, state.WsOk, state.WsFail, state.Done,
            tracker.MaxFetchNoProgressMs, tracker.MaxWsNoProgressMs);

    private static void ThrowIfExited(Process edge)
    {
        if (edge.HasExited) throw new ProbeFailure(BrowserProbeLifecycle.BrowserExited);
    }

    private sealed class ProbeFailure(BrowserProbeLifecycle lifecycle) : Exception
    {
        public BrowserProbeLifecycle Lifecycle { get; } = lifecycle;
    }
}
