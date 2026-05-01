# Computer-use MCP setup for Claude Code

Цель: дать Claude (когда работает в режиме Claude Code) возможность
самому кликать в Avalonia-окне VPNRouter'а для smoke-тестов GUI до
того как просить пользователя.

## Текущее состояние (2026-05-01)

На этой VM **нет** Python / Node.js установленных глобально. Стандартный
путь установки computer-use MCP server'а требует один из них.

```
node: NOT FOUND
npm: NOT FOUND
python: NOT FOUND
py: NOT FOUND
```

## Варианты подключения

### Вариант 1 — Node-based MCP (рекомендуется если пользователь уже работает с npm)

```powershell
# 1. Установить Node.js 20+ (если ещё нет)
winget install OpenJS.NodeJS.LTS

# 2. После рестарта shell проверить:
node --version    # должно показать v20.x.x
```

Создать в корне проекта `.mcp.json`:

```json
{
  "mcpServers": {
    "computer-use": {
      "command": "npx",
      "args": ["-y", "@nut-tree/nut.js-mcp"],
      "comment": "Cross-platform desktop automation via nut.js"
    }
  }
}
```

### Вариант 2 — Python-based MCP (если уже есть Python 3.11+)

```powershell
# 1. Установить Python 3.11+ если нет
winget install Python.Python.3.12

# 2. Установить pyautogui-based MCP server
pip install fastmcp pyautogui pillow

# 3. Создать минимальный server file (пример) или использовать готовый:
pip install mcp-server-pyautogui    # if such package exists

# 4. Verify:
python -c "import pyautogui; print(pyautogui.position())"
```

`.mcp.json`:
```json
{
  "mcpServers": {
    "computer-use": {
      "command": "python",
      "args": ["-m", "mcp_server_pyautogui"],
      "comment": "PyAutoGUI-based desktop automation"
    }
  }
}
```

### Вариант 3 — Custom .NET MCP server (zero external deps)

Поскольку у нас уже есть .NET 8 SDK, можно написать **собственный**
минимальный MCP server с использованием:
- `System.Drawing.Common` для screenshot
- `System.Windows.Automation` для UI tree introspection
- `User32` P/Invoke для mouse + keyboard

Это ~200 строк C# кода. Преимущества: zero new install, нативная
поддержка Avalonia UI Automation tree (есть AutomationProperty.Name
+ AutomationId на наших элементах).

Если выбираем этот путь — могу написать в `tools/VpnRouterTestMcp/`
проект и встроить в .mcp.json вместо внешних зависимостей.

## Безопасность / scope

Любой computer-use MCP даёт Claude управление мышью/клавиатурой на ВСЕЙ
системе, не только на VPNRouter'е. Запускать **только** в dev-VM
(VirtualBox guest), не на host.

Когда не нужен — закомментировать секцию в `.mcp.json` или удалить
файл (Claude Code прочитает его при следующем рестарте).

## Когда подключать

Не сейчас — текущая сессия v2.30.2 уже идёт через diagnostic logging
который покрывает потребности (см. `plans/release-notes-v2.30.2-r1.md`).
Computer-use нужен будет когда:
- Появляется UX-баг которого diagnostic log не ловит (визуальный
  artefact, color, layout)
- Нужно прогнать regression-suite через GUI после major refactor
- v3.0 Android Avalonia port — там diagnostic log не доступен пока
  не запустишь APK

## Decision log

- **2026-05-01**: deferred (Node + Python not installed, текущие баги
  закрываются через diag logging). Файл создан как memory для будущего.
