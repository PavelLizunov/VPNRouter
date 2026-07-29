# Phase 2 — R02 — Custom config / protocol parity

**Owner**: Qwen Code session (code-only)
**Branch**: `codex/qwen-audit-r02-config-protocol-2026-07-29`
**Base**: `origin/main` (verified: no P1 branch touches `ConfigGenerator.cs`, `CustomConfigInjector.cs`, or `VlessDeepVerifier.cs`)
**Roadmap ref**: `plans/qwen-remaining-remediation-index-2026-07-29.md` (R02); prompt pool P04
**IDs**: CFG-1, CFG-2, PROTO-1
**Effort**: ~2-3 h
**Risk**: MEDIUM (CFG-1 affects DNS leak/stall; CFG-2 can FATAL sing-box; PROTO-1 wrongly condemns servers)
**Blast radius**: `VPNRouter.Core/Services/CustomConfigInjector.cs`, `VPNRouter.Core/Services/VlessDeepVerifier.cs`, (+ read-only `ConfigGenerator.cs`), tests · ~+90 LOC · runtime: custom-config DNS strategy, injected urltest tag, deep-verify result for dns-tunnel
**Rollback**: `git revert <commit>` / delete branch

---

## 1. Final P00 verdict / severity / confidence / corrected scope

| ID | Orig | Verdict | Final | Conf |
|---|---|---|---|---|
| CFG-1 | P2 | CONFIRMED | P2 | High |
| CFG-2 | P2 | CONFIRMED | P2 | High |
| PROTO-1 | P2 | CONFIRMED | P2 | High |

All three are independent root causes in the custom-config / verifier surface.
They are grouped in one R-package because they share the same zone
(`CustomConfigInjector` / verifier) and test project, but each is a separate
commit and may be split into two PRs if the diff grows (per prompt pool P04).

## 2. Verified current root cause (commit `b39a28c3`)

### CFG-1 — DNS strategy parity gap

- Generated path (verified `ConfigGenerator.cs:995`):
  ```csharp
  Strategy = (settings.App.ForceIpv4Only || !settings.Tun.Ipv6Enabled) ? "ipv4_only" : null,
  ```
  (with the G5 comment `:988-994` explaining the `!Ipv6Enabled` rationale).
- Custom include-split path (`CustomConfigInjector.cs:1532`):
  ```csharp
  if (forceIpv4Only) dns["strategy"] = "ipv4_only";
  ```
  `:196`/`:1380` show NO `Ipv6Enabled` parameter; there are 0 `Ipv6Enabled`
  matches in the file. `:258-259` is a full/exclude backstop only; `:263`
  (include-split) sets `dns.final`, never `strategy`.
- Consequence: include-split + `ForceIpv4Only=false` + `Ipv6Enabled=false` →
  `dns.strategy` stays authored/default (AAAA enabled) on an IPv4-only TUN → the
  exact stall/leak the G5 comment warns about.
- Reachable via `StartupPipeline.cs:996`, `HealthMonitor.cs:1222/1232/1239`,
  `StartCommand.cs:281`, `AndroidConfigBuilder.cs:290`.

### CFG-2 — injected `auto` tag collision

- `CustomConfigInjector.cs:309` `EnsureUrltest` bails ONLY if an existing
  outbound is `type == "urltest"` (verified `:306-311`). It never checks for an
  existing outbound TAGGED `auto` of another type.
- `:324` sets `["tag"]="auto"`; `:332` inserts the urltest before the selector;
  `:335` prepends `auto` to the selector children.
- `Validate()` (`:340-392`, verified) has NO duplicate-tag / reserved-`auto`
  check.
- Consequence: a custom JSON with a `selector` outbound (non-empty children) plus
  a second `{type:"direct"|"vless", tag:"auto"}` outbound passes `Validate`, then
  `EnsureUrltest` inserts a SECOND outbound tagged `auto` → sing-box rejects
  duplicate outbound tags (FATAL). `auto` is a common clash-style tag.

### PROTO-1 — dns-tunnel condemned by deep verifier

- `ConfigGenerator.cs:1548` maps `"dns-tunnel" => BuildDnsTunnelOutbound`;
  `:1562-1571` builds a `127.0.0.1`:`DefaultLocalPort` outbound (uuid only, no
  TLS/Reality — the slipstream sidecar provides transport).
- `VlessDeepVerifier.cs` switch (verified `:425-435`) falls through to
  `_ => BuildVlessOutbound(s)` (`:434`). The classifier (`:223-258`) returns
  `UnsupportedByVerifier` for AWG/xhttp/naive but has 0 `dns-tunnel` matches.
