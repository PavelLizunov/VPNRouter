#!/usr/bin/env pwsh
# Parse Android programmatic UI (C# code-behind) into a flat element
# catalog. Approximate — first-pass regex over `new Type {…}` patterns.
#
# Output: catalog/android-elements.json
#
# Section detection: each `new Type` is attributed to the closest enclosing
# `private … Build…()` method above it (or the surrounding region marker).

$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$androidDir = Resolve-Path (Join-Path $here "..\..\VPNRouter.Android")
$out = Join-Path $here "android-elements.json"

# Map BuildXxx() methods → canonical page names. A given Build* may belong to
# more than one page (e.g. BuildAppRow lives inside the Apps overlay), so we
# choose the highest-level page the method participates in.
$methodToPage = @{
    "BuildSimplePageView"          = "Simple"
    "BuildLogOverlay"              = "Tools"
    "BuildSettingsOverlay"         = "Network"
    "BuildSettingsRoutingSection"  = "Network"
    "BuildSettingsLeakSection"     = "Network"
    "BuildSettingsReliabilitySection"= "Network"
    "BuildSettingsUpdatesSection"  = "Network"
    "BuildSettingsAutostartSection"= "Network"
    "BuildDpiBypassCard"           = "DpiBypass"
    "BuildProfilesOverlay"         = "Network"
    "BuildProfileCard"             = "Network"
    "BuildProfileChip"             = "Network"
    "BuildAppPickerOverlay"        = "Applications"
    "BuildAppRow"                  = "Applications"
    "BuildSubsOverlay"             = "Subscribe"
    "BuildSubCard"                 = "Subscribe"
    "BuildServerListOverlay"       = "Servers"
    "BuildServerRow"               = "Servers"
    "BuildFreeConfigsOverlay"      = "FreeConfigs"
    "BuildAdvancedSettingsPanel"   = "FreeConfigs"
    "BuildFcListHeader"            = "FreeConfigs"
    "BuildFcRow"                   = "FreeConfigs"
    "BuildUpdateBanner"            = "AutoUpdate"
    "ShowExportStatus"             = "ConfigShare"
    "ShowImportStatus"             = "ConfigShare"
    "ShowQrStatus"                 = "ConfigShare"
}

# Element types we look for. Mirror the desktop list.
$tagsOfInterest = @(
    "Border", "TextBlock", "Button", "TextBox", "RadioButton", "CheckBox",
    "Slider", "ComboBox", "ComboBoxItem", "Ellipse", "ListBox", "ListBoxItem",
    "ScrollViewer", "Grid", "StackPanel", "Image", "Path", "Rectangle",
    "ToggleSwitch", "Expander", "Separator", "ProgressBar",
    "TabControl", "TabItem", "Menu", "MenuItem", "ContextMenu", "Popup",
    "Avalonia.Controls.RadioButton", "Avalonia.Controls.CheckBox"
)

$result = [ordered]@{
    schema_version = 1
    generated_at = (Get-Date).ToString("o")
    source = "VPNRouter.Android/AndroidApp*.cs"
    pages = @{}
}

$totalElements = 0

Get-ChildItem -Path $androidDir -Filter "AndroidApp*.cs" | Sort-Object Name | ForEach-Object {
    $file = $_.FullName
    Write-Host "  parsing $($_.Name) ..."
    $lines = Get-Content -Path $file
    $currentMethod = "_File_"
    $methodStartLine = 0
    $braceDepth = 0
    $inMethod = $false

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $ln = $lines[$i]

        # Detect method entry — match "private … Identifier(" or "public … Identifier("
        if ($ln -match "(?:private|public|internal|protected)\s+(?:static\s+)?(?:async\s+)?(?:[A-Za-z_<>?,\.\s]+\s+)?([A-Za-z_][A-Za-z_0-9]*)\s*\(") {
            $methodName = $Matches[1]
            # Skip event-handler signatures we don't care about
            if ($methodName -match "^(On|Get|Set|Try|Format|Parse|Render|Compute|Build[A-Z][A-Za-z]*|Show[A-Z][A-Za-z]*|Append[A-Z][A-Za-z]*|Wrap[A-Z][A-Za-z]*)") {
                $currentMethod = $methodName
                $methodStartLine = $i + 1
            }
        }

        # Element instantiation pattern. Match either:
        #   `new TypeName {` or `new TypeName(` on the same line
        #   `new TypeName` at end of line (Avalonia code uses `new Border\n{`)
        if ($ln -match "new\s+([A-Za-z_][A-Za-z_0-9\.]*)\s*(?:[\{\(]|$)") {
            $type = $Matches[1]
            # Strip namespace prefix (e.g., Avalonia.Controls.RadioButton → RadioButton)
            $shortType = $type.Split(".")[-1]
            if ($tagsOfInterest -notcontains $shortType -and
                $tagsOfInterest -notcontains $type) { continue }

            # Look ahead for Text/Content/Name/AutomationProperties.Name within
            # the next 30 lines (keeps element block scope reasonable).
            $text = $null
            $name = $null
            $automation = $null
            $tagPropFromObj = $null
            $childTextHint = $null
            $look = [Math]::Min($lines.Count - 1, $i + 30)
            for ($j = $i; $j -le $look; $j++) {
                $ll = $lines[$j]
                if (-not $text -and $ll -match 'Text\s*=\s*"([^"]+)"') { $text = $Matches[1] }
                if (-not $text -and $ll -match 'Content\s*=\s*"([^"]+)"') { $text = $Matches[1] }
                if (-not $text -and $ll -match 'Watermark\s*=\s*"([^"]+)"') { $text = $Matches[1] }
                if (-not $name -and $ll -match '\bName\s*=\s*"([^"]+)"') { $name = $Matches[1] }
                if (-not $automation -and $ll -match 'AutomationProperties\.SetName\([^,]+,\s*"([^"]+)"') { $automation = $Matches[1] }
                if (-not $automation -and $ll -match 'AutomationProperties\.NameProperty[^"]*"([^"]+)"') { $automation = $Matches[1] }
                if (-not $tagPropFromObj -and $ll -match 'Tag\s*=\s*"([^"]+)"') { $tagPropFromObj = $Matches[1] }
                # Stop when we hit a clear element boundary — closing brace at
                # outer indent level. Heuristic: blank line followed by next
                # `var something = new …`. Cheap approximation: stop on a
                # line that is exactly "};" at column 8 or less.
                if ($j -gt $i -and $ll -match '^\s{0,16}\};') { break }
            }

            # Resolve page for this method
            $page = $methodToPage[$currentMethod]
            if (-not $page) { $page = "_Unknown_" }

            if (-not $result.pages.ContainsKey($page)) {
                $result.pages[$page] = @()
            }

            $element = [ordered]@{
                type = $shortType
                name = $name
                automation_name = $automation
                text = $text
                tag = $tagPropFromObj
                method = $currentMethod
                file = $_.Name
                line = $i + 1
            }
            $result.pages[$page] += $element
            $totalElements++
        }
    }
}

# Reshape pages dict → array matching desktop schema
$pagesArr = @()
foreach ($k in ($result.pages.Keys | Sort-Object)) {
    $els = $result.pages[$k]
    $pagesArr += [ordered]@{
        page = $k
        element_count = $els.Count
        elements = $els
    }
}
$result.pages = $pagesArr

$json = $result | ConvertTo-Json -Depth 10
Set-Content -Path $out -Value $json -Encoding utf8
Write-Host ""
Write-Host "Wrote $out"
Write-Host "  pages   = $($result.pages.Count)"
Write-Host "  elements= $totalElements"
