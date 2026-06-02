# plans/

Roadmap'ы, post-mortem'ы, handoff-документы. **Все плановые `.md` файлы хранятся
здесь** (не в `.claude/`, см. `CLAUDE.local.md`).

## Naming convention

```
vpnrouter-vX.Y.Z-<short-topic>.md       ← roadmap для конкретной версии
vpnrouter-<feature>.md                   ← фичевый план через несколько версий
session-handoff-YYYY-MM-DD.md            ← handoff между Claude-сессиями
session-night-shift-YYYY-MM-DD.md        ← post-mortem длинной автономной сессии
release-notes-vX.Y.Z[-rN].md             ← черновик release notes (.gitignored, см. .gitignore)
```

## Структура roadmap'а / planа

```markdown
# vX.Y.Z — <тема>

## Триггер
<контекст: что произошло, кто пожаловался, что не работает>

## Симптом
<что видит юзер>

## Root cause
<технический разбор, file:line, код-снippet>

## Fix strategy
<step-by-step что меняем, какие новые тесты, какой риск>

## Acceptance
<- [ ] checklist что user должен увидеть после фикса>

## Оценка
<часов / risk / dependencies>

## Связь с другими планами
<cross-refs>
```

## Активные планы (на 2026-06-02)

Stable v2.38.2; in-flight prerelease v2.40.0-r4 (folds v2.39 + v2.40).

| Файл | Статус |
|---|---|
| `interaction-contracts/README.md` | adopted framework (FC + APP page interaction contracts) |
| `v2.40.0-fc-interaction-gates.md` | Free Configs Verified-only Connect/Apply + busy/bounds gates |
| `regression-review-v2.40.0-r1-followup-2026-06-02.md` | r2 over-scrub + per-app DNS leak follow-up |
| `handle-leak-sweep-v2.40.0-r3-2026-06-02.md` | P0 handle-leak sweep (ProcessQuery + Gate 7 guard) |
| `bug-responsiveness-memory-audit-targets-2026-06-02.md` | measurement-first perf/leak audit map |
| `public-configs-pipeline-audit-and-hardening-plan-2026-06-02.md` | free-configs pipeline hardening |
| `firewall-killswitch-linux-macos-2026-06-02.md` | P0 fail-closed firewall backstop (Linux/macOS) |
| `vpn-connection-user-statistics-product-notes-2026-06-02.md` | STATS Phase 1-4 product notes |
| `android-ci-distribution-roadmap-2026-05-31.md` | Android CI (NU1102) + distribution |
| `vpnrouter-release-strategy.md` | rolling-rN policy reference |
| `cut-stable-checklist.md` | mandatory pre-cut live-update gate checklist |

## Что NOT в plans/

- Release notes (черновики) — gitignored через `release-notes-*.md`
- `.claude_handoff.md` — это **runtime memory**, не plan, в корне репо, gitignored
- `tools/live-test-r1.ps1` — это runtime harness, не план

## Persistence

Активные планы (P0/P1) держим до их завершения + 1-2 stable cycles. Закрытые
планы (после stable cut) можно архивировать в `plans/archive/<year>/`, но это
пока не сделано — план-файлов уже ~230 (+ release-notes/evidence), но скрипт
прокручивает быстро.

## Cross-references

- `CLAUDE.local.md` (user-private) → release process, version policy
- `MEMORY.md` (in user's `.claude/projects/.../memory/`) → high-level state
- `.claude/workflow.md` → harness workflow (read-only)
- `.claude_handoff.md` → handoff между Claude sessions
