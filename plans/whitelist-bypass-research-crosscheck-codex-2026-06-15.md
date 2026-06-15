# Cross-check: whitelist-bypass-research, Codex, 2026-06-15

Independent validation notes for `PavelLizunov/whitelist-bypass-research`.

Scope:

- Main target: `landscape-report.md`
- Priority hints: `README.md`, `AGENTS.md`
- Audited repo revision: `0e7f097792527cb60b23f0a5a1400dc55d1d8d55`
- Goal: validate claims and overclaims, not implement code

## Executive Verdict

The report is directionally correct: under a true Russian mobile IP/CIDR allowlist, ordinary anti-censorship protocols do not survive by looking like HTTPS if the client-facing endpoint is not on a whitelisted IP. The strongest practical class is a whitelisted domestic service acting as bearer or relay.

The main weakness is tone. Several sections say "proven", "survives", "guaranteed", or give throughput ranges where the evidence is only public PoC plus README claims plus scattered field reports. The report should distinguish:

1. true IP/CIDR allowlist,
2. SNI/Host whitelist,
3. packet/byte threshold freezing,
4. full blackout where even whitelist/SMS is disabled,
5. platform anti-fraud / account enforcement.

## Priority 1: L3/IP Thesis

Claim:

> whitelist is L3/IP-based; the far endpoint must be on a whitelisted IP.

Verdict: confirmed for true IP/CIDR allowlist, overbroad if generalized to every Russian mobile restriction mode.

Evidence:

- net4people/bbs #516 reports Beeline mobile whitelist behavior: arbitrary SNI works when the destination IP is from `yandex.ru`, but the same SNI does not work when connecting to the reporter's own VPS.
- net4people/bbs #490 says the censor also started applying a whitelist based on destination server CIDR; in that case circumvention is extremely difficult and usually requires an intermediate node with an IP from the whitelist.

Sources:

- https://github.com/net4people/bbs/issues/516
- https://github.com/net4people/bbs/issues/490

Recommended wording:

> Under true IP/CIDR allowlist, the client-facing endpoint must be whitelisted, or traffic must first terminate at a whitelisted relay/service. SNI/Host camouflage can work only in weaker/mixed modes and does not make a non-whitelisted endpoint reachable under true allowlist.

Overclaims to fix:

- Do not imply all Russian mobile shutdowns are always pure L3 allowlists.
- Do not collapse SNI whitelist, CIDR allowlist, throttling/freezing, and full blackout into one regime.

## Priority 2: Survivability Classification

Verdict: mostly confirmed, but the matrix should say "direct non-whitelisted endpoint dies" rather than "protocol dies" in absolute terms.

Direct Reality, Shadowsocks, Hysteria2, TUIC, Trojan, AmneziaWG, Tor transports, fronting, and ECH do not solve the endpoint IP problem under a true allowlist. If the first remote IP is not whitelisted, protocol camouflage is insufficient.

Nuances:

- Reality/VLESS can still be relevant under DPI/blacklist/throttling regimes, but not as a direct route through true allowlist to a foreign VPS.
- ECH: net4people #417 confirms Cloudflare ECH blocking in Russia since 2024-11-05, but the evidence is specific to Cloudflare's public ECH name/signature. Under true allowlist, ECH still does not help unless the endpoint IP is whitelisted.
- Tor Snowflake/DTLS: net4people #603 confirms Snowflake-targeted DTLS filtering starting 2026-03-30 by JA3/JA4-like fingerprint. That is evidence of a DTLS/Pion risk, not evidence that every WebRTC/TURN relay class is dead.
- Domain fronting/refraction: under true allowlist, the front/refraction endpoint still has to be reachable on a whitelisted IP. Otherwise it fails at reachability before the trick matters.

Sources:

- https://github.com/net4people/bbs/issues/490
- https://github.com/net4people/bbs/issues/417
- https://github.com/net4people/bbs/issues/603
- https://habr.com/ru/articles/1009542/

Recommended matrix label:

- `blacklist_dpi_only`: direct endpoint may work until detected/blocked.
- `true_allowlist_direct`: dead unless first hop IP is whitelisted.
- `true_allowlist_via_whitelisted_relay`: conditional/survives depending on bearer and anti-fraud.

## Priority 3: TURN Relay Finding

Claim:

> `cacggghp/vk-turn-proxy` / `kobzevvv/vk-calls-tunnel` carry native WireGuard through whitelisted VK/Yandex TURN at about 5-25 Mbps and survive allowlist.

Verdict: confirmed as a public PoC and ecosystem; not confirmed as guaranteed production survivability or stable 5-25 Mbps field throughput.

Confirmed:

- `cacggghp/vk-turn-proxy` README says it tunnels WireGuard/Hysteria through VK Calls TURN or Yandex Telemost TURN, using DTLS 1.2 plus STUN ChannelData. It has a visible ecosystem of Android/iOS/macOS clients.
- `kobzevvv/vk-calls-tunnel` README describes WireGuard through VK TURN servers, with WireGuard client -> local tunnel client -> DTLS 1.2 -> STUN ChannelData -> VK TURN -> VPS -> WireGuard.
- `kiper292/wireguard-turn-android` is an Android WireGuard fork with integrated VK TURN proxy and WB mode.

