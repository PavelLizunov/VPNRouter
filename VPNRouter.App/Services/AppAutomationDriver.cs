#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Serilog;
using VPNRouter.App.ViewModels;

namespace VPNRouter.App.Services;

/// <summary>
/// Embedded localhost-only HTTP automation & telemetry bridge.
/// Enabled only when launched with `--automation-port <port>` or `VPNROUTER_AUTOMATION_PORT`.
/// Allows gathering real-time telemetry (RAM, CPU, GC, Dispatcher latency) and programmatic
/// UI inspection / actions on Windows, macOS, and Linux without native OS UI automation dependencies.
/// </summary>
public static class AppAutomationDriver
{
    private static HttpListener? _listener;
    private static CancellationTokenSource? _cts;
    private static Window? _window;
    private static MainWindowViewModel? _viewModel;
    private static string? _token;
    private static int _port;

    public static int? AutomationPort { get; set; }
    public static string? AutomationToken { get; set; }
    public static bool IsRunning => _listener?.IsListening == true;

    public static void ParseArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--automation-port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (int.TryParse(args[i + 1], out var p) && p > 0 && p <= 65535)
                    AutomationPort = p;
            }
            else if (string.Equals(args[i], "--automation-token", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                AutomationToken = args[i + 1];
            }
        }

        if (AutomationPort is null)
        {
            var envPort = Environment.GetEnvironmentVariable("VPNROUTER_AUTOMATION_PORT");
            if (!string.IsNullOrWhiteSpace(envPort) && int.TryParse(envPort, out var p) && p > 0 && p <= 65535)
                AutomationPort = p;
        }

        if (string.IsNullOrWhiteSpace(AutomationToken))
        {
            var envToken = Environment.GetEnvironmentVariable("VPNROUTER_AUTOMATION_TOKEN");
            if (!string.IsNullOrWhiteSpace(envToken))
                AutomationToken = envToken;
        }
    }

    public static void StartIfConfigured(Window window, MainWindowViewModel viewModel)
    {
        if (AutomationPort is not { } port || port <= 0)
            return;

        Start(window, viewModel, port, AutomationToken);
    }

    public static void Start(Window window, MainWindowViewModel viewModel, int port, string? token = null)
    {
        Stop();

        _window = window;
        _viewModel = viewModel;
        _port = port;
        _token = token;
        _cts = new CancellationTokenSource();

        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();

            Log.Information("[Automation] Automation driver listening on http://127.0.0.1:{Port}/", port);
            _ = Task.Run(() => AcceptLoopAsync(_listener, _cts.Token));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Automation] Failed to start automation listener on port {Port}", port);
            Stop();
        }
    }

    public static void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }

    private static async Task AcceptLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            try
            {
                var context = await listener.GetContextAsync().ConfigureAwait(false);
                _ = Task.Run(() => HandleRequestAsync(context));
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    Log.Debug(ex, "[Automation] Error in accept loop");
            }
        }
    }

    private static async Task HandleRequestAsync(HttpListenerContext context)
    {
        var req = context.Request;
        var resp = context.Response;

        try
        {
            // Authenticate if token configured
            if (!string.IsNullOrEmpty(_token))
            {
                var authHeader = req.Headers["Authorization"] ?? "";
                var queryToken = req.QueryString["token"] ?? "";
                var bearer = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? authHeader[7..].Trim()
                    : "";

                if (!string.Equals(bearer, _token, StringComparison.Ordinal) &&
                    !string.Equals(queryToken, _token, StringComparison.Ordinal))
                {
                    await WriteJsonAsync(resp, new { ok = false, error = "Unauthorized" }, 401);
                    return;
                }
            }

            var path = req.Url?.AbsolutePath.TrimEnd('/') ?? "";
            var method = req.HttpMethod.ToUpperInvariant();

            if (method == "GET" && path == "/metrics")
            {
                await HandleMetricsAsync(resp);
            }
            else if (method == "GET" && path == "/ui/tree")
            {
                await HandleUiTreeAsync(resp);
            }
            else if (method == "POST" && path == "/ui/action")
            {
                await HandleUiActionAsync(req, resp);
            }
            else if (method == "GET" && path == "/ui/screenshot")
            {
                await HandleScreenshotAsync(resp);
            }
            else
            {
                await WriteJsonAsync(resp, new { ok = false, error = "Not found", path }, 404);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Automation] Handler error");
            try
            {
                await WriteJsonAsync(resp, new { ok = false, error = ex.Message }, 500);
            }
            catch { }
        }
    }

    private static async Task HandleMetricsAsync(HttpListenerResponse response)
    {
        using var proc = Process.GetCurrentProcess();
        proc.Refresh();
        var workingSetMb = proc.WorkingSet64 / (1024.0 * 1024.0);
        var privateMemoryMb = proc.PrivateMemorySize64 / (1024.0 * 1024.0);
        var cpuTime = proc.TotalProcessorTime.TotalMilliseconds;

        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);
        var gcTotalMemoryMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);

        double dispatcherDelayMs = -1;
        bool isConnected = false;
        bool isConnecting = false;
        bool isSimpleMode = false;
        int activeTab = 0;
        string statusText = "";
        string connectButtonText = "";
        int serversCount = 0;
        string windowState = "";
        bool isVisible = false;

        if (_viewModel != null && _window != null)
        {
            var sw = Stopwatch.StartNew();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                sw.Stop();
                dispatcherDelayMs = sw.Elapsed.TotalMilliseconds;

                isConnected = _viewModel.IsConnected;
                isConnecting = _viewModel.IsConnecting;
                isSimpleMode = _viewModel.IsSimpleMode;
                activeTab = _viewModel.SelectedTabIndex;
                statusText = _viewModel.StatusText;
                connectButtonText = _viewModel.ConnectButtonText;
                serversCount = _viewModel.Servers.Count;

                windowState = _window.WindowState.ToString();
                isVisible = _window.IsVisible;
            });
        }

        var result = new
        {
            ok = true,
            timestamp_utc = DateTime.UtcNow,
            system = new
            {
                working_set_mb = Math.Round(workingSetMb, 2),
                private_memory_mb = Math.Round(privateMemoryMb, 2),
                total_cpu_ms = cpuTime,
                gc_allocated_mb = Math.Round(gcTotalMemoryMb, 2),
                gc_gen0 = gen0,
                gc_gen1 = gen1,
                gc_gen2 = gen2,
            },
            ui = new
            {
                dispatcher_latency_ms = Math.Round(dispatcherDelayMs, 2),
                is_connected = isConnected,
                is_connecting = isConnecting,
                is_simple_mode = isSimpleMode,
                selected_tab_index = activeTab,
                status_text = statusText,
                connect_button_text = connectButtonText,
                servers_count = serversCount,
                window_state = windowState,
                is_visible = isVisible
            }
        };

        await WriteJsonAsync(response, result, 200);
    }

    private static async Task HandleUiTreeAsync(HttpListenerResponse response)
    {
        if (_window == null)
        {
            await WriteJsonAsync(response, new { ok = false, error = "MainWindow is null" }, 500);
            return;
        }

        object? tree = null;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            tree = DumpVisualNode(_window);
        });

        await WriteJsonAsync(response, new { ok = true, root = tree }, 200);
    }

    private static object DumpVisualNode(Visual visual)
    {
        var name = (visual as Control)?.Name ?? "";
        var type = visual.GetType().Name;
        var bounds = visual.Bounds;
        var isVisible = visual.IsVisible;
        string? text = (visual as ContentControl)?.Content as string
                       ?? (visual as TextBlock)?.Text
                       ?? (visual as TextBox)?.Text;

        var children = new List<object>();
        foreach (var child in visual.GetVisualChildren())
        {
            children.Add(DumpVisualNode(child));
        }

        return new
        {
            name = string.IsNullOrEmpty(name) ? null : name,
            type,
            text = string.IsNullOrEmpty(text) ? null : text,
            isVisible,
            bounds = new { bounds.X, bounds.Y, bounds.Width, bounds.Height },
            childrenCount = children.Count,
            children = children.Count > 0 ? children : null
        };
    }

    private static async Task HandleUiActionAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var action = root.TryGetProperty("action", out var pAction) ? pAction.GetString() : null;
        var target = root.TryGetProperty("target", out var pTarget) ? pTarget.GetString() : null;
        var val = root.TryGetProperty("value", out var pVal) ? pVal.GetString() : null;

        if (string.IsNullOrWhiteSpace(action))
        {
            await WriteJsonAsync(response, new { ok = false, error = "Missing action" }, 400);
            return;
        }

        bool executed = false;
        string detail = "";

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_viewModel == null || _window == null)
            {
                detail = "ViewModel or Window is null";
                return;
            }

            switch (action.ToLowerInvariant())
            {
                case "switch_tab":
                    if (int.TryParse(val ?? target, out var tabIndex))
                    {
                        _viewModel.SelectedTabIndex = tabIndex;
                        executed = true;
                        detail = $"Switched tab to {tabIndex}";
                    }
                    else
                    {
                        detail = "Invalid tab index";
                    }
                    break;

                case "toggle_mode":
                    _viewModel.ToggleUiModeCommand.Execute(null);
                    executed = true;
                    detail = $"Toggled SimpleMode to {_viewModel.IsSimpleMode}";
                    break;

                case "connect":
                case "toggle_connection":
                    _viewModel.ToggleConnectionCommand.Execute(null);
                    executed = true;
                    detail = "Invoked ToggleConnectionCommand";
                    break;

                case "click":
                    var btn = FindControl<Button>(_window, target);
                    if (btn != null)
                    {
                        if (btn.Command != null && btn.Command.CanExecute(btn.CommandParameter))
                        {
                            btn.Command.Execute(btn.CommandParameter);
                            executed = true;
                            detail = $"Executed command on button {target}";
                        }
                        else
                        {
                            btn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            executed = true;
                            detail = $"Raised click on button {target}";
                        }
                    }
                    else
                    {
                        detail = $"Button '{target}' not found";
                    }
                    break;

                case "set_text":
                    var tb = FindControl<TextBox>(_window, target);
                    if (tb != null)
                    {
                        tb.Text = val ?? "";
                        executed = true;
                        detail = $"Set text on TextBox {target}";
                    }
                    else
                    {
                        detail = $"TextBox '{target}' not found";
                    }
                    break;

                default:
                    detail = $"Unknown action '{action}'";
                    break;
            }
        });

        await WriteJsonAsync(response, new { ok = executed, message = detail }, executed ? 200 : 400);
    }

    private static T? FindControl<T>(Visual parent, string? target) where T : Control
    {
        if (string.IsNullOrWhiteSpace(target)) return null;

        foreach (var child in parent.GetVisualChildren())
        {
            if (child is T c)
            {
                if (string.Equals(c.Name, target, StringComparison.OrdinalIgnoreCase))
                    return c;
                if (c is ContentControl cc && cc.Content is string s && string.Equals(s, target, StringComparison.OrdinalIgnoreCase))
                    return c;
            }
            var found = FindControl<T>(child, target);
            if (found != null) return found;
        }
        return null;
    }

    private static async Task HandleScreenshotAsync(HttpListenerResponse response)
    {
        if (_window == null)
        {
            response.StatusCode = 500;
            response.Close();
            return;
        }

        byte[]? bytes = null;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            try
            {
                var bounds = _window.Bounds;
                var width = (int)Math.Max(1, bounds.Width);
                var height = (int)Math.Max(1, bounds.Height);
                var pixelSize = new PixelSize(width, height);
                var dpi = new Vector(96, 96);
                using var rtb = new RenderTargetBitmap(pixelSize, dpi);
                rtb.Render(_window);
                using var ms = new MemoryStream();
                rtb.Save(ms);
                bytes = ms.ToArray();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Automation] Screenshot render failed");
            }
        });

        if (bytes == null || bytes.Length == 0)
        {
            response.StatusCode = 500;
            response.Close();
            return;
        }

        response.ContentType = "image/png";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        response.OutputStream.Close();
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, object payload, int statusCode)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        response.OutputStream.Close();
    }
}
