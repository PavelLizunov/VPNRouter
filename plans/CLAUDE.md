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

## Активные планы (на 2026-08-13)

Stable v2.49.0; активного prerelease-кандидата нет. Точный список
подтверждённых блокеров хранится в
`OPEN-DEFECTS.md`; stable cut запрещён при любом OPEN P0/P1.

| Файл | Статус |
|---|---|
| `interaction-contracts/README.md` | adopted framework (FC + APP page interaction contracts) |
| `OPEN-DEFECTS.md` | release-gating ledger for every P0/P1 finding |
| `v3.0-refactor-roadmap.md` | long-running refactor roadmap and task ownership |
| `macos-linux-functional-parity-plan-2026-06-15.md` | cross-platform runtime parity follow-ups |
| `code-signing-signpath-runbook-2026-07-10.md` | Windows code-signing enrollment, still pending |
| `vpnrouter-release-strategy.md` | rolling-rN policy reference |
| `cut-stable-checklist.md` | fixed-WINBRAT pre-cut live-update gate |

## Что NOT в plans/

- Release notes (черновики) — gitignored через `release-notes-*.md`
- `.claude_handoff.md` — это **runtime memory**, не plan, в корне репо, gitignored
- `tools/live-test-r1.ps1` — это runtime harness, не план

## Persistence

Активные планы (P0/P1) держим до их завершения + 1-2 stable cycles. Закрытые
планы (после stable cut) можно архивировать в `plans/archive/<year>/`, но это
пока не сделано — план-файлов уже ~230 (+ release-notes/evidence), но скрипт
прокручивает быстро.

Любой finding (audit / review / research / bug-hunt / Qwen/Codex pass / live
verification) persists в `plans/OPEN-DEFECTS.md` ДО реализации или отсрочки —
см. root Golden rule #16. Запись может быть candidate/unverified, но хранит
source/evidence, severity/impact, disposition (candidate, confirmed, refuted,
deferred, in progress, resolved) и implementation/PR ref когда есть. Findings
только в chat или во временном отчёте — не считаются записанными.

## Cross-references

- `CLAUDE.local.md` (user-private) → release process, version policy
- `MEMORY.md` (in user's `.claude/projects/.../memory/`) → high-level state
- `.claude/workflow.md` → harness workflow (read-only)
- `.claude_handoff.md` → handoff между Claude sessions
