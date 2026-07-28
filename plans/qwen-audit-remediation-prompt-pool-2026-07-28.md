# VPNRouter — Qwen audit verification and remediation prompt pool

Дата: 2026-07-28

Статус: active planning; product code unchanged

Audit source: `plans/qwen-full-app-audit-2026-07-28/RESULTS.md`

Audit branch: `codex/qwen-full-app-audit-2026-07-28`

Audit PR: https://github.com/PavelLizunov/VPNRouter/pull/48

## 1. Назначение

Этот документ — долговечная очередь работ по 39 находкам Qwen-аудита. Он
предназначен для последующих Codex, Claude Code и других code-review сессий.

План решает четыре задачи:

1. Завершает независимое подтверждение или опровержение каждой находки.
2. Разбивает подтверждённые дефекты на небольшие тематические PR.
3. Для каждого PR задаёт отдельные пулы verification, implementation, tests,
   validation и Git/CI задач.
4. Даёт готовые копируемые промпты, по которым будущий агент может продолжить
   работу без восстановления контекста из чата.

Это не команда немедленно исправить все 39 пунктов. Сначала выполняется
adjudication: `CONFIRMED`, `PARTIALLY_CONFIRMED`, `REFUTED`, `STALE`,
`DUPLICATE` или `INCONCLUSIVE`. Код меняется только для подтверждённых
дефектов.

## 2. Источники истины

Перед любым prompt-пакетом агент обязан прочитать:

- `AGENTS.md`;
- `.claude_handoff.md`;
- relevant zone `CLAUDE.md`;
- `plans/qwen-full-app-audit-2026-07-28/RESULTS.md`;
- этот plan;
- относящиеся к ID строки в `plans/OPEN-DEFECTS.md`;
- raw Qwen output только как источник гипотез, но не как доказательство.

Порядок доверия:

1. Реальный production control flow текущего commit.
2. Исполняемый regression test, проверяющий тот же production path.
3. Platform/API contract из первичного источника.
4. Комментарии и планы.
5. Qwen/Claude/Codex выводы.

Ни одна находка не считается подтверждённой только потому, что она записана в
`RESULTS.md` или `OPEN-DEFECTS.md`.

## 3. Обязательные ограничения

### 3.1 До изменения кода

- Зафиксировать текущий commit SHA и `git status`.
- Выполнить session-start red-CI ritual из `AGENTS.md`.
- Для Core/ViewModel/Android/platform задач запустить
  `.agents/skills/phase-task-launcher/SKILL.md`.
- Прочитать все callers и sibling paths изменяемого метода.
- Найти существующий helper/pattern до добавления нового.
- Сначала попытаться опровергнуть дефект.
- Зафиксировать verdict и final severity.

### 3.2 При изменении кода

- Один root cause исправляется в общей точке, а не в каждом caller.
- Не добавлять speculative abstraction или новую dependency.
- Для нетривиальной логики оставить минимальный runnable regression check.
- Не запускать VPNRouter, sing-box, installers, service или firewall на dev box.
- Не трогать `C:\Program Files\VPNRouter` и `%ProgramData%\VPNRouter`.
- Сохранять unrelated user changes.

### 3.3 Git и CI

- Каждый тематический пакет выполняется в отдельной
  `codex/<short-topic>` ветке от актуального `origin/main`.
- Один PR содержит один root cause или небольшой связанный кластер.
- После commit немедленно `git push -u origin HEAD`.
- После каждого push запускать
  `tools/verify-last-commit-ci.ps1` до следующего code change.
- Красный PR не merge-ить.
- Merge, tag, release, deploy и stable cut требуют явной команды владельца.

### 3.4 Live verification

- До release используются unit/contract/source tests.
- После явной команды на rolling candidate обязательно выполнить
  `post-ship-mcp-verify`.
- Любая install/launch/UI/VPN проверка выполняется только на
  `windows-brat` (`192.168.0.106`) через WinRM.
- Никогда не заменять недоступный brat проверкой на dev box.

## 4. Verdict и severity

### Verdict

| Verdict | Значение |
|---|---|
| `CONFIRMED` | Достижимый production path и последствие доказаны |
| `PARTIALLY_CONFIRMED` | Механика реальна, но scope/impact/severity завышены |
| `REFUTED` | Существующий guard или фактический flow делает дефект невозможным |
| `STALE` | Дефект уже исправлен в проверяемом commit |
| `DUPLICATE` | Тот же root cause уже покрыт другим ID/ledger entry |
| `INCONCLUSIVE` | После всех безопасных проверок нужен внешний/live факт |

### Severity

| Severity | Значение |
|---|---|
| P0 | Release/stable blocker: массовый leak, takeover, гарантированный broken update/build |
| P1 | Важный достижимый дефект, security/data-loss/lifecycle failure |
| P2 | Ограниченный edge case, robustness, UX или bounded resource issue |
| P3 | Hygiene/copy/test-only follow-up без material runtime impact |

Severity исходного Qwen отчёта не наследуется автоматически.

## 5. Частичный независимый checkpoint

Внешний verifier дошёл до лимита до создания полной матрицы. В его тексте
есть явные verdict только для 18 из 39 ID. Эти verdict тоже являются
гипотезами до записи полного evidence table.

Предварительно:

- `FAIL-1`, `FLOW-1`, `CLI-2`, `UI-2` подтверждены.
- `DATA-1`, `NET-1` подтверждены, но предложен downgrade P1 → P2.
- `UPD-1` механически подтверждён, но P0 вероятно завышен: sidecar SHA
  приходит из того же GitHub release/trust root, поэтому не даёт независимой
  authenticity-защиты.
- `LIFE-1` в исходной формулировке опровергнут: `SingBoxManager.Stop`
  освобождает semaphore до повторного singleton `Dispose`; остаётся только
  возможный P3 lifecycle/handle churn.
- `DATA-2` опровергнут: `AppJsonContext` содержит `MaxDepth=32`.
- `AND-2` опровергнут: `super.onRevoke()` вызывает стандартный
  `VpnService.onRevoke()`, чья default implementation вызывает `stopSelf()`.
- `DATA-5` вероятно опровергнут, но только после доказательства parser
  constraints: Naive userinfo декодируется и делится по первому `:`, поэтому
  username с сохранённым `:` не создаётся, а password — последний компонент
  ключа.
- `FW-1`/`FW-2` имеют реальную IPv6 ruleset-механику, но исходные impact и
  reachability могут быть завышены.
- `PKG-1` реален только когда wgturn build branch достижим; текущий public CI
  этот branch не выполняет.
- `SUP-3` существует, но signing workflow сейчас manual/inert без secrets.