- Consequence: a dns-tunnel entry is probed as an ordinary VLESS outbound to
  `entry.Server` (the tunnel domain) with TLS, ignoring the sidecar → probe fails
  → `DeepVerifyResult.Failed` → a valid server is condemned, violating the
  verifier's own "never condemn the server for our own gap" invariant (which
  already grants `UnsupportedByVerifier` for AWG/xhttp/naive).
- Entry shape: `ServerUriParser.cs:330` (`Server` = domain identity).

## 3. Why

CFG-1 silently produces an IPv4-only TUN that still issues AAAA queries (stall +
leak) for custom include-split configs. CFG-2 can FATAL sing-box at startup on a
plausible user config. PROTO-1 marks working dns-tunnel servers as blocked,
defeating the verifier's stated invariant. All three are reachable on supported
config and have no guard.

## 4. What

1. **CFG-1**: reuse ONE DNS-strategy decision so the custom include-split path
   also yields `ipv4_only` when `!Ipv6Enabled` (not only when `ForceIpv4Only`).
   Thread `Ipv6Enabled` into the injector method (or read it from the same
   settings object the generator uses). Do NOT overwrite a user-authored
   `dns.strategy` without contract — only fill the default when absent.
2. **CFG-2**: choose a collision-free injected urltest tag (e.g. probe
   `auto`, `auto-2`, ... against existing outbound tags) OR reuse an existing
   urltest if one is reachable from the selector; add a duplicate/reserved-tag
   guard so `Validate` rejects an ambiguous config actionably instead of letting
   sing-box FATAL.
3. **PROTO-1**: add a typed `UnsupportedByVerifier` short-circuit for
   `dns-tunnel` in the verifier classifier/switch (mirroring AWG/xhttp/naive), so
   a dns-tunnel server is never condemned for the verifier's lack of sidecar
   support.

```diff
- if (forceIpv4Only) dns["strategy"] = "ipv4_only";
+ if ((forceIpv4Only || !ipv6Enabled) && dns["strategy"] is null)
+     dns["strategy"] = "ipv4_only";
```

```diff
  outbound = protocol switch
  {
      "hysteria2"   => BuildHysteria2Outbound(s),
      ...
      "naive"       => BuildNaiveOutbound(s),
+     "dns-tunnel"  => null,   // handled as UnsupportedByVerifier below
      _             => BuildVlessOutbound(s),
  };
```

## 5. How (ordered minimal steps)

1. Read `ConfigGenerator.cs:985-1000` and the injector's DNS block fully; locate
   the single method that owns the include-split DNS edit.
2. CFG-1: thread `Ipv6Enabled` to that method; apply the same
   `(forceIpv4Only || !ipv6Enabled)` rule; guard against overwriting an authored
   strategy.
3. Read `EnsureUrltest` + `Validate` fully. CFG-2: compute a collision-free tag
   from the existing outbound tag set; update the selector-child reference to the
   chosen tag; add the duplicate/reserved-tag check to `Validate`.
4. Read the verifier classifier (`:223-258`) and switch (`:425-435`). PROTO-1:
   add `dns-tunnel` to the `UnsupportedByVerifier` set; ensure the result type is
   the same typed result AWG/xhttp/naive return.
5. Add tests (below). Static review of generated sing-box JSON shape.

### Tests written

- `CustomConfigInjectorTests.IncludeSplit_Ipv6Disabled_ForcesIpv4OnlyDnsStrategy`
  — fails on old code (strategy stayed null).
- `CustomConfigInjectorTests.IncludeSplit_UserAuthoredStrategy_NotOverwritten`.
- `CustomConfigInjectorTests.GeneratedCustom_DnsStrategy_Parity` — for
  `ForceIpv4Only=false`+`Ipv6Enabled=false`, generated and custom paths agree.
- `CustomConfigInjectorTests.EnsureUrltest_ExistingAutoOutbound_NoDuplicateTag` —
  fails on old code (duplicate `auto`).
- `CustomConfigInjectorTests.EnsureUrltest_ExistingUrltest_Reused`.
- `CustomConfigInjectorTests.Validate_DuplicateOutboundTag_ReportsError`.
- `VlessDeepVerifierTests.DnsTunnelEntry_ReturnsUnsupportedByVerifier` — fails on
  old code (returned `Failed`).
- `VlessDeepVerifierTests.DnsTunnelEntry_NotMarkedBlocked`.

### Verification approach

Pure config-build + verifier-result assertions using the existing test helpers
(no live sing-box, no sidecar launch). Execution in remote GitHub CI.

## 6. Affected callers / consumers + invariants

- CFG-1 consumers: `StartupPipeline.cs:996`, `HealthMonitor.cs:1222/1232/1239`,
  `StartCommand.cs:281`, `AndroidConfigBuilder.cs:290`. Invariant: generated-path
  strategy unchanged; full/exclude custom paths unchanged; user-authored strategy
  preserved.
