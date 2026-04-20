# Free Configs feature — roadmap v2.13.16 → v2.14.2

**Goal**: Transform Free Configs tab from "slow, opaque, unreliable" to "fast, goal-oriented, hybrid server+client validated".

**Current baseline** (v2.13.15, stable):
- 14 sources, ~25k unique configs after dedup
- Refresh full cycle: fetch (20s) + parse (1s) + GeoIP (5-15 min) + TCP+TLS (~42 min) = ~50-60 min first run
- Deep Verify via spawned sing-box — working (v2.13.12 fix)
- Cancel preserves progress (v2.13.15)
- Skip recently-tested < 6h (v2.13.14)

**Target** (after v2.14.2):
- First run: ~3-5 min (find 50 fast configs)
- Full scan: ~15 min (was 50+)
- Deep Verify with goal-seeking: "find 5 configs > 10 Mbps, ping < 200 ms"
- Server-side aggregation (GeoIP done once on CI, shared)
- Per-user custom sources

---

## Priority order (execute top-to-bottom)

### First wave (highest ROI)
1. **v2.13.16** — Speedup TCP+TLS (foundation)
2. **v2.13.17** — Latency-goal Refresh
3. **v2.13.19** — Security warning on first Connect (small, high safety value)
4. **v2.14.1** — GitHub Actions pool aggregator (big UX win, independent)

### Second wave
5. **v2.13.18** — Fast scan TCP-only toggle
6. **v2.14.0** — Bandwidth measurement + presets
7. **v2.14.2** — User-provided sources

### Closing pass (after all features land)
8. **v2.14.3** — UI/UX design polish + cross-page consistency + bug audit

---

# v2.13.16 — TCP+TLS speedup

**Goal**: reduce full-scan time from ~42 min → ~12-15 min.

## Files to change

### `VPNRouter.Core/Services/FreeConfigs/FreeConfigTester.cs`

```csharp
public int MaxConcurrency { get; set; } = 80;  // was 30

private static readonly TimeSpan TlsHandshakeTimeout = TimeSpan.FromSeconds(3);  // was 5
```

In `TestOneAsync`, modify TCP retry logic:

```csharp
for (var attempt = 0; attempt < 2; attempt++)
{
    ct.ThrowIfCancellationRequested();
    var (status, latency, _) = await TcpPingAsync(cfg.Host, cfg.Port, ct);
    if (status == FreeConfigStatus.Ok)
    {
        latencies.Add(latency);
    }
    else
    {
        tcpError = status;
        // Smart retry: skip attempt 2 for definitive errors (no point)
        if (status == FreeConfigStatus.Unreachable) break;
    }
}
```

