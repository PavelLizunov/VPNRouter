# Research: app presets for the "Bypass VPN" list

Date: 2026-07-06

## Short answer

Do not mirror the existing "Through VPN" catalogue. The include list is built
around foreign/blocked services that benefit from VPN. The bypass list should
be built around apps that either:

1. break or degrade behind VPN / foreign IP,
2. are Russian ecosystem apps that may detect/restrict VPN,
3. are latency/anti-cheat sensitive games,
4. should stay local by design.

Default should be conservative: ship categories as selectable presets, not
pre-check a huge bypass list. A wrong bypass leaks traffic; a missing bypass is
only a UX annoyance.

Scope: this document is about Windows `.exe` process presets only. Websites,
domains, and Russian IP/domain traffic are already handled by the separate
"Russian traffic via real IP" routing option in Settings -> Routing. Do not
duplicate those web rules in the app list.

## Evidence

- RKS Global found VPN detection in popular Russian apps. In the April 16
  update, all 30 analyzed apps had VPN detection; banks and some marketplaces
  actively restrict functionality when VPN is detected. They also explicitly
  describe split tunneling as a partial workaround, but insufficient against
  newer methods that scan interfaces or installed VPN apps.
- DOXA/RBC reporting says MinTsifry asked large platforms to detect VPN and
  restrict service access by mid-April 2026. Named categories include Sber,
  Yandex, VK, Wildberries, Ozon, Avito, X5.
- Habr/Izvestia reporting says Wildberries/Ozon/VkusVill can open but fail to
  load cards/images/descriptions with some VPNs; without VPN they work.
- Steam support says VPN software can prevent the Steam client from accessing
  the Steam network; Steam purchasing support asks users to disable proxy/VPN
  for purchase problems.
- Riot publicly blocked some high-volume VPN services for cross-region access
  because VPN use caused region/latency/gameplay issues.
- Gaijin support treats connection path stability as first-class: their support
  flow asks users for tracert/ping to game endpoints.

## Recommended Bypass VPN categories

### 1. Russian ecosystem

Purpose: Russian apps/services that may block, degrade, or flag VPN. On desktop
many are browser/PWA-first, so process presets are imperfect.

Windows candidates:

- `browser.exe`, `yandex.exe`, `Yandex.exe` — Yandex Browser
- `vk.exe`, `VK.exe`, `VK Messenger.exe`, `VKTeams.exe` — VK/VK Teams/VK Messenger, low confidence on exact exe names
- `MAX.exe`, `max.exe` — MAX, low confidence on desktop distribution
- `2GIS.exe`, `2gis.exe` — 2GIS
- `RuStore.exe`, `rutube.exe`, `RUTUBE.exe` — low confidence; mostly mobile/web

UX note: warn that adding Yandex Browser bypasses all traffic from that browser.
This is useful only if the user intentionally uses it as the "Russian services"
browser.

### 2. Russian banking, marketplaces, government

Purpose: only native desktop executables, if they exist. Most banks,
marketplaces, and government services are web/PWA flows, and those should stay
under the existing "Russian traffic via real IP" routing setting.

Do not add Chrome/Edge/Firefox globally by default. That would bypass every
site opened in that browser, not just Russian services. Instead:

- recommend a dedicated browser entry for Russian services, preferably Yandex
  Browser or a custom browser profile executable/shortcut when process detection
  can see it;
- keep the existing "Russian traffic via real IP" domain/IP rule as the primary
  solution for web flows;
- let user add a specific browser if they do Sber/T-Bank/Ozon/Wildberries/Avito
  work in one isolated browser.

### 3. Games and launchers

Purpose: lower latency, fewer route changes, fewer anti-cheat / account-region
false positives.

Windows candidates:

- `steam.exe`, `steamwebhelper.exe`, `steamservice.exe`
- `EpicGamesLauncher.exe`
- `Battle.net.exe`, `Agent.exe`
- `RiotClientServices.exe`, `RiotClientUx.exe`, `LeagueClient.exe`
- `VALORANT-Win64-Shipping.exe`, `vgc.exe`
- `Gaijin.Net Updater.exe`, `launcher.exe`, plus game exes for War Thunder / Enlisted / Crossout if discovered live
- `Lesta Game Center.exe`, `lgc.exe`, `WorldOfTanks.exe`, `WorldOfWarships.exe` (needs live verification)
- `VKPlay.exe`, `GameCenter.exe` (ambiguous name; only add if discovered live)

Anti-cheat candidates to verify separately:

- `vgc.exe` (Riot Vanguard)
- `EasyAntiCheat_EOS.exe`, `EasyAntiCheat.exe`
- `BEService.exe` (BattlEye)
- `FACEITService.exe`

These matter because anti-cheats and launchers can treat VPN/proxy routing as
account-risk, region-risk, or connection-tampering signal. However, they may
run as services or helper processes where per-process routing may not affect
the actual game traffic. Verify on a Windows VM before shipping them as
predefined items.

Preset should be named "Games / launchers" and be opt-in. We should not claim
all games must bypass VPN: some Russian users need VPN for blocked game login
or store pages. This category is for latency/account stability.

### 4. Local network and remote access

Purpose: avoid accidentally sending LAN/admin tools through the tunnel.

Windows candidates:

- `mstsc.exe`
- `AnyDesk.exe`
- `TeamViewer.exe`, `TeamViewer_Service.exe`
- `rustdesk.exe`
- `parsecd.exe`, `parsec.exe`
- `moonlight.exe`, `Sunshine.exe`
- `vmconnect.exe`, `VirtualBox.exe`, `VirtualBoxVM.exe`, `VBoxHeadless.exe`

Note: private IP ranges already route direct by network rules, so this category
is mostly belt-and-braces for process-level UI clarity.

### 5. Dev / package managers that should use local corporate/RU mirrors

Purpose: optional, not a consumer default.

Windows candidates:

- `git.exe`, `ssh.exe`
- `node.exe`, `npm.exe`, `pnpm.exe`, `yarn.exe`
- `python.exe`, `pip.exe`
- `dotnet.exe`
- `Docker Desktop.exe`, `com.docker.backend.exe`

This should not be enabled by default because dev tools often need GitHub,
OpenAI, npm, PyPI, Docker Hub via VPN.

## Product recommendation

Add a separate bundled catalogue for bypass presets, not reuse the include
profile catalogue:

- `Russian services` — conservative, mostly Yandex Browser + VK/2GIS/MAX when
  exact process names are known.
- `Games / launchers` — Steam/Epic/Battle.net/Riot + a few RU launchers.
- `Remote / local network` — mstsc/AnyDesk/TeamViewer/RustDesk/Parsec/VMs.
- `Dev tools (local mirrors)` — optional advanced preset.

Correction: `Russian services` means `Russian apps` here: real desktop
executables only, not websites. Add `Game anti-cheats` as a separate opt-in
group because these are service/helper processes and need VM verification.

Keep "Custom" prominent. For Russian services, the real answer is often a
dedicated browser/profile, not trying to enumerate every bank/marketplace app.
Avoid a `Russian websites` app preset: the Routing setting already handles
Russian domains/IPs via real IP.

## Proposed Bypass VPN catalogue v1

Use the same `ProfileCollection` schema as `profiles/default.json`, but load a
separate Windows catalogue for the bypass list. Do not reuse the include
catalogue: include and exclude are different product intents.

Suggested categories:

### Russian desktop apps

Only real desktop executables. No websites.

- `browser.exe`, `yandex.exe`, `Yandex.exe` - Yandex Browser
- `VKTeams.exe`, `VK Messenger.exe`, `VK.exe`
- `2GIS.exe`, `2gis.exe`
- `MAX.exe`, `max.exe` - verify desktop build/process name
- `VKPlay.exe`, `GameCenter.exe` - verify exact VK Play process names
- `Lesta Game Center.exe`, `lgc.exe` - verify exact process names

### Game launchers

Stable launcher/helper processes. These are safe as recommendations, not as
default-checked items.

