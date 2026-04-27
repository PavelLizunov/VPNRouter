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

1. Pick version `vX.Y.Z` (patch bump vs. current stable).
2. Ship first iteration as `vX.Y.Z-r1` prerelease (autonomously, no confirm).
3. User tests на своей машине. Если репортит баги — итерируем; если молчит и
   verification gate зелёная — продвигаемся (см. шаг 5).
4. Ship fix as `vX.Y.Z-r2`, **delete previous candidate**:
   ```bash
   gh release delete "vX.Y.Z-r1" --yes --repo PavelLizunov/VPNRouter
   ```
5. Repeat until verification gate зелёная (build + tests + Mac/Linux CI green +
   12 assets) и no user-reported regressions за ~24h.
6. Cut stable `vX.Y.Z` (no suffix) **autonomously** когда gate зелёная:
   ```bash
   gh release create vX.Y.Z <assets> --title "vX.Y.Z — ..." --notes "..."
   gh release edit "vX.Y.Z" --prerelease=false --latest
   gh release delete "vX.Y.Z-rN" --yes --repo PavelLizunov/VPNRouter
   ```

**Only one in-flight prerelease visible at any time.** User паузит явной
командой "hold stable" если хочет задержать promotion. Full strategy +
rationale + hotfix emergency path in `plans/vpnrouter-release-strategy.md`.

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

Promote to stable когда verification gate зелёная (build + tests + Mac/Linux CI +
12 assets) и user не репортил regressions за ~24h. Default mode: autonomous;
user паузит явной командой "hold stable" если хочет задержать promotion.

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
