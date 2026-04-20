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
2. Ship first iteration as `vX.Y.Z-r1` prerelease.
3. User tests → reports feedback.
4. Ship fix as `vX.Y.Z-r2`, **delete previous candidate**:
   ```bash
   gh release delete "vX.Y.Z-r1" --yes --repo PavelLizunov/VPNRouter
   ```
5. Repeat until user says "works".
6. Cut stable `vX.Y.Z` (no suffix):
   ```bash
   gh release create vX.Y.Z <assets> --title "vX.Y.Z — ..." --notes "..."
   gh release edit "vX.Y.Z" --prerelease=false --latest
   gh release delete "vX.Y.Z-rN" --yes --repo PavelLizunov/VPNRouter
   ```

**Only one in-flight prerelease visible at any time.** Stable releases
persist forever. Full strategy + rationale + hotfix emergency path in
`plans/vpnrouter-release-strategy.md`.

Never promote to stable until the user confirms it works.

### Build / push steps (unchanged)

1. Обновить версию в `VPNRouter.Core/AppVersion.cs`.
2. `dotnet build VPNRouter.sln` — 0 errors.
3. Коммит + `git push origin main && git push github main`.
4. Push tag `git push --tags` (triggers mac + linux CI workflows).
5. `build.ps1 -Version "X.Y.Z-rN" -Upload` (Windows artifacts).
6. Mark prerelease + write notes:
   ```bash
   gh release edit "vX.Y.Z-rN" --prerelease --notes "..."
   ```

## Forgejo Access

- VPN IP: `10.9.1.1` (AmneziaWG)
- Web UI: http://10.9.1.1:18300
- SSH: `ssh://git@10.9.1.1:18222`
- User: slovn
- VPN должен быть активен для доступа
