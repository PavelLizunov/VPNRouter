#nullable enable

using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Pins the custom-config fork-only feature gate (OPEN-DEFECTS P1, audit
/// batch-1 #6): AWG obfuscation fields / xhttp transport in raw custom JSON
/// must be rejected with an actionable error when the active sing-box core
/// lacks with_awg / with_xhttp — instead of FATALing sing-box at start.
/// Plain wireguard endpoints (official sing-box 1.11+) must NOT be gated.
///
/// <para>Convention (OPEN-DEFECTS P2, SingBoxFeatures probe): every test sets
/// <see cref="SingBoxFeatures.OverrideAwg"/>/<see cref="SingBoxFeatures.OverrideXhttp"/>
/// in try/finally so no test ever probes the real installed binary.</para>
/// </summary>
public class CustomConfigInjectorForkGateTests
{
    private const string AwgEndpointConfig = /*lang=json*/ """
        {
          "endpoints": [
            {
              "type": "wireguard",
              "tag": "proxy",
              "address": ["10.66.0.2/32"],
              "private_key": "aaa",
              "jc": 4, "jmin": 40, "jmax": 70, "s1": 15, "s2": 68,
              "h1": "123456",
              "peers": [{ "address": "1.2.3.4", "port": 51820, "public_key": "bbb" }]
            }
          ],
          "outbounds": [ { "type": "direct", "tag": "direct" } ]
        }
        """;

    private const string PlainWireGuardEndpointConfig = /*lang=json*/ """
        {
          "endpoints": [
            {
              "type": "wireguard",
              "tag": "proxy",
              "address": ["10.66.0.2/32"],
              "private_key": "aaa",
              "peers": [{ "address": "1.2.3.4", "port": 51820, "public_key": "bbb" }]
            }
          ],
          "outbounds": [ { "type": "direct", "tag": "direct" } ]
        }
        """;

    private const string XhttpOutboundConfig = /*lang=json*/ """
        {
          "outbounds": [
            {
              "type": "vless",
              "tag": "proxy",
              "server": "1.2.3.4",
              "server_port": 443,
              "uuid": "11111111-2222-3333-4444-555555555555",
              "transport": { "type": "xhttp", "path": "/x" }
            },
            { "type": "direct", "tag": "direct" }
          ]
        }
        """;

    private static void WithOverrides(bool awg, bool xhttp, Action body)
    {
        SingBoxFeatures.OverrideAwg = awg;
        SingBoxFeatures.OverrideXhttp = xhttp;
        try { body(); }
        finally { SingBoxFeatures.ResetForTests(); }
    }

    // ── Validate: AWG fields ────────────────────────────────────────────────

    [Fact]
    public void Validate_AwgFields_CoreWithoutAwg_IsRejectedWithActionableError()
        => WithOverrides(awg: false, xhttp: false, () =>
        {
            var (isValid, errors) = CustomConfigInjector.Validate(AwgEndpointConfig);
            Assert.False(isValid);
            Assert.Contains(errors, e => e.Contains("with_awg"));
        });

    [Fact]
    public void Validate_AwgFields_CoreWithAwg_Passes()
        => WithOverrides(awg: true, xhttp: false, () =>
        {
            var (isValid, errors) = CustomConfigInjector.Validate(AwgEndpointConfig);
            Assert.True(isValid, string.Join("; ", errors));
        });

    [Fact]
    public void Validate_PlainWireGuardEndpoint_IsOfficial_NeverGated()
        => WithOverrides(awg: false, xhttp: false, () =>
        {
            // Official upstream construct (sing-box 1.11+): no AWG fields → no gate,
            // and the endpoint counts as the proxy egress (no false "No proxy outbound").
            var (isValid, errors) = CustomConfigInjector.Validate(PlainWireGuardEndpointConfig);
            Assert.True(isValid, string.Join("; ", errors));
        });

    // ── Validate: xhttp transport ───────────────────────────────────────────

    [Fact]
    public void Validate_XhttpTransport_CoreWithoutXhttp_IsRejectedWithActionableError()
        => WithOverrides(awg: false, xhttp: false, () =>
        {
            var (isValid, errors) = CustomConfigInjector.Validate(XhttpOutboundConfig);
            Assert.False(isValid);
            Assert.Contains(errors, e => e.Contains("with_xhttp"));
        });

    [Fact]
    public void Validate_XhttpTransport_CoreWithXhttp_Passes()
        => WithOverrides(awg: false, xhttp: true, () =>
        {
            var (isValid, errors) = CustomConfigInjector.Validate(XhttpOutboundConfig);
            Assert.True(isValid, string.Join("; ", errors));
        });

    // ── Inject: runtime backstop ────────────────────────────────────────────

    [Fact]
    public void Inject_AwgFields_CoreWithoutAwg_ThrowsActionable_NotSingBoxFatal()
        => WithOverrides(awg: false, xhttp: false, () =>
        {
            var ex = Assert.Throws<NotSupportedException>(() =>
                CustomConfigInjector.Inject(AwgEndpointConfig, new[] { "Discord.exe" }, new AppSettings()));
            Assert.Contains("with_awg", ex.Message);
        });

    [Fact]
    public void Inject_XhttpTransport_CoreWithXhttp_DoesNotThrowForkGate()
        => WithOverrides(awg: false, xhttp: true, () =>
        {
            // Should sail past the fork gate (whatever later phases do with the
            // config, the gate itself must not fire when the core carries the tag).
            var json = CustomConfigInjector.Inject(XhttpOutboundConfig, new[] { "Discord.exe" }, new AppSettings());
            Assert.Contains("xhttp", json);
        });
}
