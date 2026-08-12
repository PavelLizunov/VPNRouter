# Localization verification (windows-brat)

Use for Strings.cs/localization-only changes. Set `$v`. This is visual and semantic verification, not a translation rewrite.

1. Keep WINBRAT in the locale named by the release notes. Render every changed
page/dialog at the top and bottom of each viewport through isolated headless
screenshot cases.

2. For each changed interactive label with a proven runtime accessibility Name, assert it through UIA:

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "<exact current localized Name>" -UiaOperation Inspect
```

Never invent an AutomationId or translate a string from memory. TextBlocks are
verified in isolated renders; changed interactive controls require semantic
selectors or the checklist fails.

3. Check exact strings, interpolation values, wrapping at the normal narrow window size, clipped/overflowing text, mixed-language fragments, untranslated resource keys, and every interactive control in the changed scope. Open the real dialog/state when a string is conditional.

```powershell
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~PageScreenshotTests|FullyQualifiedName~VisualDiffTests"
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action logs
```

Pass only if every release-note string is covered in its isolated render and
live semantic state, the viewport bottom is covered, no clipping/mixed
locale/resource key appears, and remote logs are clean.