In `TcpPingAsync`, expose `SocketErrorCode` so we can distinguish `ConnectionRefused`/`HostUnreachable` (don't retry) from timeouts (do retry).

## Testing
- Run Refresh on fresh cache, time it. Expected: ~15 min for 25k.
- Watch Task Manager → no ephemeral port exhaustion (TCP_TIME_WAIT flood).
- If port pressure appears, back off to 60.

## Risk: Windows ephemeral port range
Default: 49152-65535 = ~16k ports. With conn reuse in TIME_WAIT (2 min each), sustained 80 concurrent is fine. If user complains about "network slowdown" during scan — reduce to 60.

## Release
- Bump `AppVersion.cs` → `2.13.16`
- `dotnet build VPNRouter.sln`
- `build.ps1 -Version "2.13.16" -Upload`
- `gh release edit v2.13.16 --prerelease --notes "..."`
- GH Actions triggers mac build automatically

---

# v2.13.17 — Latency-goal Refresh

**Goal**: user presses Refresh, system finds N fast configs in ~3-5 min and stops. Full scan becomes optional.

## Files to change

### `VPNRouter.Core/Services/FreeConfigs/FreeConfigAggregator.cs`

Add parameters to `RefreshAsync`:
```csharp
public async Task<List<FreeConfigEntry>> RefreshAsync(
    IReadOnlyList<FreeConfigSource>? sources = null,
    int maxTestCount = int.MaxValue,
    int skipRecentHours = 6,
    int? targetCount = null,         // NEW — stop after finding N matching
    int? maxLatencyMs = null,        // NEW — filter for "matching"
    CancellationToken ct = default)
```

In Stage 4 (test connectivity), wrap test loop:
```csharp
var foundCount = 0;
await _tester.TestAllAsync(toTest, new Progress<(int done, int total)>(p =>
{
    OnTestProgress?.Invoke(p.done, p.total);
    // Count matches after each completion
    if (targetCount.HasValue && maxLatencyMs.HasValue)
    {
        var current = toTest.Count(c =>
            c.Status == FreeConfigStatus.Ok &&
            c.LatencyMs > 0 && c.LatencyMs <= maxLatencyMs.Value);
        foundCount = current;
        if (foundCount >= targetCount.Value)
        {
            _logger.Information("Latency goal reached: {found}/{target}", foundCount, targetCount);
            _goalReachedCts?.Cancel();
        }
    }
}), ct);
```

Need linked CancellationTokenSource to stop tester early when goal reached. Wrap ct:
```csharp
using var goalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
_goalReachedCts = goalCts;
try { await _tester.TestAllAsync(toTest, progress, goalCts.Token); }
catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* goal reached, not user cancel */ }
```

### `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigsPageViewModel.cs`

Add properties:
```csharp
[ObservableProperty] private bool _useLatencyGoal = false;
[ObservableProperty] private int _latencyGoalTarget = 50;
[ObservableProperty] private int _latencyGoalMaxPingMs = 200;
```

In `RefreshAsync`:
```csharp
var fresh = await Task.Run(() => _aggregator.RefreshAsync(
    targetCount: UseLatencyGoal ? LatencyGoalTarget : null,
    maxLatencyMs: UseLatencyGoal ? LatencyGoalMaxPingMs : null,
    ct: _refreshCts.Token));
```

### `VPNRouter.App/Views/Pages/FreeConfigsPage.axaml`

New block above Refresh button:
```xml
<Border Padding="8" CornerRadius="4" Background="#F0F9FF"
        BorderBrush="#0284C7" BorderThickness="1">
    <StackPanel Spacing="6">
        <CheckBox Content="🎯 Smart refresh (stop at target)"
                  IsChecked="{Binding FreeConfigsVm.UseLatencyGoal}"/>
        <StackPanel Orientation="Horizontal" Spacing="8"
                    IsVisible="{Binding FreeConfigsVm.UseLatencyGoal}">
            <TextBlock Text="Target:" VerticalAlignment="Center"/>
            <NumericUpDown Value="{Binding FreeConfigsVm.LatencyGoalTarget}"
                           Minimum="5" Maximum="500" Width="80"/>
            <TextBlock Text="Ping <" VerticalAlignment="Center"/>
            <NumericUpDown Value="{Binding FreeConfigsVm.LatencyGoalMaxPingMs}"
                           Minimum="20" Maximum="1000" Width="90"/>
            <TextBlock Text="ms" VerticalAlignment="Center"/>
        </StackPanel>
    </StackPanel>
</Border>
```

## Localization (Strings.cs)
```csharp
public static string FcSmartRefresh      => Ru ? "🎯 Умный refresh (stop at target)" : "🎯 Smart refresh (stop at target)";
public static string FcTargetLabel       => Ru ? "Цель:" : "Target:";
public static string FcPingLessThan      => Ru ? "Пинг <" : "Ping <";
public static string FcMs                => "ms";
public static string FcStatusGoalReached(int found, int tested) => Ru
    ? $"Цель достигнута: {found} конфигов (проверено {tested})"
    : $"Goal reached: {found} configs (tested {tested})";
```

## Testing
- Target=50 ping<200ms → ожидается ~3-5 мин
- Target=1000 ping<500ms → скорее всего не хватит → должен остановиться на exhaust
- Cancel во время goal → должен сохранить накопленное (как в v2.13.15)

## Release
- Bump `2.13.17`, build, commit, push, release prerelease

---

# v2.13.19 — Security warning on first Connect from Free Configs

**Goal**: Warn user about privacy implications of running traffic through unknown public operators. Users often assume "VPN = secure for everything" — dangerous for public proxies where operator can log metadata.

## Threat model recap

What operator **CAN** see:
- Destination hosts (SNI + DNS queries)
- Request timing / size patterns (fingerprinting)
- Connection metadata (frequency, duration)
- HTTP traffic IF the user's own OS trusts a MitM cert (rare but possible)

What operator **CANNOT** see (modern HTTPS + Reality):
- HTTPS request/response bodies (end-to-end TLS)
- Cookies/auth tokens in transit (TLS-encrypted)
- Passwords in HTTPS POST (TLS-encrypted)

What is **NOT** a risk:
- RCE via the config itself (sing-box parses strict fields, no code execution surface)
- Malware via the URI (VLESS/Reality is just a proxy protocol)

## Files to change

### `VPNRouter.Core/Models/AppSettings.cs`

Add one-time dismissal flag:
```csharp
[YamlMember(Alias = "free_config_security_warning_acked")]
public bool FreeConfigSecurityWarningAcked { get; set; } = false;
```

### `VPNRouter.App/ViewModels/MainWindowViewModel.cs`

Modify `ApplyFreeConfigAsync` to show warning before first-ever apply:
```csharp
private async Task<bool> ApplyFreeConfigAsync(FreeConfigEntry entry)
{
    try
    {
        // One-time security warning — show before first Connect from Free Configs tab.
        if (!_settings.App.FreeConfigSecurityWarningAcked)
        {
            var ok = await ShowSecurityWarningAsync();
            if (!ok) return false;  // user chose to cancel
            _settings.App.FreeConfigSecurityWarningAcked = true;
            SaveSettings();
        }

        // ... existing flow (add to Servers, SaveSettings, StartAsync) ...
    }
    ...
}

private async Task<bool> ShowSecurityWarningAsync()
{
    var mainWindow = GetMainWindow();
    if (mainWindow == null) return true; // fallback: can't show dialog, proceed

    var dialog = new Window
    {
        Title = Strings.FcSecWarnTitle,
        Width = 500, Height = 420,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        CanResize = false,
        SystemDecorations = SystemDecorations.BorderOnly,
    };

    var tcs = new TaskCompletionSource<bool>();

    dialog.Content = new StackPanel
    {
        Margin = new Thickness(20),
        Spacing = 12,
        Children =
        {
            new TextBlock
            {
                Text = "⚠ " + Strings.FcSecWarnHeader,
                FontSize = 16, FontWeight = FontWeight.Bold,
                Foreground = Brush.Parse("#B45309"),
            },
            new TextBlock
            {
                Text = Strings.FcSecWarnBody,
                TextWrapping = TextWrapping.Wrap, FontSize = 11,
            },
            new Border
            {
                Padding = new Thickness(10),
                Background = Brush.Parse("#FEF3C7"),
                BorderBrush = Brush.Parse("#F59E0B"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Child = new TextBlock
                {
                    Text = Strings.FcSecWarnDontUseList,
                    TextWrapping = TextWrapping.Wrap, FontSize = 11,
                },
            },
            new TextBlock
            {
                Text = Strings.FcSecWarnGoodFor,
                TextWrapping = TextWrapping.Wrap, FontSize = 11,
                Foreground = Brush.Parse("#166534"),
            },
            // Buttons
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Children =
                {
                    // ... cancel + proceed buttons with tcs.SetResult
                },
            },
        },
    };

    await dialog.ShowDialog(mainWindow);
    return await tcs.Task;
}
```

Alternative simpler implementation using Avalonia MessageBox — check if we already have a helper. Current codebase likely has `DialogHelper.ShowConfirmAsync` or similar; reuse if exists.

### `VPNRouter.App/Localization/Strings.cs`

```csharp
public static string FcSecWarnTitle => Ru
    ? "Публичный прокси — предупреждение"
    : "Public proxy — privacy warning";

public static string FcSecWarnHeader => Ru
    ? "Вы подключаетесь к публичному прокси-серверу"
    : "You're connecting to a public proxy operator";

public static string FcSecWarnBody => Ru
    ? "Оператор этого конфига может видеть метаданные вашего трафика — к каким сайтам вы обращаетесь, когда, как часто. Содержимое HTTPS-сайтов (логины, пароли, сообщения) защищено TLS и недоступно оператору."
    : "The operator of this config can see your traffic metadata — which sites you visit, when, how often. HTTPS content (logins, passwords, messages) is protected by TLS and invisible to the operator.";

public static string FcSecWarnDontUseList => Ru
    ? "🚫 НЕ используйте для:\n" +
      "  • банковских приложений / онлайн-банков\n" +
      "  • входа в почту (Gmail, Я.Почта, Mail.ru)\n" +
      "  • Госуслуги, банки, налоговая\n" +
      "  • 2FA / SMS-коды / криптокошельки\n" +
      "  • любых паролей которые вы цените"
    : "🚫 DO NOT use for:\n" +
      "  • banking apps / online banking\n" +
      "  • email logins (Gmail, Outlook, etc.)\n" +
      "  • government services, tax sites\n" +
      "  • 2FA / SMS codes / crypto wallets\n" +
      "  • any passwords you care about";

public static string FcSecWarnGoodFor => Ru
    ? "✅ Подходит для: YouTube, новостей, Wikipedia, Discord, Telegram, публичного веба"
    : "✅ Good for: YouTube, news, Wikipedia, Discord, Telegram, public web browsing";

public static string FcSecWarnCancel => Ru ? "Отмена" : "Cancel";
public static string FcSecWarnProceed => Ru ? "Понял, подключить" : "Understood, connect";
```

## UX details

- Shown **only once** per user (persisted in `AppSettings`)
- If user wants to re-enable: add toggle in Settings/Network page: "Show security warning before each Free Configs connect"
- Style: yellow/amber accent, not red (not an error — just informed consent)
- Dialog must **block** the Connect flow until user clicks Proceed or Cancel
- Cancel should NOT apply the config; user stays on Free Configs page

## Optional — Settings toggle to re-enable

In `NetworkPage.axaml` (Security/Leak section), add:
```xml
<CheckBox Content="Show security warning on Free Configs Connect"
          IsChecked="{Binding ShowFreeConfigWarning}"/>
```

Mapped to inverse of `FreeConfigSecurityWarningAcked`: when re-checked, clear the acked flag.

## Testing
- Fresh install → first Connect from Free Configs → dialog appears
- Click Proceed → dialog closes, connect proceeds normally
- Click Cancel → dialog closes, VPN does NOT start
- Second Connect (same session) → NO dialog
- After restart → still no dialog (persisted)
- Re-enable via settings → next Connect shows dialog again

## Release
- Bump `2.13.19`, build, release prerelease
- Release notes emphasize: this is about privacy transparency, not a "new feature"

---

# v2.14.1 — GitHub Actions pool.json aggregator

**Goal**: heavy I/O (fetch 14 sources + GeoIP) done once on CI, distributed as single pool.json. Client saves ~10 minutes per refresh.

## Files to create

### `.github/workflows/build-free-pool.yml`

```yaml
name: Build Free Configs Pool

on:
  schedule:
    - cron: "17 */6 * * *"   # every 6 hours, offset from :00 to avoid CI load spikes
  workflow_dispatch:

permissions:
  contents: write

jobs:
  aggregate:
    runs-on: ubuntu-latest
    timeout-minutes: 20
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "8.0.x"

      - name: Build and run PoolAggregator
        run: |
          dotnet run --project VPNRouter.Tools/PoolAggregator \
            --configuration Release \
            -- --output /tmp/pool.json

      - name: Upload pool.json to 'free-pool-latest' release
        env:
          GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}
        run: |
          # Create/update the rolling 'free-pool-latest' release
          if ! gh release view free-pool-latest >/dev/null 2>&1; then
            gh release create free-pool-latest \
              --title "Free Configs Pool (rolling)" \
              --notes "Auto-updated every 6h. Aggregated metadata only." \
              --prerelease
          fi
          gh release upload free-pool-latest /tmp/pool.json --clobber

      - name: Alert if pool is empty/small
        run: |
          COUNT=$(jq '.servers | length' /tmp/pool.json)
          if [ "$COUNT" -lt 1000 ]; then
            echo "::warning::Pool has only $COUNT entries (expected > 10000) — sources may be broken"
          fi
```

### `VPNRouter.Tools/PoolAggregator/PoolAggregator.csproj`

New .NET 8 console app:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\VPNRouter.Core\VPNRouter.Core.csproj" />
  </ItemGroup>
</Project>
```

### `VPNRouter.Tools/PoolAggregator/Program.cs`

```csharp
using System.Text.Json;
using Serilog;
using VPNRouter.Core.Services.FreeConfigs;

// Parse args
var output = "/tmp/pool.json";
for (var i = 0; i < args.Length - 1; i++)
    if (args[i] == "--output") output = args[i + 1];

var logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();
var fetcher = new FreeConfigFetcher(logger);
var geoIp = new FreeConfigGeoIp(logger);

// 1. Fetch all sources
var sources = FreeConfigSources.Default;
var allEntries = new Dictionary<string, FreeConfigEntry>(StringComparer.OrdinalIgnoreCase);

foreach (var src in sources.Where(s => s.Enabled))
{
    var raws = await fetcher.FetchAsync(src);
    foreach (var raw in raws)
    {
        try
        {
            var vless = VlessUriParser.Parse(raw);
            var id = BuildId(vless.Server, vless.Port, vless.Uuid);
            if (allEntries.ContainsKey(id)) continue;

            allEntries[id] = new FreeConfigEntry
            {
                Id = id,
                SourceUrl = src.Url,
                RawUri = raw,
                Host = vless.Server,
                Port = vless.Port,
                Uuid = vless.Uuid,
                Name = vless.Name ?? "",
                Sni = vless.Reality?.ServerName ?? vless.Tls?.ServerName ?? "",
                Transport = vless.Transport?.Type ?? "tcp",
                Security = vless.Security ?? "reality",
                FirstSeenAt = DateTime.UtcNow,
            };
        }
        catch { /* skip parse errors */ }
    }
}

var configs = allEntries.Values.ToList();

// 2. GeoIP enrich
await geoIp.EnrichAsync(configs);

// 3. Build pool.json
var pool = new
{
    updated_at = DateTime.UtcNow.ToString("O"),
    version = 1,
    source_count = sources.Count,
    total_configs = configs.Count,
    // Only metadata + raw URI — no status, no latency, no verification
    servers = configs.Select(c => new
    {
        id = c.Id,
        host = c.Host,
        port = c.Port,
        country = c.CountryCode,
        resolved_ip = c.ResolvedIp,
        source = c.SourceUrl,
        raw = c.RawUri,
        first_seen = c.FirstSeenAt.ToString("O"),
    }).ToList(),
};

await File.WriteAllTextAsync(output, JsonSerializer.Serialize(pool, new JsonSerializerOptions
{
    WriteIndented = false,
}));

Console.WriteLine($"Wrote {configs.Count} configs to {output}");
```

### `VPNRouter.Core/Services/FreeConfigs/FreeConfigPoolFetcher.cs` (NEW)

```csharp
public sealed class FreeConfigPoolFetcher
{
    private const string PoolUrl = "https://github.com/PavelLizunov/VPNRouter/releases/download/free-pool-latest/pool.json";
    private readonly string _cachePath;
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public FreeConfigPoolFetcher(ILogger logger)
    {
        _logger = logger;
        _cachePath = Path.Combine(AppPaths.CacheDir, "pool.json");
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    /// <summary>
    /// Download pool.json with ETag-conditional GET. Returns null if not modified or failed.
    /// </summary>
    public async Task<List<FreeConfigEntry>?> FetchPoolAsync(CancellationToken ct = default)
    {
        try
        {
            var etagPath = _cachePath + ".etag";
            var etag = File.Exists(etagPath) ? await File.ReadAllTextAsync(etagPath, ct) : null;

            using var req = new HttpRequestMessage(HttpMethod.Get, PoolUrl);
            if (etag != null) req.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));

            using var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotModified)
            {
                _logger.Information("Pool not modified, using local cache");
                return LoadFromLocalCache();
            }
            if (!resp.IsSuccessStatusCode)
            {
                _logger.Warning("Pool fetch failed: HTTP {code}", (int)resp.StatusCode);
                return LoadFromLocalCache();
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            await File.WriteAllTextAsync(_cachePath, body, ct);

            if (resp.Headers.ETag?.Tag != null)
                await File.WriteAllTextAsync(etagPath, resp.Headers.ETag.Tag, ct);

            return ParsePool(body);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Warning("Pool fetch error: {err}", ex.Message);
            return LoadFromLocalCache();
        }
    }

    private List<FreeConfigEntry>? LoadFromLocalCache()
    {
        if (!File.Exists(_cachePath)) return null;
        try { return ParsePool(File.ReadAllText(_cachePath)); }
        catch { return null; }
    }

    private List<FreeConfigEntry> ParsePool(string json)
    {
        var doc = JsonDocument.Parse(json);
        var servers = doc.RootElement.GetProperty("servers");
        var result = new List<FreeConfigEntry>(servers.GetArrayLength());
        foreach (var s in servers.EnumerateArray())
        {
            result.Add(new FreeConfigEntry
            {
                Id = s.GetProperty("id").GetString() ?? "",
                Host = s.GetProperty("host").GetString() ?? "",
                Port = s.GetProperty("port").GetInt32(),
                CountryCode = s.TryGetProperty("country", out var c) ? c.GetString() : null,
                ResolvedIp = s.TryGetProperty("resolved_ip", out var ip) ? ip.GetString() : null,
                SourceUrl = s.TryGetProperty("source", out var src) ? src.GetString() ?? "" : "",
                RawUri = s.GetProperty("raw").GetString() ?? "",
                FirstSeenAt = s.TryGetProperty("first_seen", out var fs) && DateTime.TryParse(fs.GetString(), out var dt) ? dt : DateTime.UtcNow,
                Status = FreeConfigStatus.Unknown,
            });
        }
        return result;
    }
}
```

### Integration into `FreeConfigAggregator.cs`

At start of `RefreshAsync`, try pool first:
```csharp
// Stage 0: try server-side pool (metadata pre-enriched, no GeoIP needed)
var pool = await _poolFetcher.FetchPoolAsync(ct);
if (pool != null && pool.Count > 1000)
{
    OnStageChanged?.Invoke($"Loaded pool.json ({pool.Count} configs, country codes included)");
    // Merge with existing cache + skip GeoIP stage
    var byId = pool.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
    // ... merge logic ...
    // skip `await _geoIp.EnrichAsync(...)` — already enriched
    goto testStage;  // or refactor to avoid goto
}
// Fallback: original fetch + parse + GeoIP flow
```

## Local testing
Before committing, run aggregator locally:
```
dotnet run --project VPNRouter.Tools/PoolAggregator -- --output /tmp/pool.json
cat /tmp/pool.json | jq '.total_configs'  # expect ~20000+
```

## Risk / gotchas
- **Release token**: workflow uses default `GITHUB_TOKEN` with contents:write — no secret setup needed
- **Release creation race**: first run creates `free-pool-latest` release; subsequent runs just upload-clobber
- **pool.json size**: ~20 MB uncompressed. GH Releases supports up to 2 GB per asset. Fine.
- **Breaking ETag on re-upload**: `gh release upload --clobber` replaces asset but GH may issue new ETag. User client's cache becomes invalid once per cron = fine.
- **User on old client without pool support**: Aggregator falls back to fetch-14-sources if PoolFetcher returns null. No breaking change.

## Release
- Bump `2.14.1` (skipping 2.14.0 until bandwidth feature ready)
- Build, commit, push
- **First manual workflow run**: `gh workflow run build-free-pool.yml` to create the initial `free-pool-latest` release
- Then cron takes over

---

# v2.13.18 — Fast scan TCP-only toggle

**Goal**: ~7 min instead of ~15 for full 25k. Optional.

## Files to change

### `VPNRouter.Core/Services/FreeConfigs/FreeConfigTester.cs`

Already has `public bool RequireTlsHandshake { get; set; } = true;` — just need to wire it to UI.

### `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigsPageViewModel.cs`

```csharp
[ObservableProperty] private bool _fastScanMode = false;
```

In `RefreshAsync`, before calling aggregator:
```csharp
_aggregator.TesterRef.RequireTlsHandshake = !FastScanMode;
```

Expose `TesterRef` on aggregator or pass via parameter.

### UI
Add checkbox next to smart-refresh:
```xml
<CheckBox Content="⚡ Fast (TCP only, no TLS check)"
          IsChecked="{Binding FreeConfigsVm.FastScanMode}"/>
