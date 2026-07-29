# Phase 2 — R01 — Firewall IPv6 wiring + kill-switch test coverage

**Owner**: Qwen Code session (code-only)
**Branch**: `codex/qwen-audit-r01-firewall-wiring-2026-07-29`
**Base**: `origin/main` (verified: no P1 branch touches the firewall managers or `StartupPipelineTests.cs`)
**Roadmap ref**: `plans/qwen-remaining-remediation-index-2026-07-29.md` (R01); prompt pool P03 + P11
**IDs**: FW-1, FW-2, TEST-1
**Effort**: ~2-3 h
**Risk**: MEDIUM (kill-switch correctness; fail-closed default must be preserved)
**Blast radius**: `VPNRouter.Core/Platform/Linux/LinuxFirewallManager.cs`, `VPNRouter.Core/Platform/macOS/MacFirewallManager.cs`, `VPNRouter.Tests/LinuxFirewallManagerTests.cs`, `VPNRouter.Tests/MacFirewallManagerTests.cs`, `VPNRouter.Tests/StartupPipelineTests.cs` · ~+120 LOC · runtime: kill-switch ruleset content on Linux/macOS
**Rollback**: `git revert <commit>` / delete branch

---

## 1. Final P00 verdict / severity / confidence / corrected scope

| ID | Orig | Verdict | Final | Conf |
|---|---|---|---|---|
| FW-1 | P1 | PARTIALLY_CONFIRMED | P2 | Med-High |
| FW-2 | P1 | PARTIALLY_CONFIRMED | P2 | Med-High |
| TEST-1 | P2 | CONFIRMED | P2 | High |

Corrected scope (from P00):

- **FW-1 / FW-2 mechanism is real** (an IPv6 server literal is dropped from the
  allow-list / emitted as a malformed `inet` rule), **but impact was overstated**:
  - Reachability is limited to **bare IPv6 literals** (custom JSON, or the AWG
    parser which strips brackets at `ServerUriParser.cs:493-498`). The dominant
    VLESS-URI path yields a **bracketed** host that `IPAddress.TryParse` rejects
    into the hostname branch (`VlessUriParser.cs:44` uses `Uri.Host`) — a
    different failure shape, not this one.
  - The "until the nftables table is manually removed" claim is **REFUTED** by the
    automatic marker-gated orphan sweep on next launch
    (`LinuxFirewallManager.cs:295-322`, `:157-175`; macOS `:210-258`, `:438-477`).
- **TEST-1**: the regression test claimed in the `StartupPipelineTests.cs` header
  comment does not exist; no test exercises the pipeline's
  `RoutingMode -> isFullTunnel -> CreateBlockRules` wiring.

## 2. Verified current root cause (commit `b39a28c3`)

### FW-1 — Linux

`VPNRouter.Core/Platform/Linux/LinuxFirewallManager.cs`:

- `:226` `ReadServerIps` accepts a bare IPv6 via `IPAddress.TryParse(s, out _)`
  and adds it to `ips`.
- `BuildRuleset` (verified at `:197-209`):
  ```csharp
  sb.AppendLine($"add chain inet {TableName} output {{ type filter hook output priority 0 ; policy drop ; }}");
  ...
  var v4 = serverIps.Where(ip => !ip.Contains(':')).ToList();   // :205 strips IPv6
  if (v4.Count > 0)
      sb.AppendLine($"add rule inet {TableName} output ip daddr {{ {string.Join(", ", v4)} }} accept");
  ```
- The table family is `inet` with output `policy drop` (`:199-201`), so all
  non-loopback IPv6 is dropped. An IPv6-only server is never allow-listed and
  cannot reconnect under an armed kill-switch within the same session.
- Load is `nft -f` (`:135`); fail-open on load error at `:144-150`.

### FW-2 — macOS

`VPNRouter.Core/Platform/macOS/MacFirewallManager.cs`:

