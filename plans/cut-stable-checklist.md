# Cut-stable checklist — mandatory pre-cut live update gate

**Status**: enforcement reference (2026-05-06+).
**Authority**: rule 6 в `CLAUDE.md`, step 6.5 в `CLAUDE.local.md` Release Process.
**Skill mirror**: `.claude/skills/cut-stable/SKILL.md` секция «Mandatory
pre-cut live update gate». Этот файл — standalone версия, доступная даже
если skill layer не подгружен.

---

## Why this exists

Зелёный CI + зелёные tests + MCP-verify самой фичи **НЕ покрывают auto-update
path**. История:

- **v2.31.2** (2026-05-02): cut на all-green, partial-fix slip — F-25 всё
  ещё видим в UI. Ловили через v2.31.3 hotfix.
- **v2.31.7** (2026-05-05): cut на all-green, helper.cmd CMD parser bug
  (`%SVC_TRIES%` pre-expanded at parse time → `if  gtr 20 (` → "20 was
  unexpected") сломал 100% user upgrades. Поймали через ~7 дней по
  user-reports, пришлось shipать v2.31.8-r10 + одноразовый
  `vpn.ninitux.com/repair.cmd`.

**Conclusion**: тот же binary, который user скачает со stable, должен в
чистой среде успешно auto-update'нуться к **текущему candidate -rN**
ПЕРЕД cut'ом. Иначе stable shipping = выпуск broken update path в
production. Ни один из 5 первых verification checkboxes этого не ловит.

---

## When to run

После 5 первых verification checkboxes PASS (build + tests + Mac/Linux CI
+ 12 assets + MCP-verify изменения), перед тем как просить user'а
"cut" / "ok" / "promote".

Если этот gate упал — НЕ просим cut, а ship'аем `-r(N+1)` с фиксом и
крутим cycle заново. Этот gate **обязателен** для каждого stable cut, кроме
явных исключений (Core-only fix без UI surface И без update-helper изменений
— тогда explicit "Core-only / no installer touch" label в докладе user'у).

---

## Steps

Выполнять в том же VM/host где работаем над release. **Не трогать
prod-инсталляцию VPNRouter** на этой машине — gate идёт в изолированный
temp dir.

### a) Identify previous stable release tag

```bash
gh release list --repo PavelLizunov/VPNRouter --exclude-pre-releases --limit 1
```

Запомни tag (e.g. `v2.31.7`). Это baseline для теста.

### b) Download install ZIP в чистый temp dir

```bash
rm -rf /c/Temp/stable-test && mkdir -p /c/Temp/stable-test
gh release download <previous-stable-tag> --repo PavelLizunov/VPNRouter \
  --pattern 'VPNRouter-*-windows-x64.zip' --dir /c/Temp/stable-test
```

### c) Extract + launch + initial settle

```bash
cd /c/Temp/stable-test
powershell -Command "Expand-Archive -Path 'VPNRouter-*-windows-x64.zip' -DestinationPath ./extracted -Force"
powershell -Command "Start-Process -FilePath './extracted/VPNRouter.App.exe'"
```

Wait 30s для App init (settings load, update check spin-up).

### d) Trigger update к ТЕКУЩЕМУ candidate -rN

Два пути:

- **UI path** (preferred — exercises full user flow): MCP click Settings →
  «Проверить обновления» / «Check for updates» → дождаться detection -rN
  кандидата (ensure `Experimental` channel is on, иначе -rN неvidible) →
  нажать «Установить» / «Install».
- **Programmatic** (fallback если UI broken): дёрнуть update helper напрямую
  через CLI если такая команда есть. Если нет — UI path mandatory.

### e) Wait for update flow to complete

Helper .cmd должен:
1. Stop running App + Service (если установлен)
2. xcopy new files over old install
3. Relaunch App

Ожидаемое время: 30-90s. Tail update.log:

```bash
tail -f "$LOCALAPPDATA/VPNRouter/Logs/update.log"
# или %ProgramData%/VPNRouter/logs/update.log в зависимости от installer mode
```

### f) Verify new version installed cleanly

```bash
powershell -Command "(Get-Item /c/Temp/stable-test/extracted/VPNRouter.App.exe).VersionInfo.ProductVersion"
```

Должна быть `<candidate-rN-version>` (e.g. `2.31.8-r10`). Также:

```bash
powershell -Command "(Get-Item /c/Temp/stable-test/extracted/VPNRouter.Core.dll).VersionInfo.FileVersion"
```

AppVersion в Core.dll должна совпадать с candidate (правило #5 в `CLAUDE.md`
— string Version ВКЛЮЧАЯ -rN суффикс).

### g) 30-second smoke

Убедись App работает после update:

- Если есть test профиль (free configs / saved subscription) — connect →
  status «Подключено» → disconnect.
- Если нет — минимум: главное окно открывается, нет crash dialog'а,
  status показывает «Готов» / «Ready», нет красных error toast'ов.

MCP screenshot для визуальной фиксации PASS.

### h) Cleanup

```bash
powershell -Command "Get-Process VPNRouter.App,VPNRouter.Service,sing-box -ErrorAction SilentlyContinue | Stop-Process -Force"
rm -rf /c/Temp/stable-test
```

### i) IF ANY STEP FAILS

(download error, install hang, helper crash, version mismatch, App не
запускается после update, smoke fails)

- **DO NOT CUT**. Stable cut откладывается.
- Diagnose root cause (logs: update.log, vpnrouter.log, Event Viewer для
  Service, helper.cmd output).
- Fix в коде / helper.cmd / install.ps1.
- Ship `-r(N+1)` через `ship-rolling-candidate` skill.
- Run этот gate заново на новом -r(N+1).
- Repeat until PASS.

---

## Detailed report template (user'у при request cut)

```
Live update gate: PASS

- Previous stable downloaded: vX.Y.(Z-1) (or same Z, prior -rN cut)
- Update path triggered: UI / programmatic
- Helper.cmd exit code: 0
- Helper.cmd log tail: <last ~10 lines>
- ProductVersion после update: <candidate-rN-version>
- FileVersion (Core.dll): <candidate-rN-version>
- Smoke: connect/disconnect SUCCESS
- Screenshot: <path or attached>

Per-step:
- (a) baseline tag identified: PASS
- (b) ZIP download: PASS
- (c) extract + launch: PASS
- (d) update trigger: PASS
- (e) helper completion: PASS
- (f) version verify: PASS
- (g) smoke: PASS
- (h) cleanup: PASS

Ready for "cut" / "ok" / "promote" command.
```

Если FAIL — детальный root-cause + plan для -r(N+1).

---

## Cross-references

- `CLAUDE.md` rule 6 — определяет 6 verification conditions для cut.
- `CLAUDE.local.md` Release Process step 6.5 — указывает на этот gate.
- `.claude/skills/cut-stable/SKILL.md` — full cut-stable workflow,
  включая эту gate секцию (mirror этого файла).
- `plans/vpnrouter-release-strategy.md` — общая rolling -rN policy.
- Lessons: v2.31.2 (partial-fix slip) и v2.31.7 (helper.cmd parser bug)
  в `CLAUDE.local.md` «Урок» секциях.
