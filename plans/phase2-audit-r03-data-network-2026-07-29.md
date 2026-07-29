# Phase 2 — R03 — Settings/free-config data safety + subscription response bound

**Owner**: Qwen Code session (code-only)
**Branch**: `codex/qwen-audit-r03-data-network-2026-07-29`
**Base**: `origin/main` (verified: no P1 branch touches `SettingsMigrator.cs`, `FreeConfigs/FreeConfigAggregator.cs`, `FreeConfigs/FreeConfigCache.cs`, or `PolicyHttpClient.cs`). Caution §10: rebase onto merged P09 before push if SEC-1 is in flight (SubscriptionFetcher.cs intake proximity).
**Roadmap ref**: `plans/qwen-remaining-remediation-index-2026-07-29.md` (R03); prompt pool P05
**IDs**: DATA-3, DATA-4, DATA-6, NET-1
**Effort**: ~3 h
**Risk**: MEDIUM (DATA-3 silently rewrites a user value; DATA-4 loses verified status; DATA-6 loses a regenerable cache; NET-1 is a bounded DoS)
**Blast radius**: `VPNRouter.Core/Services/SettingsMigrator.cs`, `VPNRouter.Core/Services/FreeConfigs/FreeConfigAggregator.cs`, `VPNRouter.Core/Services/FreeConfigs/FreeConfigCache.cs`, `VPNRouter.Core/Services/PolicyHttpClient.cs`, tests · ~+130 LOC · runtime: migration, free-config merge/cache, subscription fetch memory
**Rollback**: `git revert <commit>` / delete branch

---

## 1. Final P00 verdict / severity / confidence / corrected scope

| ID | Orig | Verdict | Final | Conf |
|---|---|---|---|---|
| DATA-3 | P2 | CONFIRMED | P2 | High |
| DATA-4 | P2 | CONFIRMED | P2 | High |
| DATA-6 | P2 | CONFIRMED | P2 | High |
| NET-1 | P1 | CONFIRMED | P2 | High |

Corrected scope:

- **NET-1 downgraded P1 → P2**: it is a DoS that requires the user to subscribe
  to an attacker-controlled URL, with a weak partial time bound (15 s timeout) —
  not an unauthenticated remote OOM.
- DATA-3/DATA-4/DATA-6 confirmed at P2 as written.

## 2. Verified current root cause (commit `b39a28c3`)

### DATA-3 — MTU migration rewrites explicit 1280

`VPNRouter.Core/Services/SettingsMigrator.cs` `Migrate_7_to_8` (verified
`:694-708`):

```csharp
if (s.Tun.Mtu == 1280 || s.Tun.Mtu == 1500 || s.Tun.Mtu <= 0 || s.Tun.Mtu > 1500)
{
    var old = s.Tun.Mtu;
    s.Tun.Mtu = TunSettings.DefaultMtu;   // 1420 (TunSettings.cs:8)
    ...
}
```

The condition is purely value-based with no "was-custom-set" flag, so an
explicitly user-selected MTU 1280 is indistinguishable from a prior-migration
default and is silently rewritten to 1420 — contradicting the inline
preserve-custom promise (`:692,:705`). 1280 is a legitimate deliberate value
(the v6→v7 step at `:676-683` calls it the IPv6 minimum MTU).

### DATA-4 — duplicate IDs abort the merge

`VPNRouter.Core/Services/FreeConfigs/FreeConfigAggregator.cs` `MergeWithCache`
(verified `:166-195`):

```csharp
var existingById = existing.Configs.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);  // ~:170
...
var byId = fresh.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);                       // ~:187
PreservePreviousValidation(byId, fresh, existing.Configs, DateTime.UtcNow);
```

