using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Serilog;

namespace VPNRouter.App.Services;

public static class SingleInstance
{
    private const string MutexName = "Global\\VPNRouter.App.SingleInstance.v2";
    private const string PipeName = "VPNRouter.App.ShowWindow.v2";

    private const byte SignalShowWindow = 0x01;

    private const byte SignalRouteApp = 0x02;

    private const byte SignalUnrouteApp = 0x03;

    private const int MaxRouteAppPayloadBytes = 64 * 1024;

    private const int RouteProbeConnectMs = 400;

    private static Mutex? _mutex;
    private static CancellationTokenSource? _serverCts;

    public static event Action? ShowWindowRequested;

    public static event Action<string, string?>? RouteAppRequested;

    public static event Action<string>? UnrouteAppRequested;

    public static bool TryAcquireOrSignal(ILogger? logger = null)
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: false, MutexName, out var createdNew);

            bool acquired;
            try
            {
                acquired = _mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                logger?.Information("[SingleInstance] previous owner abandoned the mutex — claiming ownership");
                acquired = true;
            }

            if (!acquired)
            {
                logger?.Information("[SingleInstance] another instance detected — signalling it to surface");
                TrySignalShow(logger);
                _mutex.Dispose();
                _mutex = null;
                return false;
            }

            _serverCts = new CancellationTokenSource();
            _ = Task.Run(() => RunPipeServerLoop(_serverCts.Token, logger));
            logger?.Debug("[SingleInstance] acquired single-instance slot (createdNew={CreatedNew})", createdNew);
            return true;
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[SingleInstance] mutex acquisition failed — falling back to single-instance off");
            return true;
        }
    }

    public static void Release()
    {
        try { _serverCts?.Cancel(); } catch { }
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }
        try { _mutex?.Dispose(); } catch { }
        _mutex = null;
    }

    private static void TrySignalShow(ILogger? logger)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            client.WriteByte(SignalShowWindow);
            client.Flush();
            logger?.Debug("[SingleInstance] sent show-window signal");
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[SingleInstance] failed to signal existing instance");
        }
    }

    public static bool TrySendRouteAppToRunningInstance(string path, string? category = null, ILogger? logger = null)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(RouteProbeConnectMs);
            var bytes = Encoding.UTF8.GetBytes(path ?? string.Empty);
            client.WriteByte(SignalRouteApp);
            client.Write(BitConverter.GetBytes(bytes.Length), 0, 4);
            client.Write(bytes, 0, bytes.Length);
            if (!string.IsNullOrWhiteSpace(category))
            {
                var catBytes = Encoding.UTF8.GetBytes(category);
                client.Write(BitConverter.GetBytes(catBytes.Length), 0, 4);
                client.Write(catBytes, 0, catBytes.Length);
            }
            client.Flush();
            logger?.Information("[SingleInstance] route-app handed to running instance: {Path} (cat={Cat})", path, category ?? "<default>");
            return true;
        }
        catch (Exception ex)
        {
            logger?.Debug(ex, "[SingleInstance] no running instance for route-app (processing locally)");
            return false;
        }
    }

    public static bool TrySendUnrouteAppToRunningInstance(string path, ILogger? logger = null)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(RouteProbeConnectMs);
            var bytes = Encoding.UTF8.GetBytes(path ?? string.Empty);
            client.WriteByte(SignalUnrouteApp);
            client.Write(BitConverter.GetBytes(bytes.Length), 0, 4);
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            logger?.Information("[SingleInstance] unroute-app handed to running instance: {Path}", path);
            return true;
        }
        catch (Exception ex)
        {
            logger?.Debug(ex, "[SingleInstance] no running instance for unroute-app (processing locally)");
            return false;
        }
    }

    private static bool ReadExact(Stream s, byte[] buf, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = s.Read(buf, read, count - read);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    private static NamedPipeServerStream CreateServerStream(ILogger? logger)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User;
                if (sid != null)
                {
                    var ps = new PipeSecurity();
                    ps.AddAccessRule(new PipeAccessRule(
                        sid, PipeAccessRights.FullControl,
                        System.Security.AccessControl.AccessControlType.Allow));
                    return NamedPipeServerStreamAcl.Create(
                        PipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte, PipeOptions.None,
                        inBufferSize: 0, outBufferSize: 0, pipeSecurity: ps);
                }
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "[SingleInstance] current-user pipe ACL setup failed — falling back to default DACL");
            }
        }
        return new NamedPipeServerStream(PipeName, PipeDirection.In, maxNumberOfServerInstances: 1);
    }

    private static void RunPipeServerLoop(CancellationToken ct, ILogger? logger)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = CreateServerStream(logger);

                var connectTask = server.WaitForConnectionAsync(ct);
                connectTask.GetAwaiter().GetResult();

                if (ct.IsCancellationRequested) break;

                var verb = server.ReadByte();
                if (verb == SignalShowWindow)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        try { ShowWindowRequested?.Invoke(); }
                        catch (Exception ex) { logger?.Warning(ex, "[SingleInstance] show-window handler threw"); }
                    });
                }
                else if (verb == SignalRouteApp)
                {
                    var lenBuf = new byte[4];
                    if (ReadExact(server, lenBuf, 4))
                    {
                        int len = BitConverter.ToInt32(lenBuf, 0);
                        if (len > 0 && len <= MaxRouteAppPayloadBytes)
                        {
                            var pathBuf = new byte[len];
                            if (ReadExact(server, pathBuf, len))
                            {
                                var path = Encoding.UTF8.GetString(pathBuf);

                                string? category = null;
                                var catLenBuf = new byte[4];
                                if (ReadExact(server, catLenBuf, 4))
                                {
                                    int catLen = BitConverter.ToInt32(catLenBuf, 0);
                                    if (catLen > 0 && catLen <= MaxRouteAppPayloadBytes)
                                    {
                                        var catBuf = new byte[catLen];
                                        if (ReadExact(server, catBuf, catLen))
                                            category = Encoding.UTF8.GetString(catBuf);
                                    }
                                }

                                Dispatcher.UIThread.Post(() =>
                                {
                                    try { RouteAppRequested?.Invoke(path, category); }
                                    catch (Exception ex) { logger?.Warning(ex, "[SingleInstance] route-app handler threw"); }
                                });
                            }
                        }
                        else
                        {
                            logger?.Warning("[SingleInstance] route-app payload length {Len} out of range — ignoring", len);
                        }
                    }
                }
                else if (verb == SignalUnrouteApp)
                {
                    var lenBuf = new byte[4];
                    if (ReadExact(server, lenBuf, 4))
                    {
                        int len = BitConverter.ToInt32(lenBuf, 0);
                        if (len > 0 && len <= MaxRouteAppPayloadBytes)
                        {
                            var pathBuf = new byte[len];
                            if (ReadExact(server, pathBuf, len))
                            {
                                var path = Encoding.UTF8.GetString(pathBuf);
                                Dispatcher.UIThread.Post(() =>
                                {
                                    try { UnrouteAppRequested?.Invoke(path); }
                                    catch (Exception ex) { logger?.Warning(ex, "[SingleInstance] unroute-app handler threw"); }
                                });
                            }
                        }
                        else
                        {
                            logger?.Warning("[SingleInstance] unroute-app payload length {Len} out of range — ignoring", len);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger?.Debug(ex, "[SingleInstance] pipe server iteration error");
                try { Thread.Sleep(200); } catch { }
            }
        }
    }
}
