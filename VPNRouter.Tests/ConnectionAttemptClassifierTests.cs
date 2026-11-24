#nullable enable

using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class ConnectionAttemptClassifierTests
{
    [Fact]
    public void CancellationAndBackgroundWork_AreExcluded()
    {
        Assert.Equal(ConnectionFailureCategory.UserCancelled,
            ConnectionAttemptClassifier.Classify(new OperationCanceledException()).Category);
        Assert.Equal(ConnectionFailureCategory.UserCancelled,
            ConnectionAttemptClassifier.Classify(new InvalidOperationException(), userCancelled: true).Category);
        Assert.Equal(ConnectionFailureCategory.Background,
            ConnectionAttemptClassifier.Classify(new InvalidOperationException(), background: true).Category);
    }

    [Fact]
    public void LocalEnvironmentMessages_AreExcluded()
    {
        Assert.Equal(ConnectionFailureCategory.LocalEnvironment,
            ConnectionAttemptClassifier.Classify(new UnauthorizedAccessException("access is denied")).Category);
        Assert.Equal(ConnectionFailureCategory.LocalEnvironment,
            ConnectionAttemptClassifier.Classify(new InvalidOperationException("TUN adapter busy")).Category);
    }

    [Fact]
    public void ProviderAndAuthenticationResponses_AreExcluded()
    {
        Assert.Equal(ConnectionFailureCategory.AuthenticationFailed,
            ConnectionAttemptClassifier.Classify(
                new HttpRequestException("request failed", null, HttpStatusCode.Forbidden)).Category);
        Assert.Equal(ConnectionFailureCategory.ProviderRejected,
            ConnectionAttemptClassifier.Classify(
                new InvalidOperationException("subscription expired")).Category);
    }

    [Fact]
    public void NetworkErrors_AreExcluded()
    {
        Assert.Equal(ConnectionFailureCategory.NetworkUnavailable,
            ConnectionAttemptClassifier.Classify(new SocketException((int)SocketError.HostNotFound)).Category);
        Assert.Equal(ConnectionFailureCategory.NetworkUnavailable,
            ConnectionAttemptClassifier.Classify(new HttpRequestException("name resolution failed")).Category);
    }

    [Fact]
    public void OtherwiseTechnicalFailure_IsCounted()
    {
        var assessment = ConnectionAttemptClassifier.Classify(new InvalidOperationException("sing-box exited"));

        Assert.Equal(ConnectionFailureCategory.Technical, assessment.Category);
        Assert.True(assessment.CountsTowardRollback);
        Assert.Equal(nameof(InvalidOperationException), assessment.DetailCode);
    }

    [Fact]
    public void LostStartupIdentity_IsDistinguishedFromAnOrdinaryStop()
    {
        var failure = ConnectionAttemptClassifier.Classify(new TunnelExitedDuringStartupException());
        Assert.True(failure.CountsTowardRollback);
        Assert.Equal("engine-stopped", failure.DetailCode);
        var refused = ConnectionAttemptClassifier.Classify(new SocketException((int)SocketError.ConnectionRefused));
        Assert.False(refused.CountsTowardRollback);
    }

    [Fact]
    public void TechnicalFailure_DoesNotLeakTheRawMessage()
    {
        var assessment = ConnectionAttemptClassifier.Classify(
            new InvalidOperationException("secret-token-123 should not be stored"));

        Assert.DoesNotContain("secret-token", assessment.DetailCode, StringComparison.Ordinal);
    }
}