- `steam.exe`, `steamwebhelper.exe`, `steamservice.exe`
- `EpicGamesLauncher.exe`
- `Battle.net.exe`, `Agent.exe`
- `RiotClientServices.exe`, `RiotClientUx.exe`, `RiotClientUxRender.exe`
- `LeagueClient.exe`
- `VALORANT-Win64-Shipping.exe`
- `Gaijin.Net Updater.exe`
- `Lesta Game Center.exe`, `lgc.exe`
- `VKPlay.exe`, `GameCenter.exe`

### Game anti-cheats

Separate opt-in category. Verify on VM before shipping as visible preset.

- `vgc.exe`, `vgk.exe` - Riot Vanguard user/service components
- `EasyAntiCheat.exe`, `EasyAntiCheat_EOS.exe`
- `BEService.exe` - BattlEye
- `FACEITService.exe`

### Remote and LAN tools

- `mstsc.exe`
- `AnyDesk.exe`
- `TeamViewer.exe`, `TeamViewer_Service.exe`
- `rustdesk.exe`
- `parsec.exe`, `parsecd.exe`
- `moonlight.exe`, `Sunshine.exe`
- `VirtualBox.exe`, `VirtualBoxVM.exe`, `VBoxHeadless.exe`, `vmconnect.exe`

### Work calls, optional

Not RU-specific. Add only as optional quality-of-life preset.

- `Teams.exe`, `ms-teams.exe`
- `Zoom.exe`
- `Webex.exe`

### Dev tools, optional advanced

Not a consumer default. Many dev tools need GitHub/npm/PyPI/Docker Hub through
VPN, so this is only for users with local/RU mirrors.

- `git.exe`, `ssh.exe`
- `node.exe`, `npm.exe`, `pnpm.exe`, `yarn.exe`
- `python.exe`, `pip.exe`
- `dotnet.exe`
- `Docker Desktop.exe`, `com.docker.backend.exe`

## Steam games mechanism

Do not ship a static list of popular Steam games. It will rot, miss the user's
actual library, and add games they do not have installed.

Minimum useful mechanism:

1. Find Steam install roots:
   - registry: `HKCU\Software\Valve\Steam\SteamPath`
   - fallback: `C:\Program Files (x86)\Steam`
2. Read `steamapps\libraryfolders.vdf`.
3. For each library path, read `steamapps\appmanifest_*.acf`.
4. Parse `installdir`.
5. Scan `steamapps\common\<installdir>` for candidate `.exe` files.
6. Show a review list: game name + detected exe candidates.
7. User picks which game exe files to add to "Games / launchers" in
   `RoutingAppsExclude`.

Lazy heuristic for candidate `.exe` files:

- include `.exe` files in the game root and one level below;
- skip obvious uninstallers, crash reporters, redistributables, launch helpers:
  `unins*.exe`, `uninstall*.exe`, `crash*.exe`, `UnityCrashHandler*.exe`,
  `vc_redist*.exe`, `dxsetup.exe`, `setup.exe`;
- if multiple candidates remain, show them all instead of guessing.

This avoids a bundled "top Steam games" list. Add a curated fallback only if
users ask for one, and keep it tiny: Dota 2, CS2, PUBG, Apex, War Thunder,
World of Tanks, Rust, GTA/Rockstar launcher. Even then, local Steam import is
the primary path.

## Open verification tasks

- On a clean Windows VM, install Yandex Browser, VK Teams/Messenger, 2GIS,
  Lesta Game Center, VK Play, MAX if desktop build exists; record exact process
  names.
- Decide whether service processes (`vgc.exe`, `BEService.exe`,
  `EasyAntiCheat*.exe`) are useful in VPNRouter's process matching or whether
  only launchers/game clients should be listed.
- Check if bundled profile format needs a `defaultMode` / `catalogKind` split,
  or if app UI can load two JSON catalogues with the same schema.

## Existing tools: what they usually exclude

Pattern from ready VPN clients:

- They usually do not ship a large hardcoded bypass catalogue.
- They expose two modes: selected apps bypass VPN, or only selected apps use VPN.
- The UI lists installed apps and lets the user browse to a missing `.exe`.
- Their examples cluster around browsers, banking apps, streaming apps, Steam /
  specific games, Discord, and enterprise meeting/work traffic.