Both `ToDictionary` calls throw `ArgumentException` on duplicate keys. The pool
path passes entries non-deduped (`FreeConfigPoolFetcher.cs:231-262` reads `Id`
verbatim, only a non-empty check at `:257`), so a duplicate `id` in pool.json
makes `:187` throw → `PreservePreviousValidation` is skipped → previously-
Verified entries the new pool dropped are lost (the regression v2.28.5-r2
prevents). A hand-edited/corrupted cache triggers the same via `:170`. The whole
body is in `try/catch` returning `fresh` on throw. The fallback path DOES dedupe
(`FreeConfigAggregator.cs:129`).

### DATA-6 — non-atomic cache replace

`VPNRouter.Core/Services/FreeConfigs/FreeConfigCache.cs` `Save` (verified
`:118-137`):

```csharp
var tmp = _path + ".tmp";
...
File.WriteAllText(tmp, json);
if (File.Exists(_path)) File.Delete(_path);   // :131
File.Move(tmp, _path);                         // :132
```

A crash between `:131` (delete) and `:132` (move) leaves `_path` deleted and
`tmp` un-moved → cache lost, despite the `:113-114` comment claiming "atomically".
The correct atomic overwrite-move already exists in the same component family at
`FreeConfigPoolFetcher.cs:140` (`File.Move(tmp, _cachePath, overwrite:true)`).
Impact is a regenerable cache (verified-status results lost, re-test required),
not credentials → P2.

### NET-1 — unbounded subscription response

`VPNRouter.Core/Services/PolicyHttpClient.cs` (verified `:112-119`):

```csharp
httpResponse = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, perRequestCts.Token)...;
var body = await httpResponse.Content.ReadAsByteArrayAsync(perRequestCts.Token)...;
```

Handler `:69-71` sets `AutomaticDecompression = DecompressionMethods.All`; there
are 0 `MaxResponseContentBufferSize` matches in `VPNRouter.Core/Services`. An
untrusted subscription response is fully buffered into a `byte[]` with no byte
limit (default ~2 GB ceiling) and decompressed in-memory (decompression-bomb
surface); the only bound is the 15 s per-request timeout. Subscription intake:
`SubscriptionFetcher.cs:66-69` (user-provided URL), consumed `:85`. Bounded
contrast: `FreeConfigPoolFetcher.cs:37-38,115,131,179-181`.

## 3. Why

DATA-3 violates the documented preserve-custom contract for a legitimate MTU.
DATA-4 turns a duplicate remote ID into silent loss of verified-status
preservation. DATA-6 has a crash window that loses a cache despite claiming
atomicity. NET-1 lets a malicious subscription provider exhaust process memory.
Each has an existing in-repo pattern that fixes it minimally.

## 4. What

1. **DATA-3**: preserve an explicitly selected MTU 1280. Minimum options (pick
   the smallest that honors the contract): (a) stop treating 1280 as a stale
   default (remove `== 1280` from the rewrite condition, keeping `<= 0 || > 1500`
   and possibly `== 1500`), OR (b) introduce a custom-set marker if one is
   available in the schema. Do NOT rewrite a value the user chose.
2. **DATA-4**: dedupe remote IDs before `ToDictionary` (reuse the existing
   `byId.ContainsKey`-style defense seen at `FreeConfigAggregator.cs:129`), or use
   a duplicate-tolerant lookup. Define a deterministic first/last-wins policy and
   log the dropped count (NOT secrets).
3. **DATA-6**: replace delete-then-move with `File.Move(tmp, _path, overwrite:true)`
   (reuse `FreeConfigPoolFetcher.cs:140` pattern).
4. **NET-1**: stream with a fixed maximum response size (and an expanded-size
   guard) before decoding, mirroring `FreeConfigPoolFetcher`'s bounded
   decompression. Do not break base64/plain subscription detection.

```diff
- File.WriteAllText(tmp, json);
- if (File.Exists(_path)) File.Delete(_path);
- File.Move(tmp, _path);
+ File.WriteAllText(tmp, json);
+ File.Move(tmp, _path, overwrite: true);
```

