# VPNRouter — Roadmap v2.17 "Simple mode"

**Baseline**: v2.16.9 prerelease (Arctic theme stable + service-managed
reconnect fix).

**Goal**: first-run experience for non-technical users — one screen,
paste config OR subscription URL, press Start, done. Advanced mode stays
exactly as we've built it for power users; nothing we've shipped in
v2.15 / v2.16 regresses.

**User-approved design decisions (2026-04-20)**:
1. Input field accepts BOTH `vless://...` AND subscription URLs
   (`https://...` / `http://...`) — no separate Manual/Subscribe choice.
   Auto-detect by prefix.
2. Default Split profile: **Discord + Browsers + Work apps**. Hardcoded
   in Simple; Advanced lets user pick like today.
3. First-run: **auto-Simple mode, Advanced button in header**. No modal
   dialog. New installs default to `UiMode = "simple"`.
4. Autostart: **single checkbox** "Start with Windows" — installs the
   Windows Service + enables `AutostartVpn`. Unchecking removes the
   service. Encapsulates both "app starts at login" and "VPN starts at
   boot".
5. Free Configs tab → **hidden in Simple**. User who wants it switches
   to Advanced.

**Kept from current (v2.16.9)**:
- Everything Advanced users have today. Tabs, profiles, Free Configs
  aggregator, Deep Verify, Zapret, TgProxy, custom JSON, subscription
  management, status dashboard — all preserved.
- Arctic design tokens — Simple mode renders on the same palette.
- Language toggle, theme toggle — kept in both modes.
- Status bar / Apply behaviour / service-managed warnings from v2.16.8/.9.

**NOT in scope (deferred)**:
- Simple-mode onboarding tour / tooltips overlay (v2.18 candidate).
- Free Configs "one-button mode" for Simple (user explicitly said no).
- Mobile app / Android parity (separate track).

---

## Priority order

### Block 1 — Infrastructure
1. **v2.17.0** — `AppSettings.UiMode` (`simple` | `advanced`), MainWindow
   picks page tree based on the setting, Advanced/Simple toggle button
   in header. Zero content for Simple yet (empty page). Advanced
   unchanged.

### Block 2 — Simple page content
2. **v2.17.1** — `Views/SimplePage.axaml` skeleton: logo, brand title,
   single-column layout with input, radio group, autostart checkbox,
   Start/Stop button, status line. No wiring yet (buttons are dead).
3. **v2.17.2** — Input parser (`SimpleInputDetector`): classifies
   `vless://...` vs `https://...`, writes into correct place in
   AppSettings. Start button triggers same Connect path as Advanced.
4. **v2.17.3** — Default Split profile wiring + autostart checkbox
   wired into `ServiceInstaller.Install/Uninstall` + `AutostartVpn`.

### Block 3 — Connected state + polish
5. **v2.17.4** — Connected UI: status badge, active server name, Stop
   button, mini "Change server ▸" inline dropdown for subscription
   mode (shows the aggregated server list as a simple ComboBox, no
   testing UI).
6. **v2.17.5** — Polish: empty-input validation, inline error message
   for bad VLESS URI / unreachable subscription, first-run default set
   to Simple, default UiMode read from settings on launch.

---

# v2.17.0 — Infrastructure: UiMode + toggle

**Goal**: a single flag drives whether the user sees today's multi-tab
layout or the new Simple page. Nothing functional changes yet.

## Files

### `VPNRouter.Core/Models/AppSettings.cs`
Add to `AppConfig`:
```csharp
/// <summary>
/// UI complexity. "simple" = one-page onboarding (v2.17+); "advanced"
/// = the full tabbed layout we shipped in v2.15/v2.16. Default
/// "simple" for new installs. Users toggle via the Advanced/Simple
/// button in the header.
/// </summary>
public string UiMode { get; set; } = "simple";
```

YamlDotNet picks it up automatically via existing mapping.

### `VPNRouter.App/ViewModels/MainWindowViewModel.cs`
- New `[ObservableProperty] bool _isSimpleMode` loaded from settings.
- `[RelayCommand] ToggleUiMode()` flips + persists + rebuilds window
  (reuse the `ReloadMainWindowForLocalization` pattern from v2.15.6 so
  the page tree re-parses cleanly).
- New `UiModeToggleText` getter: `IsSimpleMode ? "Advanced mode ▸" : "◂ Simple mode"`.

### `VPNRouter.App/Views/MainWindow.axaml`
- Header gets one extra button: `UiModeToggleCommand`, `Text` bound to
  `UiModeToggleText`. Placed near the theme/lang toggles.
