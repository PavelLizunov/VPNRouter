# VPNRouter — root context for Claude

Process-based split-tunnel VPN router for Windows / macOS / Linux / Android. .NET 10 / SDK 10.0.301 +
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
| Android port (libbox.aar JNI, shared Avalonia UI) | `VPNRouter.Android/CLAUDE.md` |
| CLI (Spectre.Console) | `VPNRouter.CLI/CLAUDE.md` |
| Windows Service wrapper | `VPNRouter.Service/CLAUDE.md` |
| xUnit tests | `VPNRouter.Tests/CLAUDE.md` |
| CI workflows + secrets | `.github/workflows/CLAUDE.md` |
| Per-platform install scripts + APT/winget | `packaging/CLAUDE.md` |
| Roadmap / handoff plans convention | `plans/CLAUDE.md` |

**Mirror trees**: в корне есть параллельные `AGENTS.md` + `.agents/skills/*/SKILL.md`
— зеркало этого файла и `.claude/skills/`. Канонична версия в `.claude/` (+ этот
`CLAUDE.md`); при правке правила/скилла синхронизируй `.agents/`/`AGENTS.md` следом.
Они уже расходились (audit P1-3, 2026-06-25) — не правь только одну копию.

## Quick reference commands

```bash
# Build everything (Release)
dotnet build VPNRouter.sln -c Release

# Run regression tests (v2.28.x suite)
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build \
  --filter "FullyQualifiedName~VlessServersResolverTests|FullyQualifiedName~ConfigGeneratorEmptyServersGuardTests|FullyQualifiedName~FreeConfigAggregatorPreserveTests"

# Ship a rolling candidate (skill: ship-rolling-candidate)
powershell -ExecutionPolicy Bypass -File build.ps1 -Version "2.X.Y-rN" -Upload

# Cut stable (skill: cut-stable — НЕ autonomous: по явной команде user после verification, см. rule #6)
powershell -ExecutionPolicy Bypass -File build.ps1 -Version "2.X.Y" -Upload

# Push the task branch to canonical GitHub and open a PR
git push -u origin HEAD
gh pr create --fill --base main

# Verify release state
gh release view vX.Y.Z --repo PavelLizunov/VPNRouter --json isPrerelease,assets
```

## Infrastructure quick-ref

