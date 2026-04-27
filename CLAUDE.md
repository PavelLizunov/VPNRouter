# VPNRouter — root context for Claude

Process-based split-tunnel VPN router for Windows / macOS / Linux. .NET 8 +
Avalonia + sing-box (TUN+VLESS+Reality). Solo dev project — see
`.claude_handoff.md` for current state.

## Zone ownership

- **Все зоны мои** (Pavel Lizunov, `PavelLizunov`). Нет директорий с ограниченным
  доступом. Можно редактировать всё.
- Не моя зона (внешние upstream): `tools/zapret/`, `tools/singbox-cache/` —
  скачанные binary-артефакты, не комитим в репо.

## Sub-CLAUDE.md map

Подробности по конкретной зоне — в её sub-CLAUDE.md. Этот файл тонкий.

| Зона | Sub-CLAUDE.md |
|---|---|
| Бизнес-логика, sing-box, subscriptions, free configs | `VPNRouter.Core/CLAUDE.md` |
| Avalonia GUI, ViewModels, design tokens | `VPNRouter.App/CLAUDE.md` |
| CLI (Spectre.Console) | `VPNRouter.CLI/CLAUDE.md` |
| Windows Service wrapper | `VPNRouter.Service/CLAUDE.md` |
| xUnit tests | `VPNRouter.Tests/CLAUDE.md` |
| CI workflows + secrets | `.github/workflows/CLAUDE.md` |
| Per-platform install scripts + APT/winget | `packaging/CLAUDE.md` |
| Roadmap / handoff plans convention | `plans/CLAUDE.md` |

## Quick reference commands

```bash
# Build everything (Release)
dotnet build VPNRouter.sln -c Release

# Run regression tests (v2.28.x suite)
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build \
  --filter "FullyQualifiedName~VlessServersResolverTests|FullyQualifiedName~ConfigGeneratorEmptyServersGuardTests|FullyQualifiedName~FreeConfigAggregatorPreserveTests"

# Ship a rolling candidate (skill: ship-rolling-candidate)
powershell -ExecutionPolicy Bypass -File build.ps1 -Version "2.X.Y-rN" -Upload

# Cut stable (skill: cut-stable, autonomous когда -rN прошёл verification)
powershell -ExecutionPolicy Bypass -File build.ps1 -Version "2.X.Y" -Upload

# Push to both remotes
git push github HEAD:main && git push origin HEAD:main

# Verify release state
gh release view vX.Y.Z --repo PavelLizunov/VPNRouter --json isPrerelease,assets
```

## Infrastructure quick-ref

| Что | Где |
|---|---|
| GitHub repo | `PavelLizunov/VPNRouter` |
| Forgejo mirror | `ssh://git@10.9.1.1:18222/slovn/vpnrouter.git` (через AmneziaWG VPN) |
| Mac build host (manual) | `slovn@192.168.0.246` (через host AmneziaWG route, key `id_ed25519`) |
| One-liner install domain | `vpn.ninitux.com` (CNAME → `pavellizunov.github.io`) |
| Homebrew tap | `PavelLizunov/homebrew-vpnrouter` (auto-bumps на stable) |
| APT repo | `vpn.ninitux.com/apt/` (reprepro signed, gh-pages branch) |

Полный список — `.claude_handoff.md` "Infrastructure".

## Skills layer

`.claude/skills/<name>/SKILL.md` — повторяющиеся workflow'ы. Видны через
`Skill` tool после рестарта Claude Code (или сразу через явный invoke).

| Skill | When |
|---|---|
| `ship-rolling-candidate` | Выпускаем `-rN` после code change |
| `cut-stable` | -rN прошёл verification (build/tests/CI green, 12 assets) — промоутим к stable |
| `diagnose-config` | User шлёт config.yaml + current.json + log — методичный walkthrough |
| `audit-overflow-fix` | UI overflow / стилевое несоответствие на settings page |
| `merge-design-handoff` | User шлёт `claude.ai/design` URL — fetch + extract + map tokens |
| `update-readme-versions` | После каждого release бампим version examples в README |

## Memory layer

`.claude_handoff.md` (gitignored, в корне репо). Workflow:

