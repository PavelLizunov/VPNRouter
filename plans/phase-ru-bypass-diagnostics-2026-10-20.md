# Repeated Russian-bypass report - 2026-10-20

## Task and publication state

The owner provided diagnostics after approving the stable cut. Diagnose the
reported recurrence before continuing the still-authorized release. Stable PR
#610 is now draft, not merged; no stable 2.50.5 tag or release exists. Latest
remains 2.50.4, published candidate remains 2.50.5-r3.

## Observed evidence

Source: owner-provided diagnostics generated at 01:25:06 +03:00. No raw bundle
or browsing history is committed. Summary says app 2.50.5-r3, connected, one
core process, full mode, subscription source. Settings have bypass enabled;
current.json direct rule contains both vpnrouter-geosite-ru and
vpnrouter-geoip-ru before UDP proxy routing and proxy final. No custom rules.
Geo files are present, dated April 7. This does not prove current rule-set
coverage or content correctness, but the running core accepted the config.

The last core start was at 23:31:14, connected at 23:31:16. No later apply or
restart is present in the exported app logs through 01:25. Correlated DNS
answers and outbound destinations in the current core log show:

- 01:24:45: www.avito.ru resolves to 176.114.124.24; the connection to that
  address uses outbound/direct[direct].
- 01:24:51: 2ip.io resolves to 188.40.167.81; its connection uses
  outbound/vless[proxy].
- Earlier 21:52:08: 2ip.ru at 188.40.167.82 uses direct; 21:52:09:
  2ip.io at 188.40.167.81 uses proxy.

Thus the export does not establish a session-wide loss of bypass. It does
establish different routes for the RU site and the .io IP checker. A site's
language/ownership does not imply that its domain/IP belongs to the RU sets.
Do not silently add public IP-check services or all shared foreign CDN IPs to
bypass. Foreign CAPTCHA resources also use proxy; causality for the owner's
reported symptom is not established by this alone.

## Verification and limits

Bounded analysis read settings, current config, lifecycle and correlated
DNS/outbound records through export time. No speculative product edit or
new claim of resolution. Asked the owner for the exact site and displayed IP,
and whether 2ip.io was the checker. Without the URL there is no specific
traffic target for a finite reproduction. No direct access to the owner's
Windows device is configured in this session.

The separate mandatory stable-upgrade precondition completed on WINBRAT:
2.50.4 -> 2.50.5-r3 helper PASS, exact version/relaunch/receipt PASS, two cold
cycles PASS, lifecycle 172 entries/zero error/fatal/unknown errors, Cleanup PASS.
This does not resolve or disprove the owner's reported site-specific problem.

Outcome: awaiting exact failing site/IP; stable publication paused, not revoked.