Sources:

- https://github.com/cacggghp/vk-turn-proxy
- https://github.com/kobzevvv/vk-calls-tunnel
- https://github.com/kiper292/wireguard-turn-android
- https://habr.com/ru/articles/1017410/

Throughput evidence:

- `kobzevvv/vk-calls-tunnel` claims about 5 Mbps per stream and 4 streams about 20 Mbps.
- `cacggghp/vk-turn-proxy` issue #170 reports much lower real throughput: about 1.7-2.2 Mbps download and about 1.5 Mbps upload on 4G with 8 streams and 3 allocations.
- Other public issues mention sub-1 Mbps or a few Mbps reports. Treat 5-25 Mbps as optimistic/claimed, not guaranteed.

Source:

- https://github.com/cacggghp/vk-turn-proxy/issues/170

Yandex nuance:

- In `cacggghp/vk-turn-proxy`, Yandex Telemost is struck through in the opening line while a Yandex mode example still exists later. Treat VK as the primary validated path; treat Yandex as deprecated/fragile until field-verified.

Operational concerns:

- Whitelisted-service TURN is the best fit for a VPNRouter `wgturn-core` style abstraction because the client-facing endpoint is the third-party whitelisted TURN service, not our rented relay IP.
- The server/VPS still receives relay traffic from the third-party TURN infrastructure, so firewall allow rules and dynamic VK/WB/Yandex ranges must be handled carefully.
- WireGuard client routing must exclude the bearer service IPs; otherwise the tunnel captures its own TURN traffic.
- Public mass adoption may trigger platform countermeasures: account checks, invite-link churn, call metadata, DTLS fingerprinting, TURN allocation quotas, rate limiting.

License concerns for integration:

- `cacggghp/vk-turn-proxy` is GPL-3.0. Direct code reuse can infect a proprietary/non-GPL distribution model.
- `kobzevvv/vk-calls-tunnel` appeared to have no explicit license in the GitHub page inspected. No-license code should not be copied without permission.
- `kiper292/wireguard-turn-android` is Apache-2.0 but is a WireGuard Android fork and needs dependency/license review before reuse.

Recommended wording:

> TURN over VK Calls is the strongest current PoC class for true allowlist because the first hop is a whitelisted media/TURN service. Public repos show native WG encapsulation via DTLS/STUN ChannelData. Field survivability and throughput remain operator/region/account dependent and require our own measurements.

## Priority 4: Yellow Sections

The sections marked as least verified should remain conditional.

### IM-as-bearer / cloud storage / API bearers

Verdict: unclear as data plane, plausible as control plane.

Use for:

- bootstrap,
- signed config/key delivery,
- endpoint rotation,
- delayed store-and-forward.

Do not claim as:

- low-latency WireGuard bearer,
- proven full tunnel,
- stable production path.

The `Max`, Telegram, Yandex S3, RuTube/VK Video, and similar claims need separate proof of:

1. service is actually whitelisted under target operator/region,
2. API endpoints are reachable during allowlist,
3. throughput/latency is enough for intended role,
4. platform anti-abuse will not quickly burn accounts/keys.

### MASQUE

Verdict: technically valid standard, unproven as a Russian allowlist bypass path.

Confirmed:

- RFC 9298 defines UDP proxying in HTTP.
- RFC 9484 defines IP packet proxying in HTTP and explicitly mentions VPN-like use cases.

Sources:

- https://www.rfc-editor.org/rfc/rfc9298
- https://www.rfc-editor.org/rfc/rfc9484

Missing proof:

- a whitelisted HTTP/3 endpoint that will run or front an allowed MASQUE proxy,
- stable origin placement behind that endpoint,
- proof that CONNECT-UDP / CONNECT-IP is not blocked or unsupported by the relevant service/CDN,
- RU field measurements.

Recommended label: `research / conditional`, not `survives`.

### Academic threat model

Verdict: useful context, weak product evidence.

Keep academic sources as conceptual framing for collateral freedom, default-deny, and protocol mimicry, but do not use them as proof that a specific 2026 Russian operator path works.

### DNS through NSDI

Verdict: conditional, correctly marked as not end-to-end proven.

The report should keep DNS as last-resort/control-plane unless there are successful tests through `195.208.4.1` / `.5.1` with realistic query volume, domain delegation, upstream authoritative server placement, and detection-risk measurements.

## Priority 5: Dates and Numbers

### Yandex.Cloud myth / Habr 1021160 UPD2

Verdict: confirmed.

Habr 1021160 initially claimed a Yandex Cloud relay path, then UPD2 says the author removed the Yandex Cloud relay because the "YC IPs are whitelisted" thesis was a myth: `Yandex.Cloud LLC` and `YANDEX LLC` are different ASes, and YC VMs are cut under allowlist.

