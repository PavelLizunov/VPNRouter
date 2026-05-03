# Session handoff — 2026-05-03 (compact восстановление)

Сессия закрывается context-compact. Этот файл — primary state restore
для следующего Claude. Прочитай его в начале session, затем
`.claude_handoff.md` (если активен), затем актуальные `plans/*.md`.

## Текущее состояние (TL;DR)

- **Latest stable**: `v2.31.4` (F-25 tail: Recheck button visibility regression)
- **Live prerelease**: `v2.31.5-r1` (test-infra hardening, NO product changes)
- **Awaiting**: user команда `"cut"` / `"ok"` / `"promote"` для promotion
  v2.31.5 → stable. Per **новой политики 2026-05-03**, autonomous cut
  больше не делается.

## Релиз-политика (важно — ИЗМЕНЕНА в этой сессии)

**Commit `ca451c7` (2026-05-03) обновил `CLAUDE.md` rule #1 + #6 +
`CLAUDE.local.md` Release Process.** Было: autonomous full cycle до
stable. Стало: ship `-rN` autonomously → verify → **STOP wait for
user "cut"** → cut stable.

Урок: в одной session 2026-05-02..03 cut'нули 5 stable releases
подряд, 2 из них (v2.31.2 + v2.31.4) оказались partial fixes
поскольку cut был автоматический по "all-green" gate без
MCP-verification. Поймали только потому что MCP retest сделали
сами после cut. v2.31.3 пришлось shipать как hotfix.

