#nullable enable
using System;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// QrCode (v2.32.0 — vendored Nayuki QR-encoder, byte mode only)
// ═══════════════════════════════════════════════════════════════════════════════
//
// LOW-priority smoke coverage per Phase 2G Wave 7c-2 brief
// (plans/phase2-2G-untested-services-2026-05-17.md). The encoder is a pure-C#
// adaptation of the well-known Nayuki reference — we are not retesting the
// QR-spec maths, only pinning the public surface (EncodeText, properties,
// ToMatrix / GetModule, null + payload-size guards) so we notice if a future
// refactor flips the contract.
//
// Why no decode round-trip: the vendored module is encode-only (the
// counterpart `QrCodeDecoder.cs` lives Android-side and uses ZXing — outside
// Core's surface). We assert structural invariants instead.

public sealed class QrCodeTests
{
    // Sample VLESS Reality URI — typical share payload from the Subscribe
    // tab. Length ≈ 230 chars, well within byte-mode capacity for v10ish.
    private const string SampleVlessUri =
        "vless://2d54442d-158f-49e2-b225-67ba1a5b77f4@194.87.222.111:443" +
        "?security=reality&sni=yahoo.com&fp=firefox" +
        "&pbk=vJgL2dRZSp_DOaXEm9wYwK0pH-c5fJqr1L3y7zT8xK4&sid=deadbeef" +
        "&spx=/&type=tcp&flow=xtls-rprx-vision&encryption=none#bratik";

    [Fact]
    public void EncodeText_VlessUri_ProducesNonEmptyMatrix()
    {
        var qr = QrCode.EncodeText(SampleVlessUri, QrCode.Ecc.Medium);

        Assert.NotNull(qr);
        Assert.InRange(qr.Version, 1, 40);
        // Size = version * 4 + 17 — sanity-check the structural invariant.
        Assert.Equal(qr.Version * 4 + 17, qr.Size);
        Assert.InRange(qr.Mask, 0, 7);

        var matrix = qr.ToMatrix();
        Assert.Equal(qr.Size, matrix.GetLength(0));
        Assert.Equal(qr.Size, matrix.GetLength(1));

        // A real QR contains both dark and light modules (finder patterns
        // alone guarantee this). An all-false matrix would mean rendering
        // never ran.
        bool sawDark = false;
        bool sawLight = false;
        for (int y = 0; y < qr.Size && !(sawDark && sawLight); y++)
            for (int x = 0; x < qr.Size && !(sawDark && sawLight); x++)
            {
                if (matrix[y, x]) sawDark = true;
                else sawLight = true;
            }
        Assert.True(sawDark, "expected at least one dark module");
        Assert.True(sawLight, "expected at least one light module");
    }

    [Fact]
    public void EncodeText_Null_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            QrCode.EncodeText(null!, QrCode.Ecc.Medium));
    }

    [Fact]
    public void EncodeText_EmptyString_ProducesSmallestVersion()
    {
        // Empty payload should still encode cleanly — the smallest version
        // (v1, 21×21) trivially holds 0 data bytes plus mode header.
        var qr = QrCode.EncodeText(string.Empty, QrCode.Ecc.Low);

        Assert.NotNull(qr);
        Assert.Equal(1, qr.Version);
        Assert.Equal(21, qr.Size); // v1: 1 * 4 + 17

        // GetModule must agree with ToMatrix on every coordinate.
        var matrix = qr.ToMatrix();
        for (int y = 0; y < qr.Size; y++)
            for (int x = 0; x < qr.Size; x++)
                Assert.Equal(matrix[y, x], qr.GetModule(x, y));
    }

    [Fact]
    public void EncodeText_LongSubscriptionUrl_FitsWithoutCrash()
    {
        // Real subscription URLs from sub-aggregators routinely run 1-2 KB
        // (base64-encoded JSON). v40 byte-mode capacity at Ecc.Low is
        // 2,953 bytes, so a 2,000-char ASCII URL must fit.
        var longUrl = "https://example.org/sub?token=" + new string('a', 1970);
        Assert.Equal(2000, longUrl.Length);

        var qr = QrCode.EncodeText(longUrl, QrCode.Ecc.Low);

        Assert.NotNull(qr);
        Assert.InRange(qr.Version, 1, 40);
        // 2 KB of byte-mode data needs a high version — at minimum v20-ish
        // at Ecc.Low. Pin the lower bound to catch a regression where the
        // encoder picks v1 (which would mean data was silently truncated).
        Assert.True(qr.Version >= 15,
            $"expected high QR version for 2KB payload, got v{qr.Version}");
    }

    [Fact]
    public void EncodeText_GetModule_OutOfBoundsReturnsFalse()
    {
        // Defensive contract — the rendering layer may probe a 1-module
        // border around the matrix for the quiet zone; out-of-range coords
        // must NOT throw, they return false (= light = quiet zone).
        var qr = QrCode.EncodeText("test", QrCode.Ecc.Medium);

        Assert.False(qr.GetModule(-1, 0));
        Assert.False(qr.GetModule(0, -1));
        Assert.False(qr.GetModule(qr.Size, 0));
        Assert.False(qr.GetModule(0, qr.Size));
        Assert.False(qr.GetModule(int.MaxValue, int.MaxValue));
    }

    [Fact]
    public void EncodeText_HigherEcc_PicksLargerOrEqualVersion()
    {
        // Same payload at increasing ECC levels must monotonically grow (or
        // stay equal — encoder may upgrade ECC for free if data still fits)
        // the required version. This pins the version-selection loop in
        // EncodeSegment.
        var lo = QrCode.EncodeText(SampleVlessUri, QrCode.Ecc.Low);
        var hi = QrCode.EncodeText(SampleVlessUri, QrCode.Ecc.High);

        Assert.True(hi.Version >= lo.Version,
            $"High-ECC version ({hi.Version}) must be >= Low-ECC version ({lo.Version})");

        // ErrorCorrection on the returned object may differ from the asked
        // level because the encoder upgrades ECC for free when capacity
        // permits — assert only that it never *downgrades* below what was
        // requested.
        Assert.True((int)lo.ErrorCorrection >= (int)QrCode.Ecc.Low);
        Assert.True((int)hi.ErrorCorrection >= (int)QrCode.Ecc.High);
    }
}
