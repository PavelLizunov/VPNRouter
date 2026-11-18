using System.Security.Cryptography.X509Certificates;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class VerificationProxyTests
{
    [Fact]
    public void EachEngineSharesOneCredentialOnlyWithItsOwnProbe()
    {
        var first = new VerificationProxy(18080);
        var second = new VerificationProxy(18081);
        var inbound = first.CreateInbound();
        var users = inbound["users"]!.AsArray();
        var user = Assert.Single(users)!;
        var password = (string)user["password"]!;
        Assert.Equal(64, password.Length);
        Assert.NotEqual(password, (string?)second.CreateInbound()["users"]![0]!["password"]);
        Assert.Equal("127.0.0.1", (string?)inbound["listen"]);

        using var handler = first.CreateHttpHandler(TimeSpan.FromSeconds(5));
        Assert.False(handler.UseProxy);
        Assert.Null(handler.Proxy);
        Assert.Null(handler.DefaultProxyCredentials);
        Assert.Null(handler.Credentials);
        Assert.NotNull(handler.ConnectCallback);
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
    }

    [Fact]
    public void OnlyTheCertificateForThisEngineAuthenticatesItsProxy()
    {
        var first = new VerificationProxy(18080);
        var second = new VerificationProxy(18080);
        var tls = first.CreateInbound()["tls"]!;
        Assert.True((bool)tls["enabled"]!);
        using var firstCert = X509Certificate2.CreateFromPem((string)tls["certificate"]!);
        using var secondCert = X509Certificate2.CreateFromPem((string)second.CreateInbound()["tls"]!["certificate"]!);
        Assert.True(first.MatchesCertificate(firstCert));
        Assert.False(first.MatchesCertificate(secondCert));
        Assert.False(first.MatchesCertificate(null));
    }

    [Fact]
    public async Task ReadinessRequiresTheChildToReportItsExpectedListener()
    {
        var proxy = new VerificationProxy(18080);
        proxy.ObserveChildOutput("FATAL: bind failed");
        proxy.ObserveChildOutput("ERROR wrapped INFO[0000] inbound/http[verify-in]: tcp server started at 127.0.0.1:18080");
        proxy.ObserveChildOutput("INFO[0000] inbound/http[verify-in]: tcp server started at 127.0.0.1:18081");
        Assert.False(await proxy.WaitUntilBoundAsync(TimeSpan.Zero, TestContext.Current.CancellationToken));

        proxy.ObserveChildOutput("INFO[0000] inbound/http[verify-in]: tcp server started at 127.0.0.1:18080");
        Assert.True(await proxy.WaitUntilBoundAsync(TimeSpan.Zero, TestContext.Current.CancellationToken));
    }
}