Checkpoint не заменяет Prompt P00.

## 6. Coverage map: 39 ID → owner prompt

| ID | Исходная severity | Owner prompt | Кластер |
|---|---:|---|---|
| UPD-1 | P0 | P01 | Desktop update integrity |
| UPD-2 | P1 | P01 | Repair trampoline |
| LIFE-1 | P1 | P02 | TUN ownership lifecycle |
| FAIL-1 | P1 | P02 | Failover restart wiring |
| FW-1 | P1 | P03 | Linux kill-switch IPv6 |
| FW-2 | P1 | P03 | macOS kill-switch IPv6 |
| CFG-1 | P2 | P04 | Custom DNS strategy |
| CFG-2 | P2 | P04 | Injected tag collision |
| PROTO-1 | P2 | P04 | DNS-tunnel deep verify |
| DATA-1 | P1 | P05 | Atomic settings save |
| DATA-2 | P2 | P05 | JSON depth guard |
| DATA-3 | P2 | P05 | MTU migration |
| DATA-4 | P2 | P05 | Duplicate free-config IDs |
| DATA-5 | P2 | P05 | Subscription dedupe identity |
| DATA-6 | P2 | P05 | Atomic free-config cache |
| NET-1 | P1 | P05 | Subscription response bound |
| FLOW-1 | P1 | P06 | Smart Connect persistence |
| UI-1 | P2 | P06 | Update localization |
| UI-2 | P2 | P06 | Narrow rule layout |
| CLI-1 | P1 | P07 | CLI stop ownership protocol |
| CLI-2 | P1 | P07 | PID reuse/ownership |
| AND-1 | P1 | P07 | Android error secret scrub |
| AND-2 | P2 | P07 | Android onRevoke lifecycle |
| PKG-1 | P1 | P08 | macOS ARCH |
| SUP-1 | P1 | P08 | appimagetool pin |
| SUP-2 | P1 | P08 | libcronet digest |
| SUP-3 | P2 | P08 | Signing action pins |
| SUP-4 | P2 | P08 | wgturn source pin |
| SEC-1 | P1 | P09 | Subscription URL logging |
| SEC-2 | P1 | P09 | ProgramData ACL |
| SEC-3 | P2 | P09 | wgturn argument injection |
| OBS-1 | P1 | P09 | Clash token logging |
| OBS-2 | P2 | P09 | Crash log tail memory |
| ZAP-1 | P1 | P10 | Partial Zapret update marker |
| ZAP-2 | P2 | P10 | Emergency process disposal |
| ZAP-3 | P2 | P10 | Wgturn atomic replacement |
| PERF-1 | P2 | P10 | ETW monitor disposal |
| PERF-2 | P2 | P10 | Free-config owned resources |
| TEST-1 | P2 | P11 | Kill-switch wiring coverage |

Coverage invariant:

```text
Expected IDs: 39
Owner prompts: P01-P11
IDs with no owner: 0
IDs with multiple owners: 0
```

## 7. Execution graph

```text
P00 independent adjudication
  |
  +--> P01 update/recovery ---------+
  +--> P02 lifecycle/failover ------+
  +--> P03 kill-switch -------------+
  +--> P04 config/protocol ---------+
  +--> P05 data/subscriptions ------+
  +--> P06 desktop/UI --------------+--> P11 cross-cut regression/bug-hunt
  +--> P07 CLI/Android -------------+               |
  +--> P08 packaging/supply chain --+               v
  +--> P09 security/diagnostics ----+--> P12 ledger and PR integration
  +--> P10 updater/resources -------+               |
                                                  v
                              P13 optional rolling ship, user-command only
```

P01-P10 могут выполняться параллельно только после adjudication относящихся к
ним ID. Они не должны изменять один общий worktree.

## 8. Универсальные task pools

Каждый owner prompt имеет пять обязательных типов задач.

### Type V — Verification

- Воспроизвести claim как control-flow graph.
- Найти entry point, callers, state owner и cleanup.
- Найти existing guard/helper/test.
- Доказать reachability входных данных.
- Разделить mechanism, impact и severity.
- Проверить ledger на duplicate/stale.
- Записать verdict до изменения кода.

### Type I — Implementation

- Найти минимальную общую точку root fix.
- Сохранить sibling behavior.
- Не добавлять dependency без необходимости.
- Не создавать interface/factory для одной реализации.
- Обработать trust-boundary input и failure rollback.
- Обновить comments только если изменился contract.

### Type T — Tests

- Один минимальный regression test на root cause.
- Negative/edge case, который отличает фикс от прежнего поведения.
- Использовать temp files/fake handlers/pure builders вместо live system state.
- Не source-pin там, где можно проверить runtime contract.
- Запустить targeted suite, затем required regression suite.

### Type S — Safety and validation

- `git diff --check`.
- Build затронутых проектов.
- Platform syntax/static validation.
- Проверка отсутствия secret/absolute-path/log regressions.
- Никаких live VPN действий на dev box.

### Type G — Git, CI and handoff

- Обновить verdict/acceptance в task plan.
- Отметить соответствующий ledger ID только после доказанного fix/refutation.
- Commit без bypass hooks.
- Немедленный push.
- Дождаться фактического CI результата.
- Открыть/обновить draft PR.
- Не ship без явной команды.

## 9. Prompt P00 — завершить независимую adjudication всех 39 ID

Статус: `[ ] pending`

### Цель

Создать полную независимую матрицу 39/39, исправить завышенные severity и
выявить false positives до изменения product code.

### Task pool V

- [ ] Зафиксировать commit SHA, branch и dirty state.
- [ ] Прочитать все 39 строк `RESULTS.md`.
- [ ] Использовать partial checkpoint только как список гипотез.
- [ ] Для каждого ID найти production entry point и все callers.
- [ ] Для каждого ID найти существующие guards и tests.
- [ ] Проверить reachability для platform/config edge cases.
- [ ] Для security claims назвать attacker, trust boundary и impact.
- [ ] Для leaks доказать удерживаемый ресурс и owner lifetime.
- [ ] Для update claims разделить authenticity, integrity и corruption checks.
- [ ] Для UI claims проверить минимальные размеры и reachable controls.
- [ ] Для Android использовать официальный platform contract.
- [ ] Для nft/PF использовать точную family semantics.
- [ ] Проставить verdict, final severity и confidence.
- [ ] Указать минимальный root fix и regression test только для survivors.
- [ ] Вывести missing/duplicate ID check.

### Task pool T/S

- [ ] Запускать только безопасные targeted tests.
- [ ] Не запускать VPN/service/firewall/installers.
- [ ] Не менять code/ledger.
- [ ] Проверить, что processed count равен 39.
- [ ] Сохранить отчёт:
  `plans/qwen-audit-independent-verification-2026-07-28.md`.

