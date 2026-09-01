# sing-box-lx 1.14 readiness (2026-09-01)

Status: externally gated; no binary, release, tag, or deployment change is authorized by this plan.

## Verdict

**Extend `Leadaxe/sing-box-lx`, but do not adopt its current release.** VPNRouter needs LX for XHTTP and AmneziaWG, while `v1.14.0-lx.29` explicitly declares SagerNet `v1.14.0-beta.9` as its base. The accepted target must be a non-prerelease LX tag and immutable commit whose ancestry proves final SagerNet `v1.14.0` (`0b8995879f29a9b98ee027bc17b75e101445b238`) or a later stable release.

## Current evidence

- Official SagerNet stable is `v1.14.0`, published 2026-08-31, and requires Go 1.25 or newer.
- Latest non-prerelease LX release checked for this plan is `v1.14.0-lx.29` at `4959f2084fd00895fb41914ec407bdaf01300666`, published 2026-08-26. Its release body says base `v1.14.0-beta.9`, so it does not meet the requested stable-base gate.
- Current VPNRouter desktop pin is LX commit `c7a2592e750406ade9ebaae1d0fdb7482fc0773e` plus AmneziaWG runtime commit `0c0c10b5d3236796bd3832a6813223d6dc7d0bb1`, labeled `1.13.13-lx-awg`. The upstream `1.13.14` fallback is local-build-only and cannot satisfy a release upload.
- Current Android shipment is tooling AAR `tooling-libbox-singbox-1.13.10`, SHA256 `239c4101465edcc270de75182764fb7566efd5fd284fbce35720fe70fd69f1a6`.
- Existing local source backports `0b7ffbaa...` and `72a8723e...` are ancestors of official `v1.14.0`; a future stable-base LX build should remove them instead of reapplying them.

## Desktop acceptance gate

1. Pin an immutable non-prerelease LX tag and commit whose ancestry includes final SagerNet `v1.14.0` or a later stable release.
2. Build with `with_gvisor,with_quic,with_dhcp,with_wireguard,with_utls,with_clash_api,with_naive_outbound,with_purego,badlinkname,tfogo_checklinkname0,with_xhttp,with_awg` plus any required LX-native tags.
3. Assert `sing-box version` and Go build metadata report the intended version, commit, and required tags; update all desktop pins and checksums atomically.
4. Run `sing-box check` over generated VLESS/Reality, XHTTP, AWG2, DNS-tunnel, full/split/exclude, geo-bypass, and representative custom configs.
5. Run the full build, test, packaging, updater, and fixed-WINBRAT candidate gates under separate release authority.

## Android acceptance gate

LX `.29`'s prebuilt AAR enables `with_xhttp` and `with_awg` but replaces the externally reachable Clash HTTP API with `with_lx_command`. VPNRouter currently polls authenticated `experimental.clash_api` HTTP endpoints, so the AAR is not a drop-in replacement.

Either build and verify an LX AAR that retains `with_clash_api`, or migrate the Java/JNI runtime to the LX command API in a separate reviewed phase. Require an immutable source pin, AAR SHA256, Java/JNI API compatibility, APK build, service lifecycle tests, stats polling, deep verification, and device verification before changing the shipped Android pin.

## AmneziaWG 3.1

Current LX and VPNRouter support AWG2 only. LX `.29` documents AWG2, and source inspection found none of the AWG 3.1 fields `header_protection_key`, `content_padding_addition`, `rekey_after_time`, `random_trailers`, or `disable_cookies`; LX issue #15 remains open. VPNRouter's `AwgConfig`, parser, and generator expose only AWG2 fields.

AWG 3.1 therefore requires a separate gated phase after runtime support exists: add explicit schema and parser fields, distinguish AWG2 from AWG3 capability, fail closed on unsupported configs, and prove live client/server interoperability. Never down-convert an AWG 3.1 config to AWG2; official Amnezia documentation says they are not interchangeable.

## DNS posture

VPNRouter routes `protocol=dns` through `hijack-dns`. `vpn-dns` is detoured through `proxy`; UDP DNS for AWG/WireGuard is permitted only inside that encrypted tunnel. Direct and smart public DNS use Cloudflare DoH, while VPNRouter-owned geo/censorship rules reuse tunnel-routed DNS instead of assigning a country-specific resolver. Only configured LAN suffixes may use the OS resolver.

VPNRouter-owned direct public UDP DNS in custom bootstrap and deep-verifier configs uses Cloudflare DoH. Custom geo rules select an existing proxy-detour resolver or synthesize Cloudflare DoH through the proxy, preserving `dns-direct` bootstrap loop avoidance and explicitly authored DNS servers. Evaluate sing-box 1.14 TUN DNS-interface hijacking separately because enabling it globally could alter process-based split-tunnel semantics.

## References

- [SagerNet sing-box v1.14.0](https://github.com/SagerNet/sing-box/releases/tag/v1.14.0)
- [sing-box migration guide](https://sing-box.sagernet.org/migration/)
- [sing-box deprecated features](https://sing-box.sagernet.org/deprecated/)
- [sing-box typed HTTPS DNS](https://sing-box.sagernet.org/configuration/dns/server/https/)
- [Leadaxe sing-box-lx v1.14.0-lx.29](https://github.com/Leadaxe/sing-box-lx/releases/tag/v1.14.0-lx.29)
- [Leadaxe issue #15: AmneziaWG 3 support](https://github.com/Leadaxe/sing-box-lx/issues/15)
- [Official AmneziaWG documentation](https://docs.amnezia.org/documentation/amnezia-wg/)
- [Official Amnezia FAQ](https://docs.amnezia.org/faq/)
