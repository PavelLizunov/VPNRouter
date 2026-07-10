VPNRouter v2.46.0-r33
====================

Quick Start:
1. Double-click "Start VPN.cmd" (or run app\VPNRouter.App.exe directly)
2. Accept the UAC prompt
3. Paste your VLESS URI(s) in the Servers tab
4. Select application groups in the Applications tab
5. Click Start VPN

Folder Structure:
- Start VPN.cmd            Launcher (double-click to start)
- README.txt               This file
- app\                     Application files
  - VPNRouter.App.exe      Main app (Avalonia GUI, tray icon, settings)
  - VPNRouter.CLI.exe      Command-line interface (advanced)
  - VPNRouter.Service.exe  Windows Service (optional, for auto-start)
  - sing-box.exe           VPN engine (auto-copied on first run)
  - profiles\              Application profiles

CLI Usage (run from app\ folder):
  VPNRouter.CLI.exe start --profile Discord_Privacy
  VPNRouter.CLI.exe status
  VPNRouter.CLI.exe stop

Service Installation (run as admin):
  VPNRouter.CLI.exe service install
  VPNRouter.CLI.exe service start