### Acceptance

- [ ] Ровно 39 уникальных строк.
- [ ] Нет claim без `file:line`.
- [ ] Все P0/P1 имеют полный control-flow proof.
- [ ] Все refutations имеют конкретный guard/caller proof.
- [ ] Отдельно перечислены ledger entries, которые нужно downgrade/remove.

### Copy prompt

```text
Выполни Prompt P00 из
plans/qwen-audit-remediation-prompt-pool-2026-07-28.md.
Это независимая read-only adjudication 39 Qwen findings. Сначала прочитай
AGENTS.md, .claude_handoff.md, relevant CLAUDE.md, RESULTS.md и весь Prompt P00.
Попытайся опровергнуть каждый claim. Не меняй product code, tests или
OPEN-DEFECTS.md. Разрешён только итоговый файл
plans/qwen-audit-independent-verification-2026-07-28.md.
Финал обязан содержать 39/39 verdict rows, точные file:line, final severity,
confidence и coverage check. Не запускай VPNRouter, sing-box, installers,
service или firewall. Не используй dev box для live validation.
```

## 10. Prompt P01 — desktop update integrity and repair

IDs: `UPD-1`, `UPD-2`

Статус: `[ ] blocked by P00`

Рекомендуемый branch: `codex/update-integrity-repair`

### Task pool V

- [ ] Проследить `GitHubReleaseSource.CheckAsync` → `AssetSha256`.
- [ ] Проследить `DownloadAsync` → `IDesktopInstaller`.
- [ ] Проследить legacy `UpdateInfo.FullChecksumUrl` contract.
- [ ] Перечислить все desktop update sources и adapters.
- [ ] Отделить missing validation от независимой authenticity.
- [ ] Проверить existing ZIP size, CRC, extraction и content guards.
- [ ] Найти старый macOS audit и решить duplicate/stale status.
- [ ] Проверить оба repair пути: C# и Go trampoline.
- [ ] Сверить точную Defender mitigation, уже принятую в `SelfRepair`.

### Task pool I

- [ ] Если `UPD-1` confirmed, передать уже загруженный digest без повторного HTTP.
- [ ] Не возвращать checksum URL abstraction, если digest уже есть.
- [ ] Сделать mismatch fail-closed до staging/apply.
- [ ] Не логировать expected/actual secrets или signed URLs.
- [ ] Для `UPD-2` переиспользовать temp `.ps1` + `-File` pattern.
- [ ] Гарантировать cleanup временного script.
- [ ] Сохранить quoting путей с пробелами.

### Task pool T

- [ ] Desktop digest match passes.
- [ ] Desktop digest mismatch refuses staging.
- [ ] Missing optional digest имеет явно выбранную policy.
- [ ] Interrupted/corrupt archive не достигает apply.
- [ ] Repair command использует `-File`, не inline `-Command`.
- [ ] Path-with-spaces test для trampoline.
- [ ] Existing Android sideload tests остаются зелёными.

### Task pool S/G

- [ ] Targeted update contract tests.
- [ ] `go test`/build только для затронутого GUI helper.
- [ ] Полный relevant updater suite.
- [ ] Reassess `UPD-1` P0/P1/P2 до ledger edit.
- [ ] Draft PR; не ship.

### Acceptance

- [ ] Desktop contract соответствует `IUpdateSource`.
- [ ] Hash mismatch доказуемо не staging/apply.
- [ ] Repair больше не создаёт inline download-and-execute command.
- [ ] Старые update paths не сломаны.

### Copy prompt

```text
Выполни Prompt P01 из
plans/qwen-audit-remediation-prompt-pool-2026-07-28.md для UPD-1/UPD-2.
Используй phase-task-launcher. Сначала независимо adjudicate оба ID и
зафиксируй final severity. Исправляй только confirmed root cause минимальным
diff, переиспользуя существующие digest и SelfRepair temp-ps1 patterns.
Добавь минимальные contract tests, выполни Type S/G, commit, немедленный push
и draft PR. Не ship и не запускай installer/update на dev box.
```

## 11. Prompt P02 — lifecycle, TUN ownership and failover

IDs: `LIFE-1`, `FAIL-1`

Статус: `[ ] blocked by P00`

Рекомендуемый branch: `codex/failover-wiring-lifecycle`

### Task pool V

- [ ] Проследить весь `TunOwnershipLock` acquire/release/dispose lifecycle.
- [ ] Перечислить все `SingBoxManager.StopInternal` exit paths.
- [ ] Проверить, где lock release происходит до manager dispose.
- [ ] Измерить реальный ресурсный impact повторного singleton dispose.
- [ ] Для `FAIL-1` перечислить все `_failover` reads/writes/reset sites.
- [ ] Сравнить pre-start и post-start delegates построчно.
- [ ] Доказать последовательность pre-start dead → later post-start failure.
- [ ] Проверить interaction с `_lifecycleGate` и `_sessionCts`.
- [ ] Проверить manager replacement/disposal в `StartupPipeline`.
- [ ] Проверить существующие v2.44.3/v2.46.1 regression tests.

### Task pool I

- [ ] Если `LIFE-1` refuted, не менять code; downgrade/close ledger only.
- [ ] Если остаётся handle churn, решить, нужен ли вообще P3 fix.
- [ ] Для `FAIL-1` устранить shared incompatible callback ownership.
- [ ] Все post-start restarts должны идти через один safe teardown path.
- [ ] User disconnect должен отменять queued/in-flight failover.
- [ ] Старый manager должен быть disposed ровно один раз.
- [ ] Не создавать второй lifecycle coordinator.

### Task pool T

- [ ] Pre-start failover then post-start failure uses safe delegate.
- [ ] Disconnect during that restart never resurrects tunnel.
- [ ] Old manager disposed before replacement.
- [ ] Lifecycle gate serializes restart/stop.
- [ ] Two acquire/stop/dispose cycles release named semaphore.
- [ ] Refuted LIFE claim pinned только если тест дешёвый и действительно полезен.

### Task pool S/G

- [ ] Run lifecycle/failover targeted suite.
- [ ] Run mandated regression tests from `AGENTS.md`.
- [ ] Bug-hunt the final diff.
- [ ] Ledger separates `LIFE-1` verdict from `FAIL-1`.
- [ ] Draft PR; no release.

### Acceptance

- [ ] Один safe restart contract для post-start failures.
- [ ] Disconnect wins against failover.
- [ ] Нет orphan manager/process/hook.
- [ ] `LIFE-1` impact описан без преувеличения.

