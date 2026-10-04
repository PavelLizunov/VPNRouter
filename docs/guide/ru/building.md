# Сборка из исходников

Часть [README VPNRouter](../../../README.ru.md).

## Сборка из исходников

Установите .NET SDK из [`global.json`](../../../global.json) (10.0.301, с разрешённым переходом на новые патчи). Стандартная сборка solution не собирает Android-приложение. Для Android также нужны workload, Android SDK, JDK и локальные нативные библиотеки; см. [инструкцию сборки Android](../../../VPNRouter.Android/AGENTS.md).

```bash
git clone https://github.com/PavelLizunov/VPNRouter.git
cd VPNRouter
dotnet build VPNRouter.sln
dotnet run --project VPNRouter.App
```

Release-сборка + упаковка:

```powershell
# Windows (PowerShell) — производит full + update ZIP'ы + их .sha256
powershell -ExecutionPolicy Bypass -File build.ps1 -Version "{version}"
```

```bash
# macOS DMG — запускается на любом Mac с .NET 10 SDK
./build-mac.sh {version}
```

```bash
# Linux — .deb + .AppImage + .tar.gz через тот же GitHub Actions pipeline
# локально: dotnet publish -c Release -r linux-x64 --self-contained -o out/
```

**macOS (DMG)**, **Linux** (.deb/.AppImage/.tar.gz) и подписанный **Android ARM64 APK** собираются автоматически через GitHub Actions на каждый `v*` push тега — см. `.github/workflows/build-mac.yml`, `.github/workflows/build-linux.yml`, `.github/workflows/build-android.yml`, `.github/workflows/publish-apt.yml` (APT-репозиторий), `.github/workflows/build-free-pool.yml` (обновляющийся Free Configs пул). Загрузка релизных файлов требует существующего draft и точного соответствия тега/SHA; ручные сборки запускаются с `--ref vVERSION`. **Windows**-команда `build.ps1 -Upload` только загружает неподписанные файлы в draft и отказывается работать при полной или частичной настройке SignPath. При настроенной подписи используется `Sign Windows (SignPath)`. Публикация — отдельное действие владельца после сборки, тестов и проверки ровно 16 файлов; кандидаты остаются prerelease, а не Latest. См. [процедуру выпуска](../../../.dsh/skills/ship-rolling-candidate/SKILL.md). Актуальная матрица сборки/платформ — [`CURRENT_STATE.md`](../../../CURRENT_STATE.md).