<TextBlock Text="(faster but doesn't catch honeypots)"
           FontSize="9" Opacity="0.6"/>
```

## Testing
- With FastScanMode=true, 25k should finish in ~7 min
- Deep Verify still catches junk

## Release
- Bump `2.13.18`, build, release

---

# v2.14.0 — Bandwidth measurement + presets

**Goal**: "find 5 configs with ping < X AND bandwidth > Y Mbps" — best-of-the-best selection.

## Files to change

### `VPNRouter.Core/Services/FreeConfigs/FreeConfigModels.cs`

```csharp
public int? MeasuredBandwidthMbps { get; set; }
public DateTime? BandwidthTestedAt { get; set; }
```

### `VPNRouter.Core/Services/FreeConfigs/FreeConfigDeepVerifier.cs`

Extend `VerifyOneAsync`. After successful HTTP trace, measure bandwidth:

```csharp
// After trace returns ok, measure throughput
if (httpOk && measureBandwidth)
{
    var (bwOk, mbps, _) = await MeasureBandwidthViaSocksAsync(socksPort, overallCts.Token);
    if (bwOk)
    {
        cfg.MeasuredBandwidthMbps = (int)mbps;
        cfg.BandwidthTestedAt = DateTime.UtcNow;
    }
}
```

New method:
```csharp
private static async Task<(bool ok, double mbps, string? err)> MeasureBandwidthViaSocksAsync(int socksPort, CancellationToken ct)
{
    var handler = new SocketsHttpHandler
    {
        Proxy = new WebProxy($"socks5://127.0.0.1:{socksPort}"),
        UseProxy = true,
    };
    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

    // 5 MB download from Cloudflare speed test
    // Fallbacks: hetzner 10mb test, ovh speedtest
    var urls = new[]
    {
        "https://speed.cloudflare.com/__down?bytes=5242880",
        "https://ash-speed.hetzner.com/100MB.bin",  // larger, we'll read 5MB and close
        "https://proof.ovh.net/files/10Mb.dat",
    };

    foreach (var url in urls)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) continue;
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[8192];
            long total = 0;
            while (total < 5_242_880)
            {
                var n = await stream.ReadAsync(buffer, ct);
                if (n == 0) break;
                total += n;
            }
            sw.Stop();
            if (total < 1_000_000) continue; // too little data, probably hit cache or error
            var mbps = (total * 8.0 / 1_000_000.0) / (sw.ElapsedMilliseconds / 1000.0);
            return (true, mbps, null);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* try next URL */ }
    }
    return (false, 0, "all bandwidth test URLs failed");
}
```

### `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigsPageViewModel.cs`

```csharp
public enum DeepVerifyPreset
{
    Gaming,      // ping < 60ms, bw > 2 Mbps
    Streaming,   // ping < 250ms, bw > 10 Mbps
    Chat,        // ping < 300ms, bw > 1 Mbps
    BestEffort,  // no limits
    Custom,      // user-defined
}