- `:359` `ReadServerIps` accepts a bare IPv6.
- `BuildRuleset` (verified at `:331-341`):
  ```csharp
  foreach (var ip in serverIps)
      sb.AppendLine($"pass out quick inet from any to {ip}");   // :339-340 — always `inet`
  ```
- Every server IP is emitted as an `inet` (IPv4-family) PF rule, so an IPv6
  literal produces a malformed rule. If pfctl rejects the atomic ruleset load
  (`pfctl -a Anchor -f tmp`, `:169`), `EnableBlockRules` logs "NOT blocking",
  calls `ReleaseEnable()`, and returns (`:177-184`) → the kill-switch **fails
  open**.

### TEST-1 — missing pipeline test

`VPNRouter.Tests/StartupPipelineTests.cs`:

- `:33-34` header comment CLAIMS a `SetupFirewall_BlockOnFail_CreatesRules` test.
- `:277` the only firewall test is `SetupFirewall_NoBlockOnFail_SkipsRuleCreation`
  — it runs **HotReload** mode (skips phase 6) and asserts `host.SetFirewall` is
  null.
- `:469-471` `TestStartupHost.FirewallFactory` THROWS if phase 6 runs, so no test
  in this file can exercise the wiring.
- Production wiring: `VPNRouter.Core/Services/StartupPipeline.cs:1090-1092`
  derives `isFullTunnel` from `settings.App.RoutingMode` and calls
  `firewall.CreateBlockRules(scanResult.ProcessNames, isFullTunnel)`.

## 3. Why

A user who configures an IPv6-only VPN server (bare literal) on Linux or macOS
gets a kill-switch that either blocks the reconnect (Linux) or fails open
(macOS), defeating the purpose of BlockOnVpnFail. The defect is latent (narrow
reachability) but real, and there is no test guarding the pipeline's
full/split kill-switch wiring — so a future regression would ship silently.

## 4. What

1. **FW-1 (Linux)**: in `BuildRuleset`, additionally emit an `ip6 daddr` accept
   rule for parsed IPv6 server addresses (family split: `ip.Contains(':')`).
   Keep the `inet` table + `policy drop`; do NOT weaken the default drop; do NOT
   turn DNS/hostname resolution failure into allow-all.
2. **FW-2 (macOS)**: in `BuildRuleset`, emit `inet6` rules for IPv6 addresses and
   `inet` rules for IPv4 (family split). Keep the atomic anchor load. Improve the
   PF-load failure log to distinguish "malformed ruleset" from other failures.
3. **TEST-1**: add an executable pipeline-level regression test that runs ColdStart
   + BlockOnVpnFail for both `RoutingMode` values and asserts the correct
   `isFullTunnel` reaches `CreateBlockRules`. Fix the misleading header comment.

```diff
- var v4 = serverIps.Where(ip => !ip.Contains(':')).ToList();
- if (v4.Count > 0)
-     sb.AppendLine($"add rule inet {TableName} output ip daddr {{ {string.Join(", ", v4)} }} accept");
+ var v4 = serverIps.Where(ip => !ip.Contains(':')).ToList();
+ var v6 = serverIps.Where(ip => ip.Contains(':')).ToList();
+ if (v4.Count > 0)
+     sb.AppendLine($"add rule inet {TableName} output ip daddr {{ {string.Join(", ", v4)} }} accept");
+ if (v6.Count > 0)
+     sb.AppendLine($"add rule inet {TableName} output ip6 daddr {{ {string.Join(", ", v6)} }} accept");
```

```diff
- foreach (var ip in serverIps)
-     sb.AppendLine($"pass out quick inet from any to {ip}");
+ foreach (var ip in serverIps)
+ {
+     var family = ip.Contains(':') ? "inet6" : "inet";
+     sb.AppendLine($"pass out quick {family} from any to {ip}");
+ }
```

## 5. How (ordered minimal steps)

