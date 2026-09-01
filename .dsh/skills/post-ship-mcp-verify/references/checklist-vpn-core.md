# VPN core verification (windows-brat)

Use for subscriptions, VLESS, sing-box, TUN, connect/disconnect, and failover changes. Set `$v` to the shipped version.

1. Render the disconnected main window through isolated headless tests and
confirm mode/layout/no-error state without loading the live subscription:

```powershell
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~PageScreenshotTests|FullyQualifiedName~VisualDiffTests"
```

2. Resolve the RU Connect button Name from current XAML/resources. Inspect then invoke it through UIA. Do not guess it:

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "<current RU Connect Name>" -ControlType Button -UiaOperation Inspect
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "<current RU Connect Name>" -ControlType Button -UiaOperation Invoke -TimeoutSeconds 120
```

3. Wait for the final connected state and require `tools/brat-stability.ps1` to prove
TUN Up, tunnel route and proxy-attributed HTTPS/UDP. Inspect the Disconnect
button to prove the live UI reached connected state.

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "<current RU Disconnect Name>" -ControlType Button -UiaOperation Inspect -TimeoutSeconds 120
```

4. For failover/server-selection releases, exercise the exact release-note scenario and capture the resulting selected server/error state. Never alter the dev-box network.

5. Read the current RU Disconnect Name, inspect/invoke it, verify the disconnected state returns, then scan remote logs:

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "<current RU Disconnect Name>" -ControlType Button -UiaOperation Invoke -TimeoutSeconds 120
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "<current RU Connect Name>" -ControlType Button -UiaOperation Inspect
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action logs
```

Pass only if connect, proxy-attributed dataplane, stable connected state,
release-note scenario and disconnect complete on WINBRAT with clean logs. Attach
the isolated screenshot test result; remote desktop capture is forbidden.