[ObservableProperty] private DeepVerifyPreset _selectedPreset = DeepVerifyPreset.BestEffort;
[ObservableProperty] private int _customMaxPingMs = 200;
[ObservableProperty] private int _customMinBandwidthMbps = 5;
[ObservableProperty] private bool _measureBandwidth = false;

public (int? maxPing, int? minBw) ResolvedGoal => SelectedPreset switch
{
    DeepVerifyPreset.Gaming    => (60, 2),
    DeepVerifyPreset.Streaming => (250, 10),
    DeepVerifyPreset.Chat      => (300, 1),
    DeepVerifyPreset.BestEffort => (null, null),
    DeepVerifyPreset.Custom    => (CustomMaxPingMs, CustomMinBandwidthMbps),
    _ => (null, null),
};
```

In `DeepVerifyTopAsync`, pass goal to filter:
```csharp
var (maxPing, minBw) = ResolvedGoal;

// In loop after each verify:
var meetsGoal = cfg.Status == FreeConfigStatus.Verified
    && (maxPing == null || cfg.LatencyMs <= maxPing.Value)
    && (minBw == null || (cfg.MeasuredBandwidthMbps ?? 0) >= minBw.Value);

if (meetsGoal) Interlocked.Increment(ref foundVerified);
```

Also pass `measureBandwidth: true` to the verifier when preset needs bw.

### UI — DeepVerify block

Replace current target-only block with preset selector:

```xml
<StackPanel Spacing="6">
    <TextBlock Text="Deep verify goal:" FontWeight="SemiBold"/>
    <ComboBox SelectedIndex="{Binding FreeConfigsVm.SelectedPresetIndex}">
        <ComboBoxItem Content="⚡ Gaming (ping<60ms, bw>2 Mbps)"/>
        <ComboBoxItem Content="📺 Streaming (ping<250ms, bw>10 Mbps)"/>
        <ComboBoxItem Content="💬 Chat/web (ping<300ms, bw>1 Mbps)"/>
        <ComboBoxItem Content="🚀 Best effort (any verified)"/>
        <ComboBoxItem Content="⚙ Custom"/>
    </ComboBox>
    <StackPanel Orientation="Horizontal" Spacing="8"
                IsVisible="{Binding FreeConfigsVm.IsCustomPreset}">
        <TextBlock Text="Ping <"/>
        <NumericUpDown Value="{Binding FreeConfigsVm.CustomMaxPingMs}" Width="80"/>
        <TextBlock Text="ms, BW >"/>
        <NumericUpDown Value="{Binding FreeConfigsVm.CustomMinBandwidthMbps}" Width="80"/>
        <TextBlock Text="Mbps"/>
    </StackPanel>
    <StackPanel Orientation="Horizontal" Spacing="8">
        <TextBlock Text="Target:" VerticalAlignment="Center"/>
        <NumericUpDown Value="{Binding FreeConfigsVm.DeepVerifyTargetCount}" Width="80"/>
        <CheckBox Content="Skip RU" IsChecked="{Binding FreeConfigsVm.ExcludeRu}"/>
    </StackPanel>
    <Button Content="✓✓ Deep verify"
            Command="{Binding FreeConfigsVm.DeepVerifyTopCommand}"
            Background="#059669" Foreground="White"/>
