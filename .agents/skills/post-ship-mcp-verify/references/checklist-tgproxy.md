# TgProxy verification (windows-brat)

Use when release notes mention Telegram proxy, MTProto, `tg://`, or TgProxy.

All interaction runs through `tools/brat-verify.ps1` on fixed WINBRAT. Set `$v` to the shipped version. Never use local UI tools.

## End-to-end scenario

1. Render **Tools > Telegram proxy** in the isolated screenshot suite:

```powershell
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~PageScreenshotTests|FullyQualifiedName~VisualDiffTests"
```

The headless render uses in-memory settings and must cover the page/title/three
setup steps. Live navigation or state without a stable selector is a failed
checklist item, not permission to capture the remote desktop.

2. Resolve the current RU main-action label from `Strings.cs`/XAML. The button has `AutomationProperties.Name="{Binding LblTgProxyMainAction}"`; inspect it, then invoke it:

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "<current RU main-action Name>" -ControlType Button -UiaOperation Inspect
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "<current RU main-action Name>" -ControlType Button -UiaOperation Invoke -TimeoutSeconds 120
```

3. Wait for startup/download completion and prove it through the running
main-action UIA Name. The isolated running-state screenshot case covers the
title, `:1443` air-pill, localized stats and error-banner layout.

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "<current RU stop-action Name>" -ControlType Button -UiaOperation Inspect -TimeoutSeconds 120
```

4. If the release touches link handling, inspect and invoke the copy-link button using its current RU label; it has `AutomationProperties.Name="{Binding L_TgProxyCopyLink}"`. Port and secret inputs also expose bound accessibility names. Do not guess their runtime translations.

5. Read the current running main-action label, inspect every interactive control in the visible scope, invoke Stop, and confirm the stopped state returns and the air-pill disappears.

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "<current RU stop-action Name>" -ControlType Button -UiaOperation Invoke
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "<current RU start-action Name>" -ControlType Button -UiaOperation Inspect
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action logs
```

## Pass gate

- Stopped, downloading/running, stats, and stopped-again states render correctly.
- Expected lifecycle includes proxy spawn, port `1443`, stats refresh, scheme registration, and clean stop.
- `Already running on port 1443` is benign only after a deliberate repeated Start.
- First-run dependency download is benign; unexpected proxy exit is a failure.
- Attach the isolated screenshot test result and sanitized remote log result.
