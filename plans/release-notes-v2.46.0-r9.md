# VPNRouter v2.46.0-r9

## Summary

- Split the Apps editor catalogues: "Через VPN" keeps the existing platform app profiles, while "Мимо VPN" now loads its own Windows bypass catalogue.
- Added Windows bypass categories for Russian desktop apps, game launchers, anti-cheat helpers, remote/LAN tools, work calls, and advanced dev tools.
- Added local Steam game import in the "Мимо VPN" editor. It reads local Steam libraries and manifests, scans installed game folders for `.exe` files, and skips uninstallers, crash reporters, setup files, and redists.
- Steam import is local-only: no Steam Web API and no static popular-games list.

## Test Flow

1. Open Applications.
2. Switch between "Через VPN" and "Мимо VPN"; confirm the lists are separate.
3. In "Мимо VPN", confirm Steam/game/anti-cheat categories are available but unchecked by default.
4. Click "Импорт Steam" on a machine with Steam installed; confirm detected game `.exe` entries appear as custom candidates.
5. Select an imported Steam executable and confirm it is saved to `RoutingAppsExclude`, not `RoutingAppsInclude`.

## Verification

- `dotnet build VPNRouter.sln -c Release`
- `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~MainWindowViewModelAppsModeTests|FullyQualifiedName~SteamLibraryScannerTests|FullyQualifiedName~MainWindowViewModelCharacterizationTests"`
- `powershell -ExecutionPolicy Bypass -File tools/verify-last-commit-ci.ps1`
