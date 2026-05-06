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

**Mode = autonomous до stable cut.** Подтверждений от user'а не запрашиваем
для code → -rN ship cycle (commit / push / tag / release / cleanup). User
прерывает явной командой ("стоп", "hold", "откати"). **Stable cut требует
явной user-команды** ("cut" / "ok" / "promote") — см. урок v2.31.2 в
`CLAUDE.local.md`. Safety rails ниже остаются — про destructive ops.

1. **Default = autonomous до stable.** Code change → build → tests → commit →
   push в оба remote → ship -rN → mac/linux CI → finalize prerelease → delete
   previous -rN → **MCP+UIA test (mandatory, см. rule #1a)** → доложить
   user'у с детальным test report'ом + ждать cut. Без вопросов между
   intermediate шагами. **Cut stable НЕ autonomous** — только по явной
   команде. См. rule #6.

   **1a. MCP test после каждого ship — обязательно, не "где testable".**
   Установлено user'ом 2026-05-04 после iter#7. Flow: ship -rN → CI green →
   12 assets → НЕМЕДЛЕННО запускаю VPNRouter (или auto-update) → MCP
   computer-use тестит изменение по сценарию который описан в release
   notes / commit message → скриншоты + PASS/FAIL по каждому пункту →
   доклад user'у. Без user prompt'а — это часть ship cycle. У меня есть
   `mcp__vpnrouter-test__*` (window control / mouse / keyboard /
   screenshot) + Bash для logs → нет нужды просить user'а скрин или
   "проверь сам". Если изменение Core-only без UI surface (parser,
   migration helper, etc.) — explicit "Core-only / not UI-testable"
   label в докладе. Иначе MCP-test обязателен.
2. **Push в ОБА remote** после commit'а: `git push github HEAD:main && git push origin HEAD:main`.
   Forgejo через VPN — может быть down, retry позже автоматически.
3. **Никогда `--no-verify` / `--no-gpg-sign`** без явного запроса. Если pre-commit
   hook упал — фиксить причину, не bypass. (Safety rail, не workflow confirm.)
4. **Никогда `git push --force` на `main`** — destructive, можно потерять работу.
   Force-update tag (`git tag -f`) допустим только для prerelease tag'ов
   до того как опубликован release. (Safety rail.)
5. **`AppVersion.Version` ВСЕГДА совпадает с release tag**, включая `-rN`
   суффикс. Урок v2.25.0-r1→r2 в `CLAUDE.local.md`.
6. **Stable cut по user-команде** (изменено 2026-05-03 после v2.31.4,
   расширено 2026-05-06 после v2.31.7 helper.cmd parser bug).
   Verification gate (6 conditions ниже) — обязательное READY условие, но
   само не cut'ает. Жди explicit "cut" / "ok" / "promote" перед `vX.Y.Z`
   stable. Conditions: (a) `dotnet build -c Release` 0 errors,
   (b) regression tests зелёные, (c) Mac+Linux CI на последнем -rN зелёные,
   (d) `gh release view` показывает 12 assets, (e) MCP+UIA verify PASS
   где testable (или explicit "Core-only / not UI-testable" label),
   **(f) live update gate — install previous stable, trigger update к
   текущему -rN, verify success (см. cut-stable skill «Mandatory pre-cut
   live update gate» секцию + `plans/cut-stable-checklist.md`).**
   **Урок v2.31.2**: 2 из 5 stable cuts в одной session оказались
   partial-fix slips потому что MCP verify не делался — нужен
   human-in-the-loop. **Урок v2.31.7**: helper.cmd parser bug сломал 100%
   user-upgrades, поймали через ~7 дней. Live update gate — единственный
   способ это поймать до cut'а. Tiny / config-only / typo fixes —
   exception: ship + flag + let user decide if нужен ceremonial stable.
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
