# Phase 3 — R14 — UI error-message redaction (R13-P3 follow-up)

**Owner**: Qwen Code session
**Branch**: `codex/qwen-audit-r14-ui-error-redaction-2026-07-29`
**Base**: commit `0a5ee573` (R13 tip — `fix(security): redact App URLs and wgturn logs`).
**Dependency**: stacked on R13 PR #72 -> R06 PR #68 -> P09 PR #60.
R14 closes the R13-P3 residual: raw `ex.Message` rendered to the UI.
Do NOT rebase onto `origin/main` while R13/R06/P09 are unmerged.
**Roadmap ref**: R13 brief §R13-P3 (`plans/phase2-audit-r13-app-url-redaction-2026-07-29.md`)
**Effort**: ~30 min · **Risk**: LOW (two `ex.Message` wraps, existing pinned helper)
**Blast radius**: 2 App ViewModel partials + 1 test file · ~+4 LOC product
**Rollback**: `git revert <commit>` / delete branch

---

## 1. Verdict / severity / confidence

| ID | Verdict | Severity | Confidence |
|---|---|---|---|
| R14 (R13-P3) | CONFIRMED | P3 | High (mechanism) / Med (input reachability) |

Two UI surfaces render raw `ex.Message`. A proven producer
(`PolicyHttpClient.SendAsync` timeout) embeds the full request URI with
token in its message. Internal catches prevent propagation today; the broad
`catch (Exception ex)` blocks are a defence-in-depth gap.

## 2. Verified root cause (commit `0a5ee573`)

### Sink 1 — `MainWindowViewModel.Subscriptions.cs:231`

[FACT] `SyncSubscriptionAsync` catch:
```csharp
catch (Exception ex)
{
    _logger.Error(ex, "[VM] Subscription sync failed");
    StatusText = Strings.SyncFailed(ex.Message);   // raw ex.Message to UI
}
```
Log at :230 is safe (no URL argument). Only `StatusText` renders raw message.

### Sink 2 — `MainWindowViewModel.SimpleMode.cs:492-493`

[FACT] Smart Connect subscription-refresh catch:
```csharp
SmpErrorText = IsRussian
    ? $"Не удалось получить подписку: {ex.Message}"
    : $"Couldn't fetch the subscription: {ex.Message}";
```

### Proven exception producer

[FACT] `PolicyHttpClient.SendAsync` (`PolicyHttpClient.cs:144`):
```csharp
throw new TimeoutException(
    $"HTTP request to {request.Uri} timed out after {request.Timeout.Value.TotalMilliseconds:F0} ms.");
```
`request.Uri` = full subscription URI with path/query/token. Interpolation
calls `Uri.ToString()` — unescaped absolute URI.

[FACT] `FetchWithDiagnosticsAsync:110` catches ALL exceptions internally —
the `TimeoutException` does NOT propagate to the UI catch blocks today.
`FetchAsync` / `RefreshEntryAsync` never throw.

[INFER] Defence-in-depth: a future refactor removing the internal catch, a
new exception path in post-fetch code (`SaveSettings`, `RebuildSubscriptionPool`),
or a changed `IHttpClient` impl could propagate a message carrying URL/UUID/key.
`HttpRequestException` inner `SocketException` can also embed the hostname.

## 3. Helper selection — `CrashReporter.ScrubSecrets`

[FACT] `public static`, namespace `VPNRouter.Core.Services` (already imported
by both partials). Five compiled regexes: proxy URIs -> `scheme://[redacted]`;
HTTP(S) URLs -> `host/[redacted]` (host kept); UUIDs -> `<uuid>`; base64
(>=40) -> `<key>`; `token=` params -> `token=[REDACTED]`.

[FACT] Pinned by 14 tests in `CrashReporterScrubberTests.cs` incl.
`ScrubSecrets_LeavesShortBenignTextAlone`. Already used for exception text
at `CrashReporter.WriteReport:111` (`ScrubSecrets(ex.ToString())`).

**Why NOT `CanaryPolicy.RedactUrl`**: designed for URL-shaped strings;
mangles arbitrary text containing `/` ("Error at step 3/5" -> "Err").
`ScrubSecrets` is the correct tool for free-form text.