### Copy prompt

```text
Выполни Prompt P02 из
plans/qwen-audit-remediation-prompt-pool-2026-07-28.md для LIFE-1/FAIL-1.
Используй phase-task-launcher. Не считай LIFE-1 подтверждённым: сначала
проверь Stop->Release->Dispose во всех путях. Для FAIL-1 проследи
pre-start-dead -> post-start-failure end to end и исправь callback ownership
в общей точке. Добавь минимальные concurrency/lifecycle tests, запусти
bug-hunt, commit, push, CI и draft PR. Не запускай VPN на dev box.
```

## 12. Prompt P03 — Linux/macOS kill-switch IPv6

IDs: `FW-1`, `FW-2`

Статус: `[ ] blocked by P00`

Рекомендуемый branch: `codex/killswitch-ipv6-rules`

### Task pool V

- [ ] Проверить supported URI/config representation IPv6 host.
- [ ] Сравнить `Uri.Host`, `DnsSafeHost` и stored `Server`.
- [ ] Проверить bare/bracketed IPv6 в generated `current.json`.
- [ ] Проверить custom JSON and manual config reachability.
- [ ] Проверить Linux `inet` output policy для IPv6.
- [ ] Проверить PF `inet`/`inet6` syntax.
- [ ] Перечислить все automatic table/anchor cleanup paths.
- [ ] Проверить crash marker и app restart recovery.
- [ ] Отделить invalid-rules fail-open от reconnect-blocked fail-closed.
- [ ] Проверить hostname resolver: возвращает ли он только IPv4.

### Task pool I

- [ ] Нормализовать parsed IP family в одном helper только если он уже нужен.
- [ ] Linux: выдавать `ip6 daddr` allow rule для IPv6 server.
- [ ] macOS: выдавать `inet6` allow rule для IPv6 server.
- [ ] Не ослаблять default drop.
- [ ] Не превращать hostname resolution failure в allow-all.
- [ ] Сохранить atomic ruleset/anchor load.
- [ ] Улучшить failure log, если PF load отвергнут.

### Task pool T

- [ ] Linux IPv4-only rules unchanged.
- [ ] Linux IPv6-only server receives `ip6 daddr`.
- [ ] Linux mixed-family output contains both families.
- [ ] Mac IPv6 uses `inet6`, not `inet`.
- [ ] Bracketed input normalized or explicitly rejected before ruleset.
- [ ] Custom bare IPv6 config covered.
- [ ] Split/full intent tests remain green.
- [ ] `TEST-1` firewall wiring case coordinated with P11.

### Task pool S/G

- [ ] Pure builder tests on Windows dev environment.
- [ ] `nft --check`/PF syntax only on safe disposable test environment if available.
- [ ] No live firewall changes on dev box.
- [ ] Document real cleanup/recovery, not the original exaggerated impact.
- [ ] Draft PR; no ship.

### Acceptance

- [ ] Supported IPv6 server can reconnect under armed kill-switch.
- [ ] PF ruleset loads with mixed-family servers.
- [ ] No IPv6 traffic is accidentally allowed globally.
- [ ] Refined severity reflects actual reachability.

### Copy prompt

```text
Выполни Prompt P03 из
plans/qwen-audit-remediation-prompt-pool-2026-07-28.md для FW-1/FW-2.
Используй phase-task-launcher. Сначала докажи reachability bare/bracketed IPv6
через реальные parsers/current.json и перечисли cleanup paths. Исправляй
только подтверждённую family mismatch в pure ruleset builders. Добавь mixed
IPv4/IPv6 tests. Не применяй nftables/PF на dev box. Commit, push, CI, draft
PR; no release.
```

## 13. Prompt P04 — custom config and protocol parity

IDs: `CFG-1`, `CFG-2`, `PROTO-1`

Статус: `[ ] blocked by P00`

Рекомендуемый branch: `codex/custom-config-protocol-parity`

### Task pool V

- [ ] Сравнить DNS strategy в generated и injected configs.
- [ ] Проверить комбинацию `ForceIpv4Only=false`, `Ipv6Enabled=false`.
- [ ] Найти все `EnsureUrltest` callers и existing tag lookup helpers.
- [ ] Доказать duplicate `"auto"` tag FATAL на supported custom config.
- [ ] Проследить dns-tunnel parse → sidecar → outbound generation.
- [ ] Проследить dns-tunnel deep verifier path.
- [ ] Проверить classifier consequence false-fail.
- [ ] Проверить existing UnsupportedByVerifier patterns для AWG/xhttp/naive.

### Task pool I

- [ ] Переиспользовать одну DNS strategy decision в generator/injector.
- [ ] Не перезаписывать user-authored DNS strategy без contract.
- [ ] Выбирать collision-free injected tag или использовать existing urltest.
- [ ] Сохранять references на user outbound tags.
- [ ] Для dns-tunnel либо реализовать sidecar-aware verification, либо typed skip.
- [ ] Не маркировать unsupported verifier result как blocked server.

### Task pool T

- [ ] Custom IPv4-only TUN emits IPv4 DNS strategy.
- [ ] Existing user `"auto"` outbound не создаёт duplicate.
- [ ] Existing urltest reused.
- [ ] Generated config parity test.
- [ ] dns-tunnel verify returns correct typed result.
- [ ] Classifier не банит working dns-tunnel за unsupported verifier.

### Task pool S/G

- [ ] Config serialization/sanity tests.
- [ ] Relevant Vless verifier tests.
- [ ] No live sidecar/process launch.
- [ ] Small PR; if root causes independent, split P04 into two PR.

### Acceptance

- [ ] Generator/injector не расходятся по IPv6 decision.
- [ ] Injected tags уникальны.
- [ ] dns-tunnel не получает ложный protocol failure.

### Copy prompt

```text
Выполни Prompt P04 из
plans/qwen-audit-remediation-prompt-pool-2026-07-28.md для CFG-1/CFG-2/PROTO-1.
Используй phase-task-launcher. Сначала adjudicate каждый ID отдельно.
Переиспользуй существующие generator/tag/verifier patterns; не добавляй новую
abstraction. Добавь pure config/verifier tests, commit, push, CI и draft PR.
Если root causes независимы, сделай два маленьких PR. Не запускай sing-box.
```

## 14. Prompt P05 — settings, subscriptions and free-config data safety

IDs: `DATA-1`, `DATA-2`, `DATA-3`, `DATA-4`, `DATA-5`, `DATA-6`, `NET-1`

Статус: `[ ] blocked by P00`

Рекомендуемый branch: `codex/settings-subscription-data-safety`

### Task pool V