1. Read `LinuxFirewallManager.cs` and `MacFirewallManager.cs` `BuildRuleset` +
   `ReadServerIps` fully; confirm family-split point.
2. Linux: add the `ip6 daddr` rule for the IPv6 subset. Reuse the existing
   `string.Join` formatting; no new helper.
3. macOS: switch the per-IP loop to a family-selected `inet`/`inet6` keyword.
4. macOS: extend the PF-load failure log message (no behavior change beyond log
   clarity).
5. Normalize/reject bracketed IPv6 BEFORE the ruleset only if it reaches
   `ReadServerIps` bracketed — verify via `ServerUriParser`/current.json whether
   bracketed literals are possible; if `IPAddress.TryParse` already rejects them
   into the hostname branch, document that and do not add speculative parsing.
6. TEST-1: extend `TestStartupHost` so `FirewallFactory` returns a capturing fake
   (records `isFullTunnel`) instead of throwing; add two `[Fact]`s (split → false,
   full → true) running ColdStart + BlockOnVpnFail. Correct the `:33-34` comment.
7. Add pure-builder IPv6 tests to `LinuxFirewallManagerTests.cs` and
   `MacFirewallManagerTests.cs`.
8. Static review of nft/PF rule strings (NO local `nft`/`pfctl` execution).

### Tests written

- `LinuxFirewallManagerTests.BuildRuleset_Ipv4Only_Unchanged` — IPv4-only output
  identical to current (guards against accidental family regression).
- `LinuxFirewallManagerTests.BuildRuleset_Ipv6Only_EmitsIp6Daddr` — IPv6-only
  server gets an `ip6 daddr { ... } accept` rule.
- `LinuxFirewallManagerTests.BuildRuleset_MixedFamily_EmitsBoth` — mixed input
  contains both `ip daddr` and `ip6 daddr`.
- `MacFirewallManagerTests.BuildRuleset_Ipv6_UsesInet6` — IPv6 uses `inet6`, not
  `inet`.
- `MacFirewallManagerTests.BuildRuleset_MixedFamily_BothFamilies` — mixed-family
  ruleset contains both `inet` and `inet6` lines.
- `StartupPipelineTests.SetupFirewall_BlockOnFail_SplitRouting_PassesIsFullTunnelFalse`
  — fails on old code (no test existed; fake factory threw).
- `StartupPipelineTests.SetupFirewall_BlockOnFail_FullRouting_PassesIsFullTunnelTrue`.

### Verification approach

Pure-builder string assertions + pipeline fake-capture. nft/PF syntax validated
by static inspection only. Actual execution happens in remote GitHub CI after the
orchestrator pushes.

## 6. Affected callers / consumers + invariants

- `BuildRuleset` callers: `EnableBlockRules` (Linux `:135`, macOS `:169`) and the
  existing builder tests. Invariant: IPv4-only ruleset output is byte-identical to
  today.
- `ReadServerIps` callers: ruleset build + cleanup sweep. Invariant: hostname
  resolution path (resolve-NOW-while-healthy) is unchanged; resolution failure
  must NOT become allow-all.
- `StartupPipeline.cs:1090-1092` consumers: `CreateBlockRules(processNames,
  isFullTunnel)` on every platform firewall impl. Invariant: `isFullTunnel`
  derivation from `RoutingMode` is unchanged; only test coverage is added.
- Orphan-sweep cleanup (Linux `:295-322`, macOS `:210-258`) must remain intact —
  this is what refutes the "manual removal" claim.

## 7. Exact expected file list

- `VPNRouter.Core/Platform/Linux/LinuxFirewallManager.cs` (edit `BuildRuleset`)
- `VPNRouter.Core/Platform/macOS/MacFirewallManager.cs` (edit `BuildRuleset` + log)
- `VPNRouter.Tests/LinuxFirewallManagerTests.cs` (add IPv6 tests)
- `VPNRouter.Tests/MacFirewallManagerTests.cs` (add inet6 tests)
- `VPNRouter.Tests/StartupPipelineTests.cs` (capturing fake factory + 2 tests + comment fix)