- Main content area becomes:
  ```xml
  <ContentControl Content="{Binding}">
    <ContentControl.ContentTemplate>
      <DataTemplate x:DataType="vm:MainWindowViewModel">
        <Panel>
          <!-- Advanced: existing tab grid -->
          <Grid IsVisible="{Binding !IsSimpleMode}">
            ...current tab grid + pages...
          </Grid>
          <!-- Simple: new single page -->
          <pages:SimplePage IsVisible="{Binding IsSimpleMode}"/>
        </Panel>
      </DataTemplate>
    </ContentControl.ContentTemplate>
  </ContentControl>
  ```

## Testing
- Launch → since existing installs have no UiMode field, YAML loader
  returns default `"simple"` → SimplePage shows (empty placeholder).
  Existing users who've been on Advanced suddenly see Simple? **No, risky.**
  Mitigation: if `Subscriptions.Count > 0` OR `Vless.Servers.Count > 0`
  OR `Profile != null`, infer this is an upgrading user → force
  `UiMode = "advanced"` on first settings load. Fresh installs with
  empty settings get Simple.
- Click toggle → full Advanced tab grid visible; toggle again → Simple.
- Settings file has `ui_mode: simple` / `ui_mode: advanced`.

## Acceptance
- [ ] `UiMode` persisted in config.yaml
- [ ] Upgrade path preserves Advanced for existing users
- [ ] Toggle button works both directions
- [ ] SimplePage shown as placeholder (just "Simple mode — coming soon")
- [ ] Advanced tab grid unchanged

## Risk
- Window rebuild on every toggle — already a solved pattern (v2.15.6),
  VM state survives because DataContext is the same instance.

---

# v2.17.1 — SimplePage.axaml skeleton

**Goal**: the page exists with real layout, but buttons are dead
(`Click="..."` → placeholder toast). Visual design approval before
wiring.

## Files

### New: `VPNRouter.App/Views/Pages/SimplePage.axaml`
```xml
<UserControl ...>
  <Grid Margin="40,32" MaxWidth="440" HorizontalAlignment="Center">
    <StackPanel Spacing="18">

      <!-- Logo + brand -->
      <StackPanel Orientation="Horizontal" Spacing="12" HorizontalAlignment="Center">
        <Image Source="{Binding LogoSource}" Width="56" Height="56"/>
        <StackPanel VerticalAlignment="Center">
          <TextBlock Text="Virtual Penguin Network" FontSize="18" FontWeight="Bold"
                     Foreground="{DynamicResource AccentFgBrush}"/>
          <TextBlock Text="{Binding VersionText}" FontSize="10" Opacity="0.5"/>
        </StackPanel>
      </StackPanel>

      <!-- Input -->
      <StackPanel Spacing="6">
        <TextBlock Text="{x:Static loc:Strings.SmpInputLabel}"
                   FontSize="11" FontWeight="SemiBold"/>
        <TextBox Text="{Binding SmpInput}"
                 Watermark="vless://... or https://subscription..."
                 FontSize="11" Padding="10,8"
                 AcceptsReturn="False"/>
        <TextBlock Text="{Binding SmpInputHint}" FontSize="9"
                   Opacity="0.5" TextWrapping="Wrap"/>
      </StackPanel>

      <!-- Mode radios -->
      <StackPanel Spacing="4">
        <TextBlock Text="{x:Static loc:Strings.SmpTunnelModeLabel}"
                   FontSize="11" FontWeight="SemiBold"/>
        <RadioButton GroupName="SmpTunnel" IsChecked="{Binding IsSplitTunnel}"
                     Content="{x:Static loc:Strings.SmpSplitTunnelOption}"/>
        <RadioButton GroupName="SmpTunnel" IsChecked="{Binding !IsSplitTunnel}"
                     Content="{x:Static loc:Strings.SmpFullTunnelOption}"/>
      </StackPanel>

      <!-- Autostart -->
      <CheckBox IsChecked="{Binding SmpAutostart}"
                Content="{x:Static loc:Strings.SmpAutostartLabel}"
                FontSize="11"/>

      <!-- Start/Stop button -->
      <Button Command="{Binding SmpToggleConnectCommand}"
              Content="{Binding SmpConnectButtonText}"
              HorizontalAlignment="Stretch"
              HorizontalContentAlignment="Center"
              Padding="0,14" FontSize="14" FontWeight="Bold"
              Background="{DynamicResource AccentSolidBrush}"
              Foreground="{DynamicResource AccentOnSolidBrush}"
              CornerRadius="{StaticResource RadiusMd}"/>

      <!-- Status -->
      <Border Background="{DynamicResource SurfaceSunkenBrush}"
              CornerRadius="{StaticResource RadiusSm}" Padding="12,10">
        <StackPanel Spacing="2">
          <TextBlock Text="{Binding StatusText}" FontSize="11"
                     TextTrimming="CharacterEllipsis"/>
          <TextBlock Text="{Binding SmpActiveServerLine}" FontSize="10"
                     Opacity="0.7"
                     IsVisible="{Binding IsConnected}"/>
        </StackPanel>
      </Border>

    </StackPanel>
  </Grid>
</UserControl>
```

