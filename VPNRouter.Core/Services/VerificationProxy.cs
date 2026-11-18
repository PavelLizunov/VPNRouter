using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VPNRouter.Core.Services;

internal sealed class VerificationProxy
{
    private const string UserName = "verifier";
    private static readonly Regex BoundLine = new(
        @"^(?:\x1b\[36m)?INFO(?:\x1b\[0m)?(?:\[[0-9]+\])? inbound/http\[verify-in\]: tcp server started at 127\.0\.0\.1:(?<port>[0-9]+)\z",
        RegexOptions.CultureInvariant);
    private readonly int _port;
    private volatile bool _bindFailed;
    private readonly string _password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string _certificatePem;
    private readonly string _keyPem;
    private readonly byte[] _certificateDer;
    private readonly TaskCompletionSource _bound = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string StartupError => _bindFailed ? "sing-box: bind failed" : "sing-box didn't bind";

    public VerificationProxy(int port)
    {
        _port = port;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        var now = DateTimeOffset.UtcNow;
        using var certificate = request.CreateSelfSigned(now.AddMinutes(-5), now.AddHours(1));
        _certificatePem = certificate.ExportCertificatePem();
        _keyPem = key.ExportPkcs8PrivateKeyPem();
        _certificateDer = certificate.RawData;
    }

    public JsonObject CreateInbound() => new()
    {
        ["type"] = "http",
        ["tag"] = "verify-in",
        ["listen"] = "127.0.0.1",
        ["listen_port"] = _port,
        ["users"] = new JsonArray(new JsonObject { ["username"] = UserName, ["password"] = _password }),
        ["tls"] = new JsonObject { ["enabled"] = true, ["certificate"] = _certificatePem, ["key"] = _keyPem },
    };

    public SocketsHttpHandler CreateHttpHandler(TimeSpan connectTimeout) => new()
    {
        UseProxy = false,
        ConnectTimeout = connectTimeout,
        ConnectCallback = OpenTunnelAsync,
    };

    // Both HTTP and HTTPS origins travel through CONNECT. Only this private request is
    // authenticated; the outer handler owns origin requests, redirects and destination TLS.
    private async ValueTask<Stream> OpenTunnelAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        using var transport = new HttpMessageInvoker(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectCallback = ConnectToProxyAsync,
        });
        using var connect = new HttpRequestMessage(HttpMethod.Connect, $"http://127.0.0.1:{_port}");
        var target = new UriBuilder(Uri.UriSchemeHttp, context.DnsEndPoint.Host, context.DnsEndPoint.Port);
        connect.Headers.Host = target.Uri.GetComponents(UriComponents.HostAndPort, UriFormat.UriEscaped);

        var response = await transport.SendAsync(connect, ct).ConfigureAwait(false);
        try
        {
            if (response.StatusCode != HttpStatusCode.OK)
                throw new HttpRequestException("Verification proxy refused CONNECT", null, response.StatusCode);
            // A successful CONNECT detaches the duplex stream from the inner connection pool.
            return await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private async ValueTask<Stream> ConnectToProxyAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        if (context.DnsEndPoint.Host != "127.0.0.1" || context.DnsEndPoint.Port != _port ||
            context.InitialRequestMessage.Method != HttpMethod.Connect)
            throw new IOException("Unexpected verification proxy request");

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        SslStream? tls = null;
        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, _port, ct).ConfigureAwait(false);
            tls = new SslStream(new NetworkStream(socket, ownsSocket: true), leaveInnerStreamOpen: false);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                RemoteCertificateValidationCallback = (_, cert, _, _) => MatchesCertificate(cert),
            }, ct).ConfigureAwait(false);
            // This transport handles one CONNECT, with no challenge or redirect to another peer.
            var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{UserName}:{_password}"));
            context.InitialRequestMessage.Headers.ProxyAuthorization = new AuthenticationHeaderValue("Basic", credentials);
            return tls;
        }
        catch
        {
            tls?.Dispose();
            socket.Dispose();
            throw;
        }
    }

    internal bool MatchesCertificate(X509Certificate? certificate) =>
        certificate != null && CryptographicOperations.FixedTimeEquals(_certificateDer, certificate.GetRawCertData());

    public void ObserveChildOutput(string? line)
    {
        if (line == null) return;
        if (line.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
            _bindFailed = true;
        var match = BoundLine.Match(line);
        if (match.Success && int.TryParse(match.Groups["port"].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var port) && port == _port)
            _bound.TrySetResult();
    }

    public async Task<bool> WaitUntilBoundAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await _bound.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException) { return false; }
    }
}
