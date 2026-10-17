using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using VPNRouter.Headless;
using VPNRouter.Headless.Protocol;

namespace VPNRouter.Headless.Tests;

public static class ProtocolTests
{
    private static int s_passedTests;
    private static int s_failedTests;

    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("Running VPNRouter.Headless Protocol test suite...");

        await RunTestAsync(nameof(TestValidRequestRoundTripAsync), TestValidRequestRoundTripAsync);
        await RunTestAsync(nameof(TestInvalidVersionAsync), TestInvalidVersionAsync);
        await RunTestAsync(nameof(TestInvalidRequestIdAsync), TestInvalidRequestIdAsync);
        await RunTestAsync(nameof(TestUnknownFieldsAtRootAsync), TestUnknownFieldsAtRootAsync);
        await RunTestAsync(nameof(TestDuplicateKeysAtRootAsync), TestDuplicateKeysAtRootAsync);
        await RunTestAsync(nameof(TestDuplicateKeysInParamsAsync), TestDuplicateKeysInParamsAsync);
        await RunTestAsync(nameof(TestMaxJsonDepthExceededAsync), TestMaxJsonDepthExceededAsync);
        await RunTestAsync(nameof(TestInputFrameOversizedAsync), TestInputFrameOversizedAsync);
        await RunTestAsync(nameof(TestSingleOrdinaryCommandBusyAsync), TestSingleOrdinaryCommandBusyAsync);
        await RunTestAsync(nameof(TestCancelUrgentControlAsync), TestCancelUrgentControlAsync);
        await RunTestAsync(nameof(TestInvalidControlsHaveNoEffectsAsync), TestInvalidControlsHaveNoEffectsAsync);
        await RunTestAsync(nameof(TestDisconnectUrgentControlAsync), TestDisconnectUrgentControlAsync);
        await RunTestAsync(nameof(TestStateEventCoalescingAsync), TestStateEventCoalescingAsync);
        await RunTestAsync(nameof(TestOutputMaxFrameSizeAsync), TestOutputMaxFrameSizeAsync);
        await RunTestAsync(nameof(TestNoSecretInErrorsAsync), TestNoSecretInErrorsAsync);
        await RunTestAsync(nameof(TestEofShutdownAsync), TestEofShutdownAsync);
        await RunTestAsync(nameof(TestActiveLongOperationEofAsync), TestActiveLongOperationEofAsync);
        await RunTestAsync(nameof(TestActiveLongOperationCancelAsync), TestActiveLongOperationCancelAsync);
        await RunTestAsync(nameof(TestSynchronousHandlerDoesNotBlockControlsAsync), TestSynchronousHandlerDoesNotBlockControlsAsync);
        await RunTestAsync(nameof(TestBurst2000RequestsAsync), TestBurst2000RequestsAsync);
        await RunTestAsync(nameof(TestReadBackpressureAsync), TestReadBackpressureAsync);
        await RunTestAsync(nameof(TestAsyncResponseBackpressureBoundAsync), TestAsyncResponseBackpressureBoundAsync);
        await RunTestAsync(nameof(TestOutputStallDeadlineSelfTerminationAsync), TestOutputStallDeadlineSelfTerminationAsync);
        await RunTestAsync(nameof(TestOutputQueueStallPreventsEnqueueAsync), TestOutputQueueStallPreventsEnqueueAsync);
        await RunTestAsync(nameof(TestImmediateSnapshotHandshakeBeforeEofAsync), TestImmediateSnapshotHandshakeBeforeEofAsync);
        await RunTestAsync(nameof(TestHandlerDisposalDeadlineEnforcedAsync), TestHandlerDisposalDeadlineEnforcedAsync);
        await RunTestAsync(nameof(TestServerDisposalExactlyOnceAsync), TestServerDisposalExactlyOnceAsync);
        await RunTestAsync(nameof(TestLineReaderOwnedBoundedMemoryAsync), TestLineReaderOwnedBoundedMemoryAsync);
        await RunTestAsync(nameof(TestUnknownArgvDoesNotEchoSecretAsync), TestUnknownArgvDoesNotEchoSecretAsync);
        await RunTestAsync(nameof(TestSubprocessStdoutStallAsync), TestSubprocessStdoutStallAsync);
        await RunTestAsync(nameof(TestSubprocessSigtermWithOpenStdinPipeAsync), TestSubprocessSigtermWithOpenStdinPipeAsync);
        await RunTestAsync(nameof(TestSubprocessEofTeardownAsync), TestSubprocessEofTeardownAsync);

        // Feature and Lifecycle test suites
        await RunTestAsync(nameof(FeatureChecks), FeatureChecks.RunAsync);
        await RunTestAsync(nameof(LifecycleChecks), LifecycleChecks.RunAsync);
        await RunTestAsync(nameof(StorageChecks), StorageChecks.RunAsync);
        await RunTestAsync(nameof(ProfileChecks), ProfileChecks.RunAsync);
        await RunTestAsync(nameof(RuntimePolicyChecks), RuntimePolicyChecks.RunAsync);

