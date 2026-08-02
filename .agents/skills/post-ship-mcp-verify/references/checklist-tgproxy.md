# TgProxy verification (windows-brat)

Use when release notes mention Telegram proxy, MTProto, `tg://`, or TgProxy.

All interaction runs through `tools/brat-verify.ps1` on fixed WINBRAT. Set `$v` to the shipped version. Never use local UI tools.

## End-to-end scenario

1. Navigate visually to **Tools > Telegram proxy** and capture the stopped state:

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action screenshot -LocalOutput "artifacts/brat-verify/$v/tgproxy-stopped.png"
```

Navigation and status TextBlocks lack explicit stable selectors. Confirm the page/title/three setup steps visually and record `selector hardening: future work`.

2. Read the current RU main-action label shown in the screenshot. The button has `AutomationProperties.Name="{Binding LblTgProxyMainAction}"`; inspect it, then invoke it:

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "<current RU main-action Name>" -ControlType Button -UiaOperation Inspect
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "<current RU main-action Name>" -ControlType Button -UiaOperation Invoke -TimeoutSeconds 120
```

3. Wait for startup/download completion and capture the running state. Confirm the running title, `:1443` air-pill, localized active/total stats, and no error banner. Telegram Desktop absence may show the expected copy-link warning.

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action screenshot -LocalOutput "artifacts/brat-verify/$v/tgproxy-running.png" -TimeoutSeconds 120
```

4. If the release touches link handling, inspect and invoke the copy-link button using its current RU label; it has `AutomationProperties.Name="{Binding L_TgProxyCopyLink}"`. Port and secret inputs also expose bound accessibility names. Do not guess their runtime translations.

5. Read the current running main-action label, inspect every interactive control in the visible scope, invoke Stop, and confirm the stopped state returns and the air-pill disappears.

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "<current RU stop-action Name>" -ControlType Button -UiaOperation Invoke
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action screenshot -LocalOutput "artifacts/brat-verify/$v/tgproxy-final.png"
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action logs
```

## Pass gate

- Stopped, downloading/running, stats, and stopped-again states render correctly.
- Expected lifecycle includes proxy spawn, port `1443`, stats refresh, scheme registration, and clean stop.
- `Already running on port 1443` is benign only after a deliberate repeated Start.
- First-run dependency download is benign; unexpected proxy exit is a failure.
- Attach all three screenshot paths and the remote log result.
