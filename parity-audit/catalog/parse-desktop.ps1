#!/usr/bin/env pwsh
# Parse Avalonia XAML page files into a flat element catalog (JSON).
#
# Output: catalog/desktop-elements.json — list of {page, type, name,
# automation_name, text, content, line} entries.
#
# This is M8 (UI element catalog) of plans/vpnrouter-parity-audit-plan.md.

$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$pagesDir = Resolve-Path (Join-Path $here "..\..\VPNRouter.App\Views\Pages")
$out = Join-Path $here "desktop-elements.json"

# Element types we care about for parity diffing.
$tagsOfInterest = @(
    "Border", "TextBlock", "Button", "TextBox", "RadioButton", "CheckBox",
    "Slider", "ComboBox", "ComboBoxItem", "Ellipse", "ListBox", "ListBoxItem",
    "ScrollViewer", "Grid", "StackPanel", "Image", "Path", "Rectangle",
    "ToggleSwitch", "Expander", "Separator", "ProgressBar", "Slider",
    "TabControl", "TabItem", "Menu", "MenuItem", "ContextMenu", "Popup"
)

# Helper: pull primary text from an XML element. Tries Text, Content,
# Watermark, Header, Title, Tag attributes.
function Get-PrimaryText([System.Xml.XmlElement]$el) {
    foreach ($attr in @("Text", "Content", "Watermark", "Header", "Title")) {
        $v = $el.GetAttribute($attr)
        if ($v) { return $v }
    }
    # Inline content (e.g. <Button>Connect</Button>)
    if ($el.HasChildNodes) {
        $textNode = $el.ChildNodes | Where-Object { $_.NodeType -eq "Text" } | Select-Object -First 1
        if ($textNode) { return $textNode.Value.Trim() }
    }
    return $null
}

function Get-AttrAny([System.Xml.XmlElement]$el, [string[]]$names) {
    foreach ($n in $names) {
        $v = $el.GetAttribute($n)
        if ($v) { return $v }
    }
    return $null
}

# Walk file as text once to build line lookup. Avalonia XAML often has the
# tag name on its own line, so a simple line index lets us cite.
function Build-LineIndex([string]$text) {
    $lines = $text -split "`n"
    $idx = @{}
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i].TrimStart()
        if ($line -match "^<([A-Za-z]+)") {
            $tag = $Matches[1]
            if (-not $idx.ContainsKey($tag)) { $idx[$tag] = @() }
            $idx[$tag] += ($i + 1)
        }
    }
    return $idx
}

$result = [ordered]@{
    schema_version = 1
    generated_at = (Get-Date).ToString("o")
    source = "VPNRouter.App/Views/Pages/*.axaml"
    pages = @()
}

$totalElements = 0

Get-ChildItem -Path $pagesDir -Filter "*.axaml" | Sort-Object Name | ForEach-Object {
    $file = $_.FullName
    $page = $_.BaseName -replace "Page$", ""
    Write-Host "  parsing $($_.Name) ..."

    $raw = Get-Content -Path $file -Raw
    [xml]$xml = $raw

    $lineIndex = Build-LineIndex $raw
    $tagCounter = @{}

    $elements = @()

    # XmlNamespaceManager
    $ns = New-Object System.Xml.XmlNamespaceManager $xml.NameTable
    $ns.AddNamespace("a", "https://github.com/avaloniaui")
    $ns.AddNamespace("x", "http://schemas.microsoft.com/winfx/2006/xaml")

    foreach ($tag in $tagsOfInterest) {
        $nodes = $xml.SelectNodes("//a:$tag", $ns)
        if (-not $nodes -or $nodes.Count -eq 0) { continue }

        for ($i = 0; $i -lt $nodes.Count; $i++) {
            $n = $nodes[$i]
            $idx = $tagCounter[$tag]
            if (-not $idx) { $idx = 0 }
            $tagCounter[$tag] = $idx + 1

            # Approximate line via line index lookup
            $lineCandidates = $lineIndex[$tag]
            $line = $null
            if ($lineCandidates -and $idx -lt $lineCandidates.Count) {
                $line = $lineCandidates[$idx]
            }

            $name = Get-AttrAny $n @("x:Name", "Name")
            $automation = $n.GetAttribute("AutomationProperties.Name")
            $command = $n.GetAttribute("Command")
            $classes = $n.GetAttribute("Classes")
            $text = Get-PrimaryText $n

            # Strip Avalonia binding noise from text/automation values
            $cleanText = $null
            if ($text) {
                if ($text -match '^\{Binding\s+([A-Za-z_][A-Za-z0-9_]*)') {
                    $cleanText = "{Binding " + $Matches[1] + "}"
                } elseif ($text -match '^\{StaticResource\s+([A-Za-z_][A-Za-z0-9_]*)') {
                    $cleanText = "{StaticResource " + $Matches[1] + "}"
                } elseif ($text -match '^\{') {
                    # Other markup ext — keep first 60 chars
                    $cleanText = $text.Substring(0, [Math]::Min(60, $text.Length))
                } else {
                    $cleanText = $text
                }
            }

            $element = [ordered]@{
                type = $tag
                name = $name
                automation_name = $automation
                command = $command
                classes = $classes
                text = $cleanText
                line = $line
            }
            $elements += $element
        }
    }

    $result.pages += [ordered]@{
        page = $page
        file = "VPNRouter.App/Views/Pages/$($_.Name)"
        element_count = $elements.Count
        elements = $elements
    }
    $totalElements += $elements.Count
}

$json = $result | ConvertTo-Json -Depth 10
Set-Content -Path $out -Value $json -Encoding utf8
Write-Host ""
Write-Host "Wrote $out"
Write-Host "  pages   = $($result.pages.Count)"
Write-Host "  elements= $totalElements"