        Console.WriteLine($"\nTest Results: {s_passedTests} passed, {s_failedTests} failed.");
        return s_failedTests == 0 ? 0 : 1;
    }

    private static async Task RunTestAsync(string testName, Func<Task> testFunc)
    {
        try
        {
            await testFunc();
            Console.WriteLine($"[PASS] {testName}");
            s_passedTests++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] {testName}: {ex.Message}");
            s_failedTests++;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"Assertion failed: {message}");
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Assertion failed: {message}. Expected '{expected}', but got '{actual}'.");
    }

    #region Test Cases

    private static async Task TestValidRequestRoundTripAsync()
    {
        var fakeHandler = new FakeProtocolHandler();
        fakeHandler.SetMethodResult("snapshot", new Dictionary<string, object>
        {
            ["state"] = "disconnected",
            ["revision"] = "rev-0"
        });

        using var input = new MemoryStream(Encoding.UTF8.GetBytes("{\"v\":1,\"id\":\"req-1\",\"method\":\"snapshot\",\"params\":{}}\n"));
        using var output = new MemoryStream();

        var server = new ProtocolServer(fakeHandler, input, output);
        await server.RunAsync(CancellationToken.None);

        string responseJson = Encoding.UTF8.GetString(output.ToArray()).Trim();
        using var doc = JsonDocument.Parse(responseJson);

        AssertEqual(1, doc.RootElement.GetProperty("v").GetInt32(), "Version must be 1");
        AssertEqual("req-1", doc.RootElement.GetProperty("id").GetString(), "ID must match");
        var result = doc.RootElement.GetProperty("result");
        AssertEqual("disconnected", result.GetProperty("state").GetString(), "State must match");
    }

    private static async Task TestInvalidVersionAsync()
    {
        var fakeHandler = new FakeProtocolHandler();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("{\"v\":2,\"id\":\"req-2\",\"method\":\"snapshot\",\"params\":{}}\n"));
        using var output = new MemoryStream();

        var server = new ProtocolServer(fakeHandler, input, output);
        await server.RunAsync(CancellationToken.None);

        string responseJson = Encoding.UTF8.GetString(output.ToArray()).Trim();
        using var doc = JsonDocument.Parse(responseJson);

        var error = doc.RootElement.GetProperty("error");
        AssertEqual("invalid_version", error.GetProperty("code").GetString(), "Code must be invalid_version");
    }

    private static async Task TestInvalidRequestIdAsync()
    {
        var fakeHandler = new FakeProtocolHandler();
        // ID with invalid spaces
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("{\"v\":1,\"id\":\"invalid id!\",\"method\":\"snapshot\",\"params\":{}}\n"));
        using var output = new MemoryStream();

        var server = new ProtocolServer(fakeHandler, input, output);
        await server.RunAsync(CancellationToken.None);

        string responseJson = Encoding.UTF8.GetString(output.ToArray()).Trim();
        using var doc = JsonDocument.Parse(responseJson);

        var error = doc.RootElement.GetProperty("error");
        AssertEqual("invalid_id", error.GetProperty("code").GetString(), "Code must be invalid_id");
    }

    private static async Task TestUnknownFieldsAtRootAsync()
    {
        var fakeHandler = new FakeProtocolHandler();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("{\"v\":1,\"id\":\"req-3\",\"method\":\"snapshot\",\"params\":{},\"unknownProperty\":123}\n"));
        using var output = new MemoryStream();

        var server = new ProtocolServer(fakeHandler, input, output);
        await server.RunAsync(CancellationToken.None);

        string responseJson = Encoding.UTF8.GetString(output.ToArray()).Trim();
        using var doc = JsonDocument.Parse(responseJson);

        var error = doc.RootElement.GetProperty("error");
        AssertEqual("invalid_request", error.GetProperty("code").GetString(), "Code must be invalid_request");
    }

    private static async Task TestDuplicateKeysAtRootAsync()
    {
        var fakeHandler = new FakeProtocolHandler();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("{\"v\":1,\"id\":\"req-4\",\"method\":\"snapshot\",\"id\":\"req-4b\",\"params\":{}}\n"));
        using var output = new MemoryStream();

        var server = new ProtocolServer(fakeHandler, input, output);
        await server.RunAsync(CancellationToken.None);

        string responseJson = Encoding.UTF8.GetString(output.ToArray()).Trim();
        using var doc = JsonDocument.Parse(responseJson);

        var error = doc.RootElement.GetProperty("error");
        AssertEqual("duplicate_key", error.GetProperty("code").GetString(), "Code must be duplicate_key");
    }

    private static async Task TestDuplicateKeysInParamsAsync()
    {
        var fakeHandler = new FakeProtocolHandler();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("{\"v\":1,\"id\":\"req-5\",\"method\":\"servers.import\",\"params\":{\"text\":\"a\",\"text\":\"b\"}}\n"));
        using var output = new MemoryStream();

        var server = new ProtocolServer(fakeHandler, input, output);
        await server.RunAsync(CancellationToken.None);

        string responseJson = Encoding.UTF8.GetString(output.ToArray()).Trim();
        using var doc = JsonDocument.Parse(responseJson);

        var error = doc.RootElement.GetProperty("error");
        AssertEqual("duplicate_key", error.GetProperty("code").GetString(), "Code must be duplicate_key in params");
    }

    private static async Task TestMaxJsonDepthExceededAsync()
    {
        var fakeHandler = new FakeProtocolHandler();
        var sb = new StringBuilder();
        sb.Append("{\"v\":1,\"id\":\"req-depth\",\"method\":\"snapshot\",\"params\":");
        for (int i = 0; i < 35; i++)
        {
            sb.Append("{\"nest\":");
        }
        sb.Append("1");
        for (int i = 0; i < 35; i++)
        {
            sb.Append('}');
        }
        sb.Append("}\n");

        using var input = new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
        using var output = new MemoryStream();

        var server = new ProtocolServer(fakeHandler, input, output);
        await server.RunAsync(CancellationToken.None);

        string responseJson = Encoding.UTF8.GetString(output.ToArray()).Trim();
        using var doc = JsonDocument.Parse(responseJson);

        var error = doc.RootElement.GetProperty("error");
        AssertEqual("depth_limit_exceeded", error.GetProperty("code").GetString(), "Code must be depth_limit_exceeded");
    }

    private static async Task TestInputFrameOversizedAsync()
    {
        var fakeHandler = new FakeProtocolHandler();
        fakeHandler.SetMethodResult("snapshot", new Dictionary<string, object> { ["ok"] = true });

        // Build a line larger than 256 KiB
        using var input = new MemoryStream();
        byte[] largePadding = new byte[260 * 1024];
        Array.Fill(largePadding, (byte)'A');
        input.Write(largePadding);
        input.WriteByte((byte)'\n');

        // Next line is a valid request
        byte[] validLine = Encoding.UTF8.GetBytes("{\"v\":1,\"id\":\"req-recovery\",\"method\":\"snapshot\",\"params\":{}}\n");
        input.Write(validLine);
        input.Position = 0;

        using var output = new MemoryStream();
        var server = new ProtocolServer(fakeHandler, input, output);
        await server.RunAsync(CancellationToken.None);

        string[] responses = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert(responses.Length >= 2, "Expected at least 2 responses (oversized error + recovered response)");

        using var errorDoc = JsonDocument.Parse(responses[0]);
        AssertEqual("payload_too_large", errorDoc.RootElement.GetProperty("error").GetProperty("code").GetString(), "First response must be payload_too_large");

        using var validDoc = JsonDocument.Parse(responses[1]);
        AssertEqual("req-recovery", validDoc.RootElement.GetProperty("id").GetString(), "Second response must be recovered request ID");
    }

    private static async Task TestSingleOrdinaryCommandBusyAsync()
    {
        var fakeHandler = new FakeProtocolHandler();
        var tcs = new TaskCompletionSource<object>();
        fakeHandler.SetAsyncMethod("connect", async ct => await tcs.Task);

        var dispatcher = new ProtocolDispatcher(fakeHandler);

        var req1 = new ProtocolRequest(1, "ord-1", "connect", default);
        var req2 = new ProtocolRequest(1, "ord-2", "snapshot", default);

        // Start req1 in background
        var task1 = dispatcher.DispatchAsync(req1, CancellationToken.None);

        // Immediately try req2
        var resp2 = await dispatcher.DispatchAsync(req2, CancellationToken.None);

        AssertEqual("ord-2", resp2.Id, "ID must match req2");
        Assert(resp2.Error != null, "resp2 must have error");
        AssertEqual("busy", resp2.Error!.Code, "Error code must be busy");

        // Complete req1
        tcs.SetResult(new Dictionary<string, object> { ["status"] = "connected" });
        var resp1 = await task1;
        AssertEqual("ord-1", resp1.Id, "ID must match req1");
        Assert(resp1.Error == null, "resp1 must not have error");
    }

    private static async Task TestCancelUrgentControlAsync()
    {
        var fakeHandler = new FakeProtocolHandler();
        var tcs = new TaskCompletionSource<object>();
        fakeHandler.SetAsyncMethod("servers.test", async ct =>
        {
            using var reg = ct.Register(() => tcs.TrySetCanceled());
            return await tcs.Task;
        });

        var dispatcher = new ProtocolDispatcher(fakeHandler);

        var req1 = new ProtocolRequest(1, "long-op", "servers.test", default);
        var task1 = dispatcher.DispatchAsync(req1, CancellationToken.None);

        // Cancel long-op
        using var cancelDoc = JsonDocument.Parse("{\"id\":\"long-op\"}");
        var cancelReq = new ProtocolRequest(1, "c-1", "cancel", cancelDoc.RootElement);

        var cancelResp = await dispatcher.DispatchAsync(cancelReq, CancellationToken.None);
        Assert(cancelResp.Error == null, "Cancel response must not be an error");

        var resp1 = await task1;
        Assert(resp1.Error != null, "Cancelled request must return error");
        AssertEqual("cancelled", resp1.Error!.Code, "Cancelled request code must be cancelled");
    }

    private static async Task TestInvalidControlsHaveNoEffectsAsync()
    {
        var handler = new FakeProtocolHandler();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.SetAsyncMethod("servers.test", async ct =>
        {
            started.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, ct);
            return new { unreachable = true };
        });
        var dispatcher = new ProtocolDispatcher(handler);
        var active = dispatcher.DispatchAsync(new ProtocolRequest(1, "active", "servers.test", default), CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var invalid = new[]
            {
                ("cancel", "{\"id\":\"active\",\"extra\":true}"),
                ("cancel", "{\"id\":\"bad id\"}"),
                ("cancel", "{\"id\":42}"),
                ("cancel", "{}"),
                ("cancel", "{\"id\":\"" + new string('x', 65) + "\"}"),
                ("disconnect", "{\"extra\":true}")
            };
            foreach (var (method, json) in invalid)
            {
                using var doc = JsonDocument.Parse(json);
                var reply = await dispatcher.DispatchAsync(new ProtocolRequest(1, "invalid-control", method, doc.RootElement), CancellationToken.None);
                AssertEqual("invalid_request", reply.Error?.Code, "Invalid control must be rejected");
                Assert(!active.IsCompleted, "Rejected control must not cancel active work");
                AssertEqual(1, handler.TotalInvocations, "Rejected control must not reach backend");
            }
            using var valid = JsonDocument.Parse("{\"id\":\"active\"}");
            var cancelled = await dispatcher.DispatchAsync(new ProtocolRequest(1, "valid-control", "cancel", valid.RootElement), CancellationToken.None);
            Assert(cancelled.Error == null, "Valid cancel must remain usable");
            var outcome = await active.WaitAsync(TimeSpan.FromSeconds(2));
            AssertEqual("cancelled", outcome.Error?.Code, "Valid cancel must cancel target operation");
        }
        finally
        {
            dispatcher.CancelActiveOperation();
            await active.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private static async Task TestDisconnectUrgentControlAsync()
    {
        var fakeHandler = new FakeProtocolHandler();
        var tcs = new TaskCompletionSource<object>();
        fakeHandler.SetAsyncMethod("connect", async ct =>
        {
            using var reg = ct.Register(() => tcs.TrySetCanceled());
            return await tcs.Task;
        });

        fakeHandler.SetMethodResult("disconnect", new Dictionary<string, object> { ["state"] = "disconnected" });

        var dispatcher = new ProtocolDispatcher(fakeHandler);

        var connectReq = new ProtocolRequest(1, "conn-1", "connect", default);
        var connectTask = dispatcher.DispatchAsync(connectReq, CancellationToken.None);

        var disconnectReq = new ProtocolRequest(1, "disc-1", "disconnect", default);
        var disconnectResp = await dispatcher.DispatchAsync(disconnectReq, CancellationToken.None);

        Assert(disconnectResp.Error == null, "Disconnect must succeed and not be busy");
        AssertEqual("disc-1", disconnectResp.Id, "Disconnect ID must match");

        var connectResp = await connectTask;
        Assert(connectResp.Error != null, "Connect was cancelled by disconnect");
        AssertEqual("cancelled", connectResp.Error!.Code, "Connect error code must be cancelled");
    }

    private static async Task TestStateEventCoalescingAsync()
    {
        using var output = new MemoryStream();
        bool shutdownTriggered = false;
        var queue = new ProtocolOutputQueue(output, () => shutdownTriggered = true);
        queue.Start(CancellationToken.None);

        // Enqueue 10 rapid state updates
        for (int i = 0; i < 10; i++)
        {
            queue.EnqueueStateEvent(new Dictionary<string, object> { ["state"] = "state_" + i });
        }

        // Wait brief moment for write loop
        await Task.Delay(100);
        await queue.DisposeAsync();

        string outputText = Encoding.UTF8.GetString(output.ToArray());
        string[] lines = outputText.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Because state coalesces, we should not have 10 frames in the output
        Assert(lines.Length <= 8, $"State frames coalesced: expected <= 8 lines, got {lines.Length}");
        Assert(!shutdownTriggered, "Backpressure shutdown should not have been triggered");
    }

    private static async Task TestOutputMaxFrameSizeAsync()
    {
        var fakeHandler = new FakeProtocolHandler();
        // Return result exceeding 256 KiB
        byte[] hugeArray = new byte[260 * 1024];
        fakeHandler.SetMethodResult("snapshot", new Dictionary<string, object>
        {
            ["data"] = Convert.ToBase64String(hugeArray)
        });

        using var input = new MemoryStream(Encoding.UTF8.GetBytes("{\"v\":1,\"id\":\"req-large\",\"method\":\"snapshot\",\"params\":{}}\n"));
        using var output = new MemoryStream();

        var server = new ProtocolServer(fakeHandler, input, output);
        await server.RunAsync(CancellationToken.None);

        string responseJson = Encoding.UTF8.GetString(output.ToArray()).Trim();
        using var doc = JsonDocument.Parse(responseJson);

        var error = doc.RootElement.GetProperty("error");
        AssertEqual("internal_error", error.GetProperty("code").GetString(), "Oversized output must produce internal_error");
        AssertEqual("Response size exceeds limit.", error.GetProperty("message").GetString(), "Message must match");
    }

    private static async Task TestNoSecretInErrorsAsync()
    {
        var fakeHandler = new FakeProtocolHandler();
        fakeHandler.SetAsyncMethod("subscriptions.add", ct =>
        {
            throw new InvalidOperationException("Failed to connect to https://secret-token:p@ssw0rd@vpn.provider.com/sub?id=super-secret");
        });

        using var input = new MemoryStream(Encoding.UTF8.GetBytes("{\"v\":1,\"id\":\"req-secret\",\"method\":\"subscriptions.add\",\"params\":{}}\n"));
        using var output = new MemoryStream();

        var server = new ProtocolServer(fakeHandler, input, output);
        await server.RunAsync(CancellationToken.None);

        string responseJson = Encoding.UTF8.GetString(output.ToArray()).Trim();
        Assert(!responseJson.Contains("secret-token"), "Response must not leak secrets!");
        Assert(!responseJson.Contains("p@ssw0rd"), "Response must not leak password!");
        Assert(!responseJson.Contains("vpn.provider.com"), "Response must not leak server URL!");

        using var doc = JsonDocument.Parse(responseJson);
        var error = doc.RootElement.GetProperty("error");
        AssertEqual("internal_error", error.GetProperty("code").GetString(), "Error code must be sanitized to internal_error");
        AssertEqual("An internal error occurred.", error.GetProperty("message").GetString(), "Error message must be safe generic text");
    }

    private static async Task TestEofShutdownAsync()
    {
        var fakeHandler = new FakeProtocolHandler();
        // Empty input stream simulates immediate EOF
        using var input = new MemoryStream();
        using var output = new MemoryStream();

        var server = new ProtocolServer(fakeHandler, input, output);
        int exitCode = await server.RunAsync(CancellationToken.None);

        AssertEqual(0, exitCode, "EOF must result in clean exit code 0");
        Assert(fakeHandler.IsDisposed, "Handler must be disposed on shutdown");
        AssertEqual(1, fakeHandler.DisposeCount, "Handler must be disposed exactly once on shutdown");
    }

    private static async Task TestLineReaderOwnedBoundedMemoryAsync()
    {
        // 1. Valid line ending with \n and \r\n
        using var stream1 = new MemoryStream(Encoding.UTF8.GetBytes("line one\nsecond line\r\n"));
        var reader1 = new ProtocolLineReader(stream1);
        var res1 = await reader1.ReadLineAsync(CancellationToken.None);
        AssertEqual(LineReadStatus.Success, res1.Status, "First line status");
        AssertEqual("line one", Encoding.UTF8.GetString(res1.Bytes), "First line Bytes");
        AssertEqual("line one", Encoding.UTF8.GetString(res1.Memory.Span), "First line Memory.Span");

        var res2 = await reader1.ReadLineAsync(CancellationToken.None);
        AssertEqual(LineReadStatus.Success, res2.Status, "Second line status");
        AssertEqual("second line", Encoding.UTF8.GetString(res2.Bytes), "Second line Bytes (CR trimmed)");

        var res3 = await reader1.ReadLineAsync(CancellationToken.None);
        AssertEqual(LineReadStatus.Eof, res3.Status, "EOF status");

        // 2. Oversized line
        byte[] large = new byte[ProtocolConstants.MaxInputFrameBytes + 100];
        Array.Fill(large, (byte)'X');
        large[^1] = (byte)'\n';
        using var stream2 = new MemoryStream(large);
        var reader2 = new ProtocolLineReader(stream2);
        var resOversized = await reader2.ReadLineAsync(CancellationToken.None);
        AssertEqual(LineReadStatus.Oversized, resOversized.Status, "Oversized line status");
    }

    private static async Task TestUnknownArgvDoesNotEchoSecretAsync()
    {
        var originalError = Console.Error;
        using var errorCapture = new StringWriter();
        Console.SetError(errorCapture);
        try
        {
            int exitCode = await Program.Main(new[] { "--stdio", "--secret-token=MY_SUPER_SECRET_12345" });
            AssertEqual(1, exitCode, "Exit code for unknown argument must be 1");
            string err = errorCapture.ToString();
            Assert(!err.Contains("MY_SUPER_SECRET_12345"), "Stderr must never echo unknown argument content");
            Assert(err.Contains("Error: Unknown argument."), "Stderr must report generic unknown argument error");
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    private static async Task TestActiveLongOperationEofAsync()
    {
        var fakeHandler = new FakeProtocolHandler();
        var connectStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool connectCompletedSuccessfully = false;

        fakeHandler.SetAsyncMethod("connect", async ct =>
        {
            connectStarted.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                connectCancelled.TrySetResult(true);
                throw;
            }

            connectCompletedSuccessfully = true;
            return new Dictionary<string, object> { ["state"] = "connected" };
        });

        using var input = new InteractiveInputStream();
        input.WriteLine("{\"v\":1,\"id\":\"conn-1\",\"method\":\"connect\",\"params\":{\"revision\":\"rev-0\"}}");

        using var output = new MemoryStream();
        var server = new ProtocolServer(fakeHandler, input, output);

        var serverTask = server.RunAsync(CancellationToken.None);

        // Wait until connect method is actively running in fakeHandler
        await connectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Now signal EOF on standard input
        input.SignalEof();

        // Server should shut down cleanly within bounded time
        int exitCode = await serverTask.WaitAsync(TimeSpan.FromSeconds(3));

        AssertEqual(0, exitCode, "Exit code on EOF must be 0");
        Assert(fakeHandler.IsDisposed, "Handler must be disposed on shutdown");
        AssertEqual(1, fakeHandler.DisposeCount, "Handler must be disposed exactly once on shutdown");

        // Verify genuine cancellation of active long operation (no fake pass assertion!)
        bool wasCancelled = await connectCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert(wasCancelled, "Active long operation must be cancelled on EOF");
        Assert(!connectCompletedSuccessfully, "Connect must not succeed after EOF");

        // Verify output does NOT contain connect success frame
        string outputText = Encoding.UTF8.GetString(output.ToArray());
        Assert(!outputText.Contains("\"state\":\"connected\""), "Output must not contain connect success after EOF");
    }

    private static async Task TestSynchronousHandlerDoesNotBlockControlsAsync()
    {
        foreach (var method in new[] { "servers.test", "disconnect" })
        {
            using var release = new ManualResetEventSlim();
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new FakeProtocolHandler();
            handler.SetAsyncMethod(method, ct =>
            {
                started.TrySetResult(true);
                release.Wait(ct); // Intentionally blocks before returning a Task.
                return Task.FromResult<object>(new { done = true });
            });
            using var input = new InteractiveInputStream();
            using var output = new ObservableOutputStream();
            await using var server = new ProtocolServer(handler, input, output);
            input.WriteLine($"{{\"v\":1,\"id\":\"blocked\",\"method\":\"{method}\",\"params\":{{}}}}");
            // Keep the deliberately broken implementation from blocking the test runner.
            var run = Task.Run(() => server.RunAsync(CancellationToken.None));
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
                input.WriteLine("{\"v\":1,\"id\":\"control\",\"method\":\"cancel\",\"params\":{\"id\":\"blocked\"}}");
                await output.WaitForLineCountAsync(method == "disconnect" ? 1 : 2, TimeSpan.FromSeconds(2));
                var response = output.GetFlushedLines().Select(line => JsonDocument.Parse(line))
                    .ToList();
                try
                {
                    var control = response.Single(doc => doc.RootElement.GetProperty("id").GetString() == "control");
                    AssertEqual(method != "disconnect", control.RootElement.GetProperty("result").GetProperty("cancelled").GetBoolean(),
                        "Transport must process cancel while handler blocks synchronously");
                }
                finally { foreach (var doc in response) doc.Dispose(); }
            }
            finally
            {
                release.Set();
                input.SignalEof();
                await run.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    private static async Task TestActiveLongOperationCancelAsync()
    {
        var fakeHandler = new FakeProtocolHandler();
        var testStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var testCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        fakeHandler.SetAsyncMethod("servers.test", async ct =>
        {
            testStarted.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                testCancelled.TrySetResult(true);
                throw;
            }
            return new Dictionary<string, object> { ["reachable"] = true };
        });

        using var input = new InteractiveInputStream();
        input.WriteLine("{\"v\":1,\"id\":\"long-1\",\"method\":\"servers.test\",\"params\":{}}");

        using var output = new MemoryStream();
        var server = new ProtocolServer(fakeHandler, input, output);

        var serverTask = server.RunAsync(CancellationToken.None);

        // Wait until servers.test is running
        await testStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Send cancel urgent request for long-1
        input.WriteLine("{\"v\":1,\"id\":\"cancel-1\",\"method\":\"cancel\",\"params\":{\"id\":\"long-1\"}}");

        // Wait for cancellation to be observed in handler
        bool wasCancelled = await testCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert(wasCancelled, "servers.test cancellation token must be cancelled");

        // Signal EOF to end server
        input.SignalEof();
        int exitCode = await serverTask.WaitAsync(TimeSpan.FromSeconds(2));
        AssertEqual(0, exitCode, "Exit code must be 0");

        // Parse responses
        string outputText = Encoding.UTF8.GetString(output.ToArray());
        string[] lines = outputText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert(lines.Length >= 2, $"Expected at least 2 responses, got {lines.Length}");

        string? cancelLine = null;
        string? longLine = null;
        foreach (var line in lines)
        {
            if (line.Contains("\"id\":\"cancel-1\""))
                cancelLine = line;
            if (line.Contains("\"id\":\"long-1\""))
                longLine = line;
        }

        Assert(cancelLine != null, "Cancel response must be present");
        using var cancelDoc = JsonDocument.Parse(cancelLine!);
        AssertEqual(true, cancelDoc.RootElement.GetProperty("result").GetProperty("cancelled").GetBoolean(), "Cancel response must indicate cancelled: true");

        Assert(longLine != null, "Long operation response must be present");
        using var longDoc = JsonDocument.Parse(longLine!);
        AssertEqual("cancelled", longDoc.RootElement.GetProperty("error").GetProperty("code").GetString(), "Long op response must be cancelled error");
    }

    private static async Task TestBurst2000RequestsAsync()
    {
        var fakeHandler = new FakeProtocolHandler();
        var longOpStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var longOpFinish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        fakeHandler.SetAsyncMethod("connect", async ct =>
        {
            longOpStarted.TrySetResult(true);
            await longOpFinish.Task.WaitAsync(ct).ConfigureAwait(false);
            return new Dictionary<string, object> { ["state"] = "connected" };
        });

        using var input = new InteractiveInputStream();
        // Request 1: ordinary long operation
        input.WriteLine("{\"v\":1,\"id\":\"req-1\",\"method\":\"connect\",\"params\":{\"revision\":\"rev-0\"}}");

        const int totalBurst = 2000;
        // Bounded barrier: wait until all 1999 burst requests have been rejected with busy and written to output
        using var output = new ObservableOutputStream();
        var server = new ProtocolServer(fakeHandler, input, output);
        var serverTask = server.RunAsync(CancellationToken.None);

        await longOpStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Now pump requests 2 to 2000 in a rapid burst
        for (int i = 2; i <= totalBurst; i++)
        {
            input.WriteLine($"{{\"v\":1,\"id\":\"req-{i}\",\"method\":\"connect\",\"params\":{{\"revision\":\"rev-0\"}}}}");
        }

        // Bounded barrier: ensure all 1999 pipelined requests receive busy BEFORE releasing the long operation
        await output.WaitForLineCountAsync(totalBurst - 1, TimeSpan.FromSeconds(15));

        // Allow long operation to finish
        longOpFinish.SetResult(true);

        // Bounded barrier: ensure long operation response is flushed to output BEFORE signaling EOF
        await output.WaitForLineCountAsync(totalBurst, TimeSpan.FromSeconds(15));

        // Signal EOF
        input.SignalEof();

        int exitCode = await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
        AssertEqual(0, exitCode, "Exit code must be 0 after burst");

        var lines = output.GetFlushedLines();
        AssertEqual(totalBurst, lines.Count, $"Must have exactly {totalBurst} responses");

        // Match responses by request ID, not assuming first-response sequence
        var responsesById = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var parsedDocs = new List<JsonDocument>(lines.Count);
        try
        {
            foreach (var line in lines)
            {
                var doc = JsonDocument.Parse(line);
                parsedDocs.Add(doc);
                var id = doc.RootElement.GetProperty("id").GetString()!;
                responsesById[id] = doc.RootElement;
            }

            // Verify request 1 succeeded
            Assert(responsesById.TryGetValue("req-1", out var req1Elem), "Response for 'req-1' must be present");
            Assert(!req1Elem.TryGetProperty("error", out _), "req-1 must not have error");
            Assert(req1Elem.TryGetProperty("result", out var req1Result), "req-1 must have result");
            AssertEqual("connected", req1Result.GetProperty("state").GetString(), "req-1 result state");

            // Verify requests 2 to 2000 received "busy" error
            int busyCount = 0;
            for (int i = 2; i <= totalBurst; i++)
            {
                var id = $"req-{i}";
                Assert(responsesById.TryGetValue(id, out var elem), $"Response for '{id}' must be present");
                Assert(elem.TryGetProperty("error", out var errElem), $"Response for '{id}' must have error");
                AssertEqual("busy", errElem.GetProperty("code").GetString(), $"Response for '{id}' error code");
                busyCount++;
            }
            AssertEqual(totalBurst - 1, busyCount, "All pipelined ordinary requests must receive 'busy' error");

            // Ensure exactly 1 ordinary execution in handler
            AssertEqual(1, fakeHandler.TotalInvocations, "Total ordinary invocations in handler must be exactly 1");
            AssertEqual(1, fakeHandler.MaxConcurrentInvocations, "Max concurrent ordinary invocations must be exactly 1");
        }
        finally
        {
            foreach (var doc in parsedDocs)
            {
                doc.Dispose();
            }
        }
    }

    private static async Task TestAsyncResponseBackpressureBoundAsync()
    {
        var handler = new FakeProtocolHandler();
        handler.SetAsyncMethod("snapshot", async ct =>
        {
            await Task.Delay(1, ct);
            return new { state = "disconnected" };
        });
        var blocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var output = new PermanentlyGatedCancellationIgnoringStream(blocked, release.Task);
        using var input = new InteractiveInputStream();
        var server = new ProtocolServer(handler, input, output);
        var run = server.RunAsync(CancellationToken.None);
        try
        {
            // Pace requests beyond handler completion so they exercise response
            // waiters, not merely the dispatcher's already-busy rejection path.
            for (int i = 0; i < 50; i++)
            {
                input.WriteLine($"{{\"v\":1,\"id\":\"paced-{i}\",\"method\":\"snapshot\",\"params\":{{}}}}");
                await Task.Delay(20);
            }
            await blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
            int count;
            lock (handler) count = handler.TotalInvocations;
            int bound = 1 + ProtocolConstants.MaxOutputQueueFrames + 1 + ProtocolConstants.MaxUrgentOperations;
            Assert(count <= bound, $"Handler admission must stop at bounded output+transport capacity {bound}, observed {count}");
            Assert(count >= 5, "Test must exercise several asynchronous completions");
        }
        finally
        {
            release.TrySetResult(true);
            input.SignalEof();
            await run.WaitAsync(TimeSpan.FromSeconds(8));
        }
    }

    private static async Task TestReadBackpressureAsync()
    {
        var fakeHandler = new FakeProtocolHandler();
        fakeHandler.SetMethodResult("snapshot", new Dictionary<string, object> { ["state"] = "disconnected" });

        var streamGated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeBlocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // GatedOutputStream blocks after writing 2 frames
        using var output = new GatedOutputStream(maxAllowedFramesBeforeBlock: 2, writeBlocked, streamGated.Task);

        using var input = new InteractiveInputStream();
        // Write 12 snapshot requests
        for (int i = 1; i <= 12; i++)
        {
            input.WriteLine($"{{\"v\":1,\"id\":\"snap-{i}\",\"method\":\"snapshot\",\"params\":{{}}}}");
        }

        var server = new ProtocolServer(fakeHandler, input, output);
        var serverTask = server.RunAsync(CancellationToken.None);

        try
        {
            await writeBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert(!serverTask.IsCompleted, "Server must remain active while output is gated");
            streamGated.TrySetResult(true);
            // EOF cancels pending response enqueues by contract. Keep the peer
            // alive until delivery, rather than racing teardown against this check.
            await output.WaitForLineCountAsync(12, TimeSpan.FromSeconds(3));
            var lines = output.GetFlushedLines();
            AssertEqual(12, lines.Count, "All 12 responses must be delivered through backpressure drain without loss");
            var ids = new HashSet<string>();
            foreach (var line in lines)
            {
                using var doc = JsonDocument.Parse(line);
                Assert(ids.Add(doc.RootElement.GetProperty("id").GetString()!), "Responses must have unique IDs");
            }
            for (int i = 1; i <= 12; i++) Assert(ids.Contains($"snap-{i}"), "Every request must receive a response");
        }
        finally
        {
            streamGated.TrySetResult(true);
            input.SignalEof();
            int exitCode = await serverTask.WaitAsync(TimeSpan.FromSeconds(3));
            AssertEqual(0, exitCode, "Exit code must be 0 after backpressure drain");
        }
    }

    private static async Task TestOutputStallDeadlineSelfTerminationAsync()
    {
        var fakeHandler = new FakeProtocolHandler();
        fakeHandler.SetMethodResult("snapshot", new Dictionary<string, object>
        {
            ["state"] = "disconnected",
            ["revision"] = "rev-stall"
        });

        var outputGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var teardownStartedWhileBlocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        fakeHandler.CustomDisposeAsync = async () =>
        {
            // Verify teardown handler starts while output is still blocked
            if (!outputGate.Task.IsCompleted)
            {
                teardownStartedWhileBlocked.TrySetResult(true);
            }
            await Task.CompletedTask;
        };

        using var input = new InteractiveInputStream();
        using var output = new PermanentlyGatedCancellationIgnoringStream(writeStarted, outputGate.Task);

        // Open stdin with a valid snapshot request (do NOT signal EOF)
        input.WriteLine("{\"v\":1,\"id\":\"snap-stall\",\"method\":\"snapshot\",\"params\":{}}");

        var server = new ProtocolServer(fakeHandler, input, output);

        try
        {
            // Server self-terminates without user cancel or EOF
            var serverTask = server.RunAsync(CancellationToken.None);

            // Wait until output write has actually started and is blocked on the gate
            await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            // Verify output gate is still blocked prior to stall timeout
            Assert(!outputGate.Task.IsCompleted, "Output gate must remain blocked during write");

            // Server must self-terminate boundedly (production deadline is 3s)
            var sw = Stopwatch.StartNew();
            int exitCode = await serverTask.WaitAsync(TimeSpan.FromSeconds(6));
            sw.Stop();

            AssertEqual(0, exitCode, "Exit code must be 0 upon stall self-termination");
            Assert(sw.Elapsed >= TimeSpan.FromSeconds(2.5), $"Stall deadline must not fire prematurely, took {sw.Elapsed.TotalSeconds}s");
            Assert(sw.Elapsed < TimeSpan.FromSeconds(6), $"Server must terminate boundedly, took {sw.Elapsed.TotalSeconds}s");

            // Verify teardown handler started while output was still blocked
            Assert(teardownStartedWhileBlocked.Task.IsCompleted, "Teardown handler must start while output is still blocked");

            // Verify handler disposal was executed exactly once
            AssertEqual(1, fakeHandler.DisposeCount, "Handler must be disposed exactly once");

            // Verify output gate was NOT released prior to assertions (no fake green)
            Assert(!outputGate.Task.IsCompleted, "Output gate must still be blocked during assertion verification");
        }
        finally
        {
            // Ensure gates are released finally to reclaim test tasks
            outputGate.TrySetResult(true);
            input.SignalEof();
        }
    }

    private static async Task TestOutputQueueStallPreventsEnqueueAsync()
    {
        await CheckOutputQueueStallPreventsEnqueueAsync(false);
        await CheckOutputQueueStallPreventsEnqueueAsync(true);
    }

    private static async Task CheckOutputQueueStallPreventsEnqueueAsync(bool synchronousBlock)
    {
        var writeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outputGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdownSignaled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var output = new PermanentlyGatedCancellationIgnoringStream(writeStarted, outputGate.Task, synchronousBlock);
        await using var queue = new ProtocolOutputQueue(
            output,
            () => shutdownSignaled.TrySetResult(true),
            TimeSpan.FromMilliseconds(200));

        queue.Start(CancellationToken.None);

        try
        {
            // Enqueue initial response that will stall in output
            bool firstEnqueued = queue.EnqueueResponse(ProtocolResponseFrame.CreateSuccess("id-1", new Dictionary<string, object>()));
            Assert(firstEnqueued, "First response should be enqueued");

            await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            // Wait for stall timeout to signal shutdown (200ms)
            await shutdownSignaled.Task.WaitAsync(TimeSpan.FromSeconds(2));

            // Verify queue rejects any further enqueue acceptance
            bool rejectedSync = queue.EnqueueResponse(ProtocolResponseFrame.CreateSuccess("id-2", new Dictionary<string, object>()));
            Assert(!rejectedSync, "Sync response enqueue must be rejected after stall");

            bool rejectedAsync = await queue.EnqueueResponseAsync(
                ProtocolResponseFrame.CreateSuccess("id-3", new Dictionary<string, object>()),
                CancellationToken.None);
            Assert(!rejectedAsync, "Async response enqueue must be rejected after stall");

            bool rejectedState = queue.EnqueueStateEvent(new { state = "connected" });
            Assert(!rejectedState, "State event enqueue must be rejected after stall");

            bool rejectedProgress = queue.EnqueueProgressEvent(new { progress = 50 });
            Assert(!rejectedProgress, "Progress event enqueue must be rejected after stall");
        }
        finally
        {
            outputGate.TrySetResult(true);
        }
    }

    private static async Task TestImmediateSnapshotHandshakeBeforeEofAsync()
    {
        var fakeHandler = new FakeProtocolHandler();
        fakeHandler.SetMethodResult("snapshot", new Dictionary<string, object>
        {
            ["state"] = "disconnected",
            ["revision"] = "rev-initial"
        });

        // Immediate snapshot followed by EOF
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("{\"v\":1,\"id\":\"snap-handshake\",\"method\":\"snapshot\",\"params\":{}}\n"));
        using var output = new MemoryStream();

        var server = new ProtocolServer(fakeHandler, input, output);
        int exitCode = await server.RunAsync(CancellationToken.None);

        AssertEqual(0, exitCode, "Exit code must be 0");
        Assert(fakeHandler.IsDisposed, "Handler must be disposed on shutdown");

        string responseJson = Encoding.UTF8.GetString(output.ToArray()).Trim();
        Assert(!string.IsNullOrEmpty(responseJson), "Snapshot handshake response must NOT be empty");

        using var doc = JsonDocument.Parse(responseJson);
        AssertEqual(1, doc.RootElement.GetProperty("v").GetInt32(), "Protocol version must be 1");
        AssertEqual("snap-handshake", doc.RootElement.GetProperty("id").GetString(), "ID must match");
        var result = doc.RootElement.GetProperty("result");
        AssertEqual("disconnected", result.GetProperty("state").GetString(), "State must match");
    }

    private static async Task TestHandlerDisposalDeadlineEnforcedAsync()
    {
        var fakeHandler = new FakeProtocolHandler();
        // DisposeAsync hangs forever
        var disposalStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fakeHandler.CustomDisposeAsync = async () =>
        {
            disposalStarted.TrySetResult(true);
            await Task.Delay(Timeout.Infinite);
        };

        using var input = new MemoryStream();
        using var output = new MemoryStream();

        var server = new ProtocolServer(fakeHandler, input, output);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int exitCode = await server.RunAsync(CancellationToken.None);
        sw.Stop();

        AssertEqual(0, exitCode, "Exit code must be 0 even when handler disposal hangs");
        Assert(disposalStarted.Task.IsCompleted, "Disposal must have started");
        Assert(sw.Elapsed < TimeSpan.FromSeconds(5), $"Shutdown must enforce disposal deadline under 5s, took {sw.Elapsed.TotalSeconds}s");
    }

    private static async Task TestServerDisposalExactlyOnceAsync()
    {
        var fakeHandler = new FakeProtocolHandler();
        using var input = new MemoryStream();
        using var output = new MemoryStream();

        await using (var server = new ProtocolServer(fakeHandler, input, output))
        {
            int exitCode = await server.RunAsync(CancellationToken.None);
            AssertEqual(0, exitCode, "Exit code must be 0");
        }

        Assert(fakeHandler.IsDisposed, "Handler must be disposed");
        AssertEqual(1, fakeHandler.DisposeCount, "Handler must be disposed exactly once across RunAsync and DisposeAsync");
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    private static string FindHeadlessBinary()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string binaryName = OperatingSystem.IsWindows() ? "VPNRouter.Headless.exe" : "VPNRouter.Headless";
        string binaryPath = Path.Combine(baseDir, binaryName);
        if (File.Exists(binaryPath))
        {
            EnsureExecutable(binaryPath);
            return binaryPath;
        }

        string[] candidates =
        [
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "VPNRouter.Headless", "bin", "Release", "net10.0", binaryName)),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "VPNRouter.Headless", "bin", "Debug", "net10.0", binaryName)),
            Path.GetFullPath(Path.Combine(baseDir, "..", "VPNRouter.Headless", binaryName))
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                EnsureExecutable(candidate);
                return candidate;
            }
        }

        throw new FileNotFoundException($"Compiled Headless binary '{binaryName}' not found in '{baseDir}' or candidates.");
    }

    private static void EnsureExecutable(string path)
    {
        if (OperatingSystem.IsLinux() && File.Exists(path))
        {
            try
            {
                var mode = File.GetUnixFileMode(path);
                if (!mode.HasFlag(UnixFileMode.UserExecute))
                {
                    File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute);
                }
            }
            catch
            {
            }
        }
    }

    private static async Task TestSubprocessStdoutStallAsync()
    {
        if (!OperatingSystem.IsLinux()) return;
        var tempDir = Path.Combine(Path.GetTempPath(), "vpnrouter-stdout-stall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var psi = new ProcessStartInfo(FindHeadlessBinary())
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("--stdio");
        psi.ArgumentList.Add("--data-dir");
        psi.ArgumentList.Add(tempDir);
        using var process = Process.Start(psi) ?? throw new Exception("Could not start isolated Headless process");
        int sent = 0;
        var feeder = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < 10000; i++)
                {
                    await process.StandardInput.WriteLineAsync($"{{\"v\":1,\"id\":\"stall-{i}\",\"method\":\"snapshot\",\"params\":{{}}}}");
                    await process.StandardInput.FlushAsync();
                    Interlocked.Increment(ref sent);
                }
            }
            catch (IOException) { /* Child closed its pipe after bounded shutdown. */ }
        });
        try
        {
            // Do not read stdout, signal EOF, or cancel the child. Fill the OS
            // pipe and require the production write deadline to stop the server.
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));
            AssertEqual(0, process.ExitCode, "Non-reading stdout must cause clean bounded exit");
            Assert(Volatile.Read(ref sent) > 0, "At least one request must reach stdin");
            Assert(process.StandardInput.BaseStream.CanWrite, "Parent stdin stays open through child exit");
            Assert(!Directory.EnumerateFileSystemEntries(tempDir).Any(), "Snapshots must not persist any data");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            await feeder.WaitAsync(TimeSpan.FromSeconds(3));
            try { process.StandardInput.Close(); } catch (IOException) { }
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static async Task TestSubprocessSigtermWithOpenStdinPipeAsync()
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.WriteLine("Skipping Linux SIGTERM subprocess test on non-Linux platform.");
            return;
        }

        string binaryPath = FindHeadlessBinary();
        string tempDir = Path.Combine(Path.GetTempPath(), "vpnrouter-sigterm-pipe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var psi = new ProcessStartInfo
        {
            FileName = binaryPath,
            Arguments = $"--stdio --data-dir \"{tempDir}\"",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        Process? process = null;
        bool childExited = false;
        try
        {
            process = Process.Start(psi);
            Assert(process != null, "Failed to spawn VPNRouter.Headless subprocess");

            // Handshake first: send snapshot request (safe read-only method, no VPN methods)
            await process!.StandardInput.WriteLineAsync("{\"v\":1,\"id\":\"handshake-snap\",\"method\":\"snapshot\",\"params\":{}}");
            await process.StandardInput.FlushAsync();

            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            string? responseLine = await process.StandardOutput.ReadLineAsync(readCts.Token);
            Assert(!string.IsNullOrEmpty(responseLine), "Handshake response must not be empty");

            using var doc = JsonDocument.Parse(responseLine!);
            AssertEqual(1, doc.RootElement.GetProperty("v").GetInt32(), "Handshake response version must be 1");
            AssertEqual("handshake-snap", doc.RootElement.GetProperty("id").GetString(), "Handshake response ID must match");
            AssertEqual("disconnected", doc.RootElement.GetProperty("result").GetProperty("state").GetString(), "State must be disconnected");

            // Real observed defect snapshot407c31c4 verification:
            // Stdin write pipe is kept OPEN after SIGTERM until process exit!
            // Under unpatched line reader, blocking read(0) ignores CTS cancellation
            // and process hangs indefinitely waiting for EOF.
            int childPid = process.Id;
            int killResult = kill(childPid, 15 /* SIGTERM */);
            AssertEqual(0, killResult, $"kill({childPid}, SIGTERM) failed with return code {killResult}");

            // Must terminate within bounded timeout <= 10s
            using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(exitCts.Token);
            childExited = true;

            AssertEqual(0, process.ExitCode, $"Process must terminate cleanly with exit code 0 on SIGTERM with unclosed stdin pipe; got {process.ExitCode}");
        }
        finally
        {
            if (process != null)
            {
                if (!childExited && !process.HasExited)
                {
                    try { process.Kill(); } catch { }
                    try { process.WaitForExit(5000); } catch { }
                }
                process.Dispose();
            }

            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private static async Task TestSubprocessEofTeardownAsync()
    {
        string binaryPath = FindHeadlessBinary();
        string tempDir = Path.Combine(Path.GetTempPath(), "vpnrouter-eof-pipe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var psi = new ProcessStartInfo
        {
            FileName = binaryPath,
            Arguments = $"--stdio --data-dir \"{tempDir}\"",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        Process? process = null;
        bool childExited = false;
        try
        {
            process = Process.Start(psi);
            Assert(process != null, "Failed to spawn VPNRouter.Headless subprocess");

            await process!.StandardInput.WriteLineAsync("{\"v\":1,\"id\":\"handshake-eof\",\"method\":\"snapshot\",\"params\":{}}");
            await process.StandardInput.FlushAsync();

            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            string? responseLine = await process.StandardOutput.ReadLineAsync(readCts.Token);
            Assert(!string.IsNullOrEmpty(responseLine), "Handshake response must not be empty");

            // Close stdin pipe (signal EOF)
            process.StandardInput.Close();

            // Must terminate within bounded timeout <= 10s
            using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(exitCts.Token);
            childExited = true;

            AssertEqual(0, process.ExitCode, $"Process must terminate cleanly with exit code 0 on EOF; got {process.ExitCode}");
        }
        finally
        {
            if (process != null)
            {
                if (!childExited && !process.HasExited)
                {
                    try { process.Kill(); } catch { }
                    try { process.WaitForExit(5000); } catch { }
                }
                process.Dispose();
            }

            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    #endregion
}

/// <summary>
/// Stream that can buffer lines interactively and signal EOF on demand.
/// </summary>
public sealed class InteractiveInputStream : Stream
{
    private readonly MemoryStream _buffer = new();
    private readonly object _sync = new();
    private TaskCompletionSource<bool> _dataAvailable = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _eof;
    private long _readPosition;

    public void WriteLine(string line)
    {
        lock (_sync)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
            long origPos = _buffer.Position;
            _buffer.Position = _buffer.Length;
            _buffer.Write(bytes);
            _buffer.Position = origPos;
            _dataAvailable.TrySetResult(true);
        }
    }

    public void SignalEof()
    {
        lock (_sync)
        {
            _eof = true;
            _dataAvailable.TrySetResult(true);
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task waitTask;
            lock (_sync)
            {
                if (_readPosition < _buffer.Length)
                {
                    _buffer.Position = _readPosition;
                    int bytesRead = _buffer.Read(buffer.Span);
                    _readPosition = _buffer.Position;
                    return bytesRead;
                }

                if (_eof)
                {
                    return 0; // EOF
                }

                if (_dataAvailable.Task.IsCompleted)
                {
                    _dataAvailable = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                waitTask = _dataAvailable.Task;
            }

            await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _buffer.Length;
    public override long Position { get => _readPosition; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Stream that can pause/block writes after a given frame count to test read backpressure.
/// </summary>
public sealed class GatedOutputStream : Stream
{
    private readonly ObservableOutputStream _inner = new();
    private readonly int _maxAllowedFramesBeforeBlock;
    private readonly TaskCompletionSource<bool> _writeBlocked;
    private readonly Task _gate;
    private int _framesWritten;
    private readonly object _sync = new();

    public GatedOutputStream(int maxAllowedFramesBeforeBlock, TaskCompletionSource<bool> writeBlocked, Task gate)
    {
        _maxAllowedFramesBeforeBlock = maxAllowedFramesBeforeBlock;
        _writeBlocked = writeBlocked;
        _gate = gate;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        bool shouldBlock = false;
        lock (_sync)
        {
            if (buffer.Span.Contains((byte)'\n'))
            {
                _framesWritten++;
                if (_framesWritten >= _maxAllowedFramesBeforeBlock)
                {
                    shouldBlock = true;
                    _writeBlocked.TrySetResult(true);
                }
            }
        }

        if (shouldBlock && !_gate.IsCompleted)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        lock (_sync)
        {
            _inner.Write(buffer.Span);
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();

    public Task WaitForLineCountAsync(int count, TimeSpan timeout) => _inner.WaitForLineCountAsync(count, timeout);

    public List<string> GetFlushedLines() => _inner.GetFlushedLines();

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>
/// Output stream that permanently blocks on a gate and completely ignores cancellation tokens,
/// used to verify server write+flush stall deadlines and self-termination.
/// </summary>
public sealed class PermanentlyGatedCancellationIgnoringStream : Stream
{
    private readonly TaskCompletionSource<bool> _writeStarted;
    private readonly Task _gate;

    private readonly bool _synchronousBlock;
    public Task WriteStarted => _writeStarted.Task;

    public PermanentlyGatedCancellationIgnoringStream(TaskCompletionSource<bool> writeStarted, Task gate, bool synchronousBlock = false)
    {
        _writeStarted = writeStarted;
        _gate = gate;
        _synchronousBlock = synchronousBlock;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _writeStarted.TrySetResult(true);
        // Exercise both async stalls and blocking before returning a ValueTask.
        if (_synchronousBlock) _gate.GetAwaiter().GetResult();
        return new ValueTask(_gate);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
    {
        _writeStarted.TrySetResult(true);
        _gate.GetAwaiter().GetResult();
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _gate.ConfigureAwait(false);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => 0;
    public override long Position { get => 0; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>
/// Stream that observes output frames and signals when a bounded barrier line count is reached.
/// </summary>
public sealed class ObservableOutputStream : Stream
{
    private readonly MemoryStream _inner = new();
    private readonly object _sync = new();
    private int _lineCount;
    private readonly List<(int Count, TaskCompletionSource<bool> Tcs)> _waiters = new();

    public Task WaitForLineCountAsync(int targetCount, TimeSpan timeout)
    {
        TaskCompletionSource<bool> tcs;
        lock (_sync)
        {
            if (_lineCount >= targetCount)
                return Task.CompletedTask;
            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((targetCount, tcs));
        }
        return tcs.Task.WaitAsync(timeout);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length { get { lock (_sync) { return _inner.Length; } } }
    public override long Position { get { lock (_sync) { return _inner.Position; } } set => throw new NotSupportedException(); }
    public override void Flush() { lock (_sync) { _inner.Flush(); } }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        lock (_sync)
        {
            _inner.Write(buffer);
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i] == (byte)'\n')
                {
                    _lineCount++;
                    for (int w = _waiters.Count - 1; w >= 0; w--)
                    {
                        if (_lineCount >= _waiters[w].Count)
                        {
                            _waiters[w].Tcs.TrySetResult(true);
                            _waiters.RemoveAt(w);
                        }
                    }
                }
            }
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public List<string> GetFlushedLines()
    {
        lock (_sync)
        {
            string text = Encoding.UTF8.GetString(_inner.ToArray());
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
    }
}

/// <summary>
/// Injected fake protocol handler for offline, zero-network unit testing.
/// </summary>
public sealed class FakeProtocolHandler : IProtocolHandler
{
    private readonly Dictionary<string, object> _syncResults = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<CancellationToken, Task<object>>> _asyncMethods = new(StringComparer.Ordinal);

    private int _disposeCount;
    public bool IsDisposed => _disposeCount > 0;
    public int DisposeCount => _disposeCount;
    public int ConcurrentInvocations { get; private set; }
    public int MaxConcurrentInvocations { get; private set; }
    public int TotalInvocations { get; private set; }
    public bool CancellationObserved { get; private set; }
    public Func<Task>? CustomDisposeAsync { get; set; }

    public event Action<object>? StateChanged;
    public event Action<object>? ProgressChanged;

    public void SetMethodResult(string method, object result)
    {
        _syncResults[method] = result;
    }

    public void SetAsyncMethod(string method, Func<CancellationToken, Task<object>> asyncFunc)
    {
        _asyncMethods[method] = asyncFunc;
    }

    public void EmitState(object state) => StateChanged?.Invoke(state);
    public void EmitProgress(object progress) => ProgressChanged?.Invoke(progress);

    public async Task<object> ExecuteAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        lock (this)
        {
            TotalInvocations++;
            ConcurrentInvocations++;
            if (ConcurrentInvocations > MaxConcurrentInvocations)
                MaxConcurrentInvocations = ConcurrentInvocations;
        }

        try
        {
            if (_asyncMethods.TryGetValue(method, out var asyncFunc))
            {
                try
                {
                    return await asyncFunc(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    CancellationObserved = true;
                    throw;
                }
            }

            if (_syncResults.TryGetValue(method, out var result))
            {
                return result;
            }

            return new Dictionary<string, object> { ["status"] = "ok" };
        }
        finally
        {
            lock (this)
            {
                ConcurrentInvocations--;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Increment(ref _disposeCount) > 1)
        {
            return;
        }

        if (CustomDisposeAsync != null)
        {
            await CustomDisposeAsync().ConfigureAwait(false);
        }
    }
}
