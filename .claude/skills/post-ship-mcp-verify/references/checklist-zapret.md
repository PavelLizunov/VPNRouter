# Zapret feature checklist

Use when release notes mention Zapret / DPI bypass / probe / strategy
cache / hosts. All commands run from the repo root; all UI work happens
on brat. Proven RU selectors only — names are exact current strings.

## Navigate to the Zapret page

```powershell
# From Simple mode (skip if already in Advanced):
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "Расширенные настройки" -ControlType Button -UiaOperation Invoke
# Main tab strip, then Tools sub-tab:
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "Инструменты" -ControlType ListItem -UiaOperation Invoke
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "Zapret" -ControlType ListItem -UiaOperation Invoke
```

## Hero — stopped state

1. Inspect the magic button (proves the page rendered, button ready):

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "Включить обход блокировок" -ControlType Button
```

2. Screenshot `rdp-shots/rN-zapret-stopped.png`. Visual assertions:
   hero title «Обход блокировок», lede text, 3-step chips. (Title/lede
   TextBlocks have no stable selector — visual assertion; selector
   hardening = future work.)

## Start + running state

3. Invoke the magic button:

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "Включить обход блокировок" -ControlType Button -UiaOperation Invoke
```

4. Wait: ~10 s on cache hit, up to 2-7 min on a full probe. Then assert
   the running label exists:

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "Остановить обход" -ControlType Button
```

5. Screenshot `rdp-shots/rN-zapret-running.png`. Visual: title «Активна
   стратегия: <name>» (or «Подбираю стратегию...» mid-probe) + air-pill.
   Dynamic text — visual assertion; selector hardening = future work.

## Cache controls (Дополнительно tab)

6. Open the inner tab and inspect both cache buttons:

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "Дополнительно" -ControlType Button -UiaOperation Invoke
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "Найти стратегию заново" -ControlType Button
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "Очистить кэш стратегий" -ControlType Button
```

7. Clear the cache and screenshot `rdp-shots/rN-zapret-cache-empty.png`:

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "Очистить кэш стратегий" -ControlType Button -UiaOperation Invoke
```

   Visual: status line «Кэш пуст — следующая проверка будет полной» and
   toast «Кэш стратегий очищен». Status TextBlock is dynamic — visual
   assertion; selector hardening = future work.

## Stop

8. Invoke «Остановить обход» (same command shape as step 3 with the
   running label), screenshot `rdp-shots/rN-zapret-stopped-again.png`,
   visual: hero back to stopped title.

## Known benign noise

- winws.exe immediate-exit entries during probing — AV on brat can
  block individual strategies; expected, not a failure.
- Per-strategy failures during a full sweep are normal until a winner.

## Pass criteria

- Magic button inspects in both states; Invoke transitions them.
- Cache buttons exist; clear-cache visibly mutates the status line.
- No unexpected `[ERR]`/`Exception`/`FATAL` in remote logs.

## Final evidence

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action screenshot -LocalOutput rdp-shots/rN-zapret-final.png
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action logs
```