- CFG-2 consumers: every custom-config apply that calls `EnsureUrltest`.
  Invariant: configs that already have a urltest behave identically; selector
  child references stay valid.
- PROTO-1 consumers: deep-verify result consumers (server list verdict UI,
  classifier). Invariant: AWG/xhttp/naive still return `UnsupportedByVerifier`;
  real VLESS failures still return `Failed`.

## 7. Exact expected file list

- `VPNRouter.Core/Services/CustomConfigInjector.cs` (CFG-1 DNS block, CFG-2 EnsureUrltest + Validate)
- `VPNRouter.Core/Services/VlessDeepVerifier.cs` (PROTO-1 classifier/switch)
- `VPNRouter.Tests/CustomConfigInjectorTests.cs` (or the existing injector test file — add tests)
- `VPNRouter.Tests/VlessDeepVerifierTests.cs` (or the existing verifier test file — add tests)

## 8. Non-goals

- Do NOT implement sidecar-aware dns-tunnel verification (the typed skip is the
  minimum root fix; full sidecar probing is out of scope).
- Do NOT change the generated config's DNS strategy logic (it is correct).
- Do NOT add a DNS-strategy abstraction/interface for two call sites.
- Do NOT launch sing-box or the slipstream sidecar (code-only).

## 9. Security / concurrency / data-loss / platform review

- **Security**: CFG-1 is a DNS-leak hardening (AAAA on an IPv4-only TUN). The
  fix must not overwrite a user's explicit strategy (contract). CFG-2 prevents a
  startup FATAL (availability). PROTO-1 prevents false condemnation (availability
  / correctness).
- **Concurrency**: none (pure builders / synchronous verify classification).
- **Data-loss**: none.
- **Platform**: dns-tunnel sidecar is Windows-centric, but the verifier short-
  circuit is platform-neutral typed logic.

## 10. Dependencies / overlaps

- No P1 branch touches these files → base `origin/main`.
- Independent of other R-packages. If the diff grows, split CFG-1/CFG-2 (injector)
  from PROTO-1 (verifier) into two PRs (prompt pool P04 allows this).

## 11. Remote-only verification gates

- [ ] Gate 1 — Build clean (remote CI): 0 errors.
- [ ] Gate 2 — Tests green (remote CI): new injector + verifier tests pass; existing config tests stay green.
- [ ] Gate 3 — Docs: brief Outcome filled; zone CLAUDE.md unchanged.
- [ ] Gate 4 — Self-review: static review of generated JSON shape + verifier result typing.
- [ ] Gate 5 — MCP verify: N/A (Core + tests only).
- [ ] Gate 6 — Characterization diff: N/A.

## 12. Outcome (PENDING — filled after merge)

**Status**: PENDING
**Commits**: PENDING
**Pushed**: PENDING
**Test deltas**: PENDING
**Files changed**: PENDING

**Gate results:**
- [ ] Gate 1: PENDING
- [ ] Gate 2: PENDING
- [ ] Gate 3: PENDING
- [ ] Gate 4: PENDING
- [-] Gate 5: N/A — Core + tests only
- [-] Gate 6: N/A

**Surprises encountered**: PENDING
**Follow-ups spawned**: PENDING

## 13. Rollback

`git revert <commit>` on the R02 branch, or delete
`codex/qwen-audit-r02-config-protocol-2026-07-29`. Custom configs revert to the
prior DNS-strategy/tag/verify behavior; no persistent state is written.

## 14. Self-contained copyable Qwen prompt

```text
Выполни brief plans/phase2-audit-r02-config-protocol-2026-07-29.md через Qwen
Code. IDs: CFG-1, CFG-2, PROTO-1 (все P2). Base branch: origin/main. Сначала
прочитай brief целиком, AGENTS.md, plans/CLAUDE.md и VPNRouter.Core/CLAUDE.md.
CFG-1: переиспользуй одну DNS-strategy decision (ConfigGenerator.cs:995) в
CustomConfigInjector, чтобы !Ipv6Enabled тоже давал ipv4_only на include-split
path, не перезаписывая user-authored strategy без contract. CFG-2: сделай
injected urltest tag collision-free (или переиспользуй существующий urltest) и
добавь duplicate/reserved-tag проверку в Validate. PROTO-1: добавь typed
UnsupportedByVerifier short-circuit для dns-tunnel в VlessDeepVerifier, не
маркируя unsupported-verifier результат как blocked server. Без новых
abstractions/dependencies. Напиши тесты, падающие на старом поведении. НЕ
запускай локальные build/test/sing-box/binary, не делай live мутаций. Только
чтение/поиск/редактирование и запись тестов. Commit/push/CI делает orchestrator.
Без release/merge/tag/deploy. Без emoji. Заполни Outcome шаблоном PENDING.
```