- Website/domain exclusions are usually a separate feature from app exclusions.

Concrete findings:

- PIA has the most directly useful Windows example: exclude `steamwebhelper.exe`
  together with Steam, and for a specific game add that game's own `.exe` from
  the Steam library folder.
- Mullvad warns that excluded apps expose the user's real IP, documents Discord
  needing `Update.exe` as well as Discord itself, and lists games using
  anti-cheat, Vivox voice chat, and Parsec as difficult or impossible to exclude.
- Surfshark Bypasser separates app exclusions from website/IP exclusions and
  lets the user add a missing app by locating its `.exe`.
- ExpressVPN also separates app rules from IP/site rules and warns that public
  IP exclusions for large websites are unreliable and can match unintended
  services.
- Windscribe explicitly calls out banking apps as apps that may not work behind
  a VPN, and supports both app-based and hostname/IP-based split tunneling.
- Enterprise VPN guidance commonly bypasses Microsoft 365/Teams media traffic
  for latency and VPN capacity, but this is mostly IP/endpoint routing, not a
  consumer `.exe` preset.

Implication for VPNRouter:

- Do not pre-enable a giant bypass list.
- Ship small opt-in groups.
- Prefer exact `.exe` names for native apps.
- Keep web/domain bypass in the existing Routing setting.
- Mark anti-cheats as "verify before default" because service/helper processes
  may not map cleanly to process routing.

Extra Windows `.exe` candidates from this pass:

- `Discord.exe`, `Update.exe` - Discord needs both in some split-tunnel tools.
- `steam.exe`, `steamwebhelper.exe`, `steamservice.exe` - Steam client stack.
- game-specific `.exe` files from Steam/Epic/Lesta/VK Play install folders.
- `Teams.exe`, `ms-teams.exe`, `Zoom.exe`, `Webex.exe` - optional "Work calls"
  group, not RU-specific and not default.
- `Netflix.exe`, `Hulu.exe`, Windows Web Application host entries - streaming
  examples from VPN docs, but weak for our RU app preset.

## Sources

- RKS Global — How and Why Russian Apps Search for VPN on Users' Phones:
  https://rks.global/en/research/vpn-detection/
- DOXA/RBC summary — MinTsifry VPN detection instructions:
  https://doxa.team/news/2026-04-06-mincifry-vpn
- Habr/Izvestia summary — marketplaces restricting VPN users:
  https://habr.com/ru/news/1020134/
- Steam Support — Programs Which May Interfere with Steam:
  https://help.steampowered.com/en/faqs/view/1F39-DCB4-FF28-5748
- Steam Support — Purchasing Issues:
  https://help.steampowered.com/en/faqs/view/731C-13C7-7D04-A11E
- Riot Games — Changes to Cross-Region VPN Access:
  https://www.riotgames.com/en/news/changes-to-cross-region-vpn-access
- Gaijin Support — How to troubleshoot your connection:
  https://support.gaijin.net/hc/en-us/articles/200071171-How-to-troubleshoot-your-connection

Additional tool research:

- Private Internet Access - Split Tunnel App Examples:
  https://helpdesk.privateinternetaccess.com/hc/en-us/articles/46612003321243-Split-Tunnel-App-Examples
- Surfshark - How to use Bypasser on Windows:
  https://support.surfshark.com/hc/en-us/articles/360017349293-How-to-use-Surfshark-Bypasser-on-Windows
- Mullvad - Split tunneling with the Mullvad app:
  https://mullvad.net/en/help/split-tunneling-with-the-mullvad-app
- ExpressVPN - How to use split tunneling on desktop:
  https://www.expressvpn.com/support/knowledge-hub/split-tunneling-desktop/
- Windscribe - Split Tunneling:
  https://windscribe.com/features/split-tunneling
- Microsoft - Overview: VPN split tunneling for Microsoft 365:
  https://learn.microsoft.com/en-us/microsoft-365/enterprise/microsoft-365-vpn-split-tunnel
