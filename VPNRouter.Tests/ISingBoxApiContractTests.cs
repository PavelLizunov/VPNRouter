#nullable enable
using System.Net;
using System.Text;
using System.Text.Json;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class ISingBoxApiContractTests
{
    [Fact]
    public async Task ClashSingBoxApi_AgainstMockServer_HappyPath()
    {
        var port = GetFreeLoopbackPort();
        var baseUrl = $"http://127.0.0.1:{port}";

        using var server = new MiniMockServer(port);
        server.Start();

        try
        {
            using var api = new ClashSingBoxApi(baseUrl: baseUrl);
            var ct = TestContext.Current.CancellationToken;

            var reloadOk = await api.ReloadConfigAsync(@"C:\fake\path.json", ct);
            Assert.True(reloadOk);

            var version = await api.GetVersionAsync(ct);
            Assert.Equal("1.13.10", version);

            var snapshot = await api.GetConnectionsAsync(ct);
            Assert.Equal(2, snapshot.ActiveCount);
            Assert.Equal(123L, snapshot.TotalUploadBytes);
            Assert.Equal(456L, snapshot.TotalDownloadBytes);

            var selectOk = await api.SelectProxyAsync("select", "proxyA", ct);
            Assert.True(selectOk);

            var proxies = await api.ListProxiesAsync(ct);
            Assert.NotEmpty(proxies);
            Assert.Contains(proxies, p => p.Name == "proxyA" && p.Type == "vless");

            Assert.Contains("PUT /configs?force=true", server.Calls);
            Assert.Contains("GET /version", server.Calls);
            Assert.Contains("GET /connections", server.Calls);
            Assert.Contains("PUT /proxies/select", server.Calls);
            Assert.Contains("GET /proxies", server.Calls);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public void ClashSingBoxApi_RejectsNonLoopbackBaseUrl()
    {
        Assert.Throws<ArgumentException>(() =>
            new ClashSingBoxApi(baseUrl: "http://example.com:9090"));

        Assert.Throws<ArgumentException>(() =>
            new ClashSingBoxApi(baseUrl: "http://192.168.1.1:9090"));

        Assert.Throws<ArgumentException>(() =>
            new ClashSingBoxApi(baseUrl: "not-a-url"));

        using var localhostApi = new ClashSingBoxApi(baseUrl: "http://localhost:9090");
        using var loopback = new ClashSingBoxApi(baseUrl: "http://127.0.0.1:9090");
        using var v6Loopback = new ClashSingBoxApi(baseUrl: "http://[::1]:9090");
    }

    private sealed class MiniMockServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly Thread _thread;
        private volatile bool _running;

        public List<string> Calls { get; } = new();

        public MiniMockServer(int port)
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _thread = new Thread(Loop) { IsBackground = true };
        }

        public void Start()
        {
            _listener.Start();
            _running = true;
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            try { _thread.Join(1000); } catch { }
        }

        public void Dispose() => Stop();

        private void Loop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { return; }

                try { HandleRequest(ctx); }
                catch { }
            }
        }

        private void HandleRequest(HttpListenerContext ctx)
        {
            var path = ctx.Request.Url!.PathAndQuery;
            var method = ctx.Request.HttpMethod;
            lock (Calls)
            {
                Calls.Add($"{method} {path}");
            }

            string body;
            int status = 200;

            if (method == "PUT" && path.StartsWith("/configs"))
            {
                status = 204;
                body = string.Empty;
            }
            else if (method == "GET" && path == "/version")
            {
                body = JsonSerializer.Serialize(new { version = "1.13.10", premium = true });
            }
            else if (method == "GET" && path == "/connections")
            {
                body = JsonSerializer.Serialize(new
                {
                    downloadTotal = 456L,
                    uploadTotal = 123L,
                    connections = new[]
                    {
                        new { id = "x" },
                        new { id = "y" },
                    },
                });
            }
            else if (method == "PUT" && path.StartsWith("/proxies/"))
            {
                status = 204;
                body = string.Empty;
            }
            else if (method == "GET" && path == "/proxies")
            {
                body = JsonSerializer.Serialize(new
                {
                    proxies = new Dictionary<string, object>
                    {
                        ["proxyA"] = new
                        {
                            type = "vless",
                            history = new[]
                            {
                                new { time = "2026-05-17T12:34:56Z", delay = 42 },
                            },
                        },
                        ["direct"] = new
                        {
                            type = "direct",
                            history = Array.Empty<object>(),
                        },
                    },
                });
            }
            else
            {
                status = 404;
                body = "{\"error\":\"unknown route\"}";
            }

            ctx.Response.StatusCode = status;
            if (!string.IsNullOrEmpty(body))
            {
                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            ctx.Response.OutputStream.Close();
        }
    }

    private static int GetFreeLoopbackPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
