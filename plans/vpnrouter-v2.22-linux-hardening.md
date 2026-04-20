# VPNRouter — Roadmap v2.22.x "Linux hardening + storage cleanup"

**Baseline**: v2.21.9 prerelease. Linux BETA shipping (AppImage + .deb +
tar.gz) with pkexec elevation, but two issues keep reappearing and need
a careful, definitive fix:

1. **Split tunnel apply still fails** with `Profile 'Messengers' not found`
   even after v2.21.9 aligned profile catalogues and made engine load
   the Linux variant.
2. **Auto-update button doesn't apply** on Linux — user can download
   repeatedly; no visible feedback.

Plus housekeeping: **189 releases** accumulated on GitHub (pre-Mac-era
v1.x back to v1.11.x). Worth pruning — public-repo soft limit is ~5 GiB
of artifact storage and each release holds 10–50 MiB, cumulative 4–6 GiB.

---

## Bug #1 — "Profile 'Messengers' not found" won't die

### What I already tried
- v2.21.6: created `default-linux.json` with Unix-style names
- v2.21.9: aligned `VpnEngine.BuildProfileSources` to load platform variant;
  renamed `Discord` → `Discord_Privacy` so `SimpleSplitProfile` names line up

### Why it still fails
`SimpleSplitProfile = "Browsers,Discord_Privacy,Work_Suite"` doesn't
reference `Messengers` — so `Messengers` must be coming from **somewhere
else**. Three candidates, in order of likelihood:

1. **User's persisted `_settings.ActiveProfile` in `~/.config/vpnrouter/config.yaml`**
   was set to `Messengers` at some earlier point (e.g. user opened
   Applications tab, clicked `Messengers` group, that saved ActiveProfile).
   After my renames, the Catalog no longer has the name, but the yaml
   still does. ApplyAsync looks up `_settings.ActiveProfile`, splits on
   comma, tries to resolve each name → first miss throws.

2. **`CustomCategories` or `CustomGroupApps`** in yaml reference a
   `Messengers` group. ApplyAsync's inject-custom-categories block might
   fail if a referenced group is gone from the catalogue.

3. **ProfileManager.MergeProfiles** throws on first missing name rather
   than warning + skipping.

### Fix plan

**Step 1.1 — Tolerant profile resolution (VpnEngine + ProfileManager)**
Missing profiles become **warnings, not exceptions**. If the user requests
`"Browsers,Discord_Privacy,Messengers"` and `Messengers` isn't in the
catalogue, we log:

```
[VpnEngine] Profile 'Messengers' not found in catalogue — skipping.
            Continuing with: Browsers, Discord_Privacy.
```

And proceed with the two that did resolve. If ALL three miss → then
throw (genuinely broken config).

**Step 1.2 — Migrate ActiveProfile on load**
In `SettingsLoader.Load` (or its post-load hook), resolve
`_settings.ActiveProfile` against the available catalogue and rewrite it
in-place with only the names that exist. This heals any stale yaml
automatically — user never sees the error a second time.

**Step 1.3 — Align profile catalogues across all 3 defaults**
Make `default.json` / `default-macos.json` / `default-linux.json` use
the **same group names** wherever the concept is the same:

| Purpose | Name (all platforms) |
|---|---|
| Discord voice/chat | `Discord_Privacy` |
| Telegram / Signal / WhatsApp | `Messengers` |
| Chrome / Firefox / etc | `Browsers` |
| Slack / Zoom / VS Code / work apps | `Work_Suite` |
| Steam / game launchers | `Gaming` |
| Spotify / media | `Streaming` |
| Shell / git / node / python | `Privacy_Shell` (rename Win's `Terminal`) |

Inside each group the `processes[]` differ by platform (`chrome.exe`
vs `chrome` vs `chromium-browser`), but the **group names must be
stable** across platforms so `SimpleSplitProfile` and any user-saved
`ActiveProfile` work portably.

**Step 1.4 — Manual verification checklist**
Ship v2.22.0 with a boot-time log line dumping the catalog:

```
[VpnEngine] Loaded profile catalogue: Discord_Privacy, Messengers,
            AI_Tools, Browsers, Work_Suite, Streaming, Gaming,
            Privacy_Shell
```

Easy to eyeball from `~/.config/vpnrouter/logs/vpnrouter*.log` if
anything still looks off.

### Acceptance
- [ ] Paste `Messengers,UnknownXyz,Browsers` as ActiveProfile → Apply
  succeeds using just `Messengers,Browsers`; log shows the
  `UnknownXyz` warning.
- [ ] After Apply, ActiveProfile in yaml is rewritten to the two
  valid names.
- [ ] All 3 defaults enumerate the same 8 group names.
- [ ] `SimpleSplitProfile = "Browsers,Discord_Privacy,Work_Suite"`
  resolves cleanly on Win/Mac/Linux.

---

## Bug #2 — Auto-update on Linux does nothing visible

### What's shipped now
v2.21.5 rewrote `ApplyUpdateLinux` synchronously + returns errors.
v2.21.8 fixed the tar.gz extraction (was using ZipFile).

### Why it still doesn't work
Symptoms per user: click Update → new download → still old version
next run. No message visible. Download apparently succeeds (we can
download again, so extraction-to-stage didn't fail enough to block).

Likely chain of failures:
1. `cp -rfT` with `pkexec` returns 0 but polkit may have dismissed
   silently if no session keyring was active → `pkexec` returned
   non-zero, we throw, but the exception might not bubble into the
   update banner clearly enough.
2. OR `cp` succeeds but `Process.Start(new VPNRouter.App)` fails
   silently (missing +x on new binary, missing sing-box dependency)
   and the process we already killed doesn't come back.
3. OR `Environment.Exit(0)` happens too fast — the child hasn't even
   finished execve'ing when we die, and shells don't parent
   zombie-safely on ultra-short-lived child.

### Fix plan

**Step 2.1 — Dump everything to a user-visible update log**
New file `~/.config/vpnrouter/logs/update.log` written by ApplyUpdateLinux
(via Serilog or plain StreamWriter). Every step + every subprocess exit
code + stderr. Next app launch can read this and surface it in the UI
("Previous update attempt failed — details") if it's non-empty and the
version didn't change.

**Step 2.2 — Verify the new binary launched before exit**
After `Process.Start(new VPNRouter.App)`, wait 2 s, then call
`Process.GetProcessById(newPid).Responding`. If the new process died
(ExitCode != null), throw with the actual error. If it's alive →
Environment.Exit(0) normally.

**Step 2.3 — Handle pkexec edge cases explicitly**
- Exit 126 (auth dismissed): UI "Authentication cancelled."
- Exit 127 (no polkit agent): UI "No polkit auth agent — install
  `policykit-1` and retry."
- Other non-zero: UI shows stderr first 200 chars.

Already partially done in v2.21.5 — extend to cover chmod + verify
steps, not just cp.

**Step 2.4 — Belt-and-braces: write an install receipt**
After successful apply, write `~/.config/vpnrouter/.update-installed-version`
= new version. On next launch, if that file exists and != current
AppVersion, something went wrong — show a clear banner.

**Step 2.5 — AppImage path still deferred**
Explicit throw + helpful message kept as v2.21.5 shipped. Full
AppImage self-update via zsync is out of scope for v2.22.

### Acceptance
- [ ] User on .deb clicks Update → sees either "Updated to vX.Y.Z"
  OR a clear error message with a specific cause.
- [ ] `~/.config/vpnrouter/logs/update.log` contains step-by-step
  record after any attempt.
- [ ] After successful update, new binary is running (verified by
  version in subheader).

---

## Housekeeping — GitHub releases pruning

### Current state
189 releases on GitHub. Rough sizes:
- v1.0–v1.23 (pre-Mac era, 2026-02 to 2026-03): ~80 releases × 15 MB
  = ~1.2 GB
- v2.0–v2.9 (Mac intro, early Arctic): ~40 releases × 30 MB = ~1.2 GB
- v2.10–v2.17 (stable series): ~50 releases × 40 MB = ~2 GB
- v2.18–v2.21 (recent, active): ~20 releases × 50 MB = ~1 GB
- Total: ~5.4 GB

Public-repo artifact limit is **5 GiB soft** — we're at the edge.

### What to delete
Delete everything **older than v2.10.0** (pre-Mac-stable era). Those
releases:
- Used a different app architecture (WinForms GUI, pre-Avalonia)
- Windows-only, no Mac / Linux builds
- Auto-updater from those versions points at v1.x → can't upgrade
  directly to 2.x anyway (users must reinstall)
- No one should downgrade there

That's roughly **v1.0 through v2.9.x** — about **120 releases**.

### What to keep
- v2.10.x onwards (Avalonia era, macOS support landed)
- All prereleases in the current v2.18–v2.21 cycle
- v2.16.7 (last stable Arctic theme baseline)
- v2.20.6 (previous stable Latest)

Target after cleanup: ~70 releases, ~3 GB total. Healthy margin.

### How
```bash
# Preview what would be deleted:
gh release list --limit 200 --json tagName --jq '.[] | .tagName' \
  | awk -F'v' '{ split($2, p, "."); if (p[1] < 2 || (p[1] == 2 && p[2] < 10)) print $0 }'

# Delete + don't touch tags (keep git history intact):
for tag in $(...above list...); do
    gh release delete "$tag" --yes --repo PavelLizunov/VPNRouter
done
```

Tags stay in git. If anyone actually needs to reproduce a v1.x build,
checkout the tag + build locally.

### Acceptance
- [ ] `gh release list --limit 10` shows only v2.10+ releases
- [ ] Storage footprint reduced by ~2 GB
- [ ] Current Latest + Prereleases unchanged

---

## Release sequence

- **v2.22.0** — Bug #1 (tolerant resolver + migration + catalogue
  alignment). Highest-priority since it blocks Split on Linux.
- **v2.22.1** — Bug #2 (update log + verify + receipt). Needs
  testing on user's Mint install to verify pkexec flow.
- **Repo cleanup** — orthogonal, can happen any time. Recommend
  immediately after v2.22.0 ships so the repo headroom is healthy.

## Status tracker
- [ ] v2.22.0 — Profile resolver + catalogue alignment + migration
- [ ] v2.22.1 — Update pipeline hardening
- [ ] Prune v1.0–v2.9.x GitHub releases

---

## References
- `plans/vpnrouter-linux-port-research.md` — original Linux port plan
- `plans/vpnrouter-v2.20-batch-fixes.md` — audit methodology used
  last time
- `profiles/default-linux.json` — current Linux catalogue (v2.21.9)
- `VPNRouter.Core/Services/VpnEngine.cs` `BuildProfileSources` —
  entry point for profile loading
- `VPNRouter.Core/Services/UpdateChecker.cs` `ApplyUpdateLinux` —
  current Linux updater
