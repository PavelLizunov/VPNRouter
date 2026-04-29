# Sample Rules — Import test files

Three example rule sets in the three formats VPNRouter supports.
Use Network → Rules → Import to load any of these.

## Files

| File | Format | Notes |
|---|---|---|
| `example-rules.csv` | CSV | `action,type,value,comment,enabled` header. Easy spreadsheet edits. |
| `example-rules.json` | VPNRouter JSON | Native format — full fidelity, lossless round-trip. |
| `example-rules-singbox.json` | sing-box JSON | Compatible with NekoBox / Hiddify exports. Multi-match rules expand into individual entries. |

## Rules covered

12 rules across 3 actions and 6 match types — designed to exercise the
full schema:

* `direct` — `.corp.local` (domain_suffix), `100.64.0.0/10` (CGNAT
  ip_cidr), `sberbank` (domain_keyword), `.gosuslugi.ru` (RU gov)
* `proxy` — `.youtube.com`, `.telegram.org`, `.openai.com`, `discord`
  (process_name)
* `block` — `doubleclick`, `googlesyndication` (ad networks),
  `ads` (geosite tag), one regex example (disabled)

## Test flow

1. Open VPNRouter → Network → Rules.
2. (Optional) Bulk → Clear all to start clean.
3. Click Import → choose a sample file.
4. Verify all rules appear with correct action / type / value / comment.
5. Toggle a couple, search, sort, switch view modes (Cards / Read /
   Edit) to confirm everything renders.
6. Optionally Apply VPN with rules active to verify the runtime
   behavior.

## Round-trip verification

Each format should round-trip cleanly:
1. Import `example-rules.csv` → Cards view shows 12 rules.
2. Export → CSV → diff with `example-rules.csv` → only ordering /
   comment-encoding may differ.
3. Same for JSON variants.

## sing-box import notes

The sing-box JSON sample has one rule with `process_name: ["discord",
"discord.exe"]` — both Mac/Linux and Windows naming. VPNRouter import
expands this into two `proxy + process_name` rules (one per name).
This is by design: sing-box matchers are case-sensitive lists; the
expansion preserves intent without ambiguity.