### UX semantics

| Input `ex.Message` | After `ScrubSecrets` |
|---|---|
| `Connection timed out` | unchanged |
| `HTTP request to https://provider.example/api/sub?token=secret timed out` | `...https://provider.example/[redacted] timed out` |
| `Failed to dial vless://uuid@194.87.222.111:443?security=reality` | `Failed to dial vless://[redacted]` |
| `add config wgturn://abc123/xyz?k=secret failed` | `add config wgturn://[redacted] failed` |

`ex.Message` is never null in .NET. No special fallback needed.

## 4. Why

Defence-in-depth: the UI boundary must scrub exception text regardless of
internal catches. Reuses the existing pinned `ScrubSecrets` — no new abstraction.

## 5. What

```diff
- StatusText = Strings.SyncFailed(ex.Message);
+ StatusText = Strings.SyncFailed(CrashReporter.ScrubSecrets(ex.Message));
```
```diff
  SmpErrorText = IsRussian
-     ? $"Не удалось получить подписку: {ex.Message}"
-     : $"Couldn't fetch the subscription: {ex.Message}";
+     ? $"Не удалось получить подписку: {CrashReporter.ScrubSecrets(ex.Message)}"
+     : $"Couldn't fetch the subscription: {CrashReporter.ScrubSecrets(ex.Message)}";
```
No new using needed (`VPNRouter.Core.Services` already in both partials).

## 6. How

1. Apply the two wraps at `Subscriptions.cs:231` and `SimpleMode.cs:492-493`.
2. Add the source-contract test. Grep: confirm no raw `ex.Message` survives
   in `StatusText`/`SmpErrorText`.

**Test**: `VPNRouter.Tests/UiErrorRedactionSourceTests.cs` (new) — one
`[Theory]`, 3 `[InlineData]` cases, **source-shape contract** in the exact
R13 `AppUrlRedactionSourceTests` pattern (read the .cs, `Assert.Contains`
wrapped form + `Assert.DoesNotContain` raw form). Cases:

1. `MainWindowViewModel.Subscriptions.cs` — wrapped
   `StatusText = Strings.SyncFailed(CrashReporter.ScrubSecrets(ex.Message));`
   / raw `StatusText = Strings.SyncFailed(ex.Message);`
2. `MainWindowViewModel.SimpleMode.cs` (RU) — wrapped
   `$"Не удалось получить подписку: {CrashReporter.ScrubSecrets(ex.Message)}"`
   / raw `$"Не удалось получить подписку: {ex.Message}"`
3. `MainWindowViewModel.SimpleMode.cs` (EN) — wrapped
   `$"Couldn't fetch the subscription: {CrashReporter.ScrubSecrets(ex.Message)}"`
   / raw `$"Couldn't fetch the subscription: {ex.Message}"`

Each raw string is NOT a substring of its wrapped form (`SyncFailed(` / `{`
is followed by `CrashReporter`, not `ex.Message`), so `DoesNotContain` is
meaningful. Reuses R13's `FindRepoFile` walker; no using needed
(`ImplicitUsings` + global `Using Include="Xunit"`). No VM/network/filesystem.

**Why source-contract, not a direct `ScrubSecrets` Theory**: a pure
benign/http/proxy/wgturn Theory would duplicate the 14 cases already pinned
in `CrashReporterScrubberTests.cs` and would NOT prove the two UI sinks call
the scrubber. The source contract proves exactly what this audit found
missing — the trust-boundary sinks invoke `ScrubSecrets(ex.Message)` and the
raw forms are gone; scrubber *output* semantics stay pinned by the existing
behavior tests (Gate 2).

## 7. Exact expected file list

Product:
- `VPNRouter.App/ViewModels/MainWindowViewModel.Subscriptions.cs` (:231)
- `VPNRouter.App/ViewModels/MainWindowViewModel.SimpleMode.cs` (:492-493)

Tests:
- `VPNRouter.Tests/UiErrorRedactionSourceTests.cs` (new — 1 `[Theory]` x 3
  source-shape cases; mirrors R13 `AppUrlRedactionSourceTests`)

## 8. Non-goals

