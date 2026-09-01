# VPNRouter - короткий читшит проекта

## Что это

VPNRouter - кроссплатформенный процессный split-tunnel VPN-роутер на .NET 10. Выбранные приложения направляются через sing-box и поддерживаемые VPN-протоколы, а остальной трафик следует выбранному режиму маршрутизации. Поддерживаются Windows, macOS, Linux и Android.

## Карта репозитория

| Путь | Назначение |
|---|---|
| `VPNRouter.Core/` | Бизнес-логика: настройки, маршрутизация, sing-box, подписки, firewall и VPN lifecycle |
| `VPNRouter.App/` | Desktop GUI на Avalonia: Views, ViewModels, темы и локализация |
| `VPNRouter.Android/` | Android-приложение, Java/JNI VPN runtime и переиспользуемый Core |
| `VPNRouter.CLI/` | Командная строка |
| `VPNRouter.Service/` | Windows Service |
| `VPNRouter.GUI/` | Go trampoline для запуска, обновления и self-repair на Windows |
| `VPNRouter.Tests/` | xUnit, headless Avalonia и release/tooling contract tests |
| `VPNRouter.Tools/` | Вспомогательные утилиты, включая PoolAggregator |
| `tools/` | Build, CI, release и remote-verification scripts |
| `packaging/` | Инсталляторы и platform packages |
| `.github/workflows/` | CI, platform builds и release automation |
| `plans/` | Планы, task briefs, outcomes, post-mortem и defect ledger |
| `design/` | Design handoff и референсные макеты |

Основной поток: App/CLI/Service -> `VPNRouter.Core` -> генерация и fail-closed проверка sing-box JSON -> `VpnEngine` -> sing-box/TUN и platform firewall.

## Что читать модели

1. `docs/agent-contract.md` - канонический safety/Git/test/release контракт.
2. Корневой `AGENTS.md` и `AGENTS.local.md` - DSH entry point и локальный overlay.
3. Ближайший `<zone>/AGENTS.md` перед изменением файлов этой зоны.
4. `docs/test-workers.md` перед remote build/test/deploy работой.
5. `CURRENT_STATE.md` для текущих release/platform фактов.
6. `plans/OPEN-DEFECTS.md` перед release-sensitive изменениями.

Старые планы и design transcripts являются историей решений. Если они расходятся с кодом, каноническим контрактом или zone-инструкцией, следовать актуальному источнику.

## DSH-файлы проекта

- `AGENTS.md` и scoped `*/AGENTS.md` - иерархические инструкции.
- `docs/agent-contract.md` - единый проектный контракт.
- `AGENTS.local.md` - короткий repository-local overlay без дублирования политики.
- `.dsh/skills/<name>/SKILL.md` - нативные project skills.
- `docs/test-workers.md` - роли воркеров, preflight и правила ресурсов.
- `plans/phase-*.md` - task brief и проверяемый Outcome.

Доступные project skills: `audit-overflow-fix`, `bug-hunt`, `cut-stable`, `diagnose-config`, `merge-design-handoff`, `phase-task-launcher`, `post-ship-mcp-verify`, `ship-rolling-candidate`, `update-readme-versions`.

## Есть ли RAG

Полноценного RAG в проекте нет: отсутствуют vector database, embeddings, chunking/index pipeline и retrieval service.

Вместо RAG используется файловая база знаний:

- DSH instruction hierarchy (`AGENTS.md`) и project skills (`.dsh/skills/`);
- точечные `read`, `grep` и `glob` по исходникам и Markdown;
- `plans/OPEN-DEFECTS.md` как durable ledger;
- `plans/*.md`, `CURRENT_STATE.md` и zone-инструкции как история и актуальный контекст;
- DSH session goals/state для текущей работы.

Файлы со словами `index`, `memory`, `context` или `handoff` сами по себе не являются retrieval-индексом.

## Быстрые команды

```bash
dotnet build VPNRouter.sln -c Release
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build
dotnet run --project VPNRouter.App
```

Для итерации брать узкий test command из соответствующего zone `AGENTS.md`, а полный oracle - из `docs/agent-contract.md`.