- **Старт сессии**: прочесть handoff → hydrate в `mcp__memory` граф (если активен).
- **Конец сессии**: dump граф обратно в handoff + add "Last session log" entry.
- **Compact restore**: handoff = primary state recovery file.

Секции handoff (см. файл): Persons / Infrastructure / Code Artifacts /
Open Tasks / Last session log.

## Golden rules

**Mode = autonomous by default.** Подтверждений от user'а не запрашиваем —
делаем всё сами (commit / push / tag / release / cut stable / cleanup).
User прерывает явной командой ("стоп", "hold", "откати") если направление
неверное. Safety rails ниже остаются — это про destructive ops, не workflow.

1. **Default = autonomous full cycle.** Code change → build → tests → commit →
   push в оба remote → ship -rN → mac/linux CI → finalize release → delete
   previous -rN. Без вопросов между шагами. Cut stable тоже autonomous когда
   последний -rN прошёл verification gate (см. rule #6).
2. **Push в ОБА remote** после commit'а: `git push github HEAD:main && git push origin HEAD:main`.
   Forgejo через VPN — может быть down, retry позже автоматически.
3. **Никогда `--no-verify` / `--no-gpg-sign`** без явного запроса. Если pre-commit
   hook упал — фиксить причину, не bypass. (Safety rail, не workflow confirm.)
4. **Никогда `git push --force` на `main`** — destructive, можно потерять работу.
   Force-update tag (`git tag -f`) допустим только для prerelease tag'ов
   до того как опубликован release. (Safety rail.)
5. **`AppVersion.Version` ВСЕГДА совпадает с release tag**, включая `-rN`
   суффикс. Урок v2.25.0-r1→r2 в `CLAUDE.local.md`.
6. **Stable cut autonomous gate**: cut когда (a) `dotnet build -c Release` 0 errors,
   (b) regression tests зелёные, (c) Mac+Linux CI на последнем -rN зелёные,
   (d) `gh release view` показывает 12 assets, (e) no user-reported regressions
   за reasonable timeframe (~24h по умолчанию). Все 5 → cut. User паузит явной
   командой "hold stable".
7. **process_name в sing-box case-sensitive** — не использовать `ToLowerInvariant()`.
   Дедупликация через `StringComparer.OrdinalIgnoreCase` без mutation.
8. **`.claude/` partially editable** — `.claude/skills/<name>/SKILL.md` и
   `.claude/CLAUDE.md` (если есть) — content layer, редактируем. Остальное
   (`settings.json`, `workflow.md`, `hooks/`, runtime cache) — harness config,
   не трогать без user-явного запроса.
9. **Никогда не emoji в файлах кода / config / документации** (это правило
   user'а на этот проект). Ru/En текст, технические symbols (✓ ✗ → · ║) ОК если
   user сам их использует в release notes.
10. **MEMORY.md в `~/.claude/projects/.../memory/` — auto-managed harness'ом**,
    не редактировать руками без причины. `.claude_handoff.md` в репо — это
    наш controlled file.

## Git safety

- `main` — protected (никаких force-push без запроса). Заявленные fixes идут
  через прямые commits.
- Tags `vX.Y.Z` (stable) — финальные, не force-update'ить после публикации
  release.
- Tags `vX.Y.Z-rN` (prerelease) — можно force-update'ить пока не опубликован
  release; после публикации лучше bumpнуть `-r(N+1)`.
- В `CLAUDE.local.md` — release retention policy (max ~30 на GitHub Releases page).

## Cross-references

- `CLAUDE.local.md` — user-private (не редактируем): release process, version
  policy, Forgejo creds, lessons learned.
- `.claude/workflow.md` — harness workflow, **read-only**.
- `~/.claude/projects/.../memory/MEMORY.md` — auto-managed user memory.

## Не созданы (опциональны)

- `.mcp.json` — нет MCP-серверов в проекте. `gh CLI` напрямую покрывает 95%
  GitHub ops. Если user захочет Jira/Confluence/Slack/etc — добавим.

---

**Когда genuine ambiguity** — несколько валидных путей с разной семантикой,
scope действительно непонятен, риск destructive op без отката — спросить.
Иначе **делать**. По умолчанию: действие, не вопрос.