- [ ] Подтвердить `DATA-1` direct-write и load-after-truncation behavior.
- [ ] Найти existing atomic replace pattern в repo.
- [ ] Подтвердить/refute `DATA-2` через source-generated context options.
- [ ] Восстановить schema history для MTU 1280/1500.
- [ ] Отличить legacy sentinel от explicit user value для `DATA-3`.
- [ ] Проверить duplicate IDs на raw pool boundary и merge dictionaries.
- [ ] Доказать или опровергнуть `DATA-5` с parser-constructible credentials.
- [ ] Проверить IPv6/server delimiters в dedupe key.
- [ ] Сравнить `DATA-6` с существующим overwrite-move pattern.
- [ ] Проследить decompression и buffering для `NET-1`.
- [ ] Найти current response limits в sibling fetchers.

### Task pool I

- [ ] `DATA-1`: temp sibling + flush/atomic replace без новой filesystem layer.
- [ ] Сохранить recoverable previous config при replace failure.
- [ ] `DATA-2`: закрыть как refuted, если MaxDepth реально применяется.
- [ ] `DATA-3`: менять migration только если legacy/user intent различим.
- [ ] `DATA-4`: deterministic dedupe до `ToDictionary`.
- [ ] Определить first/last-wins policy и логировать count, не secrets.
- [ ] `DATA-5`: если confirmed, заменить строковый key на typed tuple/value.
- [ ] `DATA-6`: использовать существующий overwrite-move.
- [ ] `NET-1`: bounded streaming/read с compressed/expanded cap.
- [ ] Не ломать base64/plain subscription detection.

### Task pool T

- [ ] Atomic settings save round-trip.
- [ ] Replace failure leaves previous valid config.
- [ ] MaxDepth production-path test либо удалить ложный redundant test.
- [ ] MTU migration cases: legacy, explicit 1280, normal default.
- [ ] Duplicate fresh/cache IDs do not abort merge.
- [ ] Dedupe tests используют только parser-constructible inputs.
- [ ] Cache replacement failure preserves usable file.
- [ ] Oversized response aborts before full allocation.
- [ ] Compressed expansion bomb bounded.
- [ ] Normal subscription remains compatible.

### Task pool S/G

- [ ] Targeted settings/subscription/free-config suites.
- [ ] Regression filters from `AGENTS.md`.
- [ ] Secret scan in test output/log messages.
- [ ] Split into atomic-write, input-bound, and data-semantics PRs if diff grows.
- [ ] Correct ledger refutations before stable planning.

### Acceptance

- [ ] Config/cache writes recover safely.
- [ ] Remote bodies have explicit caps.
- [ ] Duplicate data cannot abort the whole merge.
- [ ] Refuted MaxDepth/dedupe claims do not generate unnecessary code.

### Copy prompt

```text
Выполни Prompt P05 из
plans/qwen-audit-remediation-prompt-pool-2026-07-28.md.
Используй phase-task-launcher. Не исправляй DATA-2/DATA-5 до независимого
proof: AppJsonContext уже может иметь MaxDepth=32, а dedupe collision должен
быть построен только через реальный parser. Для survivors используй
существующие atomic-move и bounded-fetch patterns. Добавь temp-dir/fake-handler
tests. Раздели независимые root causes на небольшие PR, каждый commit+push+CI.
```

## 15. Prompt P06 — Smart Connect, localization and narrow UI

IDs: `FLOW-1`, `UI-1`, `UI-2`

Статус: `[ ] blocked by P00`

Рекомендуемый branch: `codex/smart-connect-narrow-ui`

### Task pool V

- [ ] Проследить Smart Connect winner assignment → SaveSettings → Connect.
- [ ] Проверить selected server VM synchronization.
- [ ] Проверить advanced/simple mode sibling flows.
- [ ] Найти существующий localized Update string.
- [ ] Найти все hardcoded Update button variants.
- [ ] Вычислить фактическую ширину NetworkPage detail pane.
- [ ] Проверить все три read-mode row templates.
- [ ] Проверить `IsRulesNarrow` и existing narrow templates.
- [ ] Проверить keyboard/focus доступ к delete action.

### Task pool I

- [ ] Победитель probe становится единственным selected/active source of truth.
- [ ] SaveSettings не должен обратно выбирать stale server.
- [ ] Не дублировать connect pipeline.
- [ ] UI-1 использует существующую localization infrastructure.
- [ ] UI-2 переиспользует `IsRulesNarrow`.
- [ ] Narrow layout остаётся keyboard-accessible.
- [ ] Не добавлять horizontal scrolling как маскировку clipping.

### Task pool T

- [ ] Smart Connect winner survives SaveSettings.
- [ ] Dead previous selection cannot overwrite winner.
- [ ] Winner reaches engine input.
- [ ] RU/EN Update binding test/source contract.
- [ ] Narrow template present for all affected rows.
- [ ] Existing wide layout remains unchanged.
- [ ] Overflow audit finds no sibling bare strings.

### Task pool S/G

- [ ] Использовать `audit-overflow-fix` для UI scope.
- [ ] Build Avalonia app without launching it locally.
- [ ] Run ViewModel/UI characterization tests.
- [ ] Bug-hunt non-trivial ViewModel diff.
- [ ] Post-ship end-to-end UI only after explicit rolling release.

### Acceptance

- [ ] Smart Connect действительно подключает измеренного победителя.
- [ ] Update text локализован.
- [ ] Rule value/delete доступны на minimum width.

### Copy prompt

```text
Выполни Prompt P06 из
plans/qwen-audit-remediation-prompt-pool-2026-07-28.md для FLOW-1/UI-1/UI-2.
Используй phase-task-launcher и audit-overflow-fix. Исправь Smart Connect в
единой selected/active state точке, не обходи SaveSettings. Для UI переиспользуй
localization и IsRulesNarrow. Добавь минимальные VM/layout contracts, build и
tests без локального запуска UI. Commit, push, CI, draft PR; no ship.
```

## 16. Prompt P07 — CLI ownership and Android lifecycle/privacy

IDs: `CLI-1`, `CLI-2`, `AND-1`, `AND-2`

Статус: `[ ] blocked by P00`

Рекомендуемые branches:

- `codex/cli-stop-ownership`;
- `codex/android-error-redaction`.

### Task pool V

- [ ] Проследить CLI `start` owner process lifetime.
- [ ] Проследить `stop` state-file PID path.
- [ ] Доказать HealthMonitor restart после external child kill.
- [ ] Проверить, есть ли IPC/owner stop mechanism.
- [ ] Найти public process ownership helper.
- [ ] Проверить PID reuse window и executable path identity.
- [ ] Для Android перечислить exception sources из libbox.
- [ ] Проверить существующий `scrubSecrets` contract.
- [ ] Проверить logcat и broadcast consumers.
- [ ] Для `AND-2` сверить `super.onRevoke()` с Android official contract.

