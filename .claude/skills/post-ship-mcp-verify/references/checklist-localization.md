# Localization verification (windows-brat)

Use for Strings.cs/localization-only changes. Set `$v`. This is visual and semantic verification, not a translation rewrite.

1. Keep WINBRAT in the locale named by the release notes. Visit every changed page/dialog and capture the top and bottom of each viewport.

2. For each changed interactive label with a proven runtime accessibility Name, assert it through UIA:

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "<exact current localized Name>" -UiaOperation Inspect
```

Never invent an AutomationId or translate a string from memory. TextBlocks and navigation without stable semantic selectors are verified from screenshots and recorded as `selector hardening: future work`.

3. Check exact strings, interpolation values, wrapping at the normal narrow window size, clipped/overflowing text, mixed-language fragments, untranslated resource keys, and every interactive control in the changed scope. Open the real dialog/state when a string is conditional.

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action screenshot -LocalOutput "artifacts/brat-verify/$v/localization-final.png"
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action logs
```

Pass only if every release-note string is visible in its real state, the viewport bottom is captured, no clipping/mixed locale/resource key appears, and remote logs are clean. Attach all page-specific screenshots, not only the final one.