| Что | Где |
|---|---|
| GitHub repo | `PavelLizunov/VPNRouter` |
| Forgejo mirror | `ssh://git@10.9.1.1:18222/slovn/vpnrouter.git` (через AmneziaWG VPN) |
| Mac build host (manual) | `slovn@192.168.0.246` (через host AmneziaWG route, key `id_ed25519`) |
| Proxmox test lab | `pve-ninitux` (https://192.168.0.169:8006) — Win `windows-brat`(100)@192.168.0.106, Debian `debian-xfce`(101)@192.168.0.99; creds/детали в `.claude_handoff.md` |
| One-liner install domain | `vpn.ninitux.com` (CNAME → `pavellizunov.github.io`) |
| Homebrew tap | `PavelLizunov/homebrew-vpnrouter` (auto-bumps на stable) |
| APT repo | `vpn.ninitux.com/apt/` (reprepro signed, gh-pages branch) |

Полный список — `.claude_handoff.md` "Infrastructure".

### GitHub fetch caveat

The `github` remote may advertise corrupt checkpoint refs under
`refs/codex/turn-diffs/...`, which can make full fetches fail with
`fatal: bad object refs/codex/...` / `did not send all necessary objects`.
Avoid `git fetch github` / `git fetch github --tags` in release flows. Prefer
`gh api`, targeted refs, or the `cut-stable` skill's verified local-tag mirror
step when pushing a stable tag to Forgejo.

## Skills layer

`.claude/skills/<name>/SKILL.md` — повторяющиеся workflow'ы. Видны через
`Skill` tool после рестарта Claude Code (или сразу через явный invoke).

| Skill | When |
|---|---|
| `ship-rolling-candidate` | Выпускаем `-rN` после code change |
| `cut-stable` | -rN прошёл verification (build/tests/CI green, 14 desktop assets / 16 with Android) — промоутим к stable |
| `diagnose-config` | User шлёт config.yaml + current.json + log — методичный walkthrough |
| `audit-overflow-fix` | UI overflow / стилевое несоответствие на settings page |
| `merge-design-handoff` | User шлёт `claude.ai/design` URL — fetch + extract + map tokens |
| `update-readme-versions` | После каждого release бампим version examples в README |
| `phase-task-launcher` | START любой v3.0 refactor task / >30-строчного изменения — 6-gate lifecycle из `plans/v3.0-execution-methodology.md` |
| `post-ship-mcp-verify` | **MUST** запускать после каждого ship-rolling-candidate (auto-chain). Fixed VM WINBRAT (192.168.0.106) через `tools/brat-verify.ps1`: deploy → launch → remote UIA + screenshots (`artifacts/brat-verify`) → log scan на brat → PASS/FAIL report. Без local fallback. |

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

1. **Default = branch → PR → CI.** Code change → build → tests → commit →
   `git push -u origin HEAD` → PR в `main` → фактические checks. Красный PR не
   merge-ить. После зелёного PR ждать решения владельца о merge/release.

   **1a. Remote brat verify после каждого ship — обязательно, не "где testable".**
   Установлено user'ом 2026-05-04 после iter#7 (2026-07-31 переведено на
   фиксированную remote-цель). Flow: ship -rN → CI green →
   14 desktop assets (16 with Android) → НЕМЕДЛЕННО deploy + launch VPNRouter
   на тест-VM → remote UIA + screenshots тестят изменение по сценарию который
   описан в release notes / commit message → PASS/FAIL по каждому пункту →
   доклад user'у. Без user prompt'а — это часть ship cycle. Весь verify идёт
   через `tools/brat-verify.ps1` (actions identity / deploy / uia /
   screenshot / logs) против фиксированной VM → нет нужды просить user'а
   скрин или "проверь сам". Скриншоты — под `artifacts/brat-verify/`
   (не комитим). Если изменение Core-only без UI surface (parser,
   migration helper, etc.) — explicit "Core-only / not UI-testable"
   label в докладе. Иначе remote brat verify обязателен.

   **КРИТИЧНО — цель = windows-brat, НЕ dev box (инцидент 2026-07-06).**
   Install / launch / connect VPNRouter и любые UIA/screenshot-действия идут
   ТОЛЬКО на тест-VM **windows-brat (192.168.0.106, MachineName `WINBRAT`)
   через WinRM via `tools/brat-verify.ps1`, невидимо и fail-closed** (каждое
   действие повторно верифицирует identity WINBRAT). НИКОГДА не
   ставить/запускать/останавливать VPNRouter на машине агента (dev box)
   и не трогать `C:\Program Files\VPNRouter` — локальные UI-инструменты
   управляют dev box'ом (WRONG target, хватает мышь/экран user'а).
   Никакого local fallback: brat/WinRM/credential недоступны — STOP +
   спросить user'а, НЕ откатываться на локальную машину. Рецепт (внутри
   `tools/brat-verify.ps1`): identity check WINBRAT → `Copy-Item -ToSession`
   ZIP → scheduled task (Interactive principal, `RunLevel Highest`,
   tester=admin → без UAC) → `tscon` на console для рендера → UIA +
   `CopyFromScreen`, PNG назад по WinRM. См. memory
   `dev-box-not-a-test-target` + `no-devbox-input-hijack` + скилл post-ship-mcp-verify.
2. **Canonical remote = `origin` (GitHub).** Push только текущей task-ветки:
   `git push -u origin HEAD`. `forgejo` — зеркало; синхронизация `main`
   выполняется только после принятого merge/release. НЕТ remote с именем `github`.
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
   (d) `gh release view` показывает 14 desktop assets / 16 with Android, (e) remote brat UIA verify PASS
   где testable (или explicit "Core-only / not UI-testable" label),
   **(f) live update gate — install previous stable, trigger update к
   текущему -rN, verify success (см. cut-stable skill «Mandatory pre-cut
   live update gate» секцию + `plans/cut-stable-checklist.md`).**
   **Урок v2.31.2**: 2 из 5 stable cuts в одной session оказались
   partial-fix slips потому что post-ship UI verify не делался — нужен
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
11. **CI-gate перед каждым push** (added 2026-05-25 после r7..r18 red-CI streak).
    После `git push` следующего commit'а — **MUST** запустить
    `powershell -ExecutionPolicy Bypass -File tools/verify-last-commit-ci.ps1`
    BEFORE next code change. Exit 0 = можно дальше, exit 1/2/3 = STOP. Также
    установлен `.githooks/pre-push` hook (`git config core.hooksPath .githooks`)
    который физически блокирует push если предыдущий commit красный. Bypass
    через `--no-verify` только с явной user-командой. Урок: 12 ships подряд
    (r7..r18) с красным commit-CI потому что я забывал проверять между
    ships — единственный red-X X на главной странице commits — и tag-level
    CI (build-mac/linux на тэге) скрывал что push-event CI красный.