## 8. Non-goals

- Do NOT change the default-drop policy or the `inet` table family.
- Do NOT add a generic IP-family abstraction / helper for two call sites.
- Do NOT touch the Windows firewall (netsh) path.
- Do NOT attempt to fix the bracketed-VLESS-IPv6 hostname shape here (different
  failure shape; out of scope unless proven to reach `ReadServerIps` bracketed).
- Do NOT apply nftables/PF anywhere (code-only).

## 9. Security / concurrency / data-loss / platform review

- **Security**: this is a fail-closed correctness fix. The dominant risk is
  accidentally weakening the default drop or allowing IPv6 globally — the fix
  emits targeted `ip6 daddr`/`inet6` accept rules for server IPs ONLY. Never emit
  a blanket `ip6 ... accept`.
- **Platform**: nft `ip6 daddr` is valid inside an `inet` table; PF `inet6` is the
  correct family keyword. Verify rule strings by static inspection against nft/PF
  grammar; do not execute.
- **Concurrency**: none (ruleset build is pure).
- **Data-loss**: none.

## 10. Dependencies / overlaps

- No P1 branch touches these files → base `origin/main`.
- Coordinates with the prompt pool P11 cross-cut matrix (TEST-1 is the
  firewall-wiring entry). No other R-package touches the firewall managers.

## 11. Remote-only verification gates

- [ ] Gate 1 — Build clean (remote CI): `dotnet build VPNRouter.sln -c Release` → 0 errors.
- [ ] Gate 2 — Tests green (remote CI): new builder + pipeline tests pass; existing firewall tests stay green.
- [ ] Gate 3 — Docs: brief Outcome filled; zone CLAUDE.md unchanged (no architecture change).
- [ ] Gate 4 — Self-review: static nft/PF rule-string review (security-relevant).
- [ ] Gate 5 — MCP verify: N/A (no UI surface; Core + tests only).
- [ ] Gate 6 — Characterization diff: N/A (not a god-file split), but IPv4-only ruleset must be byte-identical.

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
- [-] Gate 6: N/A — not a god-file split

**Surprises encountered**: PENDING
**Follow-ups spawned**: PENDING

## 13. Rollback

`git revert <commit>` on the R01 branch, or delete
`codex/qwen-audit-r01-firewall-wiring-2026-07-29`. The kill-switch reverts to
IPv4-only allow-listing (the prior behavior); no persistent state is written.

## 14. Self-contained copyable Qwen prompt

```text
Выполни brief plans/phase2-audit-r01-firewall-wiring-2026-07-29.md через Qwen
Code. IDs: FW-1, FW-2, TEST-1 (все P2). Base branch: origin/main. Сначала
прочитай brief целиком, AGENTS.md, plans/CLAUDE.md, VPNRouter.Core/CLAUDE.md и
VPNRouter.Tests/CLAUDE.md. Исправь IPv6 family mismatch только в pure ruleset
builders (LinuxFirewallManager.BuildRuleset -> ip6 daddr; MacFirewallManager
BuildRuleset -> inet6), не ослабляя default drop и не превращая hostname
resolution failure в allow-all; добавь executable pipeline-level regression test
для RoutingMode -> isFullTunnel -> CreateBlockRules (TEST-1). Переиспользуй
существующие helpers; без speculative abstractions. Напиши тесты, которые падают
на старом поведении. НЕ запускай локальные build/test/app/binary/service/
installer, не применяй nftables/PF нигде, не скачивай binary, не делай
VM/WinRM/ADB/MCP/live мутаций. Только чтение/поиск/редактирование кода и запись
тестов. Commit/push/CI делает orchestrator. Без release/merge/tag/deploy. Без
emoji. Подготовь diff и заполни секцию Outcome шаблоном PENDING.
```
