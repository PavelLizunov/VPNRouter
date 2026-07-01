using System;
using System.Linq;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// v2.32.3 input gate (2026-05-17) — parser-layer placeholder rejection.
///
/// <para>Pins the contract that <see cref="VlessUriParser.Parse"/> and
/// <see cref="ServerUriParser.Parse"/> throw
/// <see cref="PlaceholderConfigException"/> when ingested credentials
/// match a known-bad fingerprint
/// (see <see cref="PlaceholderDefense.KnownPubkeys"/>), and that the
/// <c>TryParse</c> / <c>ParseMultiple</c> variants drop those URIs the
/// same way they drop malformed input.</para>
///
/// <para>Foundation reference: Z:\kanareik incident — placeholder
/// <c>DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU</c> leaked from old
/// Android smoke-test code into a real user's subscription cache. F-E
/// catches it at Connect time but the user is stuck. Input gates here
/// prevent the placeholder from ever entering storage.</para>
/// </summary>
public class PlaceholderInputGateTests
{
    // The placeholder pubkey lives in PlaceholderDefense.KnownPubkeys (which
    // mirrors ConfigSanityCheck.KnownPlaceholderPubkeys). Hard-coded here
    // so the test catches future accidental changes to the fingerprint
    // list — the constant should ONLY shrink, never silently change.
    private const string PlaceholderPubkey = "DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU";
    private const string CleanPubkey = "abcDEFghi1234567890realPubkeyValueXYZ-_pq";

    private static string BuildVlessUri(string pubkey, string server = "1.2.3.4")
    {
        // Realistic share-link shape — matches what subscription endpoints
        // emit and what users paste from VPN provider pages.
        return $"vless://2d54442d-158f-49e2-b225-67ba1a5b77f4@{server}:443" +
               $"?security=reality&sni=yahoo.com&fp=firefox&pbk={pubkey}" +
               $"&sid=abcd1234&type=tcp&flow=xtls-rprx-vision#test-server";
    }

    // ─── VlessUriParser ────────────────────────────────────────────────────

    [Fact]
    public void VlessUriParser_PlaceholderPubkey_Throws()
    {
        var uri = BuildVlessUri(PlaceholderPubkey);

        var ex = Assert.Throws<PlaceholderConfigException>(() => VlessUriParser.Parse(uri));
        Assert.Equal("reality.public_key", ex.OffendingField);
        Assert.Equal(PlaceholderPubkey, ex.OffendingValue);
    }

    [Fact]
    public void VlessUriParser_TryParse_PlaceholderPubkey_ReturnsNull()
    {
        var uri = BuildVlessUri(PlaceholderPubkey);

        var result = VlessUriParser.TryParse(uri);

        Assert.Null(result);
    }

    [Fact]
    public void VlessUriParser_CleanUrl_Parses()
    {
        // Sanity: real-looking pubkey passes the gate. Without this we
        // can't tell apart "gate triggered correctly" from "gate broke
        // every URL".
        var uri = BuildVlessUri(CleanPubkey);

        var entry = VlessUriParser.Parse(uri);

        Assert.NotNull(entry);
        Assert.Equal(CleanPubkey, entry.Reality.PublicKey);
        Assert.Equal("1.2.3.4", entry.Server);
        Assert.Equal(443, entry.Port);
        Assert.Equal("test-server", entry.Name);
    }

    // ─── ServerUriParser dispatch ──────────────────────────────────────────

    [Fact]
    public void ServerUriParser_PlaceholderViaVlessDispatch_Throws()
    {
        // ServerUriParser.Parse dispatches vless:// to VlessUriParser.Parse;
        // confirm the typed exception propagates up unchanged.
        var uri = BuildVlessUri(PlaceholderPubkey);

        var ex = Assert.Throws<PlaceholderConfigException>(() => ServerUriParser.Parse(uri));
        Assert.Equal("reality.public_key", ex.OffendingField);
    }

    [Fact]
    public void ServerUriParser_ParseMultiple_DropsPlaceholder()
    {
        // Three-line blob: clean VLESS, placeholder VLESS, garbage line.
        // ParseMultiple's existing per-line try/catch already drops
        // FormatException; v2.32.3 contract is that it also drops
        // PlaceholderConfigException through the generic catch.
        var clean = BuildVlessUri(CleanPubkey, server: "8.8.8.8");
        var poisoned = BuildVlessUri(PlaceholderPubkey, server: "1.1.1.1");
        var garbage = "vless://this-is-not-a-valid-uri";

        var blob = string.Join("\n", clean, poisoned, garbage);

        var entries = ServerUriParser.ParseMultiple(blob);

        Assert.Single(entries);
        Assert.Equal("8.8.8.8", entries[0].Server);
        Assert.Equal(CleanPubkey, entries[0].Reality.PublicKey);
    }

    // ─── PlaceholderConfigException surface ────────────────────────────────

    [Fact]
    public void PlaceholderConfigException_ExposesField()
    {
        // Pin the OffendingField string — UI layers in later phases will
        // dispatch on this exact value to render field-specific guidance
        // ("Get a real public_key from your VPN provider").
        var uri = BuildVlessUri(PlaceholderPubkey);

        var ex = Assert.Throws<PlaceholderConfigException>(() => VlessUriParser.Parse(uri));

        Assert.Equal("reality.public_key", ex.OffendingField);
        Assert.Contains("placeholder", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
