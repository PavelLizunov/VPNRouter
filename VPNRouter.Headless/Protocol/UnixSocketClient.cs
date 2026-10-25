using System.Net.Sockets;

namespace VPNRouter.Headless.Protocol;

public static class UnixSocketClient
{
    public static async Task<int> RunAsync(string socketPath, Stream input, Stream output, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        UnixSocketSecurity.RequirePrivateDirectory(Path.GetDirectoryName(socketPath)!);
        if ((File.GetAttributes(socketPath) & FileAttributes.ReparsePoint) != 0 ||
            File.GetUnixFileMode(socketPath) != UnixSocketSecurity.PrivateFile)
            throw new IOException("Control socket must be private.");
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), token).ConfigureAwait(false);
        UnixSocketSecurity.CheckPeer(socket);
        using var stream = new NetworkStream(socket, ownsSocket: false);
        using var relay = CancellationTokenSource.CreateLinkedTokenSource(token);
        var upload = input.CopyToAsync(stream, relay.Token);
        var download = stream.CopyToAsync(output, relay.Token);
        await Task.WhenAny(upload, download).ConfigureAwait(false);
        relay.Cancel();
        socket.Dispose();
        // Console stdin cancellation is not reliable on every runtime; it must not
        // keep the relay alive after the socket closes. Observe eventual failures.
        foreach (var task in new[] { upload, download })
            _ = task.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
        return 0;
    }
}