- Do NOT modify `CrashReporter.ScrubSecrets` or `CanaryPolicy.RedactUrl`.
- Do NOT touch the ~40 other `ex.Message` UI sinks in `MainWindowViewModel.cs`
  (zapret, tgproxy, rules, VPN start) — different exception producers, no
  subscription URL in message. Separate audit if needed.
- Do NOT touch `FreeConfigsPageViewModel.cs` sinks — TCP probes / GeoIP domain.
- Do NOT re-redact R13 log sinks (already fixed in base).
- Do NOT add a new redactor / abstraction / dependency.

## 9. Security / UX / platform review

- **Security**: scrubs tokens/proxy URIs/UUIDs/keys from user-visible error
  text. Threat model: shoulder-surfing / screenshot / remote-desktop. Host
  kept for diagnostics; path/query/token dropped.
- **UX**: benign messages pass unchanged; only embedded secrets redacted.
- **Platform**: pure managed regex, identical on Win/Mac/Linux/Android.
- **Concurrency**: none new.

## 10. Remote-only verification gates

- [ ] Gate 1 — Build clean (remote CI): 0 errors.
- [ ] Gate 2 — Tests green: new `UiErrorRedactionSourceTests` (3 source-shape
      cases) passes; `CrashReporterScrubberTests` (benign/http/proxy/wgturn/
      uuid/key/token behavior) + `AppUrlRedactionSourceTests` + `CanaryPolicyTests`
      stay green. Source contract + existing behavior tests = full proof.
- [ ] Gate 3 — Docs: brief Outcome filled; zone CLAUDE.md unchanged.
- [ ] Gate 4 — Self-review: grep two sites, no raw `ex.Message` survives.
- [-] Gate 5 — MCP verify: N/A (error-text content, no layout change).
- [-] Gate 6 — Characterization diff: N/A (no public surface change).

## 11. Outcome (PENDING)

**Status**: PENDING · **Commits**: PENDING · **Pushed**: PENDING
**Test deltas**: +3 cases (UiErrorRedactionSourceTests, source-shape)
**Files**: Subscriptions.cs (1 line), SimpleMode.cs (2 lines), UiErrorRedactionSourceTests.cs (new)
**Gates**: 1-4 PENDING, 5-6 N/A
**Surprises**: PENDING · **Follow-ups**: PENDING

## 12. Rollback

`git revert <commit>` or delete branch. R14 is additive: reverting restores
raw `ex.Message`; R13/R06/P09 remain intact.

## 13. Copyable Qwen prompt

```text
Выполни brief plans/phase3-audit-r14-ui-error-redaction-2026-07-29.md.
R14 = R13-P3 follow-up: raw ex.Message в UI. Base 0a5ee573 (R13, PR #72);
зависимость R06 PR #68, P09 PR #60. Прочитай brief, AGENTS.md, plans/CLAUDE.md,
VPNRouter.Core/CLAUDE.md, VPNRouter.App/CLAUDE.md, VPNRouter.Tests/CLAUDE.md.
Оберни ex.Message в CrashReporter.ScrubSecrets(...) в двух местах:
MainWindowViewModel.Subscriptions.cs:231 и SimpleMode.cs:492-493.
НЕ используй CanaryPolicy.RedactUrl для текста. Без нового helper.
Тест: UiErrorRedactionSourceTests.cs — source-shape contract точно по образцу
R13 AppUrlRedactionSourceTests (read .cs, Assert.Contains wrapped +
Assert.DoesNotContain raw), один [Theory] x 3 InlineData (Subscriptions.cs
StatusText; SimpleMode.cs RU + EN) — точные wrapped/raw строки в brief §6.
НЕ дублируй behavior-кейсы CrashReporterScrubberTests (benign/http/proxy/wgturn):
они уже pin'ят output scrubber'а; новый тест доказывает ТОЛЬКО что оба UI sink'а
вызывают scrubber и raw формы исчезли. Reuse FindRepoFile из R13.
НЕ запускай build/test/restore/app/binary/VM/WinRM/MCP. Только чтение/
редактирование. Commit/push делает orchestrator. Без emoji. Outcome PENDING.
```
