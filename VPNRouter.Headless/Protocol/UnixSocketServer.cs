using System.Net.Sockets;

namespace VPNRouter.Headless.Protocol;

public sealed class UnixSocketServer
{
    private readonly Func<IProtocolHandler> _handlerFactory;
    private readonly string _socketPath;
    private readonly string _dataDirectory;

    public UnixSocketServer(Func<IProtocolHandler> handlerFactory, string socketPath, string dataDirectory)
    {
        _handlerFactory = handlerFactory ?? throw new ArgumentNullException(nameof(handlerFactory));
        _socketPath = socketPath;
        _dataDirectory = dataDirectory;
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var directory = Path.GetDirectoryName(_socketPath)!;
        Directory.CreateDirectory(directory, UnixSocketSecurity.PrivateDirectory);
        UnixSocketSecurity.RequirePrivateDirectory(directory);
        Directory.CreateDirectory(_dataDirectory, UnixSocketSecurity.PrivateDirectory);
        using var dataLock = UnixSocketSecurity.AcquireLock(Path.Combine(_dataDirectory, "headless-service.lock"));
        using var socketLock = UnixSocketSecurity.AcquireLock(Path.Combine(directory, "control.lock"));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Core construction can heal DNS state. It must happen only after both
        // leases prove that this process owns the session and listener.
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        var handler = _handlerFactory();
        var dispatcher = new ProtocolDispatcher(handler, concurrentSnapshots: true, quitRequested: () => stop.Cancel());
        Task? controller = null;
        bool bound = false;
        try
        {
            // Both locks are held: only this service may remove its stale listener.
            File.Delete(_socketPath);
            listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
            bound = true;
            File.SetUnixFileMode(_socketPath, UnixSocketSecurity.PrivateFile);
            listener.Listen(4);
            while (!stop.IsCancellationRequested)
            {
                var peer = await listener.AcceptAsync(stop.Token).ConfigureAwait(false);
                try
                {
                    UnixSocketSecurity.CheckPeer(peer);
                    if (controller is { IsCompleted: false })
                    {
                        using var rejected = new NetworkStream(peer, ownsSocket: true);
                        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                        var error = ProtocolResponseFrame.CreateError(null, "controller_busy",
                            "Another client controls this session. Close it before attaching.").ToUtf8Bytes();
                        await rejected.WriteAsync(error, deadline.Token).ConfigureAwait(false);
                        await rejected.WriteAsync(new byte[] { (byte)'\n' }, deadline.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        controller = ServeClientAsync(peer, handler, dispatcher, stop.Token);
                    }
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
                {
                    peer.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            stop.Cancel();
            listener.Dispose();
            try
            {
                if (controller != null) await controller.ConfigureAwait(false);
            }
            finally
            {
                dispatcher.CancelActiveOperation();
                var disposal = Task.Run(async () => await handler.DisposeAsync().ConfigureAwait(false));
                try { await disposal.WaitAsync(TimeSpan.FromSeconds(ProtocolConstants.HandlerDisposalTimeoutSeconds)).ConfigureAwait(false); }
                catch { _ = disposal.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default); }
                if (bound) File.Delete(_socketPath);
            }
        }
        return 0;
    }

    private static async Task ServeClientAsync(Socket peer, IProtocolHandler handler, ProtocolDispatcher dispatcher, CancellationToken token)
    {
        using var stream = new NetworkStream(peer, ownsSocket: true);
        await using var protocol = new ProtocolServer(handler, stream, stream, sessionDispatcher: dispatcher);
        try { await protocol.RunAsync(token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
    }
}