## 5. How (ordered minimal steps)

1. DATA-6 first (smallest): swap to `File.Move(..., overwrite:true)`; update the
   misleading "atomically" comment if needed.
2. DATA-4: read `MergeWithCache` + the fallback dedupe (`:129`); apply the same
   dedupe before both `ToDictionary` calls; log dropped-duplicate count.
3. DATA-3: read `Migrate_7_to_8` + the v6→v7 step; choose the minimal contract-
   honoring change (prefer removing `== 1280` from the rewrite set unless a
   custom-set marker already exists).
4. NET-1: read `PolicyHttpClient.SendAsync` + `FreeConfigPoolFetcher` bounded
   read; add a max-bytes streaming guard (count bytes as they are read; abort past
   the cap) and an expanded-size guard for decompression.
5. Add tests (below). Static review for secret leakage in new log lines.

### Tests written

- `SettingsMigratorTests.Migrate7To8_Explicit1280_Preserved` — fails on old code.
- `SettingsMigratorTests.Migrate7To8_InvalidValues_NormalizedToDefault` (0, >1500).
- `FreeConfigAggregatorTests.MergeWithCache_DuplicateFreshIds_DoesNotThrow` — fails
  on old code (ArgumentException).
- `FreeConfigAggregatorTests.MergeWithCache_DuplicateIds_PreservesValidation`.
- `FreeConfigAggregatorTests.MergeWithCache_DuplicateCacheIds_DoesNotThrow`.
- `FreeConfigCacheTests.Save_InterruptedReplace_LeavesReadablePriorCache` — simulate
  the crash window (e.g. inject a failing move / assert no delete-before-move).
- `PolicyHttpClientTests.OversizedResponse_AbortsPastLimit` — fake handler returns
  a body larger than the cap; assert abort before full allocation.
- `PolicyHttpClientTests.CompressedExpansionBomb_Bounded` — small compressed body
  expanding past the expanded cap; assert bounded.
- `PolicyHttpClientTests.NormalSubscription_StillParses`.

### Verification approach

Temp-dir + fake `HttpMessageHandler` tests (no live network). Execution in remote
GitHub CI.

## 6. Affected callers / consumers + invariants

- DATA-3 consumers: every settings load that runs migrations. Invariant: invalid
  MTUs (<=0, >1500) still normalize to 1420; only the explicit-1280 case changes.
- DATA-4 consumers: `FetchPoolAsync` → free-config UI list. Invariant: verified-
  status preservation (v2.28.5-r2) survives; dedupe is deterministic.
- DATA-6 consumers: cache load/save round-trip. Invariant: schema stamp
  (`CurrentSchemaVersion`) still written; load-after-save round-trips.
- NET-1 consumers: `SubscriptionFetcher` (base64/plain detection), any other
  `PolicyHttpClient` caller. Invariant: normal subscriptions parse unchanged; the
  cap is a configurable constant, not a magic number.

## 7. Exact expected file list

- `VPNRouter.Core/Services/SettingsMigrator.cs` (DATA-3)
- `VPNRouter.Core/Services/FreeConfigs/FreeConfigAggregator.cs` (DATA-4)
- `VPNRouter.Core/Services/FreeConfigs/FreeConfigCache.cs` (DATA-6)
- `VPNRouter.Core/Services/PolicyHttpClient.cs` (NET-1)
- `VPNRouter.Tests/SettingsMigratorTests.cs` (or existing migration test file)
- `VPNRouter.Tests/FreeConfigAggregatorTests.cs` (or existing aggregator test file)
- `VPNRouter.Tests/FreeConfigCacheTests.cs` (or existing cache test file)
- `VPNRouter.Tests/PolicyHttpClientTests.cs` (or existing HTTP test file)

## 8. Non-goals

- Do NOT add a new filesystem/atomic-write abstraction (reuse `File.Move` overload).
- Do NOT add a new HTTP abstraction or `IHttpClient` factory (reuse the bounded-
  read pattern inline).
