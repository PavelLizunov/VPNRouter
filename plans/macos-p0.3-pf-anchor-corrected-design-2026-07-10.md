# macOS P0.3 PF anchor kill-switch — corrected design + live-verify plan

**Status**: IMPLEMENTED + LIVE-PROVEN 2026-07-10 (ships in v2.47.0-r2). The Drive
handoff (`1y7mtDgi...`) under-specifies the anchor wiring and, as written, ships
a dead kill-switch — both halves now proven empirically on the Mac host
(slovn@192.168.0.246, macOS 26.5.2, NOPASSWD `/sbin/pfctl *` grant active):

## Live proof transcript (2026-07-10, dead-man guarded, SSH survived throughout)

1. **Baseline**: `curl -4 http://1.1.1.1` → 301 (egress up). Live main ruleset =
   stock com.apple + `anchor "amn/*"` (AmneziaWG runtime carrier — NOT in
   /etc/pf.conf; also nat-anchor/rdr-anchor "amn/*" in the nat rules).
2. **INERT proof (handoff shape)**: `pfctl -a com.vpnrouter/killswitch -f <rules>`
   loaded — anchor body verifiably contained `block drop out all` — yet
   `curl http://1.1.1.1` → **301, egress alive**. Anchor rules without a main-
   ruleset carrier are dead. The handoff's D2/D3 command-shape tests would have
   been green on a kill-switch that does not block.
3. **CARRIER proof (corrected shape)**: loaded main = /etc/pf.conf content +
   `anchor "amn/*"` + `anchor "com.vpnrouter/killswitch"` → `curl http://1.1.1.1`
   → **000 / timeout 28 = BLOCKED**; SSH (LAN 192.168/16 + 10/8 passes) stayed
   alive.
4. **FLUSH proof (disable shape)**: `pfctl -a com.vpnrouter/killswitch -F rules`
   → curl → 301 again. Carrier with an empty anchor is inert → normal disable
   never needs to touch the main ruleset.
5. **Restore**: exact pre-test state re-loaded. GOTCHA FOUND: other tools'
   runtime carriers exist in the NAT table too (`nat-anchor "amn/*"`,
   `rdr-anchor "amn/*"` — invisible in `-sr`, only in `-sn`); the first restore
   missed them and a follow-up load with the full section-ordered set
   (scrub → nat → rdr → dummynet → filter, com.apple interleaved with amn)
   repaired it. Amnezia anchor BODIES survive main reloads — only carrier lines
   need re-adding.
6. `pfctl` prints "Use of -f option, could result in flushing..." + "ALTQ
   related functions disabled" to stderr with exit 0 — RunSudo's exit-code
   check is unaffected.