**Новый поток**:
1. Code → build → tests → commit → push → ship `-rN` (всё autonomous)
2. MCP+UIA verify где testable (или explicit "Core-only / not
   UI-testable" label)
3. Доложить user'у status-summary (READY)
4. **Ждать explicit "cut" / "ok" / "promote"** — не cut'ать
   автоматически
5. Tiny config-only / typo / version bump fixes — exception: ship +
   flag + let user decide if нужен ceremonial stable

## v2.31 cycle итог (закрыт)

10 итераций, 5 stable releases в одной session 2026-05-02..03.

| Stable | Date | Scope |
|---|---|---|
| v2.31.0 | 2026-05-02 | Stability + A11y (39 fixes + 5 tests, r1..r5) |
| v2.31.1 | 2026-05-02 | AU-9 + F-4 + F-6 (3+2) |
| v2.31.2 | 2026-05-02 | F-25 prevent-new (1+1) |
| v2.31.3 | 2026-05-03 | F-25 heal-old + UI polish (1) |
| v2.31.4 | 2026-05-03 | F-25 tail: Recheck button (1) |
| v2.31.5-r1 | 2026-05-03 | **Test-infra hardening** (+6 regression tests) |

Total v2.31 cycle: 45 fixes + **14 unit tests** (после shipped
v2.31.5).

## Что сейчас "в работе"

### v2.31.5-r1 (PENDING USER CUT)

Released as prerelease. Verification gate ВСЯ зелёная:
- `dotnet build -c Release` → 0 errors
- 34/34 tests pass (28 existing + 6 new pinning тесты для
  v2.31.x фиксов)
- Mac DMG / Linux AppImage+.deb / APT publish CI → all `success`
- 12 assets на `v2.31.5-r1`
- **No product changes** vs v2.31.4 — только tests + docs
- MCP+UIA verify N/A (test-infra, no UI affordance)

**6 new regression-pin tests**:
- `FreeConfigCacheMigrationTests` — F-25 heal-old (sub-5ms → 0)
- `AvailableRuleTypesSurfaceTests` — AU-10 (ComboBox содержит
  domain_regex + process_path) — uses `[AvaloniaFact]` для VM ctor
- `FreeConfigItemViewModelDisplayTests` — F-25 polish (Verified+0 →
  "— ✓✓")
- `BoolToChevronConverterTests` — F-3 (default ▲▼ vs param ▽›)

**Action when session continues**: when user says **"cut"**, выполнить:
```bash
# 1. Bump AppVersion 2.31.5-r1 → 2.31.5
# 2. git commit + push to both remotes
# 3. powershell -ExecutionPolicy Bypass -File ./build.ps1 -Version "2.31.5" -Upload
# 4. git fetch github --tags && git push origin v2.31.5
# 5. gh release edit v2.31.5 --notes-file plans/release-notes-v2.31.5.md
# 6. gh release delete v2.31.5-r1 --yes
# 7. Mac/Linux/APT CI → wait → 12 assets
# 8. Update README.md + README.ru.md → 2.31.5 + commit + push
# 9. Verify Homebrew Cask auto-bump
# 10. Verify APT repo HTTP 200
# 11. Update MEMORY.md
```

## Backlog (приоритезированный)

| # | Item | Effort | Notes |
|---|---|---|---|
| 1 | **Real RTT measurement** — replace `TcpClient.ConnectAsync` with kernel-level RTT (Win32 `WSAIoctl(SIO_TCP_INFO)` или ICMP). Deeper fix для F-25 root cause — TcpClient inflates accuracy для cached routes. | 3-5 ч + cross-platform consideration | Бьёт root cause, но v2.31.2-r1 plausibility gate уже compensates user-side |
| 2 | **Visual-diff regression** — pin baseline PNG в `screenshots/baseline/`, diff вне порога → fail. Сейчас screenshots inspectional only. | 2-3 ч | Документировано в `VPNRouter.Tests/CLAUDE.md` "Roadmap" |
| 3 | **Headless test hangs** — `dotnet test` без `--filter` иногда зависает на PageScreenshotTests. Workaround: filter per class. | 1-2 ч инвестигация | Документировано в `VPNRouter.Tests/CLAUDE.md` "Known issues". Не блокирует |
| 4 | **Plan archive cleanup** — `plans/archive/2026/` для closed roadmaps (v2.28.x, v2.30.x). | 10 мин janitorial | Низкий приоритет |
| 5 | **Release page cleanup #2** — после v2.31.5 будет ~33 entries. Cap ~30. Drop старые non-milestone. | 5 мин | Сейчас 32 после прошлой чистки + 5 новых v2.31.x = ~37 |
| 6 | **Android v3.0 Phase 1** — libbox.aar integration + VpnRouterService.kt shim + real Avalonia.Android port | Multi-day, user-driven | Phase 0 done в commits cd36f34 + b59d51d (2026-04-29). См. `plans/vpnrouter-android-research.md` |

## Не в backlog

- **F-25** — closed (3 iterations: prevent-new + heal-old + button-fix)
- **AU-9** — closed (RuntimeStatusDetector dispose + EtwProcessMonitor.Dispose)
- **F-4 / F-6** — closed (v2.31.1)
- **All Pillar 1-4 v2.31.0 items** — closed
- **v2.31.5-r1 ship** — closed; awaits user cut

## Ключевые commit'ы этой сессии (2026-05-02..03)

```
48b50fd test(v2.31.5-r1): pin v2.31 fix invariants via headless + plain xUnit
ca451c7 docs(claude): stable cut requires user command (post v2.31.4 lesson)
879f722 docs(readme): bump build script examples to 2.31.4 (stable)
d2f7268 release: cut v2.31.4 stable (drop -r1 suffix)
e1d00b3 fix(v2.31.4-r1): F-25 tail — Recheck button visibility on healed entries
4a1822f docs(readme): bump build script examples to 2.31.3 (stable)
0294f0f release: cut v2.31.3 stable (drop -r1 suffix)
d5fd2c6 fix(v2.31.3-r1): F-25 follow-up — heal old sub-5ms cache entries
6d1a8d2 docs(readme): bump build script examples to 2.31.2 (stable)
baac8f4 release: cut v2.31.2 stable (drop -r1 suffix)
fc4e795 fix(v2.31.2-r1): F-25 implausible 1ms latency on Saved configs
003c7f3 chore(.gitignore): re-exclude tools/VpnRouterTestMcp/{bin,obj}
d7eef43 docs(readme): bump build script examples to 2.31.1 (stable)
964046f release: cut v2.31.1 stable (drop -r1 suffix)
60b023e feat(v2.31.1-r1): AU-9 handle leak fix + F-4 + F-6 (deferred from v2.31.0)
8590b81 release: cut v2.31.0 stable (drop -r5 suffix)
```

## Test machine state (для MCP testing)

- App running на v2.31.4 (auto-update подцепит v2.31.5 когда published)
- VPN connected при последней проверке (subscription mode, full tunnel)
- Saved tab: cache empty (entries got dropped during F-25 testing
  earlier session). Если нужно protest на полной cache — Search →
  ✓✓ Найти рабочие конфиги → Deep verify → wait ~1-2 мин
- Theme: Light, Locale: RU (последняя установка)

## Infrastructure (без изменений)

| Что | Где |
|---|---|
| GitHub repo | `PavelLizunov/VPNRouter` |
| Forgejo mirror | `ssh://git@10.9.1.1:18222/slovn/vpnrouter.git` (через AmneziaWG VPN) |
| Mac build host | `slovn@192.168.0.246` (через host AmneziaWG, key `id_ed25519`) |
| Custom domain | `vpn.ninitux.com` (CNAME → `pavellizunov.github.io`) |
| Homebrew tap | `PavelLizunov/homebrew-vpnrouter` (auto-bump на stable) |
| APT repo | `vpn.ninitux.com/apt/` (reprepro signed) |
| Test MCP server | `mcp__vpnrouter-test__*` (focus_window, screenshot, mouse_click, type_text, press_key, list_windows, mouse_move, wait) |

## Cross-refs

- `CLAUDE.md` — golden rules (rule #1 + #6 обновлены в commit ca451c7)
- `CLAUDE.local.md` — Release Process + урок v2.31.2→r3→r4
- `VPNRouter.Tests/CLAUDE.md` — headless harness docs
- `plans/release-notes-v2.31.4.md` — last stable release notes
- `plans/release-notes-v2.31.5-r1.md` — pending stable
- `plans/vpnrouter-v2.31.0-roadmap.md` — original v2.31 plan (всё закрыто)
- `plans/vpnrouter-extended-audit-2026-05-02.md` — 47-finding audit
- `plans/vpnrouter-ux-audit-2026-05-01.md` — 72-finding audit
- `~/.claude/projects/.../memory/MEMORY.md` — Current stable: v2.31.4 (нужно бампнуть после cut)

## Если user скажет "продолжай" / "что дальше" в новой сессии

Default ответ: "v2.31.5-r1 ready, ждём 'cut' для stable promotion.
После cut можно браться за **Visual-diff regression** (#2) или
**Real RTT measurement** (#1) — оба укрепляют foundation для
будущих циклов".
