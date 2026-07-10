# macOS P0.3 PF anchor kill-switch — corrected design + live-verify plan

**Status**: deferred from v2.47.0-r1 to -r2. Reason: the Drive handoff
(`1y7mtDgi...`) under-specifies the anchor wiring and, as written, ships a dead
kill-switch. This doc records the correction so the -r2 Mac session implements
it right and live-proves it.

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
