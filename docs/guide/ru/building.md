# Сборка из исходников

Часть [README VPNRouter](../../../README.ru.md).

## Сборка из исходников

Установите .NET SDK из [`global.json`](../../../global.json). Стандартная сборка solution не собирает Android-приложение. Для Android также нужны workload, Android SDK, JDK и локальные нативные библиотеки; см. [инструкцию сборки Android](../../../VPNRouter.Android/AGENTS.md).

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

[Команда подготовки релиза](../../release-strategy.md) создаёт черновик и параллельно запускает сборки Windows, macOS, Linux и Android в GitHub Actions по точному тегу. Задание Windows собирает неподписанный установщик и проверяет ZIP обновления. Для подписи Windows используется [SignPath](../../code-signing-signpath-runbook.md). Владелец разрешает публикацию после сборок, тестов и проверки всех 18 файлов. Кандидаты остаются prerelease, а стабильные релизы обновляют APT и Homebrew. См. [актуальную матрицу платформ](../../../CURRENT_STATE.md).
