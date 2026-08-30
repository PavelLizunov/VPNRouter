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

2. Run the isolated `PageScreenshotTests`/`VisualDiffTests` Tools-page case.
   It must cover the stopped hero, lede, 3-step chips and viewport bottom with
   in-memory settings. Add a synthetic-state screenshot test when the release
   changes an uncovered state.

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

5. Use the running action Name as the live state proof. Render the running and
   mid-probe titles plus air-pill through isolated synthetic-state screenshot
   cases when those visuals changed; never capture the live remote desktop.

## Cache controls (Дополнительно tab)

6. Open the inner tab and inspect both cache buttons:

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "Дополнительно" -ControlType Button -UiaOperation Invoke
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "Найти стратегию заново" -ControlType Button
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "Очистить кэш стратегий" -ControlType Button
```

7. Clear the cache:

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "Очистить кэш стратегий" -ControlType Button -UiaOperation Invoke
```

   Require a stable UIA assertion for the resulting action/state when the
   release depends on it. Cover the status line and toast with an isolated
   synthetic-state screenshot test.

## Stop

8. Invoke «Остановить обход» (same command shape as step 3 with the running
   label), then re-inspect the stopped action Name. This proves the live state
   returned without exposing the desktop.

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
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~PageScreenshotTests|FullyQualifiedName~VisualDiffTests"
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action logs
```
