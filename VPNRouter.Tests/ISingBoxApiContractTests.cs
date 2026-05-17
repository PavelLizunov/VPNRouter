#nullable enable
using System.Net;
using System.Text;
using System.Text.Json;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

/// <summary>
/// Phase 2D-4 (2026-05-17) contract tests for <see cref="ISingBoxApi"/>.
///
/// <para>Covers two layers:</para>
/// <list type="bullet">
///   <item><see cref="FakeSingBoxApi"/> happy-path + crash semantics —
///   the test fake itself must record calls and reflect state changes
///   so the HealthMonitor / AutoFailover tests downstream of this can
///   trust their assertions.</item>
///   <item><see cref="ClashSingBoxApi"/> end-to-end against an in-process
///   <see cref="HttpListener"/> mock server — verifies the real HTTP
///   wire shape (URL paths, JSON body, response parsing) matches what
///   sing-box's Clash API serves. Catches drift if someone refactors
///   the URL templates or DTOs.</item>
/// </list>
///
/// <para>Why no <c>Moq</c>: per <c>plans/v3.0-execution-methodology.md</c>
/// §5 — "Don't use Moq for fakes — write small inline impls per test
/// class." The HttpListener pattern is the cheap, dependency-free way
/// to hit the real HttpClient code path; the FakeSingBoxApi is hand-rolled.</para>
/// </summary>
public sealed class ISingBoxApiContractTests
{
    // ── FakeSingBoxApi contract ────────────────────────────────────────

    [Fact]
    public async Task ReloadConfigAsync_FakeReturnsTrue_HappyPath()
    {
        // Arrange
        var fake = new FakeSingBoxApi { TunnelHealthy = true };
        const string path = @"C:\ProgramData\VPNRouter\config\current.json";

        // Act
        var result = await fake.ReloadConfigAsync(path);

        // Assert
        Assert.True(result);
        Assert.Single(fake.Calls);
        Assert.Equal("Reload", fake.Calls[0].Method);
        Assert.Equal(path, fake.Calls[0].Detail);
    }

    [Fact]
    public async Task ReloadConfigAsync_FakeCrashed_ReturnsFalse()
    {
        // Arrange — simulate a crashed tunnel after a Start.
        var fake = new FakeSingBoxApi();
        fake.SimulateCrash();

        // Act
        var result = await fake.ReloadConfigAsync("any/path");

        // Assert: returns false but call is still recorded so the test
        // can assert HealthMonitor *attempted* the hot-reload before
        // escalating to a full restart.
        Assert.False(result);
        Assert.Single(fake.Calls);
        Assert.Equal("Reload", fake.Calls[0].Method);
    }

    [Fact]
    public async Task GetVersionAsync_FakeReturnsConfiguredString()
    {
        // The fake's default Version mirrors the currently-bundled
        // sing-box upstream — verify it round-trips unchanged and the
        // call is logged.
        var fake = new FakeSingBoxApi { Version = "1.13.10", TunnelHealthy = true };

        var version = await fake.GetVersionAsync();

        Assert.Equal("1.13.10", version);
        Assert.Contains(fake.Calls, c => c.Method == "GetVersion");
    }

    [Fact]
    public async Task SelectProxyAsync_RecordsCall_AndUpdatesSelectedByGroup()
    {
        // Arrange
        var fake = new FakeSingBoxApi();
        fake.Proxies.Add(new ProxyInfo("a", "vless", 50, DateTimeOffset.UtcNow));
        fake.Proxies.Add(new ProxyInfo("b", "vless", 80, DateTimeOffset.UtcNow));

        // Act
        var ok = await fake.SelectProxyAsync("select", "b");

        // Assert
        Assert.True(ok);
        Assert.Single(fake.Calls);
        Assert.Equal("SelectProxy", fake.Calls[0].Method);
        Assert.Equal("select=b", fake.Calls[0].Detail);
        Assert.True(fake.SelectedByGroup.TryGetValue("select", out var sel));
        Assert.Equal("b", sel);
    }

