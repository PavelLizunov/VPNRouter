# VPNRouter — Local Instructions

## Git Remotes

| Remote | URL | Notes |
|---|---|---|
| origin | `ssh://git@10.9.1.1:18222/slovn/vpnrouter.git` | Forgejo (через AmneziaWG VPN) |
| github | `https://github.com/PavelLizunov/VPNRouter.git` | GitHub (public) |

**Push policy**: всегда пушить в оба remote после коммита:
```bash
git push origin main && git push github main
```

## Release Process

1. Обновить версию в `VPNRouter.GUI/AppBranding.cs`
2. `dotnet build VPNRouter.sln` — проверить компиляцию
3. Остановить VPNRouter (DLL заблокированы)
4. `powershell -ExecutionPolicy Bypass -File build.ps1 -Version "X.Y.Z"`
5. Коммит + push в оба remote
6. GitHub Release: **ВСЕГДА с `--prerelease`** пока пользователь явно не скажет "стабильный релиз":
   ```bash
   gh release create vX.Y.Z VPNRouter-install-vX.Y.Z.zip VPNRouter-update-vX.Y.Z.zip --prerelease --title "vX.Y.Z — Description" --notes "..."
   ```

## Forgejo Access

- VPN IP: `10.9.1.1` (AmneziaWG)
- Web UI: http://10.9.1.1:18300
- SSH: `ssh://git@10.9.1.1:18222`
- User: slovn
- VPN должен быть активен для доступа