### `VPNRouter.App/Localization/Strings.cs`
Add `Smp*` keys (bilingual):
- `SmpInputLabel` — "Paste VLESS config or subscription URL" / "Вставь VLESS-конфиг или URL подписки"
- `SmpInputHint` — "Accepts a vless:// link or an https:// subscription link." / "Принимает vless:// или https:// подписку."
- `SmpTunnelModeLabel` — "What to route through VPN" / "Что направлять через VPN"
- `SmpSplitTunnelOption` — "Selected apps only (Discord, browsers, work)"
- `SmpFullTunnelOption` — "All traffic"
- `SmpAutostartLabel` — "Start with Windows" / "Запускать вместе с Windows"
- `SmpStartVpn` / `SmpStopVpn` — same icons + Russian/English
- `SmpErrorEmptyInput` — "Paste a config or subscription URL first." / "Сначала вставь конфиг или URL подписки."

## Testing
- Resize window — layout stays centered, max width 440 px.
- Light + Dark theme — all tokens work.
- Radio/CheckBox keyboard navigable.

## Acceptance
- [ ] SimplePage renders with all 5 sections (brand, input, radios,
  autostart, button, status)
- [ ] Toggle to Advanced still works (v2.17.0 behaviour preserved)
- [ ] No raw hex — all colours from tokens

---

# v2.17.2 — Input parsing + Connect wiring

**Goal**: Start button actually starts VPN from the pasted input.

## Files

### New: `VPNRouter.App/SimpleInputDetector.cs`
```csharp
public enum SmpInputKind { Invalid, Vless, SubscriptionUrl }

public static class SimpleInputDetector
{
    public static SmpInputKind Classify(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return SmpInputKind.Invalid;
        var trimmed = input.Trim();
        if (trimmed.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
            return SmpInputKind.Vless;
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return SmpInputKind.SubscriptionUrl;
        return SmpInputKind.Invalid;
    }
}
```

### `MainWindowViewModel.cs` — Simple-mode connect
```csharp
[ObservableProperty] private string _smpInput = "";

[RelayCommand]
private async Task SmpToggleConnectAsync()
{
    if (IsConnected) { await DisconnectAsync(); return; }

    var kind = SimpleInputDetector.Classify(SmpInput);
    switch (kind)
    {
        case SmpInputKind.Vless:
            var entry = VlessUriParser.Parse(SmpInput);
            _settings.Vless.Servers = new List<VlessServerEntry> { entry };
            _settings.Vless.ActiveServer = entry.Name;
            _settings.App.ConfigMode = "generated";
            IsSubscribeMode = false;
            IsVlessMode = true;
            SaveSettings();
            await ConnectAsync();
            break;

        case SmpInputKind.SubscriptionUrl:
            // Add a single-entry subscription, enable it, refresh, then connect.
            _settings.App.Subscriptions = new List<SubscriptionEntry>
            {
                new() { Name = "simple", Url = SmpInput.Trim(), Enabled = true }
            };
            _settings.App.ConfigMode = "subscribe";
            IsSubscribeMode = true;
            IsVlessMode = false;
            SaveSettings();
            await RefreshAllSubscriptionsAsync();
            await ConnectAsync();
            break;

        default:
            StatusText = Strings.SmpErrorEmptyInput;
            break;
    }
}
```

### UI text when connected
`SmpActiveServerLine` = `$"{Strings.SmpActiveThrough} {server.Name} · {server.Host}"`.

## Testing
- Paste a real VLESS URI → parses → connects.
- Paste https:// to a subscription → fetches → picks first server → connects.
- Paste garbage → error below input.
- Service-managed path: if `IsServiceManagedVpn` → use the same
  WarnServiceManagedReconnect path we added in v2.16.9.

## Acceptance
- [ ] Both input kinds work end-to-end
- [ ] Saving input persists in config.yaml
- [ ] Bad input shows inline error, doesn't touch settings
- [ ] On reconnect with new input, previous subscription is replaced

---

# v2.17.3 — Default profile + autostart

**Goal**: two remaining settings (Split profile, autostart) actually
control system state.

