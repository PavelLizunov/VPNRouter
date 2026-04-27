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

## Активные планы (на 2026-04-27)

| Файл | Статус |
|---|---|
| `vpnrouter-v2.28-ux-bugfix.md` | основной — Bug 1 + Bug 2 + Free Configs UX (Phase 3A/3B/3C) |
| `vpnrouter-v2.28.4-ux-redesign.md` | новый — NetworkPage overflow + DpiBypass/Apps style + Free Configs Simple+green-card |
| `vpnrouter-core-stability-audit.md` | core layer audit (in flight, P2/P3 в backlog) |
| `vpnrouter-release-strategy.md` | rolling-rN policy reference |
| `session-handoff-2026-04-24.md` | handoff после v2.27.2 stable cut |
| `session-night-shift-2026-04-25.md` | v2.28.2-r1 post-mortem (silent leak fix) |

## Что NOT в plans/

- Release notes (черновики) — gitignored через `release-notes-*.md`
- `.claude_handoff.md` — это **runtime memory**, не plan, в корне репо, gitignored
- `tools/live-test-r1.ps1` — это runtime harness, не план

## Persistence

Активные планы (P0/P1) держим до их завершения + 1-2 stable cycles. Закрытые
плану (после v2.28.x stable cut) можно архивировать в `plans/archive/<year>/`,
но это пока не сделано — planов всего ~15, скрипт прокручивает быстро.

## Cross-references

- `CLAUDE.local.md` (user-private) → release process, version policy
- `MEMORY.md` (in user's `.claude/projects/.../memory/`) → high-level state
- `.claude/workflow.md` → harness workflow (read-only)
- `.claude_handoff.md` → handoff между Claude sessions
