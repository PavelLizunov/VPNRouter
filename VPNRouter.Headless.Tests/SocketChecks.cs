using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VPNRouter.Headless.Protocol;

namespace VPNRouter.Headless.Tests;

public static class SocketChecks
{
    public static async Task RunAsync()
    {
        if (!OperatingSystem.IsLinux()) return;
        var directory = Path.Combine(Path.GetTempPath(), "vpn-socket-" + Guid.NewGuid().ToString("N"));
        var socketPath = Path.Combine(directory, "runtime", "control.sock");
        var data = Path.Combine(directory, "data");
        var handler = new SessionHandler();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var service = new UnixSocketServer(handler, socketPath, data).RunAsync(stop.Token);
        try
        {
            Check(File.GetUnixFileMode(socketPath) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "Socket mode must be 0600");
            Check(File.GetUnixFileMode(Path.GetDirectoryName(socketPath)!) ==
                (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute), "Runtime directory mode must be 0700");
            Console.WriteLine("  SocketPermissions passed");
            await using (var first = await Peer.OpenAsync(socketPath))
            {
                Check((await first.ReadAsync()).GetProperty("data").GetProperty("selected").GetInt32() == 0, "Initial snapshot");
                await using (var second = await Peer.OpenAsync(socketPath))
                {
                    Check((await second.ReadAsync()).GetProperty("error").GetProperty("code").GetString() == "controller_busy", "Second controller must be refused");
                }
                await first.SendAsync("select", "servers.select");
                Check((await first.ResponseAsync("select")).GetProperty("result").GetProperty("selected").GetInt32() == 1, "State mutation reached backend");
                await first.SendAsync("probe", "servers.test");
                await handler.ProbeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
            Console.WriteLine("  SingleController passed");
            await using (var reattached = await ReattachAsync(socketPath))
            {
                Check(handler.Disposed == 0 && !handler.ProbeCancelled, "EOF cannot tear down session or operation");
                await reattached.SendAsync("snap", "snapshot");
                var snap = (await reattached.ResponseAsync("snap")).GetProperty("result");
                Check(snap.GetProperty("selected").GetInt32() == 1 && snap.GetProperty("probing").GetBoolean(), "Reattach snapshot must be fresh during pending operation");
                await reattached.SendAsync("competing", "servers.select");
                Check((await reattached.ResponseAsync("competing")).GetProperty("error").GetProperty("code").GetString() == "busy", "Detached operation retains global admission");
                handler.ProbeRelease.TrySetResult();
                await handler.ProbeFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
                await reattached.SendAsync("snap2", "snapshot");
                Check((await reattached.ResponseAsync("snap2")).GetProperty("result").GetProperty("tested").GetBoolean(), "Probe survives EOF and retains result");
                await reattached.SendAsync("stop", "disconnect");
                Check(!(await reattached.ResponseAsync("stop")).GetProperty("result").GetProperty("connected").GetBoolean(), "Explicit disconnect stops session");
                Console.WriteLine("  ReattachSnapshotAndEofOperationLifetime passed");
                Console.WriteLine("  ExplicitDisconnect passed");
                await reattached.SendAsync("quit", "quit");
                await service.WaitAsync(TimeSpan.FromSeconds(3));
                Check(handler.Disconnects == 2, "Quit must also disconnect before service exit");
                Console.WriteLine("  ExplicitQuit passed");
            }
            Check(!File.Exists(socketPath), "Stopped service must remove owned socket");
            // Stale listener recovery: bind without unlinking on dispose.
            using (var stale = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
                stale.Bind(new UnixDomainSocketEndPoint(socketPath));
            using var restart = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var restarted = new UnixSocketServer(handler, socketPath, data).RunAsync(restart.Token);
            await using (var peer = await Peer.OpenAsync(socketPath))
                Check((await peer.ReadAsync()).GetProperty("event").GetString() == "state", "Stale socket recovery snapshot");
            await ExpectFailureAsync(new UnixSocketServer(handler, Path.Combine(directory, "other", "control.sock"), data).RunAsync(stop.Token), "Data directory must have one daemon");
            await ExpectFailureAsync(new UnixSocketServer(handler, socketPath, Path.Combine(directory, "other-data")).RunAsync(stop.Token), "Socket lock must protect existing listener");
            restart.Cancel();
            await restarted;
            Console.WriteLine("  StaleSocketAndDaemonLocks passed");
            File.SetUnixFileMode(Path.GetDirectoryName(socketPath)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead);
            await ExpectFailureAsync(new UnixSocketServer(handler, socketPath, data).RunAsync(stop.Token), "Insecure runtime directory must be refused");
            Console.WriteLine("  InsecureDirectoryRefusal passed");
        }
        finally
        {
            stop.Cancel();
            await service;
            await handler.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<Peer> ReattachAsync(string path)
    {
        for (int attempt = 0; attempt < 30; attempt++)
        {
            var peer = await Peer.OpenAsync(path);
            var frame = await peer.ReadAsync();
            if (frame.TryGetProperty("event", out _))
            {
                Check(frame.GetProperty("data").GetProperty("selected").GetInt32() == 1, "Fresh unsolicited reattach snapshot");
                return peer;
            }
            await peer.DisposeAsync();
            await Task.Delay(25);
        }
        throw new Exception("Controller did not detach within deadline");
    }

    private static async Task ExpectFailureAsync(Task task, string message)
    {
        try { await task; } catch (IOException) { return; }
        throw new Exception(message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class Peer : IAsyncDisposable
    {
        private readonly NetworkStream _stream;
        private readonly StreamReader _reader;
        private Peer(Socket socket)
        {
            _stream = new NetworkStream(socket, ownsSocket: true);
            _reader = new StreamReader(_stream, Encoding.UTF8, leaveOpen: true);
        }
        public static async Task<Peer> OpenAsync(string path)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path));
            return new Peer(socket);
        }
        public Task SendAsync(string id, string method) => _stream.WriteAsync(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { v = 1, id, method, @params = new { } }) + "\n")).AsTask();
        public async Task<JsonElement> ReadAsync()
        {
            var line = await _reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3));
            using var document = JsonDocument.Parse(line ?? throw new Exception("Unexpected socket EOF"));
            return document.RootElement.Clone();
        }
        public async Task<JsonElement> ResponseAsync(string id)
        {
            for (int i = 0; i < 20; i++)
            {
                var frame = await ReadAsync();
                if (frame.TryGetProperty("id", out var actual) && actual.GetString() == id) return frame;
            }
            throw new Exception("Missing response");
        }
        public ValueTask DisposeAsync() { _reader.Dispose(); return _stream.DisposeAsync(); }
    }

    private sealed class SessionHandler : IProtocolHandler
    {
        public int Disposed, Disconnects, Selected;
        public bool Connected = true, Probing, Tested, ProbeCancelled;
        public readonly TaskCompletionSource ProbeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ProbeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ProbeFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event Action<object>? StateChanged { add { } remove { } }
        public event Action<object>? ProgressChanged { add { } remove { } }
        public async Task<object> ExecuteAsync(string method, JsonElement parameters, CancellationToken token)
        {
            if (method == "servers.select") Selected++;
            if (method == "servers.test")
            {
                Probing = true;
                ProbeStarted.TrySetResult();
                try { await ProbeRelease.Task.WaitAsync(token); Tested = true; }
                catch (OperationCanceledException) { ProbeCancelled = true; throw; }
                finally { Probing = false; ProbeFinished.TrySetResult(); }
            }
            if (method == "disconnect") { Connected = false; Disconnects++; }
            return new { selected = Selected, connected = Connected, probing = Probing, tested = Tested };
        }
        public ValueTask DisposeAsync() { Disposed++; Connected = false; return ValueTask.CompletedTask; }
    }
}
