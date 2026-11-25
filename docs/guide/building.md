# Build from source

Part of the [VPNRouter README](../../README.md).

## Build from source

Install the .NET SDK specified by [`global.json`](../../global.json). The solution's default build excludes the Android app. Android packaging also requires the Android workload, SDK, JDK and local native libraries; see [Android build instructions](../../VPNRouter.Android/AGENTS.md).

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

The [release preparation command](../release-strategy.md) creates the draft and starts the exact-tag GitHub Actions builds for Windows, macOS, Linux and Android in parallel. The Windows workflow builds the unsigned installer and tests the update ZIP. Configured Windows signing uses [SignPath](../code-signing-signpath-runbook.md). The owner authorizes publication after the build, tests and verification of all 18 files. Candidates remain prereleases and stable releases update APT and Homebrew. See [the current platform matrix](../../CURRENT_STATE.md).