- Do NOT change the free-config fallback path (it already dedupes).
- Do NOT touch `SubscriptionFetcher` dedupe identity (DATA-5 is REFUTED — out of scope).
- Do NOT touch JSON MaxDepth (DATA-2 is REFUTED — out of scope).

## 9. Security / concurrency / data-loss / platform review

- **Data-loss**: DATA-3 (user value), DATA-4 (verified status), DATA-6 (cache)
  are all data-preservation fixes. DATA-6's atomic replace removes the only crash
  window.
- **Security**: NET-1 is a memory-exhaustion DoS bound; the expanded-size guard
  covers decompression bombs. New log lines must NOT log subscription URLs/tokens
  (SEC-1 territory) — log counts only.
- **Concurrency**: `MergeWithCache` is called per-refresh; dedupe must be
  deterministic under the existing single-flight refresh.
- **Platform**: all four are platform-neutral Core logic.

## 10. Dependencies / overlaps

- No P1 branch touches these four files → base `origin/main`.
- **Caution (NET-1 vs P09/SEC-1)**: SEC-1 (P1 wave, branch
  `codex/qwen-audit-p09-secrets-acl-diagnostics-2026-07-29`) edits
  `SubscriptionFetcher.cs` (URL redaction). NET-1's fix is in `PolicyHttpClient.cs`
  (different file), but both sit in the subscription intake. If P09 is merged
  first, rebase R03 onto it before pushing to avoid a textual conflict near
  `SubscriptionFetcher.cs:66-85`.
- DATA-6/DATA-4 share the free-config component family; keep their commits
  adjacent for reviewability.

## 11. Remote-only verification gates

- [ ] Gate 1 — Build clean (remote CI): 0 errors.
- [ ] Gate 2 — Tests green (remote CI): new migration/aggregator/cache/HTTP tests pass; AGENTS.md regression filters (`VlessServersResolverTests|ConfigGeneratorEmptyServersGuardTests|FreeConfigAggregatorPreserveTests`) stay green.
- [ ] Gate 3 — Docs: brief Outcome filled; zone CLAUDE.md unchanged.
- [ ] Gate 4 — Self-review: secret-scan new log lines (static).
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

`git revert <commit>` on the R03 branch, or delete
`codex/qwen-audit-r03-data-network-2026-07-29`. Each fix is independently
revertable; no schema version bump is required (DATA-3 changes migration
behavior, not schema).

## 14. Self-contained copyable Qwen prompt

```text
Выполни brief plans/phase2-audit-r03-data-network-2026-07-29.md через Qwen Code.
IDs: DATA-3, DATA-4, DATA-6, NET-1 (все P2). Base branch: origin/main (см.
caution: при наличии merged P09/SEC-1 сделай rebase до push). Сначала прочитай
brief целиком, AGENTS.md, plans/CLAUDE.md и VPNRouter.Core/CLAUDE.md. DATA-3:
сохрани explicitly-selected MTU 1280 в SettingsMigrator (не переписывай
custom-set значение). DATA-4: дедуплицируй remote IDs до ToDictionary в
FreeConfigAggregator (переиспользуй byId.ContainsKey защиту). DATA-6: замени
delete-then-move на File.Move(tmp, path, overwrite:true) в FreeConfigCache
(переиспользуй паттерн из FreeConfigPoolFetcher.cs:140). NET-1: ограничь
response size в PolicyHttpClient bounded streaming read (mirror
FreeConfigPoolFetcher bounded decompression). Без новых filesystem/HTTP
abstractions. Напиши тесты, падающие на старом поведении. НЕ запускай локальные
build/test/app/binary, не делай live мутаций. Только чтение/поиск/редактирование
и запись тестов. Commit/push/CI делает orchestrator. Без release/merge/tag/
deploy. Без emoji. Заполни Outcome шаблоном PENDING.
```
