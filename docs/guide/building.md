# Build from source

Part of the [VPNRouter README](../../README.md).

## Build from source

Install the .NET SDK specified by [`global.json`](../../global.json) (10.0.301, with patch roll-forward). The solution's default build excludes the Android app. Android packaging also requires the Android workload, SDK, JDK and local native libraries; see [Android build instructions](../../VPNRouter.Android/AGENTS.md).

```bash
git clone https://github.com/PavelLizunov/VPNRouter.git
cd VPNRouter
dotnet build VPNRouter.sln
dotnet run --project VPNRouter.App
```

Release build + packaging:

```powershell
# Windows (PowerShell) — produces both full + update ZIPs plus their .sha256
powershell -ExecutionPolicy Bypass -File build.ps1 -Version "{version}"
```

```bash
# macOS DMG — runs on any Mac with .NET 10 SDK
./build-mac.sh {version}
```

```bash
# Linux — .deb + .AppImage + .tar.gz via the same GitHub Actions pipeline
# locally: dotnet publish -c Release -r linux-x64 --self-contained -o out/
```

**macOS (DMG)**, **Linux** (.deb/.AppImage/.tar.gz), and the signed **Android ARM64 APK** are built automatically by GitHub Actions on every `v*` tag push — see `.github/workflows/build-mac.yml`, `.github/workflows/build-linux.yml`, `.github/workflows/build-android.yml`, `.github/workflows/publish-apt.yml` (APT repo), and `.github/workflows/build-free-pool.yml` (rolling Free Configs pool). Release uploads require an existing draft and an exact tag/SHA; manual builds must use `--ref vVERSION`. The **Windows** unsigned path `build.ps1 -Upload` only stages files on that draft and refuses configured or incomplete SignPath enrollment. When signing is configured, use `Sign Windows (SignPath)`. Publication is a separate maintainer action after the build/test and exact 16-asset integrity gates; candidates remain prereleases, not Latest. See the [release procedure](../../.dsh/skills/ship-rolling-candidate/SKILL.md). See [`CURRENT_STATE.md`](../../CURRENT_STATE.md) for the live build/platform matrix.
