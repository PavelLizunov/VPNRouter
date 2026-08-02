using System;
using System.IO;
using System.Security.Cryptography;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// P1-2 (dep-review 2026-07-09): TgProxy pulls down + runs a Python interpreter
/// and PyPI wheels (cffi/cryptography = compiled C/Rust extensions) under the
/// user's account. Pre-fix they were installed on trust with zero integrity
/// check. `VerifyPinnedSha256Static` is the fail-closed primitive: it throws
/// unless the downloaded file matches the trusted digest (PyPI's published
/// `digests.sha256` for wheels; a pinned constant for the python.org zip).
/// </summary>
public sealed class TgProxyDigestVerifyTests : IDisposable
{
    private readonly string _f = Path.Combine(Path.GetTempPath(), "tgproxy-digest-" + Guid.NewGuid().ToString("N") + ".bin");

    public TgProxyDigestVerifyTests() => File.WriteAllText(_f, "the tg-ws-proxy python payload");

    public void Dispose() { try { if (File.Exists(_f)) File.Delete(_f); } catch { } }

    private string ActualSha() =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(_f)));

    [Fact]
    public void MatchingDigest_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
            TgProxyUpdater.VerifyPinnedSha256Static(_f, ActualSha(), "test"));
        Assert.Null(ex);
    }

    [Fact]
    public void MatchingDigest_IsCaseInsensitive()
    {
        var ex = Record.Exception(() =>
            TgProxyUpdater.VerifyPinnedSha256Static(_f, ActualSha().ToUpperInvariant(), "test"));
        Assert.Null(ex);
    }

    [Fact]
    public void MismatchedDigest_ThrowsFailClosed()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TgProxyUpdater.VerifyPinnedSha256Static(
                _f, "0000000000000000000000000000000000000000000000000000000000000000", "test"));
        Assert.Contains("sha256 mismatch", ex.Message);
    }

    [Fact]
    public void EmptyExpected_ThrowsFailClosed()
        => Assert.Throws<InvalidOperationException>(() =>
            TgProxyUpdater.VerifyPinnedSha256Static(_f, "", "test"));
}
