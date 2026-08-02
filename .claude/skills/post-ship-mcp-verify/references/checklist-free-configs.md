# Free Configs verification (windows-brat)

Use for public-pool, refresh, source, parsing, import, and Free Configs UI changes. Set `$v`.

1. Navigate visually to Free Configs and capture the initial plus bottom-of-viewport state. If navigation or status text has no explicit selector, record `selector hardening: future work`.

2. Read the current RU refresh/action Name from the UI. Inspect then invoke it semantically; do not invent an AutomationId or translation:

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "<current RU refresh/action Name>" -ControlType Button -UiaOperation Inspect
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "<current RU refresh/action Name>" -ControlType Button -UiaOperation Invoke -TimeoutSeconds 120
```

3. Screenshot the completed result and verify source/status text, non-empty rows when the source succeeds, no duplicate/blank entries, and every changed row action. For offline/source failure releases, verify the exact intended error and preservation behavior instead of requiring rows.

4. If release notes change import/apply behavior, invoke the current proven RU action and verify the resulting selected config without exposing server secrets in screenshots or the report.

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action screenshot -LocalOutput "artifacts/brat-verify/$v/free-configs-final.png" -TimeoutSeconds 120
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action logs
```

Pass only if the complete refresh/result/import scenario matches release notes, the viewport bottom is captured, secrets are redacted, and remote logs are clean.
