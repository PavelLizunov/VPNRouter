# Free Configs verification (windows-brat)

Use for public-pool, refresh, source, parsing, import, and Free Configs UI changes. Set `$v`.

1. Render Free Configs initial/result states and viewport bottom through isolated
headless screenshot cases. Do not load live subscription/server data.

2. Read the current RU refresh/action Name from the UI. Inspect then invoke it semantically; do not invent an AutomationId or translation:

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "<current RU refresh/action Name>" -ControlType Button -UiaOperation Inspect
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "<current RU refresh/action Name>" -ControlType Button -UiaOperation Invoke -TimeoutSeconds 120
```

3. Use isolated synthetic rows to verify source/status text, duplicates/blanks
and row layout. Live refresh completion and every changed row action require
semantic UIA; source failure must preserve existing data as specified.

4. If release notes change import/apply behavior, invoke the current proven RU
action and verify the resulting state semantically without exposing servers.

```powershell
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~PageScreenshotTests|FullyQualifiedName~VisualDiffTests"
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action logs
```

Pass only if the complete refresh/result/import scenario matches release notes,
the isolated viewport bottom is covered, no secrets leave WINBRAT and remote
logs are clean.