</StackPanel>
```

## UX
Important: show user traffic estimate:
- Gaming/Chat preset → low bw usage (~5 MB × 30 = 150 MB)
- Add "Test will download ~{N} MB — ok on wifi, beware on mobile" warning

## Testing
- Gaming preset → should find low-ping configs that also have decent bw
- Streaming → fewer candidates but high-quality
- BestEffort preset = existing behavior unchanged

## Release
- Bump `2.14.0`, build, release

---

# v2.14.2 — User-provided sources (opt-in)

**Goal**: user can add private subscription URLs, validated locally, optionally submitted to public pool via PR/issue.

## Files to change

### `VPNRouter.Core/Models/AppSettings.cs`

Add to root:
```csharp
[YamlMember(Alias = "user_free_sources")]
public List<UserFreeSource> UserFreeSources { get; set; } = new();

public class UserFreeSource
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}
```

### `VPNRouter.Core/Services/FreeConfigs/FreeConfigSources.cs`

Add static method:
```csharp
public static List<FreeConfigSource> GetAll(AppSettings settings)
{
    var result = new List<FreeConfigSource>(Default);
    foreach (var u in settings.UserFreeSources.Where(s => s.Enabled))
    {
        result.Add(new FreeConfigSource
        {
            Name = $"👤 {u.Name}",
            Url = u.Url,
            ExpectedCount = 0,
        });
    }
    return result;
}
```

### `VPNRouter.Core/Services/FreeConfigs/FreeConfigAggregator.cs`

Accept sources parameter already exists. VM pass merged list:
```csharp
var sources = FreeConfigSources.GetAll(_settings);
await _aggregator.RefreshAsync(sources, ...);
```

### `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigsPageViewModel.cs`

```csharp
[ObservableProperty] private ObservableCollection<UserFreeSourceViewModel> _userSources = new();

