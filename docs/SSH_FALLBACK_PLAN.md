# SSH Fallback Transport для VPNRouter

## Контекст
VLESS+Reality блокируется ТСПУ всё чаще. SSH пока не блокируется (whitelisted как легитимный протокол). Нужен аварийный режим — переключатель `--transport ssh` в CLI, отдельная секция в config.yaml. sing-box нативно поддерживает `type: "ssh"` outbound.

**Ограничения SSH:** TCP-only (нет UDP → нет голосовых звонков, QUIC). Это emergency fallback, не замена VLESS.

**Безопасность:** Использовать ДРУГОЙ IP для SSH сервера (не тот же что VLESS). Если ТСПУ обнаружит VLESS → заблокирует SSH к тому же IP (trigger-based blocking).

---

## Изменения по файлам

### 1. `VPNRouter.Core/Models/AppSettings.cs`
- Добавить класс `SshConfig` (server, port, user, password, private_key_path, private_key_passphrase, host_key)
- Добавить `[YamlMember(Alias = "ssh")] public SshConfig Ssh` в `AppSettings`
- Добавить `[YamlMember(Alias = "transport")] public string Transport = "vless"` в `AppConfig`

### 2. `VPNRouter.Core/Models/VPNConfig.cs`
- Добавить SSH-поля в `SingBoxOutbound`: User, Password, PrivateKeyPath, PrivateKeyPassphrase, HostKey (все nullable, `NullValueHandling.Ignore`)

### 3. `VPNRouter.Core/Services/ConfigGenerator.cs` (основная логика)
- Добавить параметр `transportOverride` в `Generate()`
- Новый метод `BuildSshOutbounds()` → создаёт `type: "ssh"` outbound с тегом "proxy"
- В `BuildRoute()`: когда SSH → добавить `{ network: "udp", action: "reject" }` для routed processes (UDP блокируем, не лекаем)
- DNS без изменений — DoH через `detour: "proxy"` работает и с SSH (DoH = TCP)

### 4. `VPNRouter.Core/Services/LeakProtection.cs`
- Новый метод `ValidateSshOutbound()`: проверка server, port, user, auth (password OR key), файл ключа существует
- Warning: "SSH is TCP-only", "no host_key"
- Распознавать `type == "ssh"` в цикле валидации outbounds

### 5. `VPNRouter.Core/Services/VpnEngine.cs`
- Принимать `transportOverride` в `StartAsync()`
- Бейкить transport mode в `settings.App.Transport` чтобы HealthMonitor подхватил
- Warning если SSH server IP == VLESS server IP
- Валидация SSH конфига при старте

### 6. `VPNRouter.CLI/Commands/StartCommand.cs`
- Новый CLI флаг: `[CommandOption("-t|--transport <TRANSPORT>")]`
- Передать в VpnEngine и DryRunAsync

### 7. `VPNRouter.Core/Services/SettingsLoader.cs`
- Null-safety: `settings.Ssh ??= new SshConfig()`
- Дефолты в `CreateDefaults()`

### 8. Config example + CLAUDE.md
- Добавить секцию `ssh:` в пример конфига с комментариями
- Обновить документацию

---

## Использование

```bash
# Обычный режим (VLESS)
vpnrouter start --profile Discord_Privacy

# Аварийный SSH fallback
vpnrouter start --profile Discord_Privacy --transport ssh

# Dry-run для проверки конфига
vpnrouter start --profile Discord_Privacy --transport ssh --dry-run
```

```yaml
# config.yaml
app:
  transport: vless  # или ssh для постоянного режима

ssh:
  server: "different-server.com"  # ДРУГОЙ IP чем VLESS!
  port: 22
  user: "vpnuser"
  private_key_path: "C:\\Users\\you\\.ssh\\id_ed25519"
```

---

## Безопасность на мобилках

### Рекомендации для минимального риска блокировки

1. **Разные IP для разных протоколов**
   - Сервер A (IP-1): VLESS+Reality (основной)
   - Сервер B (IP-2): SSH fallback (аварийный)
   - Никогда не используй оба протокола на одном IP

2. **Промежуточный сервер (relay chain)**
   ```
   Телефон → Рос. VPS (whitelist IP) → Заруб. VPS → Интернет
   ```
   Самый надёжный вариант, но с января 2026 провайдерам запретили сдавать whitelist IP.

3. **SSH tunnel для мобилок**
   - Android: Termius, JuiceSSH, ConnectBot (ssh -D для SOCKS5)
   - iOS: Termius, Prompt 3
   - Настроить системный прокси → SOCKS5 127.0.0.1:1080

4. **Правила безопасности**
   - SSH сервер на нестандартном порту (не 22 — меньше сканирования)
   - Только key-based auth (без паролей)
   - Не держать SSH туннель постоянно (паттерн long-lived tunnel детектируется)
   - Периодически разрывать и переподключать
   - Не гонять большие объёмы (видео) через SSH — паттерн трафика выдаёт туннель

5. **Мониторинг блокировок**
   - [hxehex Discord](https://discord.gg/QPBdMf8dxG) — live статус по регионам
   - [net4people/bbs](https://github.com/net4people/bbs/issues) — технические обсуждения
   - [OONI Explorer](https://explorer.ooni.org/) — данные измерений

---

## Что НЕ входит
- Автоматический failover VLESS→SSH (пользователь сказал — отдельный переключатель)
- GUI изменения (SSH конфигурируется через YAML)
- Мобильная версия (VPNRouter = Windows only)
- Multi-SSH urltest

## Верификация
1. `--dry-run` с `--transport ssh` → current.json содержит SSH outbound + UDP reject rules
2. `--dry-run` без `--transport` → обычный VLESS (регрессия)
3. Пустой SSH конфиг + `--transport ssh` → ошибка валидации
4. Тот же IP что VLESS → warning
5. Live тест: TCP трафик через SSH, UDP gracefully rejected