    [Fact]
    public async Task ListProxiesAsync_ReturnsConfiguredProxies()
    {
        // Arrange
        var fake = new FakeSingBoxApi();
        fake.Proxies.Add(new ProxyInfo("proxy", "vless", 42, DateTimeOffset.UtcNow));
        fake.Proxies.Add(new ProxyInfo("direct", "direct", DelayMs: null, DelayMeasuredAt: null));

        // Act
        var list = await fake.ListProxiesAsync();

        // Assert — both proxies surface, both fields preserved.
        Assert.Equal(2, list.Count);
        Assert.Equal("proxy", list[0].Name);
        Assert.Equal("vless", list[0].Type);
        Assert.Equal(42, list[0].DelayMs);
        Assert.Null(list[1].DelayMs);
    }

    // ── ClashSingBoxApi against in-process mock server ─────────────────

    [Fact]
    public async Task ClashSingBoxApi_AgainstMockServer_HappyPath()
    {
        // Pick a free loopback port; we serve canned JSON for each Clash
        // API endpoint and verify ClashSingBoxApi shapes the request +
        // parses the response correctly. HttpListener is dependency-free
        // and lives in the BCL, so this works in CI without extra deps.
        var port = GetFreeLoopbackPort();
        var baseUrl = $"http://127.0.0.1:{port}";

        using var server = new MiniMockServer(port);
        server.Start();

        try
        {
            using var api = new ClashSingBoxApi(baseUrl: baseUrl);

            // --- ReloadConfigAsync: PUT /configs?force=true ---
            var reloadOk = await api.ReloadConfigAsync(@"C:\fake\path.json");
            Assert.True(reloadOk);

            // --- GetVersionAsync: GET /version → {"version": "1.13.10"} ---
            var version = await api.GetVersionAsync();
            Assert.Equal("1.13.10", version);

            // --- GetConnectionsAsync: GET /connections ---
            var snapshot = await api.GetConnectionsAsync();
            Assert.Equal(2, snapshot.ActiveCount);
            Assert.Equal(123L, snapshot.TotalUploadBytes);
            Assert.Equal(456L, snapshot.TotalDownloadBytes);

            // --- SelectProxyAsync: PUT /proxies/select ---
            var selectOk = await api.SelectProxyAsync("select", "proxyA");
            Assert.True(selectOk);

            // --- ListProxiesAsync: GET /proxies ---
            var proxies = await api.ListProxiesAsync();
            Assert.NotEmpty(proxies);
            Assert.Contains(proxies, p => p.Name == "proxyA" && p.Type == "vless");

            // Verify the server saw the expected calls (catches accidental
            // URL refactors silently in CI).
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
        // Security guard pin: ClashSingBoxApi must refuse public-Internet
        // base URLs. Clash API is loopback-only by convention — allowing
        // a remote endpoint would let a misconfigured / hostile network
        // re-aim SelectProxyAsync.
        Assert.Throws<ArgumentException>(() =>
            new ClashSingBoxApi(baseUrl: "http://example.com:9090"));

        Assert.Throws<ArgumentException>(() =>
            new ClashSingBoxApi(baseUrl: "http://192.168.1.1:9090"));

        Assert.Throws<ArgumentException>(() =>
            new ClashSingBoxApi(baseUrl: "not-a-url"));

        // Loopback variants must be accepted.
        using var localhostApi = new ClashSingBoxApi(baseUrl: "http://localhost:9090");
        using var loopback = new ClashSingBoxApi(baseUrl: "http://127.0.0.1:9090");
        using var v6Loopback = new ClashSingBoxApi(baseUrl: "http://[::1]:9090");
    }

    // ── In-process mock server (HttpListener) ──────────────────────────

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
            try { _listener.Stop(); } catch { /* idempotent */ }
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
                catch { return; } // listener closed → exit

                try { HandleRequest(ctx); }
                catch { /* keep the loop alive */ }
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
                // 204 No Content — same as sing-box on success.
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
        // Lease an ephemeral port from the OS so two concurrent test runs
        // don't collide. Closing the listener releases the port back to
        // OS, so a brief race is possible — we accept it (tests still
        // pass under retry; the alternative is leaking the port forever).
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