### Task pool I

- [ ] CLI stop должен обратиться к owner, а не просто убить child.
- [ ] Если полноценный IPC отсутствует, выбрать минимальный безопасный protocol.
- [ ] Перед kill переиспользовать ownership/path validation.
- [ ] State file очищается только после подтверждённого stop.
- [ ] Android error scrubbing выполняется один раз до log+broadcast.
- [ ] Не скрывать полезный error category целиком.
- [ ] `AND-2` не менять, если default `stopSelf()` доказан.

### Task pool T

- [ ] Stop cannot trigger HealthMonitor resurrection.
- [ ] Recycled/unrelated PID never killed.
- [ ] Valid owned sing-box stop still works.
- [ ] Failed owner communication leaves honest state.
- [ ] Android UUID/server/token scrubbed.
- [ ] Benign Android error remains actionable.
- [ ] `onRevoke` refutation source/contract test только если поддерживаемо.

### Task pool S/G

- [ ] CLI and Android — отдельные PR.
- [ ] Build CLI/Core and Android compile tests.
- [ ] No real process kill or VPN start on dev box.
- [ ] Android live validation only on physical device after explicit ship path.
- [ ] Correct `AND-2` ledger if refuted.

### Acceptance

- [ ] CLI stop действует на правильного owner и не убивает чужой PID.
- [ ] Runtime state соответствует реальному процессу.
- [ ] Android errors не раскрывают credentials.
- [ ] Нет лишнего onRevoke patch.

### Copy prompt

```text
Выполни Prompt P07 из
plans/qwen-audit-remediation-prompt-pool-2026-07-28.md.
Используй phase-task-launcher. Раздели CLI и Android на два PR. Для CLI
докажи owner/restart flow и переиспользуй ProcessOwnership; не тестируй через
реальный kill на dev box. Для Android исправляй AND-1 через общий scrubber, а
AND-2 сначала проверь по official VpnService.onRevoke contract и закрой без
кода, если super уже вызывает stopSelf. Commit, push, CI; no ship.
```

## 17. Prompt P08 — packaging and supply-chain reproducibility

IDs: `PKG-1`, `SUP-1`, `SUP-2`, `SUP-3`, `SUP-4`

Статус: `[ ] blocked by P00`

Рекомендуемый branch: `codex/release-input-pins`

### Task pool V

- [ ] Определить все entry conditions wgturn branch в `build-mac.sh`.
- [ ] Проверить current CI reachability и local/private reachability.
- [ ] Определить mapping macOS arch → Go `GOARCH`.
- [ ] Проверить provenance appimagetool continuous URL.
- [ ] Найти immutable release/digest source.
- [ ] Проверить exact sing-box/libcronet archive URL и version pin.
- [ ] Проверить наличие upstream checksums/signatures.
- [ ] Перечислить mutable `uses:` во всех workflows.
- [ ] Проверить SignPath workflow reachability/secrets guard.
- [ ] Проверить wgturn source commit publication/ownership.

### Task pool I

- [ ] Вычислять ARCH из build target, не host guess при cross-build.
- [ ] Pin appimagetool immutable version and digest.
- [ ] Verify digest before executable bit/run.
- [ ] Pin/verify sing-box archive before extraction.
- [ ] Pin actions to full commit SHA.
- [ ] Pin wgturn source commit owned by release configuration.
- [ ] Fail closed на mismatch.
- [ ] Документировать update procedure для каждого pin.
- [ ] Не дублировать existing native dependency manifest.

### Task pool T

- [ ] Shell syntax check.
- [ ] ARCH mapping cases arm64/x64.
- [ ] Wrong digest fails before execute/extract.
- [ ] Correct digest passes.
- [ ] Workflow YAML parse/actionlint if already available.
- [ ] Grep test: no mutable `continuous` executable URL.
- [ ] Grep test: no unpinned signing actions.
- [ ] Existing build paths without wgturn unchanged.

### Task pool S/G

- [ ] Не скачивать/исполнять unverified binary во время audit.
- [ ] Primary-source digest capture documented in commit.
- [ ] Mac CI after push.
- [ ] Linux CI after push.
- [ ] Separate latent PKG-1 severity from active CI failures.
- [ ] No tag/release.

### Acceptance

- [ ] Все release-controlling executables immutable and verified.
- [ ] libcronet archive verified.
- [ ] macOS wgturn branch has defined reproducible architecture/source.
- [ ] Signing actions SHA-pinned.

### Copy prompt

```text
Выполни Prompt P08 из
plans/qwen-audit-remediation-prompt-pool-2026-07-28.md.
Сначала adjudicate active versus latent reachability каждого ID. Используй
существующий native dependency manifest и fail-closed checksum patterns.
Не исполняй непроверенные downloads. Добавь минимальные shell/workflow checks,
commit, немедленный push, дождись Linux+Mac CI и открой draft PR. Не создавай
tag/release.
```

## 18. Prompt P09 — secrets, ACL and diagnostics

IDs: `SEC-1`, `SEC-2`, `SEC-3`, `OBS-1`, `OBS-2`

Статус: `[ ] blocked by P00`

Рекомендуемые branches:

- `codex/log-secret-redaction`;
- `codex/windows-data-acl`;
- `codex/diagnostic-tail-bound`.

### Task pool V

- [ ] Перечислить все subscription URL log sites.
- [ ] Определить sensitive URL components.
- [ ] Найти shared redactor и его supported schemes/key forms.
- [ ] Проверить Clash WS URI log и token placement.
- [ ] Проверить crash-report path после logging.
- [ ] Измерить реальный Windows inherited ACL current install.
- [ ] Определить App/Service accounts, которым нужен доступ.
- [ ] Проверить installer, repair и runtime directory creation.
- [ ] Для SEC-3 доказать quote injection через реальный URI parser.
- [ ] Проверить `ProcessStartInfo.ArgumentList` availability/current patterns.
- [ ] Для OBS-2 сравнить `ReadAllLines` с bounded `TailLines`.

### Task pool I

- [ ] Никогда не логировать полный subscription URL.
- [ ] Логировать redacted host/provider identifier.
- [ ] Не логировать Clash token-bearing WS URI.
- [ ] Расширить один shared redactor для `ws/wss` и key/value secrets.
- [ ] SEC-3: использовать `ArgumentList`, не вручную quoted string.
- [ ] OBS-2: переиспользовать bounded tail reader.
- [ ] ACL: применить минимальные права installer/runtime idempotently.
- [ ] Сохранить доступ Windows Service и текущего пользователя.
- [ ] Не рекурсивно ломать binaries/updates без explicit ACL design.

