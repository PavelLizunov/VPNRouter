# H-69: accept a raw sing-box JSON subscription

## Why

The owner's control daemon serves its subscription as `Content-Type: application/json` with a complete sing-box
configuration (`log`, `outbounds`, `route`): a `selector`, `hysteria2`, `vless` (Reality, TCP or XHTTP), `direct` and
`block` outbounds. `SubscriptionFetcher.TryDecodeBody` only understood the wrapper `{"config":"<base64>"}`, so a raw
sing-box document ended in "JSON response has no 'config' field" and the app showed zero servers. It was found while
testing the Android app on an emulator: the subscription had to be converted by hand into a list of share links first.

## What

- New `VPNRouter.Core/Services/SingBoxJsonSubscription.cs` (internal, same shape as `ClashYamlParser`): detects a
  document whose root has an `outbounds` array and maps each proxy outbound to the share link the existing
  `ServerUriParser` already reads. `TryDecodeBody` calls it after the `config` wrapper check, so the wrapper still wins and
  nothing changes for base64 lists, plain link lists, the wrapper, or Clash YAML.
- Mapped: `vless` (Reality or TLS or none; transport tcp, ws, grpc, xhttp; flow, sni, fp, pbk, sid, alpn, insecure),
  `hysteria2` (sni, insecure, salamander obfs, up/down Mbps), `tuic`, `shadowsocks` (with plugin). The outbound `tag` is
  the display name, the server address is the fallback, IPv6 hosts are bracketed, values are percent-encoded.
- Ignored on purpose: `selector`, `urltest`, `direct`, `block`, `dns`. Skipped with an Information log (tag, type,
  reason; never the link): protocols the app has no parser for (trojan, vmess, wireguard...), transports the link parser
  or config generator does not know (http, quic, httpupgrade), and outbounds missing server, valid `server_port`, uuid,
  password or method. A bad outbound never throws and never hides the good ones.
- XHTTP: not special-cased in the converter. It emits `type=xhttp` with mode, path, host and padding, and the existing
  `VlessUriParser` decides: it rejects the link unless `SingBoxFeatures.XhttpAvailable` (a sing-box-lx build), so on a
  stock sing-box those outbounds drop out exactly like a pasted `vless://...type=xhttp` link, and with XHTTP available they
  are kept. No new XHTTP support is claimed.
- Not mapped (the link parser does not read them): `packet_encoding`, TLS `utls.enabled=false` semantics, hysteria2 ALPN
  and port hopping, `route`, `dns`, `log`.

## Verification

- `SingBoxJsonSubscriptionTests` (27 cases: 22 facts and a 5-row theory, synthetic fixtures only: fake uuids, 203.0.113.x addresses, example.com):
  Reality/hysteria2 mapping, XHTTP with and without the feature, ignored and unsupported outbounds, ws/grpc/unsupported
  transports, TLS and no-TLS VLESS, hysteria2 options, TUIC, Shadowsocks with plugin, missing or invalid fields, string
  ports, missing tag, special characters in tag and password, IPv6, wrapper precedence, empty or malformed documents,
  placeholder counting, deduplication, and unchanged plain/base64/wrapper/Clash inputs. They run in the CI `test` job.
- Real-data shape check (2026-09-30, counts only; the URL, token and values are not recorded anywhere): the owner's
  subscription has 9 proxy outbounds (3 hysteria2, 4 VLESS Reality TCP, 2 VLESS Reality XHTTP), no field the mapping
  needs is missing, so all 9 map and 7 survive when sing-box has no XHTTP (9 with it). This checked the data against the
  mapping rules with a script; the C# code itself was exercised by CI, not run against the live subscription.

## Outcome

Merged after green exact-head CI. Runtime behaviour on a device is not yet verified.