Source:

- https://habr.com/ru/articles/1021160/

### 24.01.2026 pool segregation

Verdict: confirmed as media report, not confirmed as official RKN order.

Habr relays "Kod Durova": RKN allegedly required cloud providers to keep IP pools used for whitelisted resources separate from pools rented to other clients; VPN services lost many such servers on 2026-01-24. RKN did not officially comment.

Source:

- https://habr.com/ru/news/988544/

### 09.05.2026 Moscow full mobile blackout

Verdict: confirmed.

Interfax and CNews report MinTsifry's statement that on 2026-05-09 Moscow mobile internet would be temporarily fully restricted, including white-listed sites and SMS. Home internet and Wi-Fi were said to remain normal.

Sources:

- https://www.interfax.ru/russia/1088123
- https://www.cnews.ru/news/top/2026-05-07_v_moskve_na_den_pobedy_otklyuchat

### 15.04.2026 platform/VPN directive

Verdict: partly confirmed, wording overstates.

Media reports say MinTsifry asked major platforms to restrict access for users with enabled VPN by around 2026-04-15 and threatened exclusion from white lists / loss of benefits. The report's wording "obliges VK/Yandex/WB to detect and ban anti-censorship users" should be softened to "media-reported pressure to block VPN access and share/consume VPN IP lists".

Sources:

- https://rb.ru/news/krupnejshie-kompanii-rossii-ogranichat-dostup-k-sajtam-pri-vklyuchyonnom-vpn-mera-mozhet-vstupit-v-silu-k-15-aprelya/
- https://zona.media/news/2026/03/30/cancel

### Snowflake JA3/JA4 DTLS filter

Verdict: confirmed.

net4people/bbs #603 reports Snowflake-targeted DTLS filtering starting 2026-03-30, with blocking tied to a particular JA3/JA4-like fingerprint. This matters for Pion/default DTLS fingerprints and WebRTC mimicry.

Source:

- https://github.com/net4people/bbs/issues/603

### 68-71 regions by March 2026

Verdict: plausible but should cite the original "Na svyazi" / media source, not treat as primary fact unless directly referenced.

Search results show the number repeated by media, but the report should either cite the original monitoring project or downgrade confidence.

### Speeds

Verdict: weakly verified.

- TURN 5-25 Mbps: optimistic/claimed; real public issue reports include 1-2 Mbps and lower.
- DNS 20-60 KB/s: plausible for DNS tunnel class but needs source and our own tests.
- WebRTC SFU DataChannel 44 Mbps: not validated as a stable censorship-bypass field rate; keep as benchmark claim only with source.

## Suggested Report Edits for Claude

1. Replace "TURN-relay ... dokazanno rabotaet" with "strongest public PoC; field validation required".
2. Replace "VK/Yandex TURN" with "VK TURN confirmed primary; Yandex Telemost path uncertain/deprecated until retested".
3. Replace "5-25 Mbps" with "claimed up to about 20 Mbps in README; public field reports range from sub-1 Mbps to a few Mbps; measure ourselves".
4. Add a regime taxonomy section before the survivability matrix:
   - true IP/CIDR allowlist,
   - SNI/Host allowlist,
   - packet/byte threshold freezing,
   - full mobile blackout,
   - platform anti-fraud/account enforcement.
5. Add license note before any integration plan:
   - GPL-3.0 for `cacggghp/vk-turn-proxy`,
   - no explicit license observed for `kobzevvv/vk-calls-tunnel`,
   - Apache-2.0 plus WireGuard fork review for `kiper292/wireguard-turn-android`.
6. For MASQUE, keep as research unless there is a whitelisted HTTP/3/MASQUE-capable endpoint and RU field test.
7. For IM/cloud/API bearers, move to "control-plane / bootstrap" until a full tunnel PoC exists.
8. For the 2026-04-15 claim, cite it as media-reported MinTsifry pressure rather than formal technical directive.

## Product Implication for VPNRouter

Best next experiment:

1. Build a measurement harness, not a product integration first.
2. Test VK TURN path on at least MTS, MegaFon, Beeline, T2/Yota, across normal mobile internet, allowlist mode, and any known regional restriction window.
3. Measure:
   - handshake success,
   - sustained throughput,
   - packet loss/jitter,
   - reconnect behavior,
   - battery impact on Android,
   - account/link lifetime,
   - whether bearer service IPs need routing exclusions,
   - whether DNS/auth endpoints for VK/WB/Yandex remain reachable during allowlist.
4. Only after that decide whether VPNRouter needs:
   - external process wrapper around existing client,
   - clean-room TURN-over-DTLS/STUN implementation,
   - Android-specific fork/integration,
   - or only documented support for third-party clients.

Bottom line:

The research's central model is valid. The actionable path is TURN/WebRTC via whitelisted services, but the report should lower confidence language from "proven production bypass" to "highest-priority public PoC requiring our own field validation".
