# VPNRouter.Service

Windows Service wrapper. Запускается при boot до user logon. Использует тот же
`VpnEngine` что GUI/CLI, плюс `TunOwnershipLock` для координации.

## Быстрая проверка

```powershell
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~ServiceAppCoexistenceTests|FullyQualifiedName~AutostartContractTests|FullyQualifiedName~RuntimeStatusAdoptionTests"
```

## Layout

```
Program.cs              ← --service vs console mode detection
VPNRouterService.cs     ← BackgroundService implementation. Использует SubscriptionResolver.
ServiceInstaller.cs     ← sc.exe install/uninstall, failure recovery (3x/60s)
```

(`Worker.cs` template-scaffold удалён в одной из ранних v2.27 чисток —
он никогда не был зарегистрирован в DI и не запускался.)

## Lifecycle

1. **`sc.exe create VPNRouter binPath= "...\VPNRouter.Service.exe --service" start= auto`** (regular auto-start, не delayed — VPN должен подняться ASAP после boot; см. комментарий в `ServiceInstaller.Install`).
2. Service запускается под `LocalSystem` после Tcpip/Dnscache/Dhcp (boot deps).
3. `VPNRouterService.ExecuteAsync` стартует → читает `config.yaml` →
   `SubscriptionResolver.ResolveAsync(refreshFromNetwork: true)` → запускает
   `_engine` через `ResilientStarter` (5/10/20/40s backoff).
4. Watcher mode: если другой процесс уже владеет TUN-локом
   (`TunOwnershipLock.IsOwnedByAnyone()`), service не контендит — паркуется,
   но file-watcher на `config.yaml` остаётся активен (hot-reload работает).

## Critical patterns

### TunOwnershipLock
Single global semaphore. Один процесс владеет sing-box за раз. Если GUI и
Service оба пытаются запустить sing-box — второй переходит в watcher mode
вместо ошибки. См. `plans/vpnrouter-v2.27-service-ux.md`.

### Hot-reload через config.yaml watcher
```csharp
_watcher.Changed += async (_, _) => {
    var newSettings = SettingsLoader.Load(AppPaths.ConfigYamlPath);
    if (_engine?.IsRunning == true)
        await _engine.ApplyAsync(newSettings);
};
```
**Apply path обязан звать VlessServersResolver.Resolve внутри** (v2.28.2 fix).
В Service path он делается косвенно — через инициализированный VpnEngine.

### EventLog
Все ошибки идут в Windows Event Log (Source: "VPNRouter"). Юзер ищет проблемы
через Event Viewer.

### Install / uninstall
`VPNRouter.Service.exe` сам распознаёт только `--service` (Program.cs ветвится
на `args.Contains("--service")`; любой другой arg → console mode). Install /
uninstall запускаются из CLI:
```
VPNRouter.CLI service install   → ServiceInstaller.Install()   (sc create)
VPNRouter.CLI service uninstall → ServiceInstaller.Uninstall() (sc stop + delete)
VPNRouter.Service.exe --service → запуск от sc.exe (не вручную!)
```

`ServiceInstaller.RunSc` не повышает права сам (`UseShellExecute=false`, без
`Verb`); caller обязан быть уже elevated (CLI проверяет `AdminHelper.IsAdmin()`).
