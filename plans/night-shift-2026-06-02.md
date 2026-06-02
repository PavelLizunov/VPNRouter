# Night shift 2026-06-02 — автономный 6h+ (safe-only)

Пользователь ушёл спать: "Исправляй и накидай себе план работ минимум на 6 часов".
Это night-shift лог — дополняю по мере выполнения блоков (anchor для restore после компакта).

## Расширенный мандат (user grant, ночь 2026-06-02)

User дал: **(1) делать пре-релизы (-rN)** — шиплю свободно после значимых батчей;
**(2) /workflows** для regression/bug-hunt **после каждой большой доработки** —
запускаю адверсариальную проверку; **(3) computer-use** для проверки; **(4) телефон**
полностью; **(5) полная свобода в спорных решениях** (HttpClient A/B, release-подход и
т.д. — решаю сам); **(6) токены — как будто бесконечны** → тщательность > экономия.

**ВАЖНО — что НЕ покрыто грантом:** **stable cut** остаётся за user'ом. «Пре-релизы» ≠
«cut». Стандартное правило (CLAUDE.md #6, урок v2.31.2: green-gates прячут UX-баги →
human-in-the-loop) НЕ отменено этим грантом. Поэтому: шиплю -rN + прогоняю live-update
gate (готовлю к cut), но сам cut — только по утреннему явному «cut/ok/promote».

**Ревизованный цикл на каждую большую доработку:** code → build → unit/test → device/MCP
verify → **/workflow** (regression+bug-hunt, fix findings) → commit/push оба remote →
**ship -rN** → CI green → post-ship MCP/device verify → journal. Loop.

## Жёсткие рельсы (unsupervised)
- **Safe-only**: НЕ трогать core-VPN routing / firewall / config-gen / sing-box lifecycle
  так, чтобы можно было сломать туннель без присмотра. Можно: Android UI/perf,
  additive-фичи, Core leak/test hardening, измерения, docs.
- **НЕТ stable cut** (нужна явная user-команда + live-update gate — невозможно без user).
- **НЕ шипить -rN** (держим r4-соак; фиксы копятся на main). Релизное решение — утром user.
- Push в ОБА remote + CI-гейт (pre-push hook) на каждый commit. Без --no-verify. Без emoji в коде/доках.
- Телефон A101BM через Mac-SSH = тестовый стенд (user разрешил «делать вообще всё»).
  После device-тестов оставить телефон в чистом состоянии.
- Каждый блок: brief → implement → build → test/device-verify → commit → push. Гейты не пропускать.

## Состояние на старт
HEAD `5de4ad9`. Stable v2.38.2; in-flight v2.40.0-r4 (соак). Android csproj 2.38.2.
Сделано ранее этой ночью: Android no-doze (device-verified) + SingBoxManager ProcessExit
leak + HealthMonitor.Start идемпотентность. Full suite 1562 green.

## План блоков (~6h, safe-first → escalating)

### Block 1 (~1.5h) — Android parity: diagnostics export (ADDITIVE, safe)
Десктоп имеет «Export diagnostics» (v2.39/v2.40); Android — нет. Портировать:
Android собирает filesDir-логи + `singbox.stderr.log` + crash-репорты + redacted config →
redacted ZIP в shareable место → запись в kebab/Advanced. Core `DiagnosticsRedactor`
переиспользуем. Device-verify: trigger → pull ZIP → проверить redaction. Risk: LOW (новая
фича, не трогает существующие flow).

### Block 2 (~2h) — Android perf: Servers O(N^2) ИЗМЕРЕНИЕ + (gated) fix
Audit P0. Сначала ИЗМЕРИТЬ (safe): добавить rebuild-счётчики (additive log) → синтезировать
100/500 серверов на девайсе → `dumpsys gfxinfo`/`meminfo` + frame timing + число rebuild.
GO/NO-GO: если O(N^2) подтверждён И могу безопасно device-verify рендеринг → инкрементальный
апдейт строк (behavior-preserving) + verify на 5/100/500. Иначе → документ + готовлю фикс на
утро (не мержу рискованный UI-рефактор вслепую).

### Block 3 (~1h) — Core leak/test hardening + app-picker измерение
Остаток objective-audit + расширение test-coverage. App picker (audit P1, Control-per-row) —
измерить на реальном app-count девайса; фикс только если ясно подтверждён + verifiable.

### Block 4 (~1h) — консолидация
Full suite, device cleanup, обновить handoff + docs, CI-гейт ритуал (rule #15), morning report
с decision-меню (релиз r5/cut, HttpClient A/B, dep rebase).

Буфер на итерации/откаты.

## Журнал выполнения
- [x] Block 1 — Android diagnostics export — DONE + device-verified on A101BM.
      Kebab item renders (Diagnostics section), tap → `AND-DIAG-EXPORT: wrote …zip
      (6 entries)`. Pulled+unzipped: README + non-secret summary + 3 redacted
      crash reports + singbox-stderr. **Secret scan clean** (no token/vless/UUID/
      server-IP). New `AndroidDiagnosticsExporter` + kebab wiring + MenuItemExportDiag
      (Core+Android loc) + re-pinned AndroidApp hash 6038c7f9. Fixed a build bug:
      `global::` at top level of `$"{}"` breaks the interpolation parser → moved to
      locals. v1 = save-to-external + toast (share-sheet = follow-up). Commit: (next)
- [ ] Block 2 — Servers perf measure (+gated fix)
- [ ] Block 3 — Core hardening + app-picker measure
- [ ] Block 4 — consolidate + morning report

### Workflow #1 — regression-review-since-r4 (15 agents, ~1.1M tokens)
Adversarial review of the post-r4 diff (no-doze + SingBoxManager/HealthMonitor leaks +
diagnostics export + page-subs). **1 confirmed MEDIUM, 8 false-positives** (verifier
correctly refuted: TOCTOU prompt race, boxService null-check race, catch-breadth, etc.).
- CONFIRMED: `DiagnosticsExporter.TailLines` (desktop + Android) read the WHOLE log into
  memory before tailing 800 lines → corrupt/runaway multi-GB log OOMs the bundle. Latent
  (not a regression), but real. **FIXED**: bounded seek-read (last 2 MB) in both;
  `DiagnosticsExporterTailBoundedTests` (huge-file bounded + small-file). Desktop 17/17.
  → folded into the r5 batch.

(дополняется по ходу)
