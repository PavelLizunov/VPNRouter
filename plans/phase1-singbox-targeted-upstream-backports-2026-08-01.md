# Phase 1 — Точечные upstream-backport'ы sing-box (TUN NAT + DNS single-flight)

**Owner**: Qwen session 2026-08-01
**Branch**: `qwen/singbox-1.13.15-base`
**Roadmap ref**: `plans/OPEN-DEFECTS.md` § Internet-optimization research — 2026-08-01, P2 (a)+(b) + `plans/qwen-internet-optimization-research-2026-08-01.md` §F1/F2/F6
**Effort**: ~1 час (скрипты + тест + локальная Windows-сборка)
**Risk**: MEDIUM — меняется shipped native core (два cherry-pick'а в дерево форка), но config schema и product C# остаются неизменными; fork-пин не ротируется.
**Blast radius**: `tools/build-singbox-lx.ps1`, `tools/build-singbox-lx.sh`, `VPNRouter.Tests/SingBoxBackportBuildScriptTests.cs` · <100 LOC product/tooling diff (без brief) · runtime: два upstream-фикса в бинаре sing-box-lx
**Rollback**: `git revert <implementation-commit>`; неизменяемый fork-пин `c7a2592e750406ade9ebaae1d0fdb7482fc0773e` остаётся прежним — откат не требует re-pin.

## Why

Десктопный sing-box core — это форк `Leadaxe/sing-box-lx`, запиненный на коммите `c7a2592e` (upstream-база доказана `go.mod` форка: `sing-tun` v0.8.10). Исследование 2026-08-01 (read-only, adversarially validated Claude Opus) выявило два отсутствующих upstream-фикса:

1. **F1 — TUN system-stack TCP NAT collision** (sing-box `0b7ffba`, фикс в sing-tun `8caaa93`): при быстром переиспользовании одного source IP:port для другого destination, пока старая NAT-запись жива, пакеты могут уйти не тому адресату. Windows/Linux используют `system` TUN-стек (`ConfigGenerator.cs:39-40`) → exposure есть; macOS на `gvisor` не затронут.
2. **F2 — детерминированный self-deadlock вложенного single-flight в DNS** (sing-box `72a8723e`): внешний DNS-запрос Q на транспорте T1 внутри себя бутстрапит T2, которому нужен тот же Q; дедупликация ждёт сама себя. Применимые кандидаты: DoH `vpn-dns` hostname с `DomainResolver=local-dns` и proxy-outbound'ы hostname с `DomainResolver=local-dns`.

Оба фикса — точечные upstream-коммиты. Codex доказал на временном клоне, что оба cherry-pick'а ложатся чисто, в порядке, на `c7a2592e`. Задача — применить их build-time (в скриптах сборки), не ротируя базу форка.

**Источник коммитов (immutable)**: рабочее дерево сборки — клон `Leadaxe/sing-box-lx`; его `origin` указывает на Leadaxe и НЕ является доказанным источником двух upstream-коммитов SagerNet. Скрипты обязаны fetch'ить оба SHA из неизменяемого upstream-URL `https://github.com/SagerNet/sing-box.git` (не из `origin`/любого mutable remote).

**Почему НЕ полный rebase на v1.13.15 / v1.14 (F6):** ротация базы забирает непроинспектированные изменения (не утверждаем, что они безопасны), требует одновременного re-pin `libcronet` (версия + SHA256) на Linux и перепроверки всех downstream-патчей на новом дереве. Это отдельная measure-first задача (F6), не входящая в этот таск.

**Почему НЕ Android libbox / Avalonia (F3/F5):** Android libbox 1.13.10 — отдельный P3-пункт с узким exposure (BypassRu UDP DNS); Avalonia Android — P3 controlled upgrade research. Ни один из них не пересекается с desktop build-скриптами sing-box-lx.

## What

### Изменяемые файлы

1. **`tools/build-singbox-lx.ps1`** — после `Assert-GitHead` для sing-box-lx (шаг [1/4]), до клонирования wireguard-go (шаг [2/4]): добавить блок cherry-pick двух upstream-коммитов + fail-closed assertions.

2. **`tools/build-singbox-lx.sh`** — аналогичный блок после `assert_git_head "$SRC" "$LX_COMMIT" "sing-box-lx"`, до клонирования wireguard-go.

3. **`VPNRouter.Tests/SingBoxBackportBuildScriptTests.cs`** — новый xUnit source-invariant тест (стиль `BuildLinuxAppImageToolPinTests`: repo-root discovery через `AppContext.BaseDirectory` parent walk, без новых зависимостей).

### Что НЕ меняется

- Fork-пин: `c7a2592e750406ade9ebaae1d0fdb7482fc0773e` — неизменяем.
- Wireguard-go-awg2-lx пин: `0c0c10b5d3236796bd3832a6813223d6dc7d0bb1` — неизменяем.
- Все существующие AWG/XHTTP/downstream-патчи (WSAEFAULT send+recv, H4 reserved-byte gate, WSAENOBUFS retry) — сохраняются без изменений.
- Существующие sing-box `check` + handshake smoke — сохраняются.
- Linux `libcronet` 1.13.14 / SHA256 — не трогаем (core source остаётся 1.13.13 + два backport'а; cronet не связан).
- `AppVersion`, release-файлы — не трогаем.
- `build.ps1`, `build-mac.sh`, `.github/workflows/*` — не трогаем.

### Логика cherry-pick блока (оба скрипта, эквивалентно)

```
# После checkout + Assert-GitHead на $LX_COMMIT:
# UPSTREAM = https://github.com/SagerNet/sing-box.git  (immutable; origin = Leadaxe, НЕ источник)
# 1. git fetch https://github.com/SagerNet/sing-box.git <SHA1> <SHA2> (точные SHA, не ветки/теги, НЕ origin)
# 2. git cherry-pick --no-commit <SHA1>
# 3. git cherry-pick --no-commit <SHA2>
#    (--no-commit: не создаём коммиты → не нужен Git user identity)
# 4. Fail-closed assertions:
#    a) go.mod содержит "github.com/sagernet/sing-tun v0.8.11"
#       И НЕ содержит "github.com/sagernet/sing-tun v0.8.10"
#    b) dns/client.go содержит "compatible.Map[transportCacheKey, chan struct{}]"
#    c) dns/client.go содержит "cacheKey := transportCacheKey{Question: question, transportTag: transport.Tag()}"
#    d) dns/client.go НЕ содержит "compatible.Map[dns.Question, chan struct{}]"
```

Целевые коммиты (порядок применения):
1. `0b7ffbaafa5f060dd8c762dfbc751d592cba1fea` — sing-tun bump (F1: TUN TCP NAT collision fix)
2. `72a8723e13b9574664f4c78e588069fa4aca6fc9` — DNS single-flight deadlock fix (F2)

## How

1. В `tools/build-singbox-lx.ps1` после блока `Assert-GitHead -RepoDir $src -Expected $LX_COMMIT -Label 'sing-box-lx'` добавить:
   - `$UPSTREAM = 'https://github.com/SagerNet/sing-box.git'` (immutable; origin = Leadaxe, не источник).
   - `$BACKPORTS = @('0b7ffbaafa5f060dd8c762dfbc751d592cba1fea', '72a8723e13b9574664f4c78e588069fa4aca6fc9')`
   - `Invoke-Git @('-C', $src, 'fetch', '--quiet', $UPSTREAM, $BACKPORTS[0], $BACKPORTS[1])` (fetch из явного upstream URL, не из origin).
   - Цикл: `Invoke-Git @('-C', $src, 'cherry-pick', '--no-commit', $sha)` для каждого SHA.
   - Fail-closed: прочитать `go.mod`, assert `sing-tun v0.8.11` present + `v0.8.10` absent.
   - Fail-closed: прочитать `dns/client.go`, assert три строки (две present, одна absent).

2. В `tools/build-singbox-lx.sh` после `assert_git_head "$SRC" "$LX_COMMIT" "sing-box-lx"` добавить эквивалентный bash-блок:
   - `UPSTREAM="https://github.com/SagerNet/sing-box.git"` (immutable; origin = Leadaxe, не источник).
   - `git -C "$SRC" fetch --quiet "$UPSTREAM" <SHA1> <SHA2>` (fetch из явного upstream URL, не из origin).
   - `git -C "$SRC" cherry-pick --no-commit <SHA1>` / `<SHA2>`
   - `grep -q` assertions по `go.mod` и `dns/client.go` с `exit 1` при провале.

3. Создать `VPNRouter.Tests/SingBoxBackportBuildScriptTests.cs`:
   - Repo-root discovery: parent walk от `AppContext.BaseDirectory` (стиль `BuildLinuxAppImageToolPinTests`).
   - Прочитать оба скрипта (`tools/build-singbox-lx.ps1`, `tools/build-singbox-lx.sh`).
   - Assert: оба содержат полные SHA `0b7ffbaafa5f060dd8c762dfbc751d592cba1fea` и `72a8723e13b9574664f4c78e588069fa4aca6fc9`.
   - Assert: оба содержат точный immutable upstream URL `https://github.com/SagerNet/sing-box.git` (защита от будущего переключения на mutable/incorrect remote, например origin=Leadaxe).
   - Assert: оба содержат `cherry-pick --no-commit` (или эквивалентный паттерн).
   - Assert: оба содержат fail-closed assertions по `sing-tun v0.8.11` / `v0.8.10` и по `transportCacheKey` / `dns.Question`.

4. Запустить verification gates (см. ниже).

### Tests written

- `SingBoxBackportBuildScriptTests.BothScripts_PinFullBackportShas` — оба скрипта содержат полные SHA обоих backport-коммитов.
- `SingBoxBackportBuildScriptTests.BothScripts_FetchFromImmutableUpstreamUrl` — оба скрипта fetch'ат из точного `https://github.com/SagerNet/sing-box.git`, не из origin/mutable remote.
- `SingBoxBackportBuildScriptTests.BothScripts_UseNoCommitCherryPick` — cherry-pick без коммита (не нужен Git identity).
- `SingBoxBackportBuildScriptTests.BothScripts_FailClosedOnGoModSingTun` — assert `v0.8.11` present / `v0.8.10` absent в обоих скриптах.
- `SingBoxBackportBuildScriptTests.BothScripts_FailClosedOnDnsClientTransportCacheKey` — assert `transportCacheKey` present / `dns.Question` absent в обоих скриптах.

### Verification approach

- Full test suite (xUnit) — все существующие + новый тест.
- Реальная Windows-сборка core через `tools/build-singbox-lx.ps1` — проверяет cherry-pick + fail-closed + существующие AWG/XHTTP check/handshake smoke.
- `bash -n tools/build-singbox-lx.sh` — синтаксис.
- Mac/Linux CI / native build — когда доступны.
- Независимый security/supply-chain review (build fetch logic меняется; fetch идёт из явного immutable `https://github.com/SagerNet/sing-box.git`, не из origin).

## Verification gate

- [ ] **Gate 1 — Build clean**: `dotnet build VPNRouter.sln -c Release` → 0 errors.
- [ ] **Gate 2 — Tests green**: full `VPNRouter.Tests` suite passes. New `SingBoxBackportBuildScriptTests` included.
- [ ] **Gate 3 — Docs**: brief Outcome filled. README/CLAUDE.md unchanged (не user-facing).
- [ ] **Gate 4 — Self-review**: `security-review` — build fetch logic меняется (git fetch + cherry-pick); обязателен. `simplify` — N/A (diff <100 LOC).
- [ ] **Gate 5 — MCP verify**: N/A — no UI surface.
- [ ] **Gate 6 — Characterization diff**: N/A — not a god-file split.

### Дополнительные verification-шаги (вне стандартных gates)

1. `powershell -ExecutionPolicy Bypass -File tools/build-singbox-lx.ps1` — реальная Windows-сборка core (включает существующие check + handshake smoke).
2. Built binary: `sing-box.exe version` → tag line содержит `with_awg` + `with_xhttp`; `check` на AWG probe config проходит; handshake smoke без WSAEFAULT.
3. `bash -n tools/build-singbox-lx.sh` — синтаксис (локально).
4. Mac/Linux CI / native build — когда доступны (GitHub Actions `build-mac.yml` / `build-linux.yml`).
5. Независимый security/supply-chain review: git fetch точных SHA из явного immutable upstream `https://github.com/SagerNet/sing-box.git` (не из origin=Leadaxe) + cherry-pick --no-commit — новый паттерн в build-скриптах.

## Outcome

**Status**: PASS
**Commits**: `f586f473` (brief), `889b0fe7` (implementation). Этот Outcome — docs-only follow-up после завершения verification.
**Pushed**: да, draft PR #96 — https://github.com/PavelLizunov/VPNRouter/pull/96
**Test deltas**: один новый Fact-класс `SingBoxBackportBuildScriptTests` — 22 assertion'а по исполняемым фрагментам обоих скриптов; перед матчингом тест стрипает PowerShell block-комментарии и full-line `#`-комментарии (защита от ложного срабатывания на закомментированных командах).
**Files changed**: `tools/build-singbox-lx.ps1`, `tools/build-singbox-lx.sh`, `VPNRouter.Tests/SingBoxBackportBuildScriptTests.cs`

**Gate results:**
- [x] Gate 1: PASS — `dotnet build VPNRouter.sln -c Release`: 0 warnings / 0 errors.
- [x] Gate 2: PASS — targeted regression 23/23; GitHub commit CI (test / go-test-windows / grep) — green. Локальный full suite: 2686 passed, 23 failed (только `UnauthorizedAccess` к `C:\ProgramData\VPNRouter` — локальный permission noise, не связан с изменением), 2 skipped; GitHub full test — passed.
- [x] Gate 3: PASS — Outcome заполнен; README/CLAUDE.md не менялись (изменение не user-facing).
- [x] Gate 4: PASS — Qwen ponytail review: сокращение комментариев/boilerplate; независимый Opus supply-chain review — PASS после усиления теста против закомментированных команд.
- [-] Gate 5: N/A — no UI surface
- [-] Gate 6: N/A — not a god-file split

**Дополнительная verification (вне стандартных gates):**
- Реальная Windows-сборка `tools/build-singbox-lx.ps1` — PASS: оба точных backport'а применены; бинарь `v1.13.13-lx-awg` с тегами `with_awg` + `with_xhttp`; AWG config `check` и runtime handshake — PASS, без WSAEFAULT.
- `bash -n tools/build-singbox-lx.sh` локально недоступен (WSL без `/bin/bash`); синтаксис покрыт native Linux/macOS CI.
- Native CI (head `889b0fe7`, `upload_to_release=false`): macOS — success (https://github.com/PavelLizunov/VPNRouter/actions/runs/30715941305), Linux — success (https://github.com/PavelLizunov/VPNRouter/actions/runs/30715941366).

**Surprises encountered**:
- Локальный permission noise: 23 падения full suite — только `UnauthorizedAccess` к `C:\ProgramData\VPNRouter`; не связано с изменением, GitHub full test зелёный.
- Локальный Bash недоступен (WSL без `/bin/bash`) — `bash -n` выполнить нельзя; покрыто native CI.
- Первый diff-only Opus review не имел контекста `Invoke-Git` и выдал ложный High по обработке ошибок; проверка показала, что wrapper уже бросает исключение на ненулевой exit code.
- Финальный full-diff review нашёл и исправил пробел в матчинге комментариев теста (усиление против закомментированных команд).

**Follow-ups spawned**:
- В этот PR не spawned: полная ротация базы форка (F6) и Android libbox / Avalonia (F3/F5) остаются отдельными backlog-задачами.

**Lessons for methodology doc** (if any):
- Независимым ревьюерам нужно передавать untracked-файлы и контракты релевантных helper'ов (например, `Invoke-Git`), иначе diff-only review даёт ложные срабатывания.
