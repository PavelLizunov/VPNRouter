# VPN core verification (windows-brat)

Use for subscriptions, VLESS, sing-box, TUN, connect/disconnect, and failover changes. Set `$v` to the shipped version.

1. Capture the disconnected main window and visually confirm selected server/config, mode, and no error banner:

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action screenshot -LocalOutput "artifacts/brat-verify/$v/vpn-core-disconnected.png"
```

2. Read the current RU Connect button Name from the screenshot. Inspect then invoke it through UIA. Do not guess the translated name or AutomationId:

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "<current RU Connect Name>" -ControlType Button -UiaOperation Inspect
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "<current RU Connect Name>" -ControlType Button -UiaOperation Invoke -TimeoutSeconds 120
```

3. Wait for the final connected state. Screenshot and verify the tunnel status, selected outbound/server, traffic/status indicators, and that every interactive control in the visible scope remains usable. Status TextBlocks lack stable selectors: `selector hardening: future work`.

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action screenshot -LocalOutput "artifacts/brat-verify/$v/vpn-core-connected.png" -TimeoutSeconds 120
```

4. For failover/server-selection releases, exercise the exact release-note scenario and capture the resulting selected server/error state. Never alter the dev-box network.

5. Read the current RU Disconnect Name, inspect/invoke it, verify the disconnected state returns, then scan remote logs:

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "<current RU Disconnect Name>" -ControlType Button -UiaOperation Invoke -TimeoutSeconds 120
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action screenshot -LocalOutput "artifacts/brat-verify/$v/vpn-core-final.png"
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action logs
```

Pass only if connect, stable connected state, release-note scenario, and disconnect complete on WINBRAT with clean logs. Attach all screenshot paths.
