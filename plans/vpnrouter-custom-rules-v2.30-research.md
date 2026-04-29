# Custom Rules — v2.30+ feature research

**Trigger**: user direction 2026-04-29: «А ресерч по тому как удобнее
реализовать custom rules делал? не только для direct, но и proxy и
block, и как это лучше реализовать. И как это бьётся с нашим пунктом
russian traffic via real IP. Я хочу целое обновление этому функционалу
и его работоспособности посвятить».

v2.29.0-r4 shipped a "custom direct rules" textbox — only direct, no
proxy/block, no UI for ordering, no integration with built-in toggles
(BypassRussianTraffic, BlockAds). User wants this expanded to a full
rules engine with all 3 actions + sane interaction with built-ins.

This doc:
1. Audits the current rule system (what's already there).
2. Maps sing-box's full rule action vocabulary.
3. Surveys how 5 comparable apps (NekoBox, Hiddify, AmneziaVPN,
   Cloudflare WARP, ProtonVPN) handle this.
4. Proposes 3 design options ranked by effort vs power.
5. Recommends a phased rollout.

**Estimated total effort for full implementation: 25-40 hours.**
Spread across 2-3 minor versions (v2.30 / v2.31 / v2.32).

---

## Part 1 — Current rule system audit

### Where rules live in code

| Source | What | Where written |
|---|---|---|
| `App.BypassRussianTraffic` (bool) | `geosite-ru → direct` route + `geosite-ru → ru-dns` DNS | `ConfigGenerator.ApplyGeoBypass` |
| `App.BlockAds` (bool) | `adblock-set → reject` route + `adblock-set → reject` DNS | `ConfigGenerator.ApplyAdBlock` |
| `App.CustomDirectRules` (list) v2.29.0-r4 | `<type/value> → direct` routes | `ConfigGenerator.ApplyCustomDirectRules` |
| Profile `Processes[]` (list) | `process_name → proxy` route | `ConfigGenerator.BuildRoute` |
| Built-in `ip_is_private → direct` | LAN/AWG/WireGuard never go through proxy | `ConfigGenerator.BuildRoute` line 660 |
| `App.RoutingMode` (split/full) | Final route = direct (split) or proxy (full) | `ConfigGenerator.BuildRoute.Final` |

### Effective rule order (sing-box first-match-wins, top of file = highest priority)

For split-tunnel with both BypassRu + BlockAds + CustomDirectRules + apps selected:

```
[INSERTED BY ApplyAdBlock — currently at position 0, possibly out of order]
0. adblock-rule-set → reject              ← BlockAds (DNS + route)

[BUILT-IN, in BuildRoute construction order]
1. sniff (300ms timeout)                   ← protocol detection
2. protocol=dns → hijack-dns               ← DNS to module

[INSERTED BY ApplyCustomDirectRules — inserts after sniff/hijack-dns/private]
2.5. CustomDirectRule[i] → direct          ← v2.29.0-r4 user rules

[INSERTED BY ApplyGeoBypass — same insertion logic as above]
2.6. geosite-ru → direct                   ← BypassRussianTraffic

[BUILT-IN]
3. ip_is_private → direct                  ← LAN/AWG protection
4. process_name=[Discord.exe,...] → proxy  ← app selection
5. final = direct                          ← unmatched goes direct
```

(Bug: AdBlock inserts at index 0 which puts it **before** sniff/hijack-
dns. Probably harmless because reject doesn't need sniffed domain, but
ordering is technically inconsistent.)

### What user-controlled rule types are MISSING

| Action | Available now | Notes |
|---|---|---|
| Direct (route → direct) | ✅ via CustomDirectRules + BypassRu | Full coverage |
| Proxy (route → proxy) | ❌ only via app selection (process_name) | Cannot say "domain X always through VPN" |
| Block (route → reject) | ❌ only via BlockAds (one rule_set) | Cannot say "block ads.example.com" |
| DNS-level filtering | ❌ partially via BlockAds | Cannot create custom DNS rules |

User's request is to add: **proxy** + **block** as user-defined rules,
not just preset toggles.

### Existing toggle vs custom rule overlap

| Toggle | Equivalent custom rule | What user could replace toggle with |
|---|---|---|
| `BypassRussianTraffic = true` | `direct rule_set:geosite-ru` | A user could disable the toggle and add this rule manually with the same effect — IF we expose `rule_set` as a custom-rule type |
| `BlockAds = true` | `block rule_set:adblock-set` | Same |
| Profile `Processes[]` for "Discord_Privacy" | `proxy process_name:Discord.exe` | If we expose `process_name` as a rule action target |

**This means a unified rule engine can SUBSUME the existing toggles.**
Toggles become syntactic sugar / migration aid for older configs.

---

## Part 2 — sing-box rule action vocabulary (full reference)

From sing-box 1.13.10 `Route Rule Actions` docs (as of writing):

| sing-box action | UX action | What it does | Outbound field |
|---|---|---|---|
| `route` + `outbound:"direct"` | **direct** | Send traffic out via real interface, bypassing proxy | required |
| `route` + `outbound:"proxy"` | **proxy** | Send traffic through VLESS/VMess/Trojan group | required |
| `route` + `outbound:"<custom>"` | (advanced) | Send through a named outbound (e.g. urltest selector) | required |
| `reject` | **block** | Drop traffic with no response | n/a |
| `reject` + `method:"drop"` | **block-silent** | Drop without RST (saves probes) | n/a |
| `reject` + `method:"default"` | **block-rst** | TCP RST (default) — gives apps a fast fail signal | n/a |
| `hijack-dns` | (auto) | DNS module handles the query | n/a |
| `sniff` | (auto) | Protocol sniffing | n/a |
| `resolve` | (advanced) | Force re-resolve at this point | n/a |

For user-facing UX, only 3 actions matter: **direct / proxy / block**.
The rest are sing-box internals or DNS-only and shouldn't be exposed.

### Match types (rule conditions)

| sing-box match | UX type | Notes |
|---|---|---|
| `domain` | **domain** | Exact FQDN |
| `domain_suffix` | **domain_suffix** | Ends-with match (most common for "all of *.example.com") |
| `domain_keyword` | **domain_keyword** | Substring anywhere |
| `domain_regex` | (advanced) | Regex — power user, exposes rule to misuse |
| `ip_cidr` | **ip_cidr** | IPv4 / IPv6 CIDR |
| `port` | **port** | Single dest port |
| `port_range` | **port_range** | e.g. "1024-5000" |
| `network` | **network** | "tcp" or "udp" only |
| `process_name` | **process_name** | Windows / Linux: filename. Case-sensitive on sing-box. |
| `process_path` | (advanced) | Full path; rarely needed |
| `package_name` | **package_name** | Android only, see vpnrouter-android-research.md |
| `source_ip_cidr` | (advanced) | Source IP — irrelevant for client-mode VPN |
| `geosite` (rule_set) | **geosite** | "ru", "google", "ads", "category-games", etc. |
| `geoip` (rule_set) | **geoip** | "ru", "us", "cn", etc. |
| `rule_set` | (advanced) | Custom .srs file URL |

For user UX, expose: domain / domain_suffix / domain_keyword / ip_cidr
/ port / port_range / network / process_name (+ Android package_name).
Hide regex / source_ip_cidr / process_path / custom rule_set behind an
"Advanced" disclosure.

geosite / geoip can be exposed as **preset rule sets** (drop-down with
common categories) rather than free-form rule_set names — keeps UI simpler.

---

## Part 3 — Industry survey

### NekoBox / sing-box-android (most direct comparable)

UX: **structured row table** in a Material RecyclerView. Each row:
- Action chip (route/reject/...) — colored badge
- Match type icon + value preview
- Right-edge toggle (enable/disable)
- Long-press for delete / reorder
- "+ Add" floating action button

Rule order: explicit, user-controllable via drag handles. First-match-
wins is the mental model (matches sing-box semantics exactly).

Configuration storage: same format as sing-box JSON — direct mapping
between UI rows and config rule entries.

**Pros**: discoverable, no syntax to memorize, drag-to-reorder maps
1:1 to first-match-wins.
**Cons**: 300+ lines of XAML for a feature most users won't touch.

### Hiddify (sing-box-based, GUI mode)

UX: **YAML edit textbox** for advanced rules + a couple of preset
toggles (block-ads, bypass-china) at the top. Power-user friendly.

Config storage: editable YAML inside the app's config blob.

**Pros**: power users (the target audience for this feature) prefer
text — paste, version-control, search. Fast to implement.
**Cons**: discoverability nil. Errors only on save, not as you type.
First-time users panic at the empty textbox.

### AmneziaVPN

UX: **app-list split tunnel only**. No domain rules at all. The
"split" mode is a per-app whitelist, period.

Out of scope for our use case — we already have richer functionality.

### Cloudflare WARP / 1.1.1.1

UX: **Split Tunnel** with two modes:
- "Include only" — list of IPs/domains/IP-ranges that go through tunnel
- "Exclude" — opposite

Single action, no proxy/block split. Very limited for our needs.

### ProtonVPN

UX: **per-app whitelist** + **DNS server overrides**. No domain rules.
Same limitations as AmneziaVPN.

### Cisco AnyConnect / OpenVPN

UX: **route table** edited by sysadmin via .ovpn config push, not by
end user. Never exposed as a UI feature.

### Summary

The two relevant patterns:
1. **NekoBox structured table** — gold standard for user-friendly
   rule editing.
2. **Hiddify YAML textbox** — power-user shortcut.

VPNRouter's v2.29.0-r4 textbox is essentially Hiddify's pattern but
limited to direct-only. The user is asking us to expand toward
NekoBox's full rule-engine functionality.

---

## Part 4 — Design options

### Option A: Pure structured table (NekoBox-clone)

**UX**: Rule editor on Network → Routing → "Rules" tab (renamed from
"Custom direct rules"). Each rule is a row:

```
┌────────────────────────────────────────────────────────────┐
│  Action       Match               Value             Enabled │
│  ─────────    ──────────────      ───────           ─────── │
│  ✓ direct     ip_cidr             10.0.0.0/8          [✓]   │
│  ⤴ proxy      domain_suffix       .corp.example       [✓]   │
│  ✕ block      domain_keyword      ads-tracker         [✓]   │
│  ✓ direct     rule_set            geosite-ru          [✓]   │ ← was BypassRussianTraffic toggle
│  ✕ block      rule_set            ads-blocklist       [✓]   │ ← was BlockAds toggle
└────────────────────────────────────────────────────────────┘

[+ Add rule]  [↑↓ Reorder]  [Import...]  [Reset to defaults]
```

Action chips colored: direct=blue, proxy=orange, block=red.
Drag handles for reorder. Enabled checkbox per row.

`BypassRussianTraffic` + `BlockAds` toggles GO AWAY — they become
read-only "default" rules in this list (or simply disabled by default,
user re-enables in the Rules tab).

**Migration**: on first-run after upgrade:
- If `BypassRussianTraffic == true`: insert a `direct rule_set:geosite-ru`
  rule at top of the list, marked as "built-in (BypassRu legacy)".
- If `BlockAds == true`: insert a `block rule_set:adblock-set`.
- Existing CustomDirectRules: convert each to a row.
- Reset toggles to false (data lives in the rule list now).

**Pros**: full functionality, clear UI, drag-reorder = explicit
priority, supersedes 2 toggles + 1 list.
**Cons**: 30-40 hours total dev time. ~500 lines of XAML for the
table + drag/drop + per-row VM. Lots of edge cases (validation,
ordering, conflict detection between rules).

### Option B: Structured table BUT keep toggles separate

Same row table as Option A, but `BypassRussianTraffic` and `BlockAds`
toggles STAY at the top of Network → Routing as quick-on/off buttons.
Custom rules in the table are SEPARATE (added BEFORE the toggles in
sing-box rule order).

**Pros**: simpler migration — no schema change, toggles unchanged.
Less surface area for new bugs. ~20-25 hours.
**Cons**: two sources of truth. Confusing if user toggles BypassRu off
but has a `direct rule_set:geosite-ru` in the table — same effect by
two mechanisms. UI gets cluttered.

### Option C: Text format, expand the v2.29.0-r4 textbox

Same textbox as r4, but extended grammar:

```
# Format: [!]<action> <type> <value>[, <value>...]   [# comment]
# Actions: direct / proxy / block
# Types: domain / domain_suffix / domain_keyword / ip_cidr / port / port_range / process_name / geosite / geoip
# Disable: prefix line with !

# === Direct (bypass VPN) ===
direct ip_cidr      10.0.0.0/8, 192.168.0.0/16    # LAN
direct domain       printer.local
direct geosite      ru                            # was BypassRu toggle

# === Proxy (force through VPN, even if app not in list) ===
proxy domain_suffix .corp.example
proxy port_range    1024-5000
proxy package_name  com.discord                   # Android only

# === Block (drop, no response) ===
block domain_keyword facebook
block geosite       ads
block geoip         cn                            # block all CN traffic
```

Existing toggles `BypassRussianTraffic` + `BlockAds` could either:
- **Stay as quick UI shortcuts** — toggling them prepends/removes
  the corresponding text line in the rules block.
- **Become legacy/migrated** — first-run after upgrade migrates them
  into the textbox and disables the toggles.

**Pros**: minimal new XAML (textbox + extended parser already exists),
~8-12 hours for parser/codegen extensions, paste-friendly, version-
controllable, works for power users from day 1.
**Cons**: discoverability still bad. Order is implicit (line order).
Validation only on save. Newcomers stay confused.

### Recommended: hybrid approach

**Option C-then-A phased**: ship Option C in v2.30 (low effort, high
power), and IF user feedback shows discoverability issues, add an
Option A "structured editor" tab on top in v2.31 — both editing the
same underlying List<CustomRule> data.

This matches the v2.29 "ship a textbox first, gauge demand for
structured UI" approach we already started in r4.

---

## Part 5 — Schema design (regardless of UX)

### Replace `CustomDirectRule` with `CustomRule`

```csharp
public class CustomRule
{
    /// <summary>"direct" | "proxy" | "block".
    /// Direct = action=route, outbound=direct.
    /// Proxy  = action=route, outbound=proxy.
    /// Block  = action=reject.</summary>
    public string Action { get; set; } = "direct";

    /// <summary>"domain" / "domain_suffix" / "domain_keyword" /
    /// "ip_cidr" / "port" / "port_range" / "process_name" /
    /// "package_name" / "geosite" / "geoip" / "network".</summary>
    public string Type { get; set; } = "domain_suffix";

    /// <summary>Comma-separated multi-value.</summary>
    public string Value { get; set; } = string.Empty;

    public string Comment { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}
```

### Migration from v2.29.0-r4 `CustomDirectRule` → `CustomRule`

`CustomDirectRule` had no Action field (always implied direct).
SettingsMigrator step:

```csharp
if (settings.App.CustomDirectRules?.Count > 0
    && settings.App.CustomRules?.Count == 0)
{
    settings.App.CustomRules = settings.App.CustomDirectRules
        .Select(r => new CustomRule
        {
            Action = "direct",
            Type = r.Type,
            Value = r.Value,
            Comment = r.Comment,
            Enabled = r.Enabled,
        }).ToList();
    settings.App.CustomDirectRules = new();  // empty old field; keep
                                              // it for back-compat with
                                              // older binaries that may
                                              // still read it.
}
```

### `BypassRussianTraffic` + `BlockAds` migration

If user has either toggle ON, insert preset rules at top of list on
first run after upgrade:

```csharp
if (settings.App.BypassRussianTraffic
    && !settings.App.CustomRules.Any(r => r.Type == "geosite" && r.Value == "ru"))
{
    settings.App.CustomRules.Insert(0, new CustomRule
    {
        Action = "direct",
        Type = "geosite",
        Value = "ru",
        Comment = "[Migrated from BypassRussianTraffic toggle]",
        Enabled = true,
    });
}
// similar for BlockAds → block rule_set:adblock-set
```

Toggles remain in schema for backward compat but are read-only
display-only after migration. Deprecated in v2.31, removed in v2.32.

---

## Part 6 — ConfigGenerator integration

### Replace `ApplyCustomDirectRules` with `ApplyCustomRules`

```csharp
private static void ApplyCustomRules(SingBoxConfig config, List<CustomRule> rules)
{
    int insertAt = FindInsertionPoint(config); // after sniff/hijack/private
    foreach (var rule in rules.Where(r => r.Enabled))
    {
        var routeRule = BuildRouteRule(rule);
        if (routeRule == null) continue;
        config.Route.Rules.Insert(insertAt++, routeRule);

        // For block actions, ALSO insert a DNS-level reject so the
        // domain doesn't even get resolved (saves a roundtrip + matches
        // user expectation of "blocked = invisible to me").
        if (rule.Action == "block" && IsDomainType(rule.Type))
        {
            config.Dns.Rules.Insert(0, BuildDnsRule(rule));
        }

        // For proxy/direct rules using rule_set, ensure the rule_set
        // entry is registered in route.rule_set or DNS rule_set.
        if (rule.Type == "geosite" || rule.Type == "geoip")
        {
            EnsureRuleSetEntry(config, rule.Type, rule.Value);
        }
    }
}
```

### Rule precedence (sing-box first-match-wins)

Within `route.rules`, order = priority. Custom rules go in user-
specified order. Default insertion point = right after built-in
sniff/hijack-dns/private-ip — same place as today's CustomDirectRules.

Built-in rules that come AFTER custom rules:
- `process_name → proxy` (app selection — user can add a `direct
  process_name:Game.exe` rule BEFORE this to override)
- `final` (unmatched routes)

Built-in rules BEFORE custom rules (always-on):
- `sniff`
- `hijack-dns`
- `ip_is_private → direct`

### Conflict detection

User can shadow themselves: e.g. `direct ip_cidr:0.0.0.0/0` matches
everything → all subsequent rules unreachable. Detect on save:

```csharp
public static List<string> DetectConflicts(List<CustomRule> rules)
{
    var conflicts = new List<string>();
    for (int i = 0; i < rules.Count - 1; i++)
    {
        if (IsCatchAll(rules[i]))
            conflicts.Add($"Line {i+1}: '{rules[i].Type}:{rules[i].Value}' matches everything → rules below this line will never fire.");
    }
    return conflicts;
}
```

---

## Part 7 — UI mockup (Option C textbox extended)

```xml
<Expander Header="{Binding L_CustomRulesTitle}" IsExpanded="False">
  <StackPanel Spacing="6">
    <TextBlock Text="{Binding L_CustomRulesDescription}" />
    <TextBox Text="{Binding CustomRulesText}"
             Watermark="{Binding L_CustomRulesPlaceholder}"
             AcceptsReturn="True" MinHeight="200" MaxHeight="400"
             FontFamily="monospace" />
    <!-- Inline diagnostics -->
    <Border IsVisible="{Binding HasParseErrors}" ...>
      <TextBlock Text="{Binding ParseErrors}" />
    </Border>
    <Border IsVisible="{Binding HasConflicts}" ...>
      <TextBlock Text="{Binding ConflictWarnings}" />
    </Border>
  </StackPanel>
</Expander>
```

Watermark text changes from r4's direct-only example to:

```
# One rule per line. Format: <action> <type> <value> [# comment]
# Actions: direct / proxy / block
# Types: domain / domain_suffix / domain_keyword / ip_cidr / port /
#        port_range / process_name / geosite / geoip
# Multi-value: comma-separated. Disable: prefix '!'.

# Examples:
direct ip_cidr 10.0.0.0/8, 192.168.0.0/16    # LAN
proxy domain_suffix .corp.example
block geosite ads
```

---

## Part 8 — Estimated effort breakdown

| Item | Hours | Phase |
|---|---|---|
| Schema: CustomRule class + migration from CustomDirectRule | 2 | v2.30 |
| Parser extension: action keyword, all match types | 3 | v2.30 |
| ConfigGenerator: ApplyCustomRules with all 3 actions | 4 | v2.30 |
| Rule_set registration for geosite/geoip in custom rules | 2 | v2.30 |
| BypassRu / BlockAds migration logic | 2 | v2.30 |
| Conflict detection + UI diagnostics | 3 | v2.30 |
| Tests: 20+ new (schema, parser, generator, migration) | 4 | v2.30 |
| UI: extend r4 textbox watermark + error display | 1 | v2.30 |
| Docs / release notes | 1 | v2.30 |
| **v2.30 total** | **22** | |
| Structured table editor (Option A) | 12-16 | v2.31 (if user demand) |
| Drag-reorder per row | 4 | v2.31 |
| Per-row inline validation | 3 | v2.31 |
| **v2.31 total** | **19-23** | |

**Total: 22 hours for v2.30 + 19-23 for v2.31 = 41-45 hours.**

---

## Part 9 — Open questions for user

1. **Action vocab**: should we use `direct/proxy/block` (user-friendly)
   or `route/reject` (sing-box-native)? Recommendation: user-friendly.
2. **Migration**: silent migrate `BypassRussianTraffic` + `BlockAds`
   toggles into rules on first run, OR keep toggles + custom rules
   separate? Option B vs Option A above. Recommendation: Option A
   migration (one source of truth).
3. **rule_set values**: hardcoded list in dropdown (ru/cn/us/ads/...)
   or free-form text? Recommendation: dropdown for common, text for
   advanced. Plus auto-download mechanism for common rule_sets (ru/ads
   already bundled, others fetched on first use).
4. **Block action default method**: `drop` (silent) or `default` (RST)?
   `drop` saves probe traffic; `default` makes apps fail faster.
   Recommendation: `default` for explicit blocks, `drop` for ad-blocking.
   Let user override per rule via optional `[strict]` modifier.
5. **Per-rule network filter**: should a rule support BOTH `ip_cidr` AND
   `network=tcp` simultaneously? sing-box allows it. Adds complexity to
   the text format. Recommendation: in v2.30 keep simple (one match
   per rule); in v2.31 add multi-match via line continuation.
6. **Export / import**: CSV? JSON? sing-box-native rules.json snippet?
   Recommendation: native sing-box JSON for compatibility with NekoBox /
   Hiddify exports. v2.31.

## Cross-references

- v2.29.0-r4 introduced `CustomDirectRule` — schema we extend here.
- `plans/vpnrouter-update-reliability-strategy.md` — release process.
- `plans/vpnrouter-android-research.md` — Android `package_name` match
  type lives only on Android profile schema.
- sing-box upstream rule docs: <https://sing-box.sagernet.org/configuration/route/rule/>
- NekoBox source for reference rule editor:
  <https://github.com/MatsuriDayo/NekoBoxForAndroid> `app/src/main/java/io/nekohasekai/sagernet/database/RuleEntity.kt`