## Files

### Default profile
Simple mode's "Split" option maps to a single profile name —
hardcoded in `MainWindowViewModel`:
```csharp
private const string SimpleDefaultProfile = "Browsers_Discord_Work";
```

Ship a new profile JSON `profiles/Browsers_Discord_Work.json`:
```json
{
  "name": "Browsers_Discord_Work",
  "description": "Simple-mode default: browsers + Discord + common work apps",
  "dns_mode": "vpn_only",
  "block_on_vpn_fail": false,
  "processes": [
    { "name": "chrome.exe", "include_children": true },
    { "name": "firefox.exe", "include_children": true },
    { "name": "msedge.exe", "include_children": true },
    { "name": "brave.exe", "include_children": true },
    { "name": "Discord.exe", "include_children": true },
    { "name": "slack.exe", "include_children": true },
    { "name": "Telegram.exe", "include_children": true },
    { "name": "Code.exe", "include_children": true },
    { "name": "cursor.exe", "include_children": true },
    { "name": "zoom.exe", "include_children": true }
  ]
}
```

When `IsSplitTunnel && UiMode == "simple"`:
- `_settings.App.RoutingMode = "split"`
- `_settings.ActiveProfile = SimpleDefaultProfile`

When `!IsSplitTunnel && UiMode == "simple"`:
- `_settings.App.RoutingMode = "full"`
- `_settings.ActiveProfile` unchanged (full tunnel ignores profile).

### Autostart checkbox
```csharp
[ObservableProperty] private bool _smpAutostart;

partial void OnSmpAutostartChanged(bool value)
{
    if (_isLoadingUI) return;
    _settings.App.AutostartVpn = value;
    SaveSettings();
    Task.Run(() =>
    {
        if (value)
        {
            if (!ServiceInstaller.IsInstalled())
            {
                var result = ServiceInstaller.Install();
                _logger.Information("[Simple] Service install: {Msg}", result.Message);
            }
            ServiceInstaller.Start();
        }
        else
        {
            if (ServiceInstaller.IsInstalled())
            {
                ServiceInstaller.Stop();
                ServiceInstaller.Uninstall();
            }
        }
    });
}
```

Note: ServiceInstaller requires admin. App is already elevated
(Program.cs forks `runas`), so service ops succeed.

## Acceptance
- [ ] Simple-Split uses `Browsers_Discord_Work` profile
- [ ] Simple-Full bypasses profile, tunnels everything
- [ ] Autostart checkbox installs/removes service correctly
- [ ] Reboot → service starts → VPN comes up → Simple page opens and
  shows Connected

---

# v2.17.4 — Connected state + quick change

**Goal**: the Simple page also works well WHILE connected.

## Design

When `IsConnected`:
- Main button becomes red "Stop VPN"
- Below status: **Change server ▸** (only for subscription mode, with
  >1 server). Clicking expands a small `ComboBox` with the aggregated
  subscription server names. Picking one triggers Reconnect (same
  path as Advanced), or shows the `WarnServiceManagedReconnect` warning
  from v2.16.9.
- Hide: input field, radios, autostart (already locked in). User can
  still reach them via Advanced toggle if they want to change.

## Files

### `SimplePage.axaml` additions
```xml
<!-- Only in connected state -->
<Expander IsVisible="{Binding IsConnected}"
          Header="{x:Static loc:Strings.SmpChangeServer}">
    <ComboBox ItemsSource="{Binding SubscriptionServers}"
              SelectedItem="{Binding SelectedSubscriptionServer}"
              DisplayMemberPath="DisplayName"
              IsVisible="{Binding IsSubscribeMode}"/>
</Expander>
```

Hide the Start/Stop button's "input / radios / autostart" siblings via
`IsVisible="{Binding !IsConnected}"` during connected state.

## Acceptance
- [ ] Disconnected → full form visible
- [ ] Connected + Subscribe + >1 server → Change server expander
- [ ] Connected + single VLESS → no Change server option
- [ ] Stop button works, returns to disconnected state

---

# v2.17.5 — Polish

**Goal**: ship-ready. Handle edge cases, keyboard, error states.

## Items

### Bad VLESS URI
`VlessUriParser.Parse` can throw. Wrap in try/catch, show:
> "This isn't a valid VLESS link. It should start with `vless://` and
> end with `#name`."

### Unreachable subscription URL
Existing `SubscriptionFetcher.RefreshEntryAsync` throws on bad URL /
timeout. Catch in SmpToggleConnect; show:
> "Couldn't fetch the subscription. Check the URL or your internet
> connection."

