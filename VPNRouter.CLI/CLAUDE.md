# VPNRouter.CLI

CLI обёртка через Spectre.Console. Тонкий wrapper вокруг `VPNRouter.Core`.

## Команды

```
VPNRouter.CLI start --profile <name> [--dry-run]
VPNRouter.CLI start --profile "Name1,Name2,Name3"   ← merge нескольких профилей
VPNRouter.CLI stop
VPNRouter.CLI status
VPNRouter.CLI profiles list
VPNRouter.CLI profiles show <name>
VPNRouter.CLI profiles update
VPNRouter.CLI service install / uninstall / start / stop / status
```

## Layout

```
Program.cs                  ← Spectre.Console root + DI
Commands/
  StartCommand.cs           ← вызывает VlessServersResolver / SubscriptionResolver / VpnEngine
  StopCommand.cs
  StatusCommand.cs
  ProfilesCommands.cs
  ServiceCommands.cs        ← обёртка над ServiceInstaller (sc.exe)
Helpers/
  AdminHelper.cs            ← IsAdmin() check
```

## Critical patterns

### Admin check
```csharp
if (!AdminHelper.IsAdmin())
{
    AnsiConsole.MarkupLine("[red]Run as Administrator[/]");
    return 1;
}
```
Все команды кроме `status`, `profiles list/show`, `--dry-run` требуют admin
(TUN + ETW + Firewall — Windows; sudo — macOS/Linux).

### dry-run
`start --dry-run` собирает sing-box JSON, валидирует через `LeakProtection`,
выводит config preview. **Не запускает sing-box.** Полезно для тестирования
профилей без admin.

### Subscription resolution
Service + CLI обязаны звать `SubscriptionResolver.ResolveAsync(refreshFromNetwork: true)`
ДО `VpnEngine.StartAsync` — чтобы свежие подписочные сервера попали в
`Vless.Servers`. GUI делает это иначе (через MainWindowViewModel +
VlessServersResolver), не путать.

## Build

```powershell
dotnet publish VPNRouter.CLI -c Release -r win-x64 --self-contained -o publish/cli
```

Финальный exe ~10 MB после publish (shared runtime с App + Service).