### Task pool T

- [ ] Subscription query/path token redacted.
- [ ] Non-secret URL remains diagnosable.
- [ ] Clash WS token absent from logs/crash report.
- [ ] `ws://`, `wss://`, bearer and key/value cases.
- [ ] Quoted malicious URI remains one argument.
- [ ] Bounded tail reads only configured maximum bytes.
- [ ] Small/empty/no-newline logs handled.
- [ ] ACL unit/pure command-generation tests where possible.
- [ ] Installer ACL validation on brat only after ship.

### Task pool S/G

- [ ] Разделить logging, ACL и bounded-tail на отдельные PR.
- [ ] Security review/bug-hunt each trust-boundary diff.
- [ ] Не печатать test secrets в CI.
- [ ] No local installer/ACL mutation.
- [ ] Post-ship ACL check только на windows-brat.

### Acceptance

- [ ] Credentials не попадают в raw logs.
- [ ] Crash report scrubber покрывает Clash WS secret.
- [ ] Command arguments не injectable.
- [ ] Crash handler bounded по памяти.
- [ ] Windows data ACL соответствует owner/service contract.

### Copy prompt

```text
Выполни Prompt P09 из
plans/qwen-audit-remediation-prompt-pool-2026-07-28.md.
Используй phase-task-launcher и раздели logging, ACL и bounded-tail на
маленькие PR. Сначала докажи trust boundary каждого claim. Переиспользуй
shared redactor, ArgumentList и existing bounded TailLines. Не меняй ACL и не
запускай installer на dev box. Добавь security regression tests, bug-hunt,
commit, push, CI; live ACL verify только на brat после явного ship.
```

## 19. Prompt P10 — updater atomicity and resource ownership

IDs: `ZAP-1`, `ZAP-2`, `ZAP-3`, `PERF-1`, `PERF-2`

Статус: `[ ] blocked by P00`

Рекомендуемые branches:

- `codex/updater-atomic-replacement`;
- `codex/owned-resource-disposal`.

### Task pool V

- [ ] Проследить Zapret stop/copy/version marker ordering.
- [ ] Перечислить swallowed copy failures и required files.
- [ ] Проверить retry decision из `version.txt`.
- [ ] Проследить Wgturn temp/delete/move/finally.
- [ ] Найти existing atomic binary replace/rollback pattern.
- [ ] Проследить EmergencyChannel process exit/stop/dispose ownership.
- [ ] Проследить ETW monitor create/stop/dispose per connect.
- [ ] Проверить, создаётся ли kernel handle без `AvailableWaitHandle`.
- [ ] Проследить FreeConfig ViewModel/Aggregator lifetime.
- [ ] Проверить HttpClient ownership и frequency of recreation.
- [ ] Отличить bounded app-lifetime ownership от true accumulating leak.

### Task pool I

- [ ] Zapret marker писать только после всех required copies.
- [ ] Copy failure должен сохранять retryable version state.
- [ ] Wgturn сохраняет working binary до успешного replacement.
- [ ] Temp cleanup не уничтожает единственную recovery copy.
- [ ] Process object disposed/cleared exactly once.
- [ ] ETW monitor owner вызывает Dispose, не только Stop.
- [ ] FreeConfig resource fix только если доказано повторное accumulation.
- [ ] Не создавать global HttpClient factory abstraction без необходимости.

### Task pool T

- [ ] Locked required Zapret file prevents version marker.
- [ ] Successful Zapret update writes marker last.
- [ ] Wgturn move failure preserves previous binary.
- [ ] Successful replace cleans temp.
- [ ] Exited Emergency process disposed before replacement.
- [ ] Multiple connect/disconnect cycles dispose every ETW monitor.
- [ ] FreeConfig VM recreate resource-count test, если leak confirmed.
- [ ] Existing updater checksum policies unchanged.

### Task pool S/G

- [ ] Fake filesystem/process owners; no real binary/service mutation.
- [ ] Separate updater and resource PRs.
- [ ] Bug-hunt atomicity/rollback paths.
- [ ] Targeted tests plus Core build.
- [ ] No live Zapret/Wgturn update on dev box.

### Acceptance

- [ ] Partial update never advertises new version.
- [ ] Failed replace leaves last working binary.
- [ ] Repeated lifecycle не накапливает owned handles/resources.
- [ ] Refuted bounded lifetime claims закрыты без unnecessary code.

### Copy prompt

```text
Выполни Prompt P10 из
plans/qwen-audit-remediation-prompt-pool-2026-07-28.md.
Используй phase-task-launcher. Отдельно adjudicate updater atomicity и resource
ownership; не называй leak без доказанного accumulating lifetime. Для
confirmed updater defects переиспользуй existing atomic replace/rollback
pattern и оставь failure-injection tests. Не трогай реальные Zapret/Wgturn
binaries. Раздели PR, bug-hunt, commit, push, CI; no ship.
```

## 20. Prompt P11 — cross-cut regression and adversarial review

ID: `TEST-1` плюс все survivors P01-P10

Статус: `[ ] after relevant remediation PRs`

Рекомендуемый branch: обычно тот же PR, где найден missing test

### Task pool V

- [ ] Проверить, существует ли заявленный `SetupFirewall_BlockOnFail_CreatesRules`.
- [ ] Проследить `StartupPipeline` firewall phase в test host.
- [ ] Проверить split/full `isFullTunnel` capture.
- [ ] Составить survivor-to-test matrix по всем fixed ID.
- [ ] Найти tests, проверяющие comments/helpers вместо production path.
- [ ] Найти source-pin tests, которые можно заменить behavior tests.

### Task pool T

- [ ] ColdStart + BlockOnVpnFail + split passes `false`.
- [ ] ColdStart + BlockOnVpnFail + full passes `true`.
- [ ] Каждый confirmed P0/P1 имеет хотя бы один behavior regression.
- [ ] Refuted ID не получает бессмысленный production patch.
- [ ] Full solution Release build.
- [ ] Mandated regression filter from `AGENTS.md`.
- [ ] Relevant platform/config/update/CLI/Android tests.
- [ ] `git diff --check`.
- [ ] Characterization hashes меняются только при реальном source change.

### Task pool adversarial

- [ ] Запустить `bug-hunt` на каждом non-trivial diff.
- [ ] Независимый reviewer пытается опровергнуть fix.
- [ ] Проверить sibling paths и rollback.
- [ ] Проверить cancellation/parallel calls.
- [ ] Проверить secret logging.
- [ ] Проверить empty/null/duplicate/oversized inputs.
- [ ] Проверить platform conditional compilation.
- [ ] Проверить test действительно падает на pre-fix code.