### Empty input
Click Start with empty field → inline error, no side effects.

### First-run detection
On every app launch:
1. If `config.yaml` didn't exist before this run AND was just created
   by defaults → `UiMode = "simple"`.
2. Else if existing install has content (`Subscriptions.Count > 0 ||
   Vless.Servers.Count > 0 || CustomConfigs.Count > 0`) → upgrade path,
   `UiMode = "advanced"` silently.
3. Else honour whatever YAML says (existing preference).

### Toggle consistency
If user is in Simple → toggles to Advanced → does stuff → toggles back
to Simple, their `SmpInput` should reflect the current state (show the
single active server's URI, or the current subscription URL).

### Tooltips
- "Selected apps only" → hover: "Discord, Chrome, Firefox, Edge, VS Code,
  Slack, Telegram, Zoom, Brave."
- "Start with Windows" → hover: "Installs VPNRouter as a Windows Service
  so VPN starts at boot, before you log in."

## Acceptance
- [ ] All error states show friendly messages — no stack traces
- [ ] First-run fresh install lands on Simple
- [ ] Upgrade from v2.16.9 lands on Advanced automatically
- [ ] Tooltips on hover for the two opaque terms
- [ ] Keyboard: Tab navigates input → radios → checkbox → button

---

## Operational notes

### Release cadence
- Each release a prerelease. Promote v2.17.5 (final) to stable only
  after the whole Block 1-3 flow smoke-tests for:
  - fresh install → Simple → paste VLESS → connect → split mode works
  - fresh install → Simple → paste subscription → connect → works
  - upgrade from v2.16.9 → stays on Advanced, no visible change
  - service-managed: change server in Simple → warning shown (from v2.16.9)

### Grep hygiene
After v2.17.5, every Simple-mode label should come from `Strings.Smp*` —
no hardcoded English/Russian. Easy check:
```powershell
Select-String -Path VPNRouter.App/Views/Pages/SimplePage.axaml -Pattern '[A-Za-zА-Я]{4,}'
```
(Should show only token names and `x:Static` references.)

### Backward compatibility
`UiMode` missing from older YAML → defaults to `"simple"` from C#
default value. First-run detection (see v2.17.5) overrides to
`"advanced"` when existing content is present, so the user doesn't see
a regressed UI.

### Telemetry
None — project has none, v2.17 doesn't add any. UiMode stays a local
file setting.

---

## Summary table

| Version  | Block | Deliverable                                         | Est. effort |
|----------|-------|-----------------------------------------------------|-------------|
| v2.17.0  | 1     | UiMode + toggle + ContentControl switch             | M           |
| v2.17.1  | 2     | SimplePage.axaml skeleton + strings                 | M           |
| v2.17.2  | 2     | Input parsing + Start wiring (both VLESS + URL)     | M           |
| v2.17.3  | 2     | Default profile + autostart checkbox                | S           |
| v2.17.4  | 3     | Connected-state UI + quick server switch            | S           |
| v2.17.5  | 3     | Polish: errors, first-run, tooltips, kbd nav        | M           |

Legend: S = 1-2 h, M = 3-5 h.

Total: ~1.5-2 days focused work.

---

## Status tracker

- [x] v2.17.0 — UiMode + toggle (shipped 2026-04-20)
- [x] v2.17.1 — SimplePage skeleton (shipped 2026-04-20)
- [x] v2.17.2 — Input parsing + connect (shipped 2026-04-20)
- [x] v2.17.3 — Default profile + autostart (shipped 2026-04-20, **Block 1+2 done, MVP testable**)
- [ ] v2.17.4 — Connected state (Change server ▸ expander)
- [ ] v2.17.5 — Polish + first-run detection + default flip to Simple

Update this checklist as each release ships so a context-compacted
session picks up where we left off.

---

## Recorded user decisions (2026-04-20)

1. Input field accepts BOTH VLESS URIs AND subscription URLs — one
   field, auto-detected by prefix.
2. Default Split profile = **Discord + Browsers + Work apps**. Shipped
   as `profiles/Browsers_Discord_Work.json`.
3. First-run → **auto Simple mode**, Advanced toggle in header. No
   modal.
4. Autostart = **single checkbox** that installs + starts the Windows
   Service and flips `AutostartVpn`.
5. Free Configs tab is NOT exposed in Simple. Users who want free
   configs switch to Advanced.

## References
- `.claude/plans/vpnrouter-v2.16-arctic-theme.md` — previous roadmap
- `.claude/plans/vpnrouter-v2.15-roadmap.md` — v2.15 series
- `.claude/workflow.md` — git remotes, release policy, hotfix flow
