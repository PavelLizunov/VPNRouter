# Phase 2 — R06 — wgturn argument injection + crash-log bounded tail

**Owner**: Qwen Code session (code-only)
**Branch**: `codex/qwen-audit-r06-security-diagnostics-2026-07-29`
**Base**: `codex/qwen-audit-p09-secrets-acl-diagnostics-2026-07-29` (MANDATED: OBS-2 shares `VPNRouter.Core/Services/CrashReporter.cs`; P09 modified that file +7 for OBS-1/SEC-1/SEC-2. Verified via `git diff --stat origin/main...codex/qwen-audit-p09-secrets-acl-diagnostics-2026-07-29`.)
**Roadmap ref**: `plans/qwen-remaining-remediation-index-2026-07-29.md` (R06); prompt pool P09
**IDs**: SEC-3, OBS-2
**Effort**: ~1.5 h
**Risk**: MEDIUM (SEC-3 is argument injection; OBS-2 is an OOM in the crash path)
**Blast radius**: `VPNRouter.Core/Services/EmergencyChannel/EmergencyChannelManager.cs`, `VPNRouter.Core/Services/CrashReporter.cs`, tests · ~+70 LOC · runtime: wgturn-cli launch args + crash-report log tail memory
**Rollback**: `git revert <commit>` / delete branch

---

## 1. Final P00 verdict / severity / confidence / corrected scope

| ID | Orig | Verdict | Final | Conf |
|---|---|---|---|---|
| SEC-3 | P2 | CONFIRMED | P2 | High (mech) / Med (input) |
| OBS-2 | P2 | PARTIALLY_CONFIRMED | P2 | High |

Corrected scope:

- **SEC-3**: mechanism confirmed; input confidence Medium because `WgturnUrl`/
  `VkLink` are normally the user's own config (weak attacker model) — the boundary
  is crossed only if the value comes from an untrusted shared/imported profile.