[RelayCommand]
private async Task AddUserSourceAsync()
{
    // Modal dialog: Name + URL → validate URL returns >0 vless → add
}

[RelayCommand]
private void RemoveUserSource(UserFreeSourceViewModel vm) { ... }

[RelayCommand]
private async Task SubmitToPublicPoolAsync(UserFreeSourceViewModel vm)
{
    // Opens GitHub issue template pre-filled
    var url = $"https://github.com/PavelLizunov/VPNRouter/issues/new?template=add-source.md&title=Add%20source%3A%20{Uri.EscapeDataString(vm.Name)}&body=URL%3A%20{Uri.EscapeDataString(vm.Url)}";
    OpenUrl(url);
}
```

### UI — new section on Free Configs page

New expander at bottom:
```xml
<Expander Header="👤 Мои источники">
    <StackPanel>
        <ItemsControl ItemsSource="{Binding FreeConfigsVm.UserSources}">
            <ItemsControl.ItemTemplate>
                <DataTemplate>
                    <Grid ColumnDefinitions="*,Auto,Auto,Auto">
                        <TextBlock Grid.Column="0" Text="{Binding DisplayName}"/>
                        <CheckBox Grid.Column="1" IsChecked="{Binding Enabled}"/>
                        <Button Grid.Column="2" Content="Submit" Command="{Binding SubmitCommand}"/>
                        <Button Grid.Column="3" Content="✖" Command="{Binding RemoveCommand}"/>
                    </Grid>
                </DataTemplate>
            </ItemsControl.ItemTemplate>
        </ItemsControl>
        <Button Content="+ Add source" Command="{Binding AddUserSourceCommand}"/>
    </StackPanel>
</Expander>
```

### GitHub issue template `.github/ISSUE_TEMPLATE/add-source.md`

```markdown
---
name: Add source to Free Configs
about: Suggest a new public VLESS source for community pool
title: "Add source: "
labels: source-suggestion
---

**Source name**: 
**Source URL**: 
**Approximate VLESS count**: 
**Update frequency**: 

