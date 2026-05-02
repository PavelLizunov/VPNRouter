# VPNRouter — Local Instructions

## Plans & notes location

Все планы, roadmap'ы и рабочие заметки храним в `./plans/` в корне
проекта (а не в `.claude/plans/`). В самой папке `.claude/` никакие
`.md`-файлы не создавать и не редактировать — она для конфигов
харнесса, не для контента.

Существующие кросс-ссылки в `.claude/workflow.md` на старые пути
оставить как есть (правило запрещает её редактировать); ориентируйся
по актуальному содержимому `./plans/`.

## Git Remotes

| Remote | URL | Notes |
|---|---|---|
| origin | `ssh://git@10.9.1.1:18222/slovn/vpnrouter.git` | Forgejo (через AmneziaWG VPN) |
| github | `https://github.com/PavelLizunov/VPNRouter.git` | GitHub (public) |

**Push policy**: всегда пушить в оба remote после коммита:
```bash
git push origin main && git push github main
```

## Release Process — rolling -rN candidates

**Starting 2026-04-20**: we ship iterations as rolling release
candidates `vX.Y.Z-r1`, `vX.Y.Z-r2` to keep the Releases page clean
and avoid 30-prerelease bursts we had in v2.17 → v2.21 cycle.

**Updated 2026-05-03 (после v2.31.4)**: stable cut больше НЕ
autonomous. Партнёрство user+claude: claude ships -rN автономно и
verifies, user даёт явную команду на promotion. Слишком много
partial-fix slips в одной session показали что "5 green checkboxes"
gate скрывает UX bugs — нужен human-in-the-loop перед stable.

1. Pick version `vX.Y.Z` (patch bump vs. current stable).
2. Ship first iteration as `vX.Y.Z-r1` prerelease (autonomously, no confirm).
3. **Verify** через MCP+UIA где testable, или explicit "Core-only / not UI-testable" label.
   Доложить status-summary user'у.
4. Ship fix as `vX.Y.Z-r2`, **delete previous candidate**:
   ```bash
   gh release delete "vX.Y.Z-r1" --yes --repo PavelLizunov/VPNRouter
   ```
5. Repeat until verification gate зелёная (build + tests + Mac/Linux CI green +
   12 assets) и MCP verification PASS where applicable.
6. **STOP. Wait for user "cut" / "ok" / "promote" command.** No autonomous
   stable cut — see lesson v2.31.2 ниже. Only proceed когда user явно
   подтверждает.
7. Cut stable `vX.Y.Z` (no suffix) **по команде user'а**:
   ```bash
   gh release create vX.Y.Z <assets> --title "vX.Y.Z — ..." --notes "..."
   gh release edit "vX.Y.Z" --prerelease=false --latest
   gh release delete "vX.Y.Z-rN" --yes --repo PavelLizunov/VPNRouter
   ```

**Only one in-flight prerelease visible at any time.** User паузит явной
командой "hold stable" если хочет задержать promotion. Full strategy +
rationale + hotfix emergency path in `plans/vpnrouter-release-strategy.md`.

### Урок v2.31.2 → v2.31.3 → v2.31.4 (партнёрство, не auto-cut)

В одной session 2026-05-02..03 cut'нули 5 stable releases подряд.
Из них 2 (v2.31.2 + v2.31.4) cut'нулись по "all-green" gate БЕЗ
MCP-verify (один — потому что toast not testable, второй — cache
empty). v2.31.2 оказался partial fix — user получил stable где F-25
всё ещё видим в UI. Поймали только потому что MCP retest сделали
сами после cut. v2.31.3 пришлось shipать как hotfix.

**Vывод**: green tests + CI ≠ "ship to stable". Tests не покрывают
UI rendering, hover, tooltips, popup interactions. user-в-цикле перед
stable cut обязателен. Tiny fixes (typo, version bump, README) —
exception: ship + flag + let user decide if нужен ceremonial stable.

## GitHub Release Retention Policy

Cap: **~30 releases max** on the Releases page. Composition:

1. **Последние 20 релизов по времени** (независимо от prerelease/stable
   статуса). Сохраняются автоматически — это текущий активный цикл.
2. **10 исторически значимых milestone-релизов**, которые держим вечно:
   - последний stable каждой минорной серии (`v2.1X.(last)`)
   - стартовые точки крупных переработок (Arctic theme, Avalonia migrate,
     Free Configs feature и т.д.) — отмечены явно
   - oldest kept release (текущий = `v2.10.0`, Avalonia baseline)

Git-теги **не удаляем** — они остаются в истории репо, только release
page подчищаем. Если надо восстановить старый build — `git checkout tag`.

**Когда чистить**: каждый раз когда общее число релизов выходит за 30
(обычно после серии -rN итераций внутри одного минорного цикла).

Promote to stable **по явной команде user'а** (cut / ok / promote). Не
autonomous — см. урок v2.31.2 в Release Process выше. Verification gate
(build + tests + Mac/Linux CI + 12 assets + MCP verify где testable)
обязательно зелёная ПЕРЕД тем как просить разрешение, но cut только
по подтверждению.

### Build / push steps (unchanged)

1. Обновить версию в `VPNRouter.Core/AppVersion.cs`.
   **CRITICAL:** строка Version должна СОВПАДАТЬ с release tag **включая `-rN`**.
   Например для тега `v2.25.1-r1` ставим `Version = "2.25.1-r1"`, не `"2.25.1"`.
   Иначе update-check не различит `-r1` и `-r2` (обе скомпилятся с одинаковым
   AppVersion, и клиент на r1 при проверке r2 увидит «2.25.0-r2 OLDER чем
   2.25.0 stable» из-за semver-правила «stable > prerelease same-core» →
   «Up to date», обновление не подтянется.
2. `dotnet build VPNRouter.sln` — 0 errors.
3. Коммит + `git push origin main && git push github main`.
4. Push tag `git push --tags` (triggers mac + linux CI workflows).
5. `build.ps1 -Version "X.Y.Z-rN" -Upload` (Windows artifacts).
6. Mark prerelease + write notes:
   ```bash
   gh release edit "vX.Y.Z-rN" --prerelease --notes "..."
   ```

### Урок от v2.25.0-r1 → r2 (не повторять)

v2.25.0-r1 релизили с `AppVersion.Version = "2.25.0"` (без суффикса).
v2.25.0-r2 ШЛИ с тем же `Version = "2.25.0"`. На тестовой машине бинарь
видел себя как "2.25.0" stable, r2 как "2.25.0-r2" prerelease, и по
semver-правилу r2 < stable → update-check молча возвращал null.
Пришлось бампать до v2.25.1-r1 чтобы Core изменилось и Check нашёл
обновление.

## Forgejo Access

- VPN IP: `10.9.1.1` (AmneziaWG)
- Web UI: http://10.9.1.1:18300
- SSH: `ssh://git@10.9.1.1:18222`
- User: slovn
- VPN должен быть активен для доступа