- **OBS-2 PARTIALLY_CONFIRMED**: the substantive claim holds at
  `CrashReporter.cs:131` ONLY. The co-cited `DiagnosticsExporter.TailLines` is the
  BOUNDED implementation (verified at `Services/Diagnostics/DiagnosticsExporter.cs:527`,
  seeks from EOF, capped at `MaxTailReadBytes` 12 MB, comment "audit MEDIUM,
  2026-06-02") — that sub-citation is WRONG/stale. DiagnosticsExporter is the fixed
  path, NOT the vulnerable one. **Do NOT modify `DiagnosticsExporter.TailLines`.**

## 2. Verified current root cause (commit `b39a28c3`)

### SEC-3 — wgturn-cli argument injection

`VPNRouter.Core/Services/EmergencyChannel/EmergencyChannelManager.cs` (verified
`:173-186`):

```csharp
var args = ArgsBuilderOverride?.Invoke(config)
    ?? $"connect-url -url \"{config.WgturnUrl}\" -vk-link \"{config.VkLink}\"";

var psi = new ProcessStartInfo
{
    FileName = _exePath,
    Arguments = args,          // single interpolated string, no escaping
    UseShellExecute = false,
    ...
};
```

User-controlled `WgturnUrl`/`VkLink` are interpolated inside double quotes into a
single `Arguments` string with no escaping; a value containing `"` breaks out and
can inject/override wgturn-cli flags. `Start()` validation (`:84-90`) is non-empty
only — no scheme/quote check. The code comment (`:161-172`) documents the explicit
`-url ... -vk-link ...` flag form but does not escape the values.

### OBS-2 — crash handler reads the whole log

`VPNRouter.Core/Services/CrashReporter.cs` (verified `:130-134`):

```csharp
sb.AppendLine($"──── Tail of {Path.GetFileName(logs)} (last 200 lines) ────");
var lines = File.ReadAllLines(logs);                 // :131 — reads ENTIRE file
var startIndex = Math.Max(0, lines.Length - 200);
for (int i = startIndex; i < lines.Length; i++)
    sb.AppendLine(ScrubSecrets(lines[i]));
```

The crash handler reads the ENTIRE latest `vpnrouter*.log` into memory just to
keep 200 lines → genuine OOM risk on a large/runaway log, exactly when diagnostics
are needed. The bounded pattern already exists in the same project:
`DiagnosticsExporter.TailLines` (`Services/Diagnostics/DiagnosticsExporter.cs:527`,
`MaxTailReadBytes` 12 MB const `:37`).

## 3. Why

SEC-3 lets a crafted `WgturnUrl`/`VkLink` (e.g. from an imported/shared profile)
inject or override wgturn-cli flags. OBS-2 can OOM the crash reporter on a large
log — defeating crash diagnostics precisely when they matter. Both have an
existing in-repo pattern that fixes them minimally.

## 4. What

1. **SEC-3**: stop building a single quoted `Arguments` string. Use
   `ProcessStartInfo.ArgumentList` (one element per argument: `connect-url`,
   `-url`, `<WgturnUrl>`, `-vk-link`, `<VkLink>`), which the runtime quotes/escapes
   correctly so a value with `"` stays a single argument. Keep the
   `ArgsBuilderOverride` test seam working (if it returns a pre-built string,
   either parse it into the list or keep it only for tests). Optionally add a
   light validation rejecting control characters in `WgturnUrl`/`VkLink`.
2. **OBS-2**: replace `File.ReadAllLines` + manual tail with a bounded reverse-
   seek tail. Reuse the `DiagnosticsExporter.TailLines` pattern (or call it
   directly if visibility allows — it is `internal`); then apply `ScrubSecrets`
   per line as today. Do NOT touch `DiagnosticsExporter.TailLines` itself.

```diff
- var args = ArgsBuilderOverride?.Invoke(config)
-     ?? $"connect-url -url \"{config.WgturnUrl}\" -vk-link \"{config.VkLink}\"";
  var psi = new ProcessStartInfo
  {
      FileName = _exePath,
-     Arguments = args,
      UseShellExecute = false,
      ...
  };
+ if (ArgsBuilderOverride is { } override_)
+     psi.Arguments = override_(config);          // test seam keeps string form
+ else
+     foreach (var a in new[] { "connect-url", "-url", config.WgturnUrl, "-vk-link", config.VkLink })
+         psi.ArgumentList.Add(a);
```

```diff
- var lines = File.ReadAllLines(logs);
- var startIndex = Math.Max(0, lines.Length - 200);
- for (int i = startIndex; i < lines.Length; i++)
-     sb.AppendLine(ScrubSecrets(lines[i]));
+ foreach (var line in DiagnosticsExporter.TailLines(logs, 200).Split(Environment.NewLine))
+     sb.AppendLine(ScrubSecrets(line));
```

## 5. How (ordered minimal steps)

1. Read P09's edits to `CrashReporter.cs` first (R06 is based on P09) so the OBS-2
   edit composes with the P09 scrubber changes.
2. OBS-2: confirm `DiagnosticsExporter.TailLines` is `internal` and reachable from
   `CrashReporter`; prefer calling it directly over writing a second tail reader.
   Apply `ScrubSecrets` per line.
3. SEC-3: read `EmergencyChannelManager.Start`/`LaunchProcess` + the
   `ArgsBuilderOverride` seam; switch the production path to `ArgumentList`;
   preserve the override seam for tests.
4. Add tests (below). Static review for secret leakage and argument shape.

### Tests written

- `EmergencyChannelManagerTests.LaunchProcess_UrlWithQuotes_StaysSingleArgument`
  — fails on old code (quote broke out of the string). Captures the
  `ProcessStartInfo` via the `ArgsBuilderOverride`/a factory seam and asserts the
  argument list contains the raw URL as ONE element.
- `EmergencyChannelManagerTests.LaunchProcess_ArgumentList_Shape` — asserts
  `["connect-url","-url",<url>,"-vk-link",<link>]` ordering.
- `CrashReporterTests.WriteReport_LargeLog_BoundedRead` — write a log larger than
  the tail cap; assert the report contains only the last 200 lines and the read is
  bounded (e.g. via a size-injectable seam or by asserting no full-file array).
- `CrashReporterTests.WriteReport_SmallAndEmptyLog_Handled` — small / empty /
  no-trailing-newline logs.
- `CrashReporterTests.WriteReport_TailStillScrubbed` — a secret in the tail is
  redacted (preserves P09 behavior).

### Verification approach

Fake `ProcessStartInfo` capture + temp-file log tests (no live wgturn-cli launch,
no installer). Execution in remote GitHub CI.

## 6. Affected callers / consumers + invariants

- SEC-3 consumers: `EmergencyChannelManager.Start`/`LaunchProcess`;
  `EmergencyChannelEngine` (manager owner). Invariant: the wgturn-cli flag form
  stays `connect-url -url <URL> -vk-link <LINK>` (the comment-documented explicit
  form); `ArgsBuilderOverride` test seam still functions.
- OBS-2 consumers: crash-report file writer; `ScrubSecrets`. Invariant: the tail
  is still 200 lines, still scrubbed; report file path/return unchanged.
- `DiagnosticsExporter.TailLines` is reused UNCHANGED (it is the reference bounded
  implementation).

## 7. Exact expected file list

- `VPNRouter.Core/Services/EmergencyChannel/EmergencyChannelManager.cs` (SEC-3)
- `VPNRouter.Core/Services/CrashReporter.cs` (OBS-2)
- `VPNRouter.Tests/EmergencyChannelManagerTests.cs` (or existing emergency-channel test file)
- `VPNRouter.Tests/CrashReporterTests.cs` (or existing crash-reporter test file)

## 8. Non-goals

- Do NOT modify `DiagnosticsExporter.TailLines` (already bounded — the audit
  sub-citation is wrong).
- Do NOT change the wgturn-cli flag semantics or the `connect-url` command.
- Do NOT add a new argument-builder abstraction/interface.
- Do NOT change ACLs or run any installer (SEC-2 is P1/P09, not this package).
- Do NOT launch wgturn-cli or mutate any live system (code-only).

## 9. Security / concurrency / data-loss / platform review

- **Security**: SEC-3 is the primary security fix (argument injection across a
  trust boundary when config is imported/shared). `ArgumentList` is the canonical
  mitigation. New code must not log the raw URL/link if they may carry secrets.
- **Concurrency**: none new.
- **Data-loss**: OBS-2 protects the crash report (diagnostics integrity); the
  bounded read drops a partial first line when seeked (matches `TailLines`
  semantics) — acceptable for a tail.
- **Platform**: `ProcessStartInfo.ArgumentList` is .NET-supported on all desktop
  targets; verify the wgturn-cli (Windows-centric) still receives the same argv.

## 10. Dependencies / overlaps

- **Base is P09** (SEC-1/SEC-2/OBS-1) because both edit `CrashReporter.cs`. Do NOT
  rebase R06 onto `origin/main` while P09 is unmerged.
- SEC-3 (`EmergencyChannelManager.cs`) does not overlap P09's files but rides the
  same branch for a single security/diagnostics PR.
- ZAP-2 (R07) also touches the emergency-channel component family
  (`EmergencyChannelManager.cs` process disposal). Coordinate: R06 changes the
  launch-args block; R07 changes the process dispose/exit block — different regions,
  but if both are in flight, sequence R06 before R07 or rebase carefully.

## 11. Remote-only verification gates

- [ ] Gate 1 — Build clean (remote CI): 0 errors.
- [ ] Gate 2 — Tests green (remote CI): new emergency-channel + crash-reporter tests pass; P09 scrubber tests stay green.
- [ ] Gate 3 — Docs: brief Outcome filled; zone CLAUDE.md unchanged.
- [ ] Gate 4 — Self-review: security review of the argument-handling diff (static); secret-scan new log lines.
- [ ] Gate 5 — MCP verify: N/A (Core + tests only).
- [ ] Gate 6 — Characterization diff: N/A.

## 12. Outcome (PENDING — filled after merge)

**Status**: PENDING
**Commits**: PENDING
**Pushed**: PENDING
**Test deltas**: PENDING
**Files changed**:
- `VPNRouter.Core/Services/EmergencyChannel/EmergencyChannelManager.cs` (SEC-3)
- `VPNRouter.Core/Services/CrashReporter.cs` (OBS-2)
- `VPNRouter.Tests/EmergencyChannelManagerTests.cs` (+3 tests)
- `VPNRouter.Tests/CrashReporterScrubberTests.cs` (+3 tests)

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

`git revert <commit>` on the R06 branch, or delete
`codex/qwen-audit-r06-security-diagnostics-2026-07-29`. Because R06 is based on
P09, reverting R06 leaves the P09 redaction/ACL work intact. wgturn launch reverts
to the quoted-string form; crash tail reverts to full-file read. No state written.

## 14. Self-contained copyable Qwen prompt

```text
Выполни brief plans/phase2-audit-r06-security-diagnostics-2026-07-29.md через
Qwen Code. IDs: SEC-3, OBS-2 (SEC-3 P2, OBS-2 P2 PARTIALLY_CONFIRMED). Base
branch: codex/qwen-audit-p09-secrets-acl-diagnostics-2026-07-29 (OBS-2 делит
VPNRouter.Core/Services/CrashReporter.cs с P09). Сначала прочитай brief целиком,
AGENTS.md, plans/CLAUDE.md и VPNRouter.Core/CLAUDE.md. SEC-3: используй
ProcessStartInfo.ArgumentList (не вручную quoted Arguments string) в
EmergencyChannelManager, чтобы WgturnUrl/VkLink с кавычками не инжектировали
аргументы wgturn-cli. OBS-2: замени CrashReporter File.ReadAllLines на bounded
reverse-seek tail, переиспользуя DiagnosticsExporter.TailLines паттерн
(MaxTailReadBytes 12 MB); НЕ трогай DiagnosticsExporter.TailLines (он уже
bounded — sub-citation в аудите ошибочна). Напиши тесты, падающие на старом
поведении. НЕ запускай локальные build/test/app/binary/installer, не меняй ACL,
не делай live мутаций. Только чтение/поиск/редактирование и запись тестов.
Commit/push/CI делает orchestrator. Без release/merge/tag/deploy. Без emoji.
Заполни Outcome шаблоном PENDING.
```