### Acceptance

- [ ] TEST-1 закрыт behavior test.
- [ ] Все P0/P1 survivor fixes имеют regression coverage.
- [ ] Bug-hunt survivors отражены в ledger.
- [ ] CI green.

### Copy prompt

```text
Выполни Prompt P11 из
plans/qwen-audit-remediation-prompt-pool-2026-07-28.md на текущем remediation
diff. Используй bug-hunt. Добавь отсутствующий StartupPipeline firewall
behavior test и survivor-to-test matrix. Докажи, что каждый новый test падает
на старом поведении и проходит после root fix. Запусти Release build,
обязательные regression filters и relevant suites. Не меняй product code ради
удобства теста. Commit, push, CI; no release.
```

## 21. Prompt P12 — ledger, plans and PR integration

Статус: `[ ] continuous; final after P11`

### Task pool

- [ ] Обновить independent verification report 39/39.
- [ ] Для каждого ID записать final verdict/severity/commit/PR.
- [ ] `REFUTED` ledger item закрыть с точным proof, не удалять историю.
- [ ] `DUPLICATE` связать с canonical root ID.
- [ ] `CONFIRMED` закрывать только после merged fix.
- [ ] `PARTIALLY_CONFIRMED` переписать без старого exaggerated impact.
- [ ] Проверить `tools/check-open-p0.ps1`.
- [ ] Проверить, что stable gate отражает только реальные open P0/P1.
- [ ] Добавить краткий `.claude_handoff.md` session log.
- [ ] Обновить этот plan status/checklists.
- [ ] Ссылаться на PR/commit, не на временный чат.
- [ ] Не объявлять READY до фактического CI/live gates.

### Acceptance

- [ ] Все 39 ID имеют финальный outcome.
- [ ] Нет ложного P0 stable blocker.
- [ ] Нет подтверждённого P1, потерянного вне ledger.
- [ ] Каждый closed item имеет proof/fix reference.

### Copy prompt

```text
Выполни Prompt P12 из
plans/qwen-audit-remediation-prompt-pool-2026-07-28.md.
Сведи 39 ID, independent verification, merged fixes и CI в один ledger
outcome. Не удаляй историю: refuted/downgraded entries закрывай с proof,
confirmed — только с merged commit/PR. Проверь open-P0 gate, обнови plan и
.claude_handoff.md. Commit, немедленный push, CI и обновление audit PR.
Не ship.
```

## 22. Prompt P13 — optional rolling candidate and post-ship verification

Статус: `[ ] forbidden until explicit user command`

Этот prompt нельзя запускать из факта, что все PR зелёные. Требуется явное
сообщение владельца: `ship`, `release`, `выпускай -rN` или эквивалент.

### Task pool pre-ship

- [ ] Все intended remediation PR merged.
- [ ] `main` synced, clean worktree.
- [ ] Session red-CI ritual clean.
- [ ] `tools/check-open-p0.ps1` даёт expected result.
- [ ] Full Release build green.
- [ ] Mandatory regression suite green.
- [ ] Version/tag chosen by rolling policy.
- [ ] Release notes enumerate user-visible fixes.

### Task pool ship

- [ ] Использовать `ship-rolling-candidate` skill exactly.
- [ ] AppVersion matches full `-rN` tag.
- [ ] Desktop assets complete.
- [ ] Mac/Linux CI green.
- [ ] Commit CI green, не только tag CI.
- [ ] Latest restored per policy.

### Task pool post-ship

- [ ] Немедленно использовать `post-ship-mcp-verify`.
- [ ] Install/download only on windows-brat via WinRM.
- [ ] End-to-end user scenario per each UI/runtime fix.
- [ ] Bottom-of-viewport screenshot for UI overflow fixes.
- [ ] Log scan for ERR/Exception/FATAL.
- [ ] Core-only findings явно marked not UI-testable.
- [ ] PASS/FAIL report per release-note item.
- [ ] Не cut stable без separate explicit user command и live update gate.

### Copy prompt

```text
Владелец явно разрешил rolling release. Выполни Prompt P13 из
plans/qwen-audit-remediation-prompt-pool-2026-07-28.md строго через
ship-rolling-candidate, затем автоматически post-ship-mcp-verify.
Все install/launch/UI/VPN действия только на windows-brat 192.168.0.106 через
WinRM. Если brat недоступен — STOP, не использовать dev box. Не cut stable:
для stable нужна отдельная явная команда после всех gates, включая live update.
```

## 23. Рекомендуемый порядок PR

После P00:

1. `FAIL-1` — подтверждённый lifecycle P1.
2. `FLOW-1` — подтверждённый Smart Connect P1.
3. `CLI-1/CLI-2` — ownership and unrelated-process safety.
4. Confirmed secret leaks из P09.
5. `UPD-1/UPD-2` после final severity adjudication.
6. `DATA-1/NET-1` data safety.
7. `FW-1/FW-2` после reachability refinement.
8. Packaging/supply-chain pins.
9. Remaining P2 clusters.
10. P11 cross-cut regression.
11. P12 ledger reconciliation.

Не объединять все пункты в один mega-PR.

## 24. Master progress board

| Prompt | Status | Branch/PR | CI | Outcome |
|---|---|---|---|---|
| P00 adjudication | pending | — | — | — |
| P01 update/repair | blocked | — | — | — |
| P02 lifecycle/failover | blocked | — | — | — |
| P03 kill-switch IPv6 | blocked | — | — | — |
| P04 config/protocol | blocked | — | — | — |
| P05 data/subscriptions | blocked | — | — | — |
| P06 desktop/UI | blocked | — | — | — |
| P07 CLI/Android | blocked | — | — | — |
| P08 packaging/supply chain | blocked | — | — | — |
| P09 security/diagnostics | blocked | — | — | — |
| P10 updater/resources | blocked | — | — | — |
| P11 regression/bug-hunt | blocked | — | — | — |
| P12 ledger integration | pending | audit PR #48 | green before this plan | — |
| P13 rolling release | user-command only | — | — | — |

## 25. Session handoff template

Каждая сессия, которая выполняет prompt, добавляет в handoff:

```text
Prompt:
IDs:
Commit inspected:
Verdicts:
Files changed:
Tests:
Commit:
Branch/PR:
CI:
Ledger updates:
Remaining blockers:
Live validation:
```

Если работа остановлена из-за лимита, агент обязан заполнить этот template и
обновить progress board до завершения сессии. Частичное сообщение в чате без
Git-tracked evidence не считается завершённым prompt-пакетом.