**Implementation** (MacFirewallManager.cs): anchor `com.vpnrouter/killswitch`;
Enable = `-E` token → EnsureCarrier (`-sr` contains-check; if absent load
/etc/pf.conf + carrier) → `-a … -f` body; Disable/DeleteAllRules/Dispose =
`-a … -F rules` + `-X` (main untouched); marker content `anchor-v1` (legacy
`engaged` → old broad restore path, incl. upgraded-mid-engage installs);
legacy broad-load fallback when pf.conf is unreadable; `set block-policy`
dropped from BuildRules (`set` is main-only — would fail the anchor load; drop
is pf's default policy). 21 unit pins in MacFirewallManagerTests.

**Known ceiling (ponytail, documented in code)**: the FIRST engage reloads
pf.conf+carrier, which drops OTHER tools' runtime carrier lines (amn class) —
same event the pre-P0.3 code caused on *every* enable/disable/shutdown, now
once per boot at most, and disable no longer touches main at all. Upgrade path
if a real coexistence report needs it: faithful live reconstruction from
`-sr` + `-sn` (+ dummynet), section-ordered as in step 5 above.

## The gap in the handoff

Handoff target command shape:
```
pfctl -E
pfctl -a com.vpnrouter/killswitch -f <rules-file>     # enable
pfctl -a com.vpnrouter/killswitch -F rules            # disable
```

`pfctl -a NAME -f file` loads rules **into** the named anchor. For pf to
**evaluate** those rules during packet processing, the **main ruleset must call
the anchor** with a carrier line:
```
anchor "com.vpnrouter/killswitch"
```
Stock macOS `/etc/pf.conf` carries only `anchor "com.apple/*"` (+
`load anchor "com.apple" from "/etc/pf.anchors/com.apple"`). Nothing references
`com.vpnrouter`. So `pfctl -a com.vpnrouter/killswitch -f rules` **without** a
carrier = rules sit inert in the anchor, never consulted → egress is NOT blocked
→ a **kill-switch that silently doesn't block**. Unit tests pinning the command
shape (handoff D2/D3) would go green while the switch is dead.

Today's shipped behavior — `pfctl -f <our-rules>` — loads our block rules **as
the main ruleset**, so they ARE evaluated (that's why it works). Cost: it flushes
the existing main ruleset (drops com.apple anchors) until restore = large blast
radius. That's the hygiene P0.3 wants to fix — but not at the cost of correctness.

## Corrected design (blast-radius-reduced AND actually blocks)

The carrier must be present in the evaluated main ruleset. Minimal correct shape:

1. **Enable**
   - `pfctl -sr > <saved-main>` — snapshot current main ruleset rules (for exact
     restore). Also snapshot `pfctl -s Anchors`.
   - Build a main ruleset = `<existing main rules>` + a trailing
     `anchor "com.vpnrouter/killswitch"` carrier line. Load it: `pfctl -f <merged-main>`.
     (This preserves the existing rules AND adds our carrier, instead of blowing
     the ruleset away.)
   - `pfctl -a com.vpnrouter/killswitch -f <killswitch-rules>` — load the actual
     block/pass rules into the (now-carried, now-evaluated) anchor.
   - `pfctl -E` (keep the enable-token handling the handoff already has).
   - Marker content = `anchor-v1` + saved-main path (for restore + migration).
2. **Disable (normal)**
   - `pfctl -a com.vpnrouter/killswitch -F rules` — flush anchor body.
   - Reload the saved main ruleset (restores exact pre-engage state incl. dropping
     our carrier): `pfctl -f <saved-main>`. (Flushing the anchor body alone leaves
     the inert carrier — harmless, but restoring saved-main is cleaner.)
   - `pfctl -X <token>`; delete marker; clear `_loaded`.
3. **Orphan cleanup (marker present, post-crash)**
   - `anchor-v1` marker → `pfctl -a com.vpnrouter/killswitch -F rules` + restore
     saved-main if the path is recorded, else one-time `pfctl -f /etc/pf.conf`.
   - legacy `engaged`/unknown marker → one-time `pfctl -f /etc/pf.conf` (backward
     compat, per handoff change C).

**Trade-off note (ponytail):** snapshotting + merging the main ruleset is more
than the handoff's 2 commands, but it's the minimum that both (a) reduces blast
radius vs today and (b) actually blocks. If the merge proves fragile on a real
Mac, the fallback that's still an improvement is: keep loading our rules as the
main ruleset (current correctness) but ALSO wrap them under the named anchor for
future helper compatibility — no blast-radius win, but no regression. Decide on
the live Mac.

## Mandatory live kill-9 verify (before this counts toward stable)

Mac host: `slovn@192.168.0.246` (AmneziaWG) / tailscale `100.116.97.112`, macOS 26.5.2.

**Lockout guard**: pf is mutated over the same SSH tunnel. Before any
`pfctl -E`/`-f`, schedule a dead-man restore, e.g. a background
`sh -c 'sleep 180; pfctl -a com.vpnrouter/killswitch -F rules; pfctl -f /etc/pf.conf; pfctl -d' &`
(or an `at` job) so a bad ruleset self-heals in 3 min even if SSH drops. Confirm
LAN/loopback pass rules are in the anchor FIRST so the SSH session itself isn't
cut (192.168.x / 10.x must be in the allow set).

Sequence (per handoff "Device/manual macOS smoke"):
1. Read-only pre-check: `pfctl -sr` and `pfctl -s Anchors` — confirm no
   `com.vpnrouter` carrier exists by default (proves the gap empirically).
2. Full-tunnel + block-on-fail; engage kill-switch.
3. `curl` a public IPv4 → must FAIL (blocked). `curl` LAN/router → must succeed.
4. Confirm server-reconnect IP is allowed.
5. Disable → public egress restored.
6. `pfctl -a com.vpnrouter/killswitch -s rules` → empty after disable.
7. Grep logs: no unexpected `/etc/pf.conf` reload on normal enable/disable.

Only after a real public-egress-blocked observation does D2/D3's green count.

## Tests (D1-D9, unit — pin the CORRECTED shape)

Same D1-D9 as the handoff, EXCEPT D2 must also assert the enable flow puts the
carrier `anchor "com.vpnrouter/killswitch"` into the loaded main ruleset (not
just `-a ... -f`), so a future refactor can't regress back to the inert shape.

## Files
- `VPNRouter.Core/Platform/macOS/MacFirewallManager.cs`
- `VPNRouter.Tests/MacFirewallManagerTests.cs`
- Ref: `VPNRouter.Core/Platform/Linux/LinuxFirewallManager.cs`,
  `plans/phase3-macos-pf-killswitch-r6-design-2026-06-04.md`