Please confirm:
- [ ] Source is public (no auth)
- [ ] Source contains `vless://` URIs (raw or base64)
- [ ] You're not the operator (or if you are, you understand the list becomes public)
```

## Testing
- Add URL with raw vless:// — should merge into pool
- Add URL with 0 configs — should warn
- Private source with `@` in name → icon 👤 persists
- Disable source → excluded from Refresh

## Release
- Bump `2.14.2`, build, release

---

# v2.14.3 — UI/UX design polish + cross-page consistency + bug audit

**Goal**: after 7 feature releases the Free Configs page accumulated a lot of UI widgets (dashboard, toolbar, filters, deep-verify block, cleanup row, preset selector, user sources, security warning). Need a QA + design pass to make it look clean, fit the rest of the app, and catch regressions.

**Context**: all other master-detail pages (`NetworkPage`, `ApplicationsPage`, `DpiBypassPage`, `ToolsPage`) follow a consistent pattern: `Grid ColumnDefinitions="140,*"` or `160,*`, left ListBox of sections, right ScrollViewer detail, bottom Apply bar. FreeConfigsPage is currently flat (5 rows, no sections). Should we sectionize?

## Phase A — Design review (1 day)

### A.1 Audit current FreeConfigsPage layout

After all features ship, count concrete UI widgets:
- 6-card dashboard
- Toolbar row 1 (Refresh/Retest buttons)
- Toolbar row 2 (Smart refresh goal settings)
- Toolbar row 3 (Fast scan toggle)
- Filter row (country, only working)
- Cleanup row (Clear failed, Keep verified, Clear all)
- Logs button
- Deep Verify block (preset selector + target + skip RU + button)
- User sources expander
- Main list (column headers + DataGrid)
- Bottom status + progress bar + Connect button
- **Total**: ~11 distinct UI sections competing for attention

This is too busy. Need restructure.

### A.2 Proposed new layout — master-detail sections

Match the pattern of `NetworkPage` / `DpiBypassPage`:

```
┌────────────────┬──────────────────────────────────────┐
│ Sections (140) │ Detail (scroll)                      │
├────────────────┤                                      │
│ 🌐 Overview    │  Dashboard (6 cards)                 │
│ 🔍 Scan        │  + main list of configs              │
│ ⚙ Filters      │                                      │
│ ✓✓ Deep verify │                                      │
│ 👤 My sources  │                                      │
│ 🧹 Cleanup     │                                      │
│ 📁 Logs        │                                      │
│                │                                      │
├────────────────┴──────────────────────────────────────┤
│ Bottom bar: status + Connect button                    │
└────────────────────────────────────────────────────────┘
```

**Section mapping**:
- **Overview** — dashboard cards + config list (always shown, main content)
- **Scan** — Refresh/Retest/Fast scan buttons + smart refresh goal
- **Filters** — country dropdown, Only working, cleanup buttons
- **Deep verify** — preset selector + target + bandwidth + Skip RU
- **My sources** — user opt-in sources list + add/submit buttons
- **Cleanup** — Clear failed, Keep verified, Clear all
- **Logs** — open logs button + inline last 5 log lines preview

### A.3 Color palette consistency

Audit current colors across all pages:
- Accent purple `#7C3AED` — used for Zapret, DPI bypass, Free Configs tab label
- Accent emerald `#059669` — used for Deep Verify button
- Accent blue `#2563EB` — used for main Connect button, Apply in Network
- Accent amber `#F59E0B` — used for warnings, Apply pending
- Accent red `#EF4444` — used for Stop/Cancel

Standardize: **Free Configs page should use same color codes as its siblings**. Currently mixed (different shades of green between dashboard cards and Deep Verify button). Unify to single green `#059669` + variants.

### A.4 Typography / spacing audit

Compare FontSize/Padding across pages:
- Tab labels: 12pt SemiBold (existing pattern)
- Section headers: 14pt Bold
- Body text: 11pt Regular, 10pt for secondary
- Button padding: consistent `8,3` for small, `0,8` for primary stretch

Check FreeConfigsPage uses same values. Likely has some 9pt/10pt inconsistencies from incremental changes.

## Phase B — Bug audit (1-2 days)

Run through these scenarios on Windows + macOS (via CI/TestFlight):

### B.1 State machine edge cases
- Press Refresh, press again mid-run → should be noop (IsBusy guard) ✅ tested
- Press Cancel during fetch → no partial cache, UI restored ✅ 
- Press Cancel during GeoIP → partial cache preserved (pool.json fallback kicks in)
- Press Cancel during test → partial progress preserved (v2.13.15) ✅
- Press Cancel during Deep Verify → partial Verified preserved
- Close app during any stage → cache on disk, next launch loads
- Refresh while VPN active → warning shown, proceeds anyway
- Connect to a Free config while VPN already active → stops existing, starts new