12. **Post-ship remote brat verify обязательный** (added 2026-05-25,
    расширение rule #1a; 2026-07-31 переведено на фиксированную
    remote-цель). После каждого `ship-rolling-candidate` (-rN tag создан +
    binary uploaded) — **MUST** запустить `post-ship-mcp-verify` skill
    (имя директории сохранено для совместимости). Skill автоматически:
    SHA256-check ZIP from GitHub release → deploy + launch на фиксированной
    VM WINBRAT (192.168.0.106) через `tools/brat-verify.ps1` → walk через
    changed pages via remote UIA (clicks/toggles) + screenshots под
    `artifacts/brat-verify/` → tail `vpnrouter*.log` на brat for
    `[ERR]`/`Exception`/`FATAL` patterns → PASS/FAIL report. Реализация:
    `.claude/skills/post-ship-mcp-verify/SKILL.md` +
    `tools/brat-verify.ps1` + per-feature checklists в
    `references/checklist-{zapret,tgproxy,vpn-core,network-settings,
    free-configs,localization}.md`. VM/WinRM недоступны — STOP, никакого
    local fallback. Урок: 12 ships в r7..r18 batch с красным CI И БЕЗ
    post-ship UI test потому что я полагался на тэг-level CI green.
    Combined с rule #11 теперь невозможно skip обе проверки.

13. **Remote brat verify должен ВЕСТИ к user-сценарию end-to-end, не
    остановиться на "tab rendered"** (added 2026-05-25 после r25..r28
    scroll-bug thrash). Если фикс касается UI — verify должен пройти
    ВЕСЬ flow до конечного элемента который user reported. Пример:
    r25..r28 я shipал и claim'ил "tabs render" — но не доходил до scroll
    внутри активной вкладки → user видел тот же bug что и до фикса 4 раза.
    Чеклист: (a) invoke целевого элемента (`tools/brat-verify.ps1`
    `-Action uia`), (b) check ВСЕ interactive elements в его scope,
    (c) screenshot bottom of viewport, (d) confirm exact strings user
    мог искать.

14. **Git push reminder pattern** (added 2026-05-25 после второго
    "ты опять забыл git" от user'а). После каждого commit IMMEDIATELY
    выполняй `git push -u origin HEAD` для текущей task-ветки.
    Не задерживай push
    "пока build идёт" — commit и push должны быть атомарной парой.
    Если push заблокирован gate'ом — сразу info user'у текущий state
    + что ждём. User видит remote main как single source of truth;
    локальные коммиты "не существуют" для него.

15. **CI status hygiene — never accumulate red Xs** (added 2026-05-25
    после "опять забыл проверку состояния git" от user'а, скриншот с
    r24..r29 все red 4/6). Pattern of failure: каждый -rN ship'ался
    с Linux MVM hash drift (red `test`), я pushил с TOLERATE_FAILURE,
    дальше код и снова TOLERATE_FAILURE, и так 5 коммитов с red X на
    главной странице commits. User notices — это плохой signal.

    Mechanism (r30):
    - `tools/watch-after-push.ps1` — background watcher polls CI ~10
      min after push. If `test` fails with Linux hash drift → parses
      "Actual:" hash from job log → writes `.git-suggested-hash-bump.txt`
      at repo root.
    - `.githooks/post-push` — launches the watcher (manual invoke
      because git has no native post-push; alias `git pushw` chains).
    - **NEW RITUAL**: at start of EVERY session AND before EVERY
      ship-rolling-candidate, run:
      ```
      for sha in $(git log --pretty=format:'%h' -7); do
        echo "=== $sha ==="
        gh api repos/PavelLizunov/VPNRouter/commits/$sha/check-runs \
          --jq '.check_runs[] | select(.conclusion=="failure") | .name'
      done
      ```
      If ANY red is found, FIX it BEFORE any new code change. Check
      `.git-suggested-hash-bump.txt` first — if present, apply that
      bump to MainWindowViewModelCharacterizationTests.cs.

## Git safety

- `main` трактуется как protected: никаких прямых/force push. Изменения идут
  через task branch + PR.
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
