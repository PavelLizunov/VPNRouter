# Fable — guaranteed-safe Core verification backlog (urltest verification)

A self-contained, guaranteed-risk-free task for Fable. Everything here is **pure C# in
`VPNRouter.Core` + xUnit tests + plan `.md`** — additive only. Nothing in this task can
trip a model-switch / unsafe-op trigger, so Fable can work through it continuously,
personally (no subagents), and keep serving the new URL-test / verification
implementation.

Context already in the repo: `plans/urltest-verification-plan-2026-07-09.md`,
`plans/adr-urltest-verification-2026-07-09.md`,
`VPNRouter.Core/Services/ServerHealthClassifier.cs` (+ its tests, green),
`plans/audit-import-2026-07-09/01-audit-vector-map-batch1.md`.

## The guaranteed-safe envelope — you may ONLY do these

- Create **new** `.cs` files in `VPNRouter.Core` (pure, `#nullable enable`, no I/O).
- Create **new** xUnit test files in `VPNRouter.Tests`.
- Add **new** localization getters to `VPNRouter.Core/Localization/Strings.cs` (additive
  `public static string` getters only — no XAML, no binding, no behavior).
- Create/update **plan `.md`** files under `plans/`.
- Run `dotnet build` / `dotnet test` locally.
- Commit to `main` + `git push origin HEAD:main && git push forgejo HEAD:main`.

## NEVER do these here (if a unit needs one, STOP and append it to
## `plans/urltest-verification-deferred-risky-2026-07-09.md`, then move to the next safe unit)

- No network I/O (no HTTP/DNS/ASN lookups, no sockets).
- No process spawn (no sing-box, no shell-outs).
- No modifying `DeepVerifyResult`, `ServerProbeStatus`, or any other **existing**
  widely-consumed type — you may only **read** them and **add new** types.
- No touching the live probe pipeline, `ServerViewModel`, App/Android UI behavior,
  ConfigGenerator emission, or Auto selection wiring.
- No windows-brat / WinRM / MCP / RDP; no releases (ship / tag / cut stable); no secrets;
  no destructive git/system ops; no `--no-verify`.

## Backlog — pure units, in order (each: TDD, build clean, tests green, one focused commit)

1. **`ServerHealthPhaseMapper`** — pure map from the EXISTING probe outputs to
   `ServerHealthPhases`: `FromQuickProbe(ServerProbeStatus)` (Ok/Slow→TcpConnect=Pass;
   Unreachable/Timeout→TcpConnect=Fail; TlsFailed→Tcp=Pass+TlsCamouflage=Fail;
   Implausible/Skipped/Unknown→Unknown) and `FromDeepVerify(DeepVerifyResult)` (Ok→
   ProxiedHttpControl=Pass; local/infra errors like "binary missing"/"spawn failed"/
   "sing-box…didn't bind"/"placeholder"/"cancelled" → inconclusive Unknown, NOT a server
   verdict; http/timeout errors → ProxiedHttpControl=Fail) + a field-wise `Merge`. Tests
   pin the "local sing-box failure must not read as server-blocked" rule. **(started)**

2. **Canary list model + rules (pure, no probing)** — `CanaryTarget` record (url,
   category/tier, expectedDirectRuStatus, expectedViaVpnStatus, lastReviewed, source,
   riskNotes) + `CanaryList` with: tier selection (control / popular-blocked /
   less-popular), TTL/staleness classification (`CanaryListStaleOrAmbiguous` when an item
   is older than a configurable TTL), and a **pure URL-redaction helper** (strip query +
   path fragments for logging). Tests pin staleness, tier ordering, redaction.

3. **`ServerRankingScorer` (pure)** — score/order servers by `ServerHealthVerdict`:
   heavily penalize `ProtocolHandshakeBlockedLikely` / `ProviderSubnetHighRisk`, reward
   `Healthy`, tie-break by ASN diversity (prefer spreading across providers). Returns an
   ordered list; does NOT change any live selection. Tests pin the penalty order + the
   diversity tie-break.

4. **Extend `ServerHealthClassifier` coverage** — `PhaseOutcome.Skipped` handling,
   UDP-app + canary combinations, and any edge case the audit's regression list implies
   but isn't yet pinned. Tests only (+ minimal classifier tweaks if a gap is found).

5. **Localization (additive getters)** — RU/EN strings for the new verdicts and the
   RU-block / canary UX copy from the audit ("Хост доступен, но VPN-протокол не прошёл
   проверку", "VPN подключился, но проверка заблокированного сервиса не прошла", etc.)
   as `public static string` getters in `VPNRouter.Core/Localization/Strings.cs`. No XAML.

## Rules

- Work personally, no subagents.
- One focused commit per unit: `type(scope): subject` (<=72 chars) + trailer
  `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`. Push both remotes.
- Stop when units 1-5 are built + green, or when the user redirects. When any unit hits
  the NEVER list, it goes to the deferred file and you continue with the next safe unit.