### B.2 Data consistency
- SelectedItem survives list refresh (don't lose selection)
- Verified badge shows green, status counters match actual entry statuses
- OnlyWorking filter + Skip RU: both apply correctly together
- CountryFilter dropdown updates when new entries arrive
- Deep Verify demotes Ok → TlsFailed correctly (user can see change in dashboard)

### B.3 Localization
Every new string from v2.13.16–v2.14.2 must be in `Strings.cs` with RU/EN pairs:
- Grep for hardcoded Russian/English in .axaml and .cs
- Check both languages render correctly (UI layout doesn't break on long RU words)
- Review currency-style formatters (5 MB vs 5 МБ)

### B.4 Error paths
- No network → fetch fails → friendly message, no crash
- Corrupted cache (manual file edit) → fallback to empty, not crash
- Sing-box binary missing → Deep Verify shows clear error, not silent
- ip-api.com rate-limited (429) → GeoIP skipped, entries have no country, UI shows "—"
- User's system time wrong → LastTestedAt compares fail → "recently tested" logic may skip-all or test-all

### B.5 Cross-platform parity (macOS)
- DMG layout correct (VPNRouter.app + Applications + InstallGuide.html + Terminal alias)
- All Free Configs UI renders on macOS (Avalonia theme differences)
- Sing-box path differs (`/Applications/VPNRouter.app/Contents/MacOS/sing-box` or similar)
- Deep Verify spawning works on macOS (test manually)
- Permissions: first Connect triggers sudoers prompt

### B.6 Memory / performance
- 25k configs × DataContext = ~20 MB RAM — acceptable
- DisplayedConfigs capped at 300 — verify (not 25k)
- Check no memory leak: run 10 Refresh cycles, monitor RAM
- ListBox scroll perf with 300 items × emoji flag rendering

## Phase C — Consistency with other pages (0.5 day)

### C.1 Compare FreeConfigsPage to DpiBypassPage

Both are "feature pages". DpiBypass is well-designed (master-detail, 7 sections). Free Configs should feel like sibling:
- Same master-detail Grid structure
- Same section list style (10pt SemiBold items)
- Same bottom Apply/Connect bar style
- Same border colors, backgrounds, spacings

Pick up any patterns from DpiBypass that aren't in FreeConfigs.

### C.2 Settings page consistency (NetworkPage)

Verify the v2.13.11 Apply button at bottom of NetworkPage matches the pattern for Free Configs Connect button:
- Same green/blue for primary action
- Same Apply hint tooltip format
- Same disabled state when no-op

### C.3 Tab label style

Free Configs tab in MainWindow uses `Foreground="#7C3AED"` (purple). Other tabs use default. Decide:
- Keep purple to signal "experimental/new" — pro: visibility
- Remove to match siblings — pro: consistency
- After v2.14.x stable, probably remove purple marker

## Phase D — Accessibility / usability polish (0.5 day)

- Tab order: verify keyboard Tab navigates in logical order (dashboard → filters → Refresh → list → Connect)
- Tooltips: every button has tooltip explaining what it does
- Keyboard shortcuts: F5 = Refresh, Ctrl+Enter = Connect selected, Delete = remove user source
- Screen reader support (Avalonia AutomationProperties.Name)
- Color-blind: don't rely solely on color for status (✓✓ / ✓ / ⚠ / 🚫 emoji as secondary signal — already partially done)

## Phase E — Release

- Bump `2.14.3`, build, commit, push, release
- Release notes: "Polish pass — redesigned Free Configs page, fixed N bugs, cross-platform consistency"
- This should be the candidate for **first stable release of Free Configs feature** — all preceding 2.13.x/2.14.x were prerelease
- If user approves → `gh release edit v2.14.3 --prerelease=false --latest`

## Acceptance criteria for v2.14.3

- [ ] All 11 UI widgets reorganized into ≤ 7 master-detail sections
- [ ] Color palette consistent with DpiBypassPage / NetworkPage
- [ ] All text strings localized (no hardcoded ru/en in code)
- [ ] All state machine edge cases pass (B.1 checklist)
- [ ] All error paths produce friendly messages (B.4 checklist)
- [ ] macOS DMG install and Connect tested end-to-end
- [ ] Memory usage stable over 10 Refresh cycles
- [ ] No regressions in Windows auto-updater flow
- [ ] Ready for stable-release promotion

---

# Operational notes (applies to all releases)

## Pre-release checklist
1. ✅ Bump `VPNRouter.Core/AppVersion.cs`
2. ✅ `dotnet build VPNRouter.sln` — must be 0 errors
3. ✅ Stop running VPNRouter (DLL lock)
4. ✅ `build.ps1 -Version "X.Y.Z" -Upload`
5. ✅ `git add <specific files>` (no `git add -A` — avoids release-tmp/)
6. ✅ Commit with heredoc message + Co-Authored-By
7. ✅ Push to both remotes: `origin` (Forgejo) + `github`
8. ✅ `gh release edit vX.Y.Z --prerelease --notes "..."` (ALWAYS prerelease until user says stable)
9. ✅ macOS DMG builds auto via `.github/workflows/build-mac.yml` on tag push

## Git remotes (from CLAUDE.local.md)
- `origin` → `ssh://git@10.9.1.1:18222/slovn/vpnrouter.git` (Forgejo via AmneziaWG)
- `github` → `https://github.com/PavelLizunov/VPNRouter.git`

## Build command (Windows)
```powershell
powershell -ExecutionPolicy Bypass -File build.ps1 -Version "X.Y.Z" -Upload
```

## Build command (macOS DMG via CI)
Automatic on `v*` tag push. Manual trigger:
```bash
gh workflow run build-mac.yml --ref main -f version=X.Y.Z -f upload_to_release=true
```

## Release policy
- **ALWAYS** `--prerelease` by default
- Stable only when user explicitly says "стабильно" or "release"
- Then: `gh release edit vX.Y.Z --prerelease=false --latest`

## Testing per release (required before shipping)
- `dotnet build` — 0 errors
- Run VPNRouter.App.exe locally — basic UI smoke test if UI changed
- For Free Configs changes: trigger Refresh on real cache, verify no regressions
- For Deep Verify changes: verify at least 1 config gets Verified in logs
- Check `%ProgramData%\VPNRouter\logs\vpnrouter*.log` for any unexpected errors

## Rollback strategy
If a release breaks for users:
- Mark as prerelease + not-latest: `gh release edit vX.Y.Z --prerelease=true`
- Promote last good: `gh release edit vX.Y.{Z-1} --prerelease=false --latest`
- Users auto-updater will re-check on next launch

---

# Context anchors for future sessions

**Key files** (most-touched during this feature):
- `VPNRouter.Core/Services/FreeConfigs/FreeConfigAggregator.cs` — orchestrator
- `VPNRouter.Core/Services/FreeConfigs/FreeConfigTester.cs` — TCP+TLS test
- `VPNRouter.Core/Services/FreeConfigs/FreeConfigDeepVerifier.cs` — sing-box HTTP test
- `VPNRouter.Core/Services/FreeConfigs/FreeConfigGeoIp.cs` — country resolution
- `VPNRouter.Core/Services/FreeConfigs/FreeConfigFetcher.cs` — source download
- `VPNRouter.Core/Services/FreeConfigs/FreeConfigSources.cs` — list of 14 sources
- `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigsPageViewModel.cs` — UI state
- `VPNRouter.App/Views/Pages/FreeConfigsPage.axaml` — UI markup

**Known quirks** (from memory):
- sing-box 1.13.3: DNS server with `detour:"direct"` on empty direct outbound is FATAL. Use dedicated `dns-direct-out` with `udp_fragment:true`.
- process_name matching in sing-box is case-sensitive on Windows — preserve original casing
- Windows ephemeral ports: 49152-65535 = ~16k, TIME_WAIT 2 min → cap concurrency ~80
- ip-api.com free tier: 45 req/min unauthenticated, batch endpoint 100 IPs/query
- Incremental cache saves every 50 tests / 5s — Cancel preserves progress

**Memory file**: `C:\Users\x3d_mutant\.claude\projects\C--Users-x3d-mutant-Project\memory\MEMORY.md` — has broader project context.
