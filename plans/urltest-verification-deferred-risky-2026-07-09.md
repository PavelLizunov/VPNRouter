# Deferred / risky sub-tasks — urltest verification (do NOT auto-run)

These are the parts of `plans/urltest-verification-plan-2026-07-09.md` that Fable is
NOT executing autonomously because they are behaviour-changing, touch a wide public
surface, do network I/O, or can leak user intent. They need explicit approval / a
review pass / a more-cautious model / live windows-brat verification. Fable keeps doing
the safe, additive, pure+tested work; each item here stays a written spec until someone
signs off.

## R1 — Add an explicit failure-phase enum to `DeepVerifyResult` (public surface change)
- Why risky: `DeepVerifyResult` is consumed in ~15+ files across App / Android / CLI /
  Tests; adding fields is a public-surface change and can churn the App
  characterization hash. Also touches `FreeConfigDeepVerifier` (separate Android copy).
- What: replace the free-text `Error` heuristic (currently bridged by a string-matching
  mapper, see `ServerHealthPhaseMapper`) with an explicit
  `enum DeepVerifyFailurePhase { None, LocalSpawn, SocksBind, ProxyHandshake, ProxiedHttp, Timeout }`
  so the mapper reads a typed reason instead of parsing strings.
- Gate before doing: run the App characterization test; update the pin if it moves;
  update all call sites + the Android copy; keep `Ok`/`Failed(string)` back-compat.

## R2 — Wire the classifier into the live probe pipeline + ServerViewModel + UI
- Why risky: changes what the user sees for every server row; behaviour + UI copy;
  needs windows-brat MCP verification (never dev box).
- What: after quick + deep probes run, feed outcomes through `ServerHealthPhaseMapper`
  -> `ServerHealthClassifier` -> render the new verdict chips (`Host OK` /
  `Protocol blocked` / `HTTP via VPN` / `ASN high-risk`) + RU copy; stop rendering a
  bare "Сервер работает" when only ping/TCP passed.
- Gate: live verify on windows-brat; visual-diff baseline refresh if pages change.

## R3 — ASN / provider metadata lookup (network I/O + privacy)
- Why risky: outbound network calls; must never upload/log subscription URLs or secrets.
- What: resolve server IP -> ASN/org/prefix (offline MaxMind-style DB preferred over a
  live API), cache locally, feed `AnalyzeProviderRisk`. Redact via `DiagnosticsRedactor`.
- Gate: confirm no secret leaves the machine; prefer a bundled/offline DB.

## R4 — Blocked-target canary probes (network + can reveal user intent to the ISP)
- Why risky: direct probes to blocked targets can expose intent; must be via-VPN only by
  default, opt-in for direct, updateable/user-supplied list, URL-redacted logs.
- What: control canary + multi-canary matrix feeding the `BlockedTargetCanary` phase.
- Gate: safe-default (via-VPN only) enforced + tested; live verify from an RU ISP.

## R5 — Auto/urltest ranking + selection behaviour change
- Why risky: changes which server the user actually connects through.
- What: penalize `ProtocolHandshakeBlockedLikely` / `ProviderSubnetHighRisk`, prefer
  ASN diversity; reword "Auto" as a quick web selector; expose selected member + last
  test age.
- Gate: live verify; tests for selected-member transitions (not just generated JSON).

## R6 — Release (ship a -rN / cut stable)
- Not autonomous. Stable cut needs an explicit user command; -rN only if asked.
