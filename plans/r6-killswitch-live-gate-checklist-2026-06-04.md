# r6 macOS pf kill-switch — live gate checklist (2026-06-04)

The kill-switch is brick-risk, so before any stable cut it must be proven on the
real Mac: it blocks leaks, lets sing-box reconnect, and never bricks the network.
Only ONE step needs Pavel (the GUI admin prompt I can't click over SSH); I do the
rest via SSH on `slovn@192.168.0.246`.

## Recovery (keep this handy — un-bricks instantly if anything goes wrong)
```
sudo pfctl -d          # disables pf entirely → all rules off → connectivity back
# or:  sudo pfctl -F all && sudo pfctl -f /etc/pf.conf
```

## Pavel does on the Mac (GUI, ~2 min)
1. **Update to r6.** Open VPNRouter → Settings → make sure the update channel is
   **Experimental** → Check for updates → install **v2.41.0-r6** → relaunch.
   (Verify: About/Settings shows `2.41.0-r6`.)
2. **Grant pfctl (the one step only you can do).** Connect once. macOS shows a
   one-time admin prompt ("VPNRouter wants to make changes") — enter your Mac
   password. This writes the pfctl sudoers grant. (It re-prompts once because r6
   bumped the sudoers marker; after this it never prompts again.)
3. **Arm the kill-switch.** Set routing = **Full Tunnel** and enable
   **block_on_vpn_fail** ("block traffic if VPN fails"). Reconnect.
   - If you can't find the block_on_vpn_fail toggle in the UI, tell me — I'll set
     it in `config.yaml` over SSH and you just click Connect.
4. **Leave it connected** and ping me ("готово, подключён").

## Then I do (SSH — no further input from you)
1. Confirm the grant: `sudo -n /sbin/pfctl -s info` returns without a password.
2. Confirm armed: `vpnrouter*.log` shows `[MacFirewall] Armed full-tunnel pf
   kill-switch`.
3. Baseline: note `pfctl -s rules` (stock), confirm internet works.
4. **kill -9 the tunnel mid-session:** `sudo kill -9 $(pgrep sing-box)`.
5. **Verify the block window** (within HealthMonitor's restart backoff):
   - a public host is BLOCKED: `curl --max-time 4 https://1.1.1.1` fails;
   - the VPN server IP is still reachable (the pass rule);
   - `[MacFirewall] pf kill-switch ENGAGED` in the log;
   - `pfctl -s rules` shows `block drop out all` + the server pass.
6. **Verify reconnect + auto-lift:** HealthMonitor restarts sing-box, it
   reconnects through the server-IP pass, `[MacFirewall] pf kill-switch lifted`,
   internet returns. (This is the no-brick proof — the server pass is what lets
   recovery happen.)
7. **Verify clean teardown:** Stop the VPN → `pfctl -a com... -s rules` empty /
   default ruleset restored / connectivity normal.
8. **Verify anti-brick on abrupt exit:** `pkill -9 VPNRouter` while blocked →
   Dispose/process-exit flush restores the default ruleset (or, worst case, the
   recovery one-liner above).
9. Report PASS/FAIL per step with log excerpts.

## Gate outcome
- ALL pass → r6 kill-switch is trustworthy; eligible for stable (with your cut
  command + the standard live-update gate).
- ANY fail → I fix + ship r7, re-run this gate. r6 stays default-OFF meanwhile,
  so no shipped user is exposed.
