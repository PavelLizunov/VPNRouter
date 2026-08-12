# Network, Settings, and Apps verification (windows-brat)

Use for autostart, lockdown, custom routes/rules, app include/exclude, and settings changes. Set `$v`.

1. Render each changed page at its normal and narrow viewport through isolated
`PageScreenshotTests`; include the viewport bottom and synthetic control state.

2. For each changed control, read its current RU accessibility Name from the UI, run `Inspect`, then use the matching semantic operation (`Toggle`, `Invoke`, or `SetValue`). Never guess a Name or AutomationId.

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "<proven current RU Name>" -ControlType CheckBox -UiaOperation Inspect
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "<proven current RU Name>" -ControlType CheckBox -UiaOperation Toggle
```

Apps include/exclude controls expose bound accessibility names in current XAML;
use their runtime RU values. A changed live control without a semantic selector
fails the checklist until the product exposes one.

3. Verify persistence by leaving and returning to the page. For routing/lockdown changes, run only the release-note scenario on WINBRAT and confirm the visible final state; never touch dev-box networking.

```powershell
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~PageScreenshotTests|FullyQualifiedName~VisualDiffTests"
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action logs
```

Pass only if every changed control is reachable, mutation and persistence match the release notes, the viewport bottom is captured, and remote logs are clean.
