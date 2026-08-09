# Safety: fixed target only. Never accepts a caller-supplied host/IP/hostname
# and never falls back to the local machine; every action first verifies over
# WinRM that 100.115.182.0 really is WINBRAT, and fails closed on any mismatch.
#
# Interactive UI work (uia/screenshot) never touches local process/input/screen
# APIs from this dev box: a helper script is shipped to the verified brat box
# and run there in the interactive console session via a unique transient
# scheduled task. Moving the logged-on session onto the physical console
# (tscon) also runs through a unique transient SYSTEM scheduled task, so no
# password is ever needed. All remote transient files live under
# C:\r4review\verify and are removed again in a finally block.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('identity', 'deploy', 'uia', 'screenshot', 'logs', 'state', 'probe', 'lifecycle', 'loadtest', 'altclient')]
    [string]$Action,

    [string]$Version,

    # UIA element selectors (uia): AutomationId and/or Name required.
    [string]$AutomationId,
    [string]$Name,
    [string]$ControlType,

    [ValidateSet('Inspect', 'Invoke', 'InvokeThen', 'Toggle', 'EnsureToggle', 'Expand', 'Select', 'SelectProtocol', 'ScrollIntoView', 'SetValue')]
    [string]$UiaOperation = 'Inspect',
    [string]$Value,

    [ValidateSet('VlessReality', 'VlessWebSocket', 'VlessXhttp', 'Hysteria2', 'Tuic', 'AmneziaWG', 'Naive', 'DnsTunnel', 'Shadowsocks')]
    [string]$ProtocolClass,
    [ValidateRange(0, 20)]
    [int]$ProtocolOrdinal = 0,

    # screenshot local destination (must stay inside the checkout root).
    [string]$LocalOutput,

    # logs: only inspect entries written during the recent verification window.
    [ValidateRange(1, 1440)]
    [int]$LogWindowMinutes = 120,

    [ValidateRange(5, 120)]
    [int]$TimeoutSeconds = 30,

    [ValidateSet('Control', 'Boundary')]
    [string]$ProbeProfile = 'Control',

    [ValidateSet('GameUdp', 'BrowserBurst', 'Mixed')]
    [string]$LoadProfile = 'GameUdp',

    [ValidateSet('AmneziaWG')]
    [string]$AltClient = 'AmneziaWG',

    [ValidateSet('Preflight', 'Install', 'Cycle', 'Cleanup')]
    [string]$AltOperation = 'Preflight',

    [ValidateSet('Control', 'Target')]
    [string]$AltProfile = 'Target',

    # lifecycle: caller-provided timestamp only; log paths and raw lines never
    # leave the verified test VM for this action.
    [string]$SinceUtc
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Resolve a credential file: prefer the current checkout root (local-first),
# then fall back to the primary worktree root via Git's common directory.
# Never copies credentials into task worktrees; fails closed if neither exists.
function Resolve-CredentialFile {
    param(
        [Parameter(Mandatory = $true)] [string]$FileName,
        [Parameter(Mandatory = $true)] [string]$LocalRoot
    )
    $local = Join-Path $LocalRoot $FileName
    if (Test-Path $local) { return $local }
    $commonDir = $null
    try { $commonDir = git -C $LocalRoot rev-parse --git-common-dir 2>$null } catch { }
    if ($commonDir) {
        $commonDir = @($commonDir)[0].ToString().Trim()
        if ($commonDir -and $commonDir -ne '.') {
            if (-not [System.IO.Path]::IsPathRooted($commonDir)) {
                $commonDir = Join-Path $LocalRoot $commonDir
            }
            $primaryRoot = Split-Path $commonDir -Parent
            if ($primaryRoot) {
                $primary = Join-Path $primaryRoot $FileName
                if (Test-Path $primary) { return $primary }
            }
        }
    }
    # Neither location has the file. Return the local path so the caller's
    # own Test-Path guard (New-VerifiedBratSession) produces the actionable
    # missing-file error; this also keeps store/bootstrap paths reachable.
    return $local
}

$Root            = Split-Path $PSScriptRoot -Parent
# Fixed transport endpoint: the windows-brat VM's Tailscale CGNAT address.
# Caller-nonconfigurable on purpose (see header); the LAN 192.168.0.106 is retired.
$BratIp          = '100.115.182.0'
$BratMachineName = 'WINBRAT'
# Credential file keeps its legacy LAN-era name on purpose: the DPAPI-encrypted
# secret already exists under this filename, so we resolve it as-is and avoid any
# secret copy/migration. Only the transport IP above changed, not the credential.
$CredFile        = Resolve-CredentialFile -FileName '.testpc-cred-192.168.0.106.xml' -LocalRoot $Root
$RemoteVerifyRoot = 'C:\r4review\verify'

# Empty until an operator provisions a source-built archive and its exact
# digest is reviewed into this script. A sidecar alone is not authorization.
$ApprovedWinbratLoadPayloadSha256 = @(
    '5855167c4c89efa5c5adbd0933ee4269382785bb35d6b04f7a5fd27d80f72934'
)
$ApprovedWinbratBrowserProbePayloadSha256 = @(
    '5db024a6cf67ac88b56955144b1cfbea9e9234a1dc38a0a117ee28ce8c966290'
)
$ChromeForTestingArchive = Join-Path $Root 'artifacts\chrome-for-testing\150.0.7871.129\chrome-win64.zip'
$ApprovedChromeForTestingSha256 = '4543709a8b323e655b8550d2203468eeeed69cd0fa21e4ae0499f314d53e470d'
$ChromeForTestingEntryCount = 308
$ChromeForTestingExe = 'chrome-win64/chrome.exe'
$BrowserCandidates = @(
    [ordered]@{ Path = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'; Vendor = 'Microsoft' }
    [ordered]@{ Path = 'C:\Program Files\Microsoft\Edge\Application\msedge.exe'; Vendor = 'Microsoft' }
    [ordered]@{ Path = 'C:\Program Files\Google\Chrome\Application\chrome.exe'; Vendor = 'Google' }
    [ordered]@{ Path = 'C:\Program Files (x86)\Google\Chrome\Application\chrome.exe'; Vendor = 'Google' }
)
$OfficialAwgMsi = Join-Path $Root 'artifacts\official-alt-clients\amneziawg-amd64-2.0.2.msi'
$OfficialAwgMsiSha256 = '1b7308d0c74685193dee5d30fd30f370b5a2748a7f648869cd16f25286efc784'
$OfficialAwgSignerThumbprint = '141D90A1BA8F61863FBEDDF7DD1D66C1D1E0B128'
$OfficialAwgRemoteHelper = Join-Path $PSScriptRoot 'brat-official-awg-remote.ps1'

function Test-ApprovedWinbratLoadPayload {
    $archive = Join-Path $Root 'artifacts\brat-loadtest-payload\WinbratLoadGen-win-x64.zip'
    if (-not (Test-Path $archive) -or $ApprovedWinbratLoadPayloadSha256.Count -eq 0) { return $false }
    $hash = (Get-FileHash -Algorithm SHA256 $archive).Hash.ToLower()
    return $ApprovedWinbratLoadPayloadSha256 -contains $hash
}

function Test-ApprovedWinbratBrowserProbePayload {
    $archive = Join-Path $Root 'artifacts\brat-browser-probe-payload\WinbratBrowserProbe-win-x64.zip'
    if (-not (Test-Path $archive) -or $ApprovedWinbratBrowserProbePayloadSha256.Count -eq 0) { return $false }
    $hash = (Get-FileHash -Algorithm SHA256 $archive).Hash.ToLower()
    return $ApprovedWinbratBrowserProbePayloadSha256 -contains $hash
}

function Test-ApprovedOfficialAwgInstaller {
    if (-not (Test-Path -LiteralPath $OfficialAwgMsi -PathType Leaf)) { return $false }
    if ((Get-FileHash -LiteralPath $OfficialAwgMsi -Algorithm SHA256).Hash.ToLowerInvariant() -ne $OfficialAwgMsiSha256) { return $false }
    $signature = Get-AuthenticodeSignature -LiteralPath $OfficialAwgMsi
    return $signature.Status -eq [System.Management.Automation.SignatureStatus]::Valid -and
        $null -ne $signature.SignerCertificate -and
        $signature.SignerCertificate.Thumbprint -eq $OfficialAwgSignerThumbprint
}

function Test-ApprovedChromeForTestingArchive {
    if (-not (Test-Path -LiteralPath $ChromeForTestingArchive -PathType Leaf)) { return $false }
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $ChromeForTestingArchive).Hash.ToLower() -ne $ApprovedChromeForTestingSha256) { return $false }
    $zip = $null
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [System.IO.Compression.ZipFile]::OpenRead($ChromeForTestingArchive)
        $entries = @($zip.Entries)
        return $entries.Count -eq $ChromeForTestingEntryCount -and
            @($entries | Where-Object {
                -not $_.FullName.StartsWith('chrome-win64/', [StringComparison]::Ordinal) -or
                $_.FullName -match '(^|[\\/])\.\.([\\/]|$)'
            }).Count -eq 0 -and
            @($entries | Where-Object { $_.FullName -ceq $ChromeForTestingExe }).Count -eq 1
    }
    catch { return $false }
    finally { if ($zip) { $zip.Dispose() } }
}


function New-VerifiedBratSession {
    if (-not (Test-Path $CredFile)) {
        throw "Credential file missing: $CredFile. Create it once (interactive): Get-Credential -Message 'Local admin on $BratIp ($BratMachineName)' | Export-Clixml '$CredFile'"
    }
    $cred = Import-Clixml $CredFile
    $s = $null
    try {
        $s = New-PSSession -ComputerName $BratIp -Credential $cred
        $machine = Invoke-Command -Session $s { [Environment]::MachineName }
        if ($machine -ine $BratMachineName) {
            throw "Identity check failed: $BratIp reported MachineName '$machine', expected '$BratMachineName'. Refusing to proceed."
        }
        return $s
    }
    catch {
        if ($s) { Remove-PSSession $s -ErrorAction SilentlyContinue }
        throw
    }
}

function Invoke-BratInteractive {
    param(
        [Parameter(Mandatory = $true)] $Session,
        [Parameter(Mandatory = $true)] [ValidateSet('uia', 'screenshot')] [string]$Mode,
        [string]$AutomationId,
        [string]$Name,
        [string]$ControlType,
        [ValidateSet('Inspect', 'Invoke', 'InvokeThen', 'Toggle', 'EnsureToggle', 'Expand', 'Select', 'SelectProtocol', 'ScrollIntoView', 'SetValue')]
        [string]$UiaOperation = 'Inspect',
        [string]$Value,
        [string]$ProtocolClass,
        [int]$ProtocolOrdinal = 0,
        [string]$LocalOutput,
        [int]$TimeoutSeconds = 30
    )

    # The only place UIA / screen-capture code exists. Shipped to brat and run
    # there; never dot-sourced or invoked on this dev box.
    $helper = @'
# BEGIN REMOTE IN-SESSION HELPER
param(
    [Parameter(Mandatory = $true, Position = 0)][string]$RequestPath,
    [Parameter(Mandatory = $true, Position = 1)][string]$ResultPath
)
$ErrorActionPreference = 'Stop'
$result = [ordered]@{ Success = $false; Error = $null }
try {
    $reqText = Get-Content -Path $RequestPath -Raw
    Remove-Item -LiteralPath $RequestPath -Force -ErrorAction SilentlyContinue
    $req = $reqText | ConvertFrom-Json
    $deadline = (Get-Date).AddSeconds([int]$req.TimeoutSeconds)

    if ($req.Mode -eq 'uia') {
        Add-Type -AssemblyName UIAutomationClient
        Add-Type -AssemblyName UIAutomationTypes
        $ae = [System.Windows.Automation.AutomationElement]

        # Prefer the GUI host; only fall back to VPNRouter.App when no GUI
        # process exists. Never pick an arbitrary first result across both names.
        $proc = Get-Process -Name VPNRouter.GUI -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $proc) { $proc = Get-Process -Name VPNRouter.App -ErrorAction SilentlyContinue | Select-Object -First 1 }
        if (-not $proc) { throw "VPNRouter.GUI / VPNRouter.App process not found." }

        $root = $ae::RootElement
        $pidCond = New-Object System.Windows.Automation.PropertyCondition($ae::ProcessIdProperty, $proc.Id)
        $window = $null
        while (-not $window -and (Get-Date) -lt $deadline) {
            $window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $pidCond)
            if (-not $window) { Start-Sleep -Milliseconds 300 }
        }
        if (-not $window) { throw "No top-level window for PID $($proc.Id)." }

        $conds = @()
        if ($req.AutomationId) { $conds += New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, [string]$req.AutomationId) }
        if ($req.Name) {
            $requestedNames = @(([string]$req.Name).Split(
                [string[]]@('||'),
                [System.StringSplitOptions]::RemoveEmptyEntries))
            if ($requestedNames.Count -eq 1) {
                $conds += New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, $requestedNames[0])
            }
            else {
                $nameConditions = @($requestedNames | ForEach-Object {
                    New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, $_)
                })
                $conds += [System.Windows.Automation.OrCondition]::new(
                    [System.Windows.Automation.Condition[]]$nameConditions)
            }
        }
        if ($req.ControlType) {
            $ctProp = [System.Windows.Automation.ControlType].GetField([string]$req.ControlType, [System.Reflection.BindingFlags]'Public,Static')
            if (-not $ctProp) { throw "Unknown ControlType '$($req.ControlType)'." }
            $conds += New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ctProp.GetValue($null))
        }
        $findCond = $conds[0]
        for ($i = 1; $i -lt $conds.Count; $i++) {
            $findCond = New-Object System.Windows.Automation.AndCondition $findCond, $conds[$i]
        }
        $processFindCond = New-Object System.Windows.Automation.AndCondition $pidCond, $findCond

        $target = $null
        while (-not $target -and (Get-Date) -lt $deadline) {
            # Search every top-level window owned by the app process. Avalonia
            # flyouts and modal dialogs are siblings of the main window in UIA.
            $target = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $processFindCond)
            if (-not $target) { Start-Sleep -Milliseconds 300 }
        }
        if (-not $target) { throw "No descendant matched AutomationId='$($req.AutomationId)' Name='$($req.Name)' ControlType='$($req.ControlType)' before timeout." }

        switch ([string]$req.Operation) {
            'Inspect' {
                $result.Element = [ordered]@{
                    Name         = $target.Current.Name
                    AutomationId = $target.Current.AutomationId
                    ControlType  = $target.Current.ControlType.ProgrammaticName
                    IsEnabled    = $target.Current.IsEnabled
                }
                $toggle = $null
                if ($target.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$toggle)) {
                    $result.Element.ToggleState = $toggle.Current.ToggleState.ToString()
                }
                $selection = $null
                if ($target.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$selection)) {
                    $result.Element.IsSelected = [bool]$selection.Current.IsSelected
                }
            }
            'Invoke' {
                $pat = $null
                if (-not $target.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pat)) { throw "InvokePattern unsupported by matched element." }
                $pat.Invoke()
            }
            'InvokeThen' {
                if (-not $req.Value) { throw "InvokeThen requires Value with the follow-up element Name." }
                $pat = $null
                if (-not $target.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pat)) { throw "InvokePattern unsupported by matched element." }
                $pat.Invoke()

                $nextName = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, [string]$req.Value)
                $nextCond = New-Object System.Windows.Automation.AndCondition $pidCond, $nextName
                $next = $null
                while (-not $next -and (Get-Date) -lt $deadline) {
                    $next = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nextCond)
                    if (-not $next) { Start-Sleep -Milliseconds 200 }
                }
                if (-not $next) { throw "No process element named '$($req.Value)' appeared after invoking '$($req.Name)'." }
                $nextPat = $null
                if (-not $next.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$nextPat)) { throw "Follow-up element '$($req.Value)' does not support InvokePattern." }
                $nextPat.Invoke()
            }
            'Toggle' {
                $pat = $null
                if (-not $target.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pat)) { throw "TogglePattern unsupported by matched element." }
                $before = $pat.Current.ToggleState
                $pat.Toggle()
                $after = $before
                for ($i = 0; $i -lt 20 -and $after -eq $before; $i++) {
                    Start-Sleep -Milliseconds 100
                    $after = $pat.Current.ToggleState
                }
                Start-Sleep -Milliseconds 750
                $after = $pat.Current.ToggleState
                if ($after -eq $before) { throw "TogglePattern returned without changing the matched element state." }
            }
            'EnsureToggle' {
                if ([string]$req.Value -notin @('On', 'Off')) { throw "EnsureToggle requires Value On or Off." }
                $pat = $null
                if (-not $target.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pat)) { throw "TogglePattern unsupported by matched element." }
                $wanted = if ([string]$req.Value -eq 'On') { [System.Windows.Automation.ToggleState]::On } else { [System.Windows.Automation.ToggleState]::Off }
                $before = $pat.Current.ToggleState
                if ($before -ne $wanted) { $pat.Toggle() }
                for ($i = 0; $i -lt 20 -and $pat.Current.ToggleState -ne $wanted; $i++) { Start-Sleep -Milliseconds 100 }
                if ($pat.Current.ToggleState -ne $wanted) { throw "TogglePattern did not reach the requested state." }
                $result.Element = [ordered]@{ Before = $before.ToString(); After = $pat.Current.ToggleState.ToString() }
            }
            'Expand' {
                $pat = $null
                if (-not $target.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$pat)) { throw "ExpandCollapsePattern unsupported by matched element." }
                $pat.Expand()
            }
            'Select' {
                $pat = $null
                if (-not $target.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat)) { throw "SelectionItemPattern unsupported by matched element." }
                $pat.Select()
                for ($i = 0; $i -lt 20 -and -not $pat.Current.IsSelected; $i++) { Start-Sleep -Milliseconds 100 }
                if (-not $pat.Current.IsSelected) { throw "SelectionItemPattern returned without selecting the matched element." }
            }
            'SelectProtocol' {
                if ($target.Current.AutomationId -ne 'SubList') { throw "SelectProtocol is restricted to the SubList control." }
                if (Get-NetAdapter -Name 'VPNRouter-TUN' -ErrorAction SilentlyContinue | Where-Object Status -eq 'Up') {
                    throw "SelectProtocol requires a disconnected VPNRouter-TUN state."
                }

                $protocolNames = [ordered]@{
                    VlessReality   = @('tcp + reality', 'reality')
                    VlessWebSocket = @('ws + reality', 'ws + tls')
                    VlessXhttp     = @('xhttp + reality', 'xhttp + tls')
                    Hysteria2      = @('hysteria2', 'hysteria2 + salamander')
                    Tuic           = @('tuic', 'tuic + bbr', 'tuic + cubic', 'tuic + new_reno')
                    AmneziaWG      = @('amneziawg', 'amneziawg + obfs')
                    Naive          = @('naive', 'naive + hy2')
                    DnsTunnel      = @('dns-tunnel')
                    Shadowsocks    = @('Fallback', 'Резерв')
                }
                $expectedCounts = [ordered]@{
                    VlessReality = 4; VlessWebSocket = 3; VlessXhttp = 4
                    Hysteria2 = 4; Tuic = 0; AmneziaWG = 4
                    Naive = 1; DnsTunnel = 0; Shadowsocks = 0
                }
                if (-not $protocolNames.Contains([string]$req.ProtocolClass)) {
                    throw "SelectProtocol requires an allowlisted ProtocolClass."
                }
                $textType = New-Object System.Windows.Automation.PropertyCondition(
                    $ae::ControlTypeProperty,
                    [System.Windows.Automation.ControlType]::Text)
                $safeTextByClass = @{}
                foreach ($entry in $protocolNames.GetEnumerator()) {
                    $nameConditions = @($entry.Value | ForEach-Object {
                        New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, $_)
                    })
                    $safeName = if ($nameConditions.Count -eq 1) { $nameConditions[0] } else {
                        [System.Windows.Automation.OrCondition]::new([System.Windows.Automation.Condition[]]$nameConditions)
                    }
                    $safeTextByClass[$entry.Key] = New-Object System.Windows.Automation.AndCondition $safeName, $textType
                }
                $ownedCorePath = 'C:\ProgramData\VPNRouter\bin\sing-box.exe'
                $ownedCores = @(Get-Process -Name 'sing-box' -ErrorAction SilentlyContinue | Where-Object {
                    try { [System.IO.Path]::GetFullPath($_.Path).Equals($ownedCorePath, [StringComparison]::OrdinalIgnoreCase) }
                    catch { $false }
                })
                if ($ownedCores.Count -ne 0) { throw 'SelectProtocol requires zero owned sing-box processes.' }
                if (Get-NetAdapter -Name 'VPNRouter-TUN' -ErrorAction SilentlyContinue | Where-Object Status -eq 'Up') {
                    throw 'SelectProtocol requires an absent or down VPNRouter-TUN.'
                }

                $autoNames = @('Auto-select via quick web test', 'Авто-выбор по быстрому веб-тесту')
                $autoNameConditions = @($autoNames | ForEach-Object {
                    New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, $_)
                })
                $autoName = [System.Windows.Automation.OrCondition]::new(
                    [System.Windows.Automation.Condition[]]$autoNameConditions)
                $checkBox = New-Object System.Windows.Automation.PropertyCondition(
                    $ae::ControlTypeProperty,
                    [System.Windows.Automation.ControlType]::CheckBox)
                $autoToggleElement = $window.FindFirst(
                    [System.Windows.Automation.TreeScope]::Descendants,
                    (New-Object System.Windows.Automation.AndCondition $autoName, $checkBox))
                if (-not $autoToggleElement) { throw 'SelectProtocol requires the visible Subscribe Auto-select control.' }
                $autoToggle = $null
                if (-not $autoToggleElement.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$autoToggle)) {
                    throw 'SelectProtocol cannot verify the Subscribe Auto-select state.'
                }
                if ($autoToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) {
                    throw 'SelectProtocol requires Auto-select to already be Off.'
                }

                $listItemType = New-Object System.Windows.Automation.PropertyCondition(
                    $ae::ControlTypeProperty,
                    [System.Windows.Automation.ControlType]::ListItem)
                $getSelectedRows = {
                    $selectedRows = @()
                    foreach ($candidate in $target.FindAll([System.Windows.Automation.TreeScope]::Descendants, $listItemType)) {
                        $candidateSelection = $null
                        if ($candidate.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$candidateSelection) -and
                            $candidateSelection.Current.IsSelected) {
                            $selectedRows += $candidate
                        }
                    }
                    return $selectedRows
                }
                $classifyRow = {
                    param([System.Windows.Automation.AutomationElement]$row)
                    $matches = @($protocolNames.Keys | Where-Object {
                        $null -ne $row.FindFirst(
                            [System.Windows.Automation.TreeScope]::Descendants,
                            $safeTextByClass[$_])
                    })
                    if ($matches.Count -ne 1) {
                        throw 'SubList row does not expose exactly one allowlisted protocol class.'
                    }
                    return [string]$matches[0]
                }
                $originalSelectedRows = @(& $getSelectedRows)
                if ($originalSelectedRows.Count -gt 1) { throw 'SubList exposes more than one selected row.' }
                $originalSelection = $originalSelectedRows | Select-Object -First 1
                $selectionMutationAttempted = $false
                try {
                    Add-Type -AssemblyName System.Windows.Forms
                    $keyboardFocus = $originalSelection
                    if (-not $keyboardFocus) {
                        $keyboardFocus = $target.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $listItemType)
                    }
                    if (-not $keyboardFocus) { throw 'SubList has no focusable materialized row for fixed-key traversal.' }
                    $keyboardFocus.SetFocus()
                    $selectionMutationAttempted = $true
                    [System.Windows.Forms.SendKeys]::SendWait('{HOME}')
                    Start-Sleep -Milliseconds 250

                    $matched = 0
                    $chosenAbsoluteOrdinal = $null
                    $observedCounts = @{}
                    foreach ($class in $expectedCounts.Keys) { $observedCounts[$class] = 0 }
                    $expectedTotal = 20
                    for ($absoluteOrdinal = 0; $absoluteOrdinal -lt $expectedTotal; $absoluteOrdinal++) {
                        $currentSelection = @(& $getSelectedRows)
                        if ($currentSelection.Count -ne 1) { throw 'SubList traversal requires exactly one selected row.' }
                        $row = $currentSelection[0]
                        if ($row.Current.ControlType -ne [System.Windows.Automation.ControlType]::ListItem) {
                            throw 'SubList selected element is not a ListItem.'
                        }
                        $rowClass = & $classifyRow $row
                        $observedCounts[$rowClass]++
                        if ($rowClass -eq [string]$req.ProtocolClass) {
                            if ($matched -eq [int]$req.ProtocolOrdinal) {
                                $chosenAbsoluteOrdinal = $absoluteOrdinal
                            }
                            $matched++
                        }

                        if ($absoluteOrdinal -lt ($expectedTotal - 1)) {
                            [System.Windows.Forms.SendKeys]::SendWait('{DOWN}')
                            Start-Sleep -Milliseconds 150
                        }
                    }
                    foreach ($class in $expectedCounts.Keys) {
                        if ([int]$observedCounts[$class] -ne [int]$expectedCounts[$class]) {
                            throw 'SubList protocol composition changed during fixed traversal.'
                        }
                    }
                    if ($null -eq $chosenAbsoluteOrdinal) {
                        throw "No allowlisted row matched the requested protocol class and ordinal (matched rows: $matched)."
                    }
                    $current = @(& $getSelectedRows) | Select-Object -First 1
                    if (-not $current) { throw 'SubList traversal lost its final selection.' }
                    $current.SetFocus()
                    [System.Windows.Forms.SendKeys]::SendWait('{HOME}')
                    for ($step = 0; $step -lt [int]$chosenAbsoluteOrdinal; $step++) {
                        [System.Windows.Forms.SendKeys]::SendWait('{DOWN}')
                    }
                    Start-Sleep -Milliseconds 250
                    $chosenRows = @(& $getSelectedRows)
                    if ($chosenRows.Count -ne 1 -or (& $classifyRow $chosenRows[0]) -ne [string]$req.ProtocolClass) {
                        throw 'Protocol row selection did not reach the verified safe coordinate.'
                    }
                    $chosen = $chosenRows[0]
                    $pat = $null
                    if (-not $chosen.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat)) { throw "Matched protocol row does not support SelectionItemPattern." }
                    if (-not $pat.Current.IsSelected) { throw "Protocol row selection did not stick." }

                    # Country evidence is derived only from the visible first TextBlock.
                    # Raw row text never leaves WINBRAT.
                    $regionResult = [ordered]@{ RegionCode = 'Unknown'; Country = 'Unknown' }
                    $displayText = $chosen.FindFirst(
                        [System.Windows.Automation.TreeScope]::Descendants,
                        $textType)
                    if ($displayText) {
                        $visibleLabel = [string]$displayText.Current.Name
                        $regions = @{}
                        foreach ($culture in [System.Globalization.CultureInfo]::GetCultures(
                            [System.Globalization.CultureTypes]::SpecificCultures)) {
                            try {
                                $region = [System.Globalization.RegionInfo]::new($culture.Name)
                                $code = $region.TwoLetterISORegionName.ToUpperInvariant()
                                if ($code -notmatch '^[A-Z]{2}$' -or $regions.ContainsKey($code)) { continue }
                                $flag = [string]::Concat(
                                    [char]::ConvertFromUtf32(0x1F1E6 + ([int][char]$code[0] - [int][char]'A')),
                                    [char]::ConvertFromUtf32(0x1F1E6 + ([int][char]$code[1] - [int][char]'A')))
                                $tokens = @($region.EnglishName, $region.NativeName, $region.DisplayName) |
                                    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                                    Sort-Object -Unique
                                $regions[$code] = [pscustomobject]@{
                                    RegionCode = $code
                                    Country = $region.EnglishName
                                    Flag = $flag
                                    Tokens = $tokens
                                }
                            }
                            catch { }
                        }

                        $matches = @($regions.Values | Where-Object {
                            $candidate = $_
                            if ($visibleLabel.Contains([string]$candidate.Flag)) { return $true }
                            foreach ($token in @($candidate.Tokens)) {
                                $pattern = '(?<!\p{L})' + [regex]::Escape([string]$token) + '(?!\p{L})'
                                if ([regex]::IsMatch(
                                    $visibleLabel,
                                    $pattern,
                                    [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor
                                    [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)) { return $true }
                            }
                            return $false
                        })
                        $visibleLabel = $null
                        if ($matches.Count -eq 1) {
                            $regionResult.RegionCode = [string]$matches[0].RegionCode
                            $regionResult.Country = [string]$matches[0].Country
                        }
                    }
                    $result.Element = [ordered]@{
                        ProtocolClass = [string]$req.ProtocolClass
                        Ordinal = [int]$req.ProtocolOrdinal
                        AbsoluteOrdinal = [int]$chosenAbsoluteOrdinal
                        RegionCode = [string]$regionResult.RegionCode
                        Country = [string]$regionResult.Country
                    }
                }
                catch {
                    if ($selectionMutationAttempted) {
                        foreach ($selected in @(& $getSelectedRows)) {
                            $remove = $null
                            if (-not $selected.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$remove)) {
                                throw 'Protocol selection failed and the safe empty cleanup is unavailable.'
                            }
                            $remove.RemoveFromSelection()
                        }
                        if (@(& $getSelectedRows).Count -ne 0) {
                            throw 'Protocol selection failed and the safe empty cleanup did not stick.'
                        }
                    }
                    throw
                }
            }
            'ScrollIntoView' {
                $pat = $null
                if (-not $target.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern, [ref]$pat)) { throw "ScrollItemPattern unsupported by matched element." }
                $pat.ScrollIntoView()
            }
            'SetValue' {
                $pat = $null
                if (-not $target.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pat)) { throw "ValuePattern unsupported by matched element." }
                if ($pat.Current.IsReadOnly) { throw "Matched element value is read-only." }
                $pat.SetValue([string]$req.Value)
            }
            default { throw "Unknown UIA operation '$($req.Operation)'." }
        }
    }
    elseif ($req.Mode -eq 'screenshot') {
        Add-Type -AssemblyName System.Windows.Forms
        Add-Type -AssemblyName System.Drawing
        $vs = [System.Windows.Forms.SystemInformation]::VirtualScreen
        $bmp = New-Object System.Drawing.Bitmap $vs.Width, $vs.Height
        try {
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            try { $g.CopyFromScreen($vs.X, $vs.Y, 0, 0, $bmp.Size) } finally { $g.Dispose() }
            $bmp.Save([string]$req.ScreenshotPath, [System.Drawing.Imaging.ImageFormat]::Png)
        } finally { $bmp.Dispose() }
        $result.ScreenshotPath = [string]$req.ScreenshotPath
    }
    else { throw "Unknown mode '$($req.Mode)'." }

    $result.Success = $true
}
catch {
    $result.Error = $_.Exception.Message
}
# Publish the result atomically: write a .tmp sibling first, then move it
# over ResultPath, so the controller can never observe a partially written file.
$tmpResultPath = "$ResultPath.tmp"
Set-Content -Path $tmpResultPath -Value ($result | ConvertTo-Json -Depth 5) -Encoding UTF8
Move-Item -Path $tmpResultPath -Destination $ResultPath -Force
if (-not $result.Success) { exit 1 }
exit 0
# END REMOTE IN-SESSION HELPER
'@

    $runId        = [guid]::NewGuid().ToString('N')
    $remoteDir    = "$RemoteVerifyRoot\$runId"
    $remoteHelper = "$remoteDir\helper.ps1"
    $remoteReq    = "$remoteDir\request.json"
    $remoteRes    = "$remoteDir\result.json"
    $remotePng    = "$remoteDir\screenshot.png"
    $taskName     = "BratVerify_$runId"
    $credUser     = (Import-Clixml $CredFile).UserName

    $requestJson = ([ordered]@{
        Mode           = $Mode
        AutomationId   = $AutomationId
        Name           = $Name
        ControlType    = $ControlType
        Operation      = $UiaOperation
        Value          = $Value
        ProtocolClass  = $ProtocolClass
        ProtocolOrdinal = $ProtocolOrdinal
        TimeoutSeconds = $TimeoutSeconds
        ScreenshotPath = $remotePng
    }) | ConvertTo-Json -Depth 5

    # Put the target's logged-on session on the physical console so the helper
    # has a real interactive desktop. Fail closed if nobody is logged on. If
    # the explorer session already is the active console session, skip tscon.
    # Otherwise run tscon through a unique transient SYSTEM scheduled task:
    # tscon needs SeTcbPrivilege and a SYSTEM principal gets it without a
    # password. Everything runs on the already identity-verified PSSession.
    $sessionProbe = Invoke-Command -Session $Session -ScriptBlock {
        $e = Get-Process -Name explorer -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -gt 0 } | Select-Object -First 1
        if (-not $e) {
            return [pscustomobject]@{ Found = $false; SessionId = $null; NeedsTscon = $false }
        }

        if (-not ('BratVerify.Native' -as [type])) {
            Add-Type -Namespace BratVerify -Name Native -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32.dll")]
public static extern uint WTSGetActiveConsoleSessionId();
'@
        }
        $active = [int][BratVerify.Native]::WTSGetActiveConsoleSessionId()
        [pscustomobject]@{ Found = $true; SessionId = [int]$e.SessionId; NeedsTscon = ($e.SessionId -ne $active) }
    }
    if (-not $sessionProbe.Found) {
        throw "No logged-on interactive session on $BratMachineName (no explorer.exe with SessionId > 0). Failing closed."
    }
    if ($sessionProbe.NeedsTscon) {
        $sessionId = $sessionProbe.SessionId
        $consoleTaskName = "BratVerifyConsole_$runId"
        try {
            Invoke-Command -Session $Session -ArgumentList $consoleTaskName, $sessionId -ScriptBlock {
                param($tn, $sid)
                $action    = New-ScheduledTaskAction -Execute 'tscon.exe' -Argument "$sid /dest:console"
                $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
                Register-ScheduledTask -TaskName $tn -Action $action -Principal $principal -Force | Out-Null
                $tsconStart = Get-Date
                Start-ScheduledTask -TaskName $tn
                # Poll until the task has actually run (LastRunTime past start)
                # and finished; a first Ready state before the run starts is
                # not completion.
                $deadline = (Get-Date).AddSeconds(30)
                $finished = $false
                $t = $null
                while ((Get-Date) -lt $deadline) {
                    $t = Get-ScheduledTask -TaskName $tn -ErrorAction SilentlyContinue
                    $info = Get-ScheduledTaskInfo -TaskName $tn -ErrorAction SilentlyContinue
                    if ($t -and $t.State -ne 'Running' -and $info -and $info.LastRunTime -and $info.LastRunTime -gt $tsconStart) {
                        $finished = $true
                        break
                    }
                    Start-Sleep -Milliseconds 200
                }
                $info = Get-ScheduledTaskInfo -TaskName $tn -ErrorAction SilentlyContinue
                $code = if ($info) { $info.LastTaskResult } else { $null }
                if (-not $finished) {
                    throw "tscon task '$tn' did not complete within 30 s on $env:COMPUTERNAME (state $(if ($t) { [string]$t.State } else { 'Missing' })). Failing closed."
                }
                if ($null -eq $code -or $code -ne 0) {
                    throw "tscon.exe failed to move session $sid to the console (LastTaskResult=$code). Failing closed."
                }
            }
        }
        finally {
            Invoke-Command -Session $Session -ArgumentList $consoleTaskName -ScriptBlock {
                param($tn)
                Unregister-ScheduledTask -TaskName $tn -Confirm:$false -ErrorAction SilentlyContinue
            }
        }
    }

    $operationError = $null
    $operationResult = $null
    try {
        Invoke-Command -Session $Session -ArgumentList $remoteDir, $remoteHelper, $remoteReq, $helper, $requestJson -ScriptBlock {
            param($dir, $h, $rq, $helperText, $reqText)
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
            Set-Content -Path $h  -Value $helperText -Encoding UTF8
            Set-Content -Path $rq -Value $reqText    -Encoding UTF8
        }

        $taskStart = Invoke-Command -Session $Session -ArgumentList $taskName, $remoteHelper, $remoteReq, $remoteRes, $credUser -ScriptBlock {
            param($tn, $h, $rq, $rs, $user)
            $arg = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$h`" `"$rq`" `"$rs`""
            $action    = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arg
            $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
            Register-ScheduledTask -TaskName $tn -Action $action -Principal $principal -Force | Out-Null
            # Capture the start timestamp on the remote clock immediately
            # before Start-ScheduledTask and return it: RanSinceStart then
            # compares remote LastRunTime against a remote-clock value, which
            # removes both the fast-exit race (local Get-Date taken after
            # Invoke-Command returns can postdate a quick run) and any
            # host/VM clock skew.
            $started = Get-Date
            Start-ScheduledTask -TaskName $tn
            $started
        }

        # TimeoutSeconds bounds the helper's own UI matching; the controller
        # adds 30 s of slack for WinRM transport and task startup on top.
        # The slack deadline polls on the local clock; the remote status
        # probes compare LastRunTime against $taskStart from the remote clock.
        $deadline   = (Get-Date).AddSeconds($TimeoutSeconds + 30)
        $resultRaw  = $null
        while ((Get-Date) -lt $deadline) {
            $status = Invoke-Command -Session $Session -ArgumentList $taskName, $remoteRes, $taskStart -ScriptBlock {
                param($tn, $rs, $started)
                $t = Get-ScheduledTask -TaskName $tn -ErrorAction SilentlyContinue
                $info = Get-ScheduledTaskInfo -TaskName $tn -ErrorAction SilentlyContinue
                $ran = $false
                if ($info -and $info.LastRunTime -and $info.LastRunTime -gt $started) { $ran = $true }
                [pscustomobject]@{
                    State          = $(if ($t) { [string]$t.State } else { 'Missing' })
                    HasResult      = (Test-Path $rs)
                    RanSinceStart  = $ran
                    LastTaskResult = $(if ($info) { $info.LastTaskResult } else { $null })
                }
            }
            if ($status.HasResult) {
                $resultRaw = Invoke-Command -Session $Session -ArgumentList $remoteRes -ScriptBlock { param($rs) Get-Content $rs -Raw }
                break
            }
            if ($status.State -eq 'Missing') { throw "Transient helper task '$taskName' disappeared on $BratMachineName." }
            # Fast exit: the task ran and is already finished, but no result
            # file exists. An initial Ready state before LastRunTime advances
            # is NOT a fast exit and keeps polling.
            if ($status.State -eq 'Ready' -and $status.RanSinceStart) {
                Start-Sleep -Milliseconds 300
                $late = Invoke-Command -Session $Session -ArgumentList $remoteRes -ScriptBlock { param($rs) if (Test-Path $rs) { Get-Content $rs -Raw } else { $null } }
                if ($late) { $resultRaw = $late; break }
                throw "Interactive helper exited without writing a result JSON on $BratMachineName (LastTaskResult=$($status.LastTaskResult))."
            }
            Start-Sleep -Milliseconds 500
        }
        if (-not $resultRaw) {
            # One final atomic read: the helper may have published its result
            # while the last poll round-trip was in flight.
            $final = Invoke-Command -Session $Session -ArgumentList $remoteRes -ScriptBlock { param($rs) if (Test-Path $rs) { Get-Content $rs -Raw } else { $null } }
            if ($final) {
                $resultRaw = $final
            }
            else {
                throw "Timed out after $($TimeoutSeconds + 30) s waiting for the interactive helper on $BratMachineName (UI match budget $TimeoutSeconds s + 30 s transport/startup slack)."
            }
        }

        $res = $resultRaw | ConvertFrom-Json
        if (-not $res.Success) { throw "Remote interactive helper failed: $($res.Error)" }

        if ($Mode -eq 'screenshot') {
            $localDir = Split-Path $LocalOutput -Parent
            if ($localDir -and -not (Test-Path $localDir)) { New-Item -ItemType Directory -Path $localDir -Force | Out-Null }
            Copy-Item -Path $remotePng -Destination $LocalOutput -FromSession $Session -Force
        }
        $operationResult = $res
    }
    catch { $operationError = $_ }

    $cleanupError = $null
    try {
        Invoke-Command -Session $Session -ArgumentList $taskName, $remoteDir -ScriptBlock {
            param($tn, $dir)
            $t = Get-ScheduledTask -TaskName $tn -ErrorAction SilentlyContinue
            if ($t -and $t.State -eq 'Running') {
                Stop-ScheduledTask -TaskName $tn -ErrorAction Stop
                $stopDeadline = (Get-Date).AddSeconds(10)
                while ((Get-Date) -lt $stopDeadline) {
                    $t = Get-ScheduledTask -TaskName $tn -ErrorAction SilentlyContinue
                    if (-not $t -or $t.State -ne 'Running') { break }
                    Start-Sleep -Milliseconds 200
                }
                if ($t -and $t.State -eq 'Running') {
                    throw "Transient task '$tn' is still running after cleanup timeout."
                }
            }
            if (Get-ScheduledTask -TaskName $tn -ErrorAction SilentlyContinue) {
                Unregister-ScheduledTask -TaskName $tn -Confirm:$false -ErrorAction Stop
            }
            if (Test-Path $dir) { Remove-Item $dir -Recurse -Force -ErrorAction Stop }
            if ((Get-ScheduledTask -TaskName $tn -ErrorAction SilentlyContinue) -or (Test-Path $dir)) {
                throw "Transient task or helper directory remained after cleanup."
            }
        }
    }
    catch { $cleanupError = $_ }

    if ($cleanupError) {
        $primary = if ($operationError) { " Primary failure: $($operationError.Exception.Message)" } else { '' }
        throw "Remote helper cleanup failed on $BratMachineName`: $($cleanupError.Exception.Message).$primary"
    }
    if ($operationError) { throw $operationError }
    return $operationResult
}

function Invoke-BrowserBurstLoad {
    $payloadApproved = Test-ApprovedWinbratBrowserProbePayload
    if (-not $payloadApproved) {
        Write-Output ([ordered]@{
            Status = 'BLOCKED'; Profile = 'BrowserBurst'; RouteScope = 'Unknown'
            FullTunnel = $false; TunCorrelation = $false; DurationSeconds = 600
            Caps = '32x64KiB-5s-4x64Bps'; Metrics = [ordered]@{}; Lifecycle = 'PayloadNotApproved'
        } | ConvertTo-Json -Depth 4 -Compress)
        return
    }

    $archive = Join-Path $Root 'artifacts\brat-browser-probe-payload\WinbratBrowserProbe-win-x64.zip'
    $approvedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash.ToLower()
    $portableApproved = Test-ApprovedChromeForTestingArchive
    $runId = [guid]::NewGuid().ToString('N')
    $remoteRoot = "C:\r4review\browser-load\$runId"
    $remoteArchive = "$remoteRoot\payload.zip"
    $remoteChromeArchive = "$remoteRoot\chrome-win64.zip"
    $s = New-VerifiedBratSession
    try {
        $fullUi = $false
        try {
            $full = Invoke-BratInteractive -Session $s -Mode 'uia' -Name 'подписка · полный||subscribe · full' -ControlType Text -UiaOperation Inspect -TimeoutSeconds 15
            $fullUi = $null -ne $full.Element
        }
        catch { $fullUi = $false }

        $preflight = Invoke-Command -Session $s -ArgumentList $TimeoutSeconds, $BrowserCandidates, $portableApproved -ScriptBlock {
            param($timeoutSeconds, $browserCandidates, $portableApproved)
            $hostName = 'loadtest.vpn.ninitux.com'
            $routeScope = 'Unknown'
            $endpointAddress = ''
            try {
                $address = [System.Net.Dns]::GetHostAddresses($hostName) |
                    Where-Object { $_.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetwork } |
                    Select-Object -First 1
                if ($address) {
                    $endpointAddress = $address.IPAddressToString
                    $route = Find-NetRoute -RemoteIPAddress $address.IPAddressToString -ErrorAction Stop | Select-Object -First 1
                    $adapter = Get-NetAdapter -InterfaceIndex $route.InterfaceIndex -ErrorAction Stop
                    $routeScope = if ($adapter.Name -eq 'VPNRouter-TUN' -and $adapter.Status -eq 'Up') { 'Tunnel' } else { 'Direct' }
                }
            }
            catch { $routeScope = 'Unknown' }
            $ready = $false
            if ($routeScope -eq 'Tunnel') {
                Add-Type -AssemblyName System.Net.Http
                $http = New-Object System.Net.Http.HttpClient
                $http.Timeout = [TimeSpan]::FromSeconds($timeoutSeconds)
                try {
                    $response = $http.GetAsync("https://$hostName/health").GetAwaiter().GetResult()
                    try { $ready = ([int]$response.StatusCode -eq 200) } finally { $response.Dispose() }
            }
            catch { $ready = $false }
            finally { $http.Dispose() }
            }
            $browser = $null
            $browserExists = [bool]$portableApproved
            $browserSigned = $false
            $browserKind = if ($portableApproved) { 'Portable' } else { 'Machine' }
            if ($portableApproved) {
                $browserSigned = $true
            }
            else {
                foreach ($candidate in $browserCandidates) {
                    if (-not (Test-Path -LiteralPath $candidate.Path -PathType Leaf)) { continue }
                    $browserExists = $true
                    try {
                        $signature = Get-AuthenticodeSignature -FilePath $candidate.Path -ErrorAction Stop
                        $browserSigned = $signature.Status -eq 'Valid' -and $null -ne $signature.SignerCertificate -and
                            $signature.SignerCertificate.Subject -like ("*" + [string]$candidate.Vendor + "*")
                    }
                    catch { $browserSigned = $false }
                    if ($browserSigned) { $browser = $candidate; break }
                }
            }
            $corePath = 'C:\ProgramData\VPNRouter\bin\sing-box.exe'
            $coreCount = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
                ([string]$_.ExecutablePath) -ieq $corePath
            }).Count
            $tun = Get-NetAdapter -Name 'VPNRouter-TUN' -ErrorAction SilentlyContinue | Select-Object -First 1
            $stats = if ($tun -and $tun.Status -eq 'Up') { Get-NetAdapterStatistics -Name 'VPNRouter-TUN' -ErrorAction SilentlyContinue } else { $null }
            [ordered]@{
                RouteScope = $routeScope; Ready = $ready; BrowserExists = $browserExists; BrowserSigned = $browserSigned; CoreCount = $coreCount
                TunUp = [bool]($tun -and $tun.Status -eq 'Up')
                TunBytes = if ($stats) { [uint64]$stats.ReceivedBytes + [uint64]$stats.SentBytes } else { [uint64]0 }
                EndpointAddress = $endpointAddress
                BrowserKind = $browserKind
                BrowserPath = if ($browser) { [string]$browser.Path } else { '' }
            }
        }
        if (-not $fullUi -or -not $preflight.Ready -or -not $preflight.BrowserSigned -or
            $preflight.RouteScope -ne 'Tunnel' -or $preflight.CoreCount -ne 1 -or -not $preflight.TunUp -or
            [string]::IsNullOrWhiteSpace($preflight.EndpointAddress)) {
            $blockedReason = if (-not $preflight.BrowserExists) { 'BrowserMissing' }
                elseif (-not $preflight.BrowserSigned) { 'BrowserSignatureUnverified' }
                elseif (-not $preflight.Ready -or [string]::IsNullOrWhiteSpace($preflight.EndpointAddress)) { 'EndpointUnavailable' }
                elseif (-not $fullUi) { 'FullTunnelNotProven' }
                elseif ($preflight.RouteScope -ne 'Tunnel') { 'RouteNotTunnel' }
                else { 'TunnelStateUnavailable' }
            Write-Output ([ordered]@{
                Status = 'BLOCKED'; Profile = 'BrowserBurst'; RouteScope = [string]$preflight.RouteScope
                FullTunnel = [bool]$fullUi; TunCorrelation = $false; DurationSeconds = 600
                Caps = '32x64KiB-5s-4x64Bps'; Metrics = [ordered]@{}
                Lifecycle = $blockedReason
            } | ConvertTo-Json -Depth 4 -Compress)
            return
        }

        Invoke-Command -Session $s -ArgumentList $remoteRoot -ScriptBlock {
            param($dir)
            if (-not $dir.StartsWith('C:\r4review\browser-load\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid fixed browser-load directory.' }
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }
        Copy-Item -LiteralPath $archive -Destination $remoteArchive -ToSession $s -Force
        if ($preflight.BrowserKind -eq 'Portable') {
            Copy-Item -LiteralPath $ChromeForTestingArchive -Destination $remoteChromeArchive -ToSession $s -Force
        }
        $result = Invoke-Command -Session $s -ArgumentList $remoteRoot, $remoteArchive, $approvedHash, ([uint64]$preflight.TunBytes), ([string]$preflight.EndpointAddress), ([string]$preflight.BrowserKind), ([string]$preflight.BrowserPath), $remoteChromeArchive, $ApprovedChromeForTestingSha256, $ChromeForTestingEntryCount, $ChromeForTestingExe -ScriptBlock {
            param($dir, $zip, $expectedHash, $tunBefore, $endpointAddress, $browserKind, $browserPath, $portableArchive, $portableHash, $portableEntryCount, $portableExe)
            $probe = Join-Path $dir 'VPNRouter.Tools.WinbratBrowserProbe.exe'
            $stdout = Join-Path $dir 'result.json'
            $stderr = Join-Path $dir 'error.txt'
            $process = $null
            try {
                if ((Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash.ToLower() -ne $expectedHash) { return [ordered]@{ Success = $false; Failure = 'PayloadHashMismatch' } }
                Expand-Archive -LiteralPath $zip -DestinationPath $dir -Force
                if (-not (Test-Path -LiteralPath $probe)) { return [ordered]@{ Success = $false; Failure = 'PayloadMissing' } }
                if ($browserKind -eq 'Portable') {
                    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $portableArchive).Hash.ToLower() -ne $portableHash) { return [ordered]@{ Success = $false; Failure = 'PayloadFailed' } }
                    $portableZip = $null
                    try {
                        Add-Type -AssemblyName System.IO.Compression.FileSystem
                        $portableZip = [System.IO.Compression.ZipFile]::OpenRead($portableArchive)
                        $entries = @($portableZip.Entries)
                        $validPortable = $entries.Count -eq $portableEntryCount -and
                            @($entries | Where-Object {
                                -not $_.FullName.StartsWith('chrome-win64/', [StringComparison]::Ordinal) -or
                                $_.FullName -match '(^|[\\/])\.\.([\\/]|$)'
                            }).Count -eq 0 -and
                            @($entries | Where-Object { $_.FullName -ceq $portableExe }).Count -eq 1
                        if (-not $validPortable) { return [ordered]@{ Success = $false; Failure = 'PayloadFailed' } }
                    }
                    catch { return [ordered]@{ Success = $false; Failure = 'PayloadFailed' } }
                    finally { if ($portableZip) { $portableZip.Dispose() } }
                    Expand-Archive -LiteralPath $portableArchive -DestinationPath $dir -Force
                    $browserPath = Join-Path $dir ($portableExe -replace '/', '\')
                    if (-not (Test-Path -LiteralPath $browserPath -PathType Leaf)) { return [ordered]@{ Success = $false; Failure = 'PayloadFailed' } }
                }
                $process = Start-Process -FilePath $probe -WorkingDirectory $dir -NoNewWindow -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
                $edgeTree = $false
                $tunCorrelation = $false
                $proofDeadline = (Get-Date).AddSeconds(45)
                while (-not $process.HasExited -and (Get-Date) -lt $proofDeadline) {
                    $all = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue)
                    $byId = @{}
                    foreach ($candidate in $all) { $byId[[uint32]$candidate.ProcessId] = $candidate }
                    foreach ($candidate in $all | Where-Object { ([string]$_.ExecutablePath) -ieq $browserPath }) {
                        $ancestor = $candidate
                        for ($depth = 0; $depth -lt 8 -and $ancestor; $depth++) {
                            if ([uint32]$ancestor.ParentProcessId -eq [uint32]$process.Id) {
                                $edgeTree = $true
                                break
                            }
                            $ancestor = $byId[[uint32]$ancestor.ParentProcessId]
                        }
                    }
                    $tun = Get-NetAdapter -Name 'VPNRouter-TUN' -ErrorAction SilentlyContinue | Select-Object -First 1
                    $stats = if ($tun -and $tun.Status -eq 'Up') { Get-NetAdapterStatistics -Name 'VPNRouter-TUN' -ErrorAction SilentlyContinue } else { $null }
                    if ($stats -and (([uint64]$stats.ReceivedBytes + [uint64]$stats.SentBytes) -gt [uint64]$tunBefore)) { $tunCorrelation = $true }
                    if ($edgeTree -and $tunCorrelation) { break }
                    Start-Sleep -Milliseconds 250
                    $process.Refresh()
                }
                if (-not $edgeTree) { return [ordered]@{ Success = $false; Failure = 'BrowserProcessNotProven' } }
                if (-not $tunCorrelation) { return [ordered]@{ Success = $false; Failure = 'TunCorrelationNotProven' } }
                if (-not $process.WaitForExit(660000)) { $process.Kill(); return [ordered]@{ Success = $false; Failure = 'PayloadTimeout' } }
                $process.WaitForExit()
                if (-not (Test-Path -LiteralPath $stdout) -or (Get-Item -LiteralPath $stdout).Length -gt 8192) { return [ordered]@{ Success = $false; Failure = 'PayloadOutputMissing' } }
                try { $metrics = Get-Content -LiteralPath $stdout -Raw | ConvertFrom-Json }
                catch { return [ordered]@{ Success = $false; Failure = 'PayloadResultInvalid' } }
                $probeFailure = [string]$metrics.Lifecycle
                if ($probeFailure -ne 'Completed') {
                    if ($probeFailure -in @('InputRejected', 'AlreadyRunning', 'PlatformUnsupported', 'BrowserMissing', 'EdgeLaunchFailed',
                        'BrowserExited', 'DevToolsUnavailable', 'PageUnavailable', 'PagePollingFailure', 'DevToolsFailure', 'InvalidPageState',
                        'TimedOut', 'InternalFailure', 'CleanupFailure')) {
                        return [ordered]@{ Success = $false; Failure = "BrowserProbe$probeFailure" }
                    }
                    return [ordered]@{ Success = $false; Failure = 'BrowserProbeLifecycleUnrecognized' }
                }
                if (-not [bool]$metrics.Done) { return [ordered]@{ Success = $false; Failure = 'PayloadFailed' } }
                $corePath = 'C:\ProgramData\VPNRouter\bin\sing-box.exe'
                $coreCount = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { ([string]$_.ExecutablePath) -ieq $corePath }).Count
                $tun = Get-NetAdapter -Name 'VPNRouter-TUN' -ErrorAction SilentlyContinue | Select-Object -First 1
                $routeStable = $false
                try {
                    $routeAfter = Find-NetRoute -RemoteIPAddress $endpointAddress -ErrorAction Stop | Select-Object -First 1
                    $adapterAfter = Get-NetAdapter -InterfaceIndex $routeAfter.InterfaceIndex -ErrorAction Stop
                    $routeStable = $adapterAfter.Name -eq 'VPNRouter-TUN' -and $adapterAfter.Status -eq 'Up'
                }
                catch { $routeStable = $false }
                [ordered]@{
                    Success = $true; CoreStable = [bool]($coreCount -eq 1 -and $tun -and $tun.Status -eq 'Up'); TunCorrelation = $tunCorrelation; RouteStable = $routeStable
                    FetchOk = [int]$metrics.FetchOk; FetchFail = [int]$metrics.FetchFail; WsOk = [int]$metrics.WsOk; WsFail = [int]$metrics.WsFail
                    Done = [bool]$metrics.Done; MaxFetchNoProgressMs = [int64]$metrics.MaxFetchNoProgressMs; MaxWsNoProgressMs = [int64]$metrics.MaxWsNoProgressMs
                }
            }
            finally {
                if ($process -and -not $process.HasExited) { try { $process.Kill(); $process.WaitForExit() } catch { } }
            }
        }
        $metrics = [ordered]@{}
        $passed = $false
        $tunCorrelation = $false
        $lifecycle = 'PayloadFailed'
        if ([bool]$result.Success) {
            foreach ($name in @('FetchOk','FetchFail','WsOk','WsFail','Done','MaxFetchNoProgressMs','MaxWsNoProgressMs')) { $metrics[$name] = $result.$name }
            $tunCorrelation = [bool]$result.TunCorrelation
            $passed = [bool]($result.CoreStable -and $result.RouteStable -and $tunCorrelation -and $result.Done -and $result.FetchOk -ge 3200 -and $result.WsOk -ge 2000 -and $result.FetchFail -eq 0 -and $result.WsFail -eq 0 -and $result.MaxFetchNoProgressMs -le 15000 -and $result.MaxWsNoProgressMs -le 5000)
            $lifecycle = 'Completed'
        }
        else {
            $candidate = [string]$result.Failure
            if ($candidate -in @('PayloadHashMismatch', 'PayloadMissing', 'PayloadTimeout', 'PayloadExitNonZero', 'PayloadOutputMissing', 'PayloadFailed', 'PayloadResultInvalid', 'BrowserProcessNotProven', 'TunCorrelationNotProven',
                'BrowserProbeInputRejected', 'BrowserProbeAlreadyRunning', 'BrowserProbePlatformUnsupported', 'BrowserProbeBrowserMissing', 'BrowserProbeEdgeLaunchFailed',
                'BrowserProbeBrowserExited', 'BrowserProbeDevToolsUnavailable', 'BrowserProbePageUnavailable', 'BrowserProbePagePollingFailure', 'BrowserProbeDevToolsFailure', 'BrowserProbeInvalidPageState',
                'BrowserProbeTimedOut', 'BrowserProbeInternalFailure', 'BrowserProbeCleanupFailure', 'BrowserProbeLifecycleUnrecognized')) { $lifecycle = $candidate }
        }
        Write-Output ([ordered]@{
            Status = if ($passed) { 'PASS' } else { 'FAIL' }; Profile = 'BrowserBurst'; RouteScope = 'Tunnel'; FullTunnel = $true
            TunCorrelation = $tunCorrelation; DurationSeconds = 600; Caps = '32x64KiB-5s-4x64Bps'; Metrics = $metrics; Lifecycle = $lifecycle
        } | ConvertTo-Json -Depth 4 -Compress)
    }
    finally {
        try {
            Invoke-Command -Session $s -ArgumentList $remoteRoot -ScriptBlock {
                param($dir)
                if (-not $dir.StartsWith('C:\r4review\browser-load\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid fixed browser-load cleanup directory.' }
                if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
                if (Test-Path -LiteralPath $dir) { throw 'Fixed browser-load cleanup failed.' }
            }
        }
        finally { Remove-PSSession $s }
    }
}

switch ($Action) {
    'identity' {
        $s = New-VerifiedBratSession
        try {
            $id = Invoke-Command -Session $s {
                [pscustomobject]@{
                    MachineName = [Environment]::MachineName
                    UserName    = [Environment]::UserDomainName + '\' + [Environment]::UserName
                }
            }
            Write-Host "Verified identity: $($id.MachineName) @ $BratIp (connected as $($id.UserName))" -ForegroundColor Green
        }
        finally { Remove-PSSession $s }
    }

    'deploy' {
        if (-not $Version) { throw "deploy requires -Version (e.g. -Version 2.48.0-r3)." }

        # Fail-closed integrity gate BEFORE any remote contact: the exact
        # repo-root artifact and its .sha256 sidecar must both exist, and the
        # recomputed hash must match the sidecar exactly. No match => no deploy.
        $zipName = "VPNRouter-v$Version-win.zip"
        $zipPath = Join-Path $Root $zipName
        $shaPath = "$zipPath.sha256"
        if (-not (Test-Path $zipPath)) { throw "Artifact not found: $zipPath. Build it first (build.ps1 -Version $Version). Failing closed." }
        if (-not (Test-Path $shaPath)) { throw "SHA256 sidecar not found: $shaPath. Refusing to deploy without it. Failing closed." }

        $expected = (Get-Content $shaPath -Raw).Trim().ToLower()
        if ($expected -notmatch '^[0-9a-f]{64}$') { throw "Sidecar $shaPath must contain exactly one 64-char lowercase hex SHA256 (got '$expected'). Failing closed." }
        $actual = (Get-FileHash -Algorithm SHA256 $zipPath).Hash.ToLower()
        if ($actual -ne $expected) { throw "SHA256 mismatch for $zipName`: sidecar=$expected actual=$actual. Failing closed; not deploying." }
        Write-Host "SHA256 verified for $zipName`: $actual" -ForegroundColor Green

        # The generic deploy script verifies WINBRAT on the same session it uses
        # for process stop/copy/install, avoiding a check-then-reconnect gap.
        & (Join-Path $Root 'deploy-to-testpc.ps1') -TestHost $BratIp -Version $Version -Credential (Import-Clixml $CredFile) -ExpectedMachineName $BratMachineName
        if ($LASTEXITCODE) { throw "deploy-to-testpc.ps1 failed (exit $LASTEXITCODE)." }
    }

    'uia' {
        if (-not ($AutomationId -or $Name)) { throw "uia requires -AutomationId and/or -Name." }
        if ($UiaOperation -eq 'SetValue' -and -not $PSBoundParameters.ContainsKey('Value')) { throw "-UiaOperation SetValue requires -Value." }
        if ($UiaOperation -eq 'InvokeThen' -and -not $PSBoundParameters.ContainsKey('Value')) { throw "-UiaOperation InvokeThen requires -Value with the follow-up element Name." }
        if ($UiaOperation -eq 'EnsureToggle' -and $Value -notin @('On', 'Off')) { throw "-UiaOperation EnsureToggle requires -Value On or Off." }
        if ($UiaOperation -eq 'SelectProtocol' -and -not $ProtocolClass) { throw "-UiaOperation SelectProtocol requires -ProtocolClass." }
        $s = New-VerifiedBratSession
        try {
            $res = Invoke-BratInteractive -Session $s -Mode 'uia' -AutomationId $AutomationId -Name $Name -ControlType $ControlType -UiaOperation $UiaOperation -Value $Value -ProtocolClass $ProtocolClass -ProtocolOrdinal $ProtocolOrdinal -TimeoutSeconds $TimeoutSeconds
            if ($UiaOperation -in @('Inspect', 'EnsureToggle', 'SelectProtocol')) {
                Write-Host "Inspect result on $BratMachineName`:" -ForegroundColor Green
                Write-Output ($res.Element | ConvertTo-Json -Compress)
            } else {
                Write-Host "OK: $UiaOperation completed on $BratMachineName." -ForegroundColor Green
            }
        }
        finally { Remove-PSSession $s }
    }

    'screenshot' {
        if (-not $LocalOutput) { $LocalOutput = Join-Path $Root 'artifacts\brat-verify\screenshot.png' }
        $fullOutput = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($LocalOutput)
        $rootPrefix = (Resolve-Path $Root).Path.TrimEnd('\') + '\'
        if (-not $fullOutput.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "LocalOutput must be strictly inside the checkout root '$($rootPrefix.TrimEnd('\'))'; got '$fullOutput'."
        }
        $s = New-VerifiedBratSession
        try {
            Invoke-BratInteractive -Session $s -Mode 'screenshot' -LocalOutput $fullOutput -TimeoutSeconds $TimeoutSeconds | Out-Null
            Write-Host "Screenshot saved to $fullOutput" -ForegroundColor Green
        }
        finally { Remove-PSSession $s }
    }

    'state' {
        $s = New-VerifiedBratSession
        try {
            $state = Invoke-Command -Session $s -ScriptBlock {
                $guiPaths = @('C:\Program Files\VPNRouter\app\VPNRouter.App.exe')
                $corePath = 'C:\ProgramData\VPNRouter\bin\sing-box.exe'

                $owned = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
                    $path = [string]$_.ExecutablePath
                    $guiPaths -icontains $path -or $path -ieq $corePath
                })
                $gui = @($owned | Where-Object { $guiPaths -icontains ([string]$_.ExecutablePath) })
                $core = @($owned | Where-Object { ([string]$_.ExecutablePath) -ieq $corePath })

                $tun = Get-NetAdapter -Name 'VPNRouter-TUN' -ErrorAction SilentlyContinue | Select-Object -First 1
                $tunState = if ($tun) { [string]$tun.Status } else { 'Absent' }

                function Get-FixedProbeRouteScope {
                    $hosts = @('www.gstatic.com', 'stun.l.google.com')
                    $scopes = foreach ($hostName in $hosts) {
                        try {
                            $address = [System.Net.Dns]::GetHostAddresses($hostName) |
                                Where-Object { $_.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetwork } |
                                Select-Object -First 1
                            if (-not $address) { 'Unknown'; continue }
                            $route = Find-NetRoute -RemoteIPAddress $address.IPAddressToString -ErrorAction Stop |
                                Select-Object -First 1
                            $adapter = Get-NetAdapter -InterfaceIndex $route.InterfaceIndex -ErrorAction Stop
                            if ($adapter.Name -eq 'VPNRouter-TUN' -and $adapter.Status -eq 'Up') { 'Tunnel' } else { 'Direct' }
                        }
                        catch { 'Unknown' }
                    }
                    if (@($scopes | Where-Object { $_ -eq 'Direct' }).Count -gt 0) { return 'Direct' }
                    if (@($scopes | Where-Object { $_ -eq 'Tunnel' }).Count -eq $hosts.Count) { return 'Tunnel' }
                    return 'Unknown'
                }

                $workingSetBytes = ($owned | Measure-Object -Property WorkingSetSize -Sum).Sum
                if ($null -eq $workingSetBytes) { $workingSetBytes = 0 }
                $handleCount = ($owned | Measure-Object -Property HandleCount -Sum).Sum
                if ($null -eq $handleCount) { $handleCount = 0 }
                $threadCount = ($owned | Measure-Object -Property ThreadCount -Sum).Sum
                if ($null -eq $threadCount) { $threadCount = 0 }

                [ordered]@{
                    AtUtc        = [DateTimeOffset]::UtcNow.ToString('o')
                    GuiCount     = $gui.Count
                    CoreCount    = $core.Count
                    TunState     = $tunState
                    RouteScope   = Get-FixedProbeRouteScope
                    WorkingSetMb = [Math]::Round(([double]$workingSetBytes / 1MB), 1)
                    Handles      = [int]$handleCount
                    Threads      = [int]$threadCount
                }
            }
            $cleanState = [ordered]@{
                AtUtc        = [string]$state.AtUtc
                GuiCount     = [int]$state.GuiCount
                CoreCount    = [int]$state.CoreCount
                TunState     = [string]$state.TunState
                RouteScope   = [string]$state.RouteScope
                WorkingSetMb = [double]$state.WorkingSetMb
                Handles      = [int]$state.Handles
                Threads      = [int]$state.Threads
            }
            Write-Output ($cleanState | ConvertTo-Json -Compress)
        }
        finally { Remove-PSSession $s }
    }

    'probe' {
        $s = New-VerifiedBratSession
        try {
            $probe = Invoke-Command -Session $s -ArgumentList $ProbeProfile, $TimeoutSeconds -ScriptBlock {
                param($profile, $timeoutSeconds)

                $httpsHost = 'www.gstatic.com'
                $httpsUrl = 'https://www.gstatic.com/generate_204'
                $stunHost = 'stun.l.google.com'
                $stunPort = 19302

                function Resolve-FixedIpv4([string]$hostName) {
                    [System.Net.Dns]::GetHostAddresses($hostName) |
                        Where-Object { $_.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetwork } |
                        Select-Object -First 1
                }

                function Get-RouteScope([System.Net.IPAddress[]]$addresses) {
                    $scopes = foreach ($address in $addresses) {
                        if (-not $address) { 'Unknown'; continue }
                        try {
                            $route = Find-NetRoute -RemoteIPAddress $address.IPAddressToString -ErrorAction Stop |
                                Select-Object -First 1
                            $adapter = Get-NetAdapter -InterfaceIndex $route.InterfaceIndex -ErrorAction Stop
                            if ($adapter.Name -eq 'VPNRouter-TUN' -and $adapter.Status -eq 'Up') { 'Tunnel' } else { 'Direct' }
                        }
                        catch { 'Unknown' }
                    }
                    if (@($scopes | Where-Object { $_ -eq 'Direct' }).Count -gt 0) { return 'Direct' }
                    if (@($scopes | Where-Object { $_ -eq 'Tunnel' }).Count -eq $addresses.Count) { return 'Tunnel' }
                    return 'Unknown'
                }

                function Get-ProbeErrorKind([Exception]$exception) {
                    $current = $exception
                    while ($current.InnerException) { $current = $current.InnerException }
                    if ($current -is [System.TimeoutException] -or
                        $current -is [System.Threading.Tasks.TaskCanceledException]) { return 'Timeout' }
                    if ($current -is [System.Net.Sockets.SocketException]) {
                        if ($current.SocketErrorCode -eq [System.Net.Sockets.SocketError]::TimedOut) {
                            return 'Timeout'
                        }
                        return 'Socket'
                    }
                    return 'Other'
                }

                function New-StunBindingRequest([int]$packetSize) {
                    if ($packetSize -lt 24 -or ($packetSize % 4) -ne 0) {
                        throw 'Invalid fixed STUN packet size.'
                    }
                    $request = New-Object byte[] $packetSize
                    $messageLength = $packetSize - 20
                    $request[0] = 0x00; $request[1] = 0x01
                    $request[2] = [byte](($messageLength -shr 8) -band 0xff)
                    $request[3] = [byte]($messageLength -band 0xff)
                    $request[4] = 0x21; $request[5] = 0x12; $request[6] = 0xA4; $request[7] = 0x42
                    $transactionId = New-Object byte[] 12
                    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
                    try { $rng.GetBytes($transactionId) } finally { $rng.Dispose() }
                    [Array]::Copy($transactionId, 0, $request, 8, 12)

                    # Unknown comprehension-optional attribute. RFC STUN peers
                    # ignore it; its fixed padding makes request-size boundaries
                    # measurable without inventing a game traffic generator.
                    $attributeLength = $packetSize - 24
                    $request[20] = 0xC0; $request[21] = 0x01
                    $request[22] = [byte](($attributeLength -shr 8) -band 0xff)
                    $request[23] = [byte]($attributeLength -band 0xff)
                    for ($i = 24; $i -lt $packetSize; $i++) { $request[$i] = 0x58 }

                    [pscustomobject]@{ Bytes = $request; TransactionId = $transactionId }
                }

                $httpsAddress = Resolve-FixedIpv4 $httpsHost
                $stunAddress = Resolve-FixedIpv4 $stunHost
                $routeScope = Get-RouteScope @($httpsAddress, $stunAddress)
                if ($routeScope -ne 'Tunnel') {
                    throw "Fixed probes are not tunnel-scoped (scope=$routeScope). Dataplane verification is blocked."
                }

                Add-Type -AssemblyName System.Net.Http
                [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
                $httpResult = [ordered]@{ Success = $false; Status = 0; LatencyMs = 0; Error = 'Other' }
                $http = New-Object System.Net.Http.HttpClient
                $http.Timeout = [TimeSpan]::FromSeconds($timeoutSeconds)
                $httpWatch = [System.Diagnostics.Stopwatch]::StartNew()
                try {
                    $response = $http.GetAsync($httpsUrl).GetAwaiter().GetResult()
                    try {
                        $httpWatch.Stop()
                        $httpResult.Status = [int]$response.StatusCode
                        $httpResult.LatencyMs = [int]$httpWatch.ElapsedMilliseconds
                        $httpResult.Success = ($httpResult.Status -eq 204)
                        $httpResult.Error = if ($httpResult.Success) { 'None' } else { 'HttpStatus' }
                    }
                    finally { $response.Dispose() }
                }
                catch {
                    $httpWatch.Stop()
                    $httpResult.LatencyMs = [int]$httpWatch.ElapsedMilliseconds
                    $httpResult.Error = Get-ProbeErrorKind $_.Exception
                }
                finally { $http.Dispose() }

                $sizes = if ($profile -eq 'Boundary') { @(64, 512, 1200, 1392) } else { @(64) }
                $udpResults = @()
                foreach ($size in $sizes) {
                    $row = [ordered]@{ Size = $size; Success = $false; LatencyMs = 0; Error = 'Other' }
                    $udp = New-Object System.Net.Sockets.UdpClient([System.Net.Sockets.AddressFamily]::InterNetwork)
                    $udp.Client.ReceiveTimeout = $timeoutSeconds * 1000
                    $udpWatch = [System.Diagnostics.Stopwatch]::StartNew()
                    try {
                        $request = New-StunBindingRequest $size
                        $udp.Connect($stunAddress, $stunPort)
                        [void]$udp.Send($request.Bytes, $request.Bytes.Length)
                        $remote = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)
                        $responseBytes = $udp.Receive([ref]$remote)
                        $udpWatch.Stop()
                        $valid = $responseBytes.Length -ge 20 -and
                            $responseBytes[0] -eq 0x01 -and $responseBytes[1] -eq 0x01
                        if ($valid) {
                            for ($i = 0; $i -lt 12; $i++) {
                                if ($responseBytes[8 + $i] -ne $request.TransactionId[$i]) { $valid = $false; break }
                            }
                        }
                        $row.Success = $valid
                        $row.LatencyMs = [int]$udpWatch.ElapsedMilliseconds
                        $row.Error = if ($valid) { 'None' } else { 'InvalidResponse' }
                    }
                    catch {
                        $udpWatch.Stop()
                        $row.LatencyMs = [int]$udpWatch.ElapsedMilliseconds
                        $row.Error = Get-ProbeErrorKind $_.Exception
                    }
                    finally { $udp.Dispose() }
                    $udpResults += [pscustomobject]$row
                }

                [ordered]@{
                    AtUtc      = [DateTimeOffset]::UtcNow.ToString('o')
                    Profile    = $profile
                    RouteScope = $routeScope
                    Success    = ($httpResult.Success -and @($udpResults | Where-Object { -not $_.Success }).Count -eq 0)
                    Http       = [pscustomobject]$httpResult
                    Udp        = $udpResults
                }
            }
            $cleanUdp = @($probe.Udp | ForEach-Object {
                [ordered]@{
                    Size      = [int]$_.Size
                    Success   = [bool]$_.Success
                    LatencyMs = [int]$_.LatencyMs
                    Error     = [string]$_.Error
                }
            })
            $cleanProbe = [ordered]@{
                AtUtc      = [string]$probe.AtUtc
                Profile    = [string]$probe.Profile
                RouteScope = [string]$probe.RouteScope
                Success    = [bool]$probe.Success
                Http       = [ordered]@{
                    Success   = [bool]$probe.Http.Success
                    Status    = [int]$probe.Http.Status
                    LatencyMs = [int]$probe.Http.LatencyMs
                    Error     = [string]$probe.Http.Error
                }
                Udp        = $cleanUdp
            }
            Write-Output ($cleanProbe | ConvertTo-Json -Depth 6 -Compress)
        }
        finally { Remove-PSSession $s }
    }

    'loadtest' {
        if ($LoadProfile -eq 'BrowserBurst') {
            Invoke-BrowserBurstLoad
            break
        }
        if ($LoadProfile -eq 'Mixed') {
            Write-Output ([ordered]@{
                Status = 'BLOCKED'; Profile = 'Mixed'; RouteScope = 'Unknown'
                FullTunnel = $false; TunCorrelation = $false; DurationSeconds = 900
                Caps = 'GameUdp+BrowserBurst'; Metrics = [ordered]@{}; Lifecycle = 'MeasurementGated'
            } | ConvertTo-Json -Depth 4 -Compress)
            break
        }
        $payloadApproved = Test-ApprovedWinbratLoadPayload
        if (-not $payloadApproved) {
            Write-Output ([ordered]@{
                Status = 'BLOCKED'; Profile = 'GameUdp'; RouteScope = 'Unknown'
                FullTunnel = $false; TunCorrelation = $false; DurationSeconds = 300
                Caps = '20pps-256B-burst50pps'; Metrics = [ordered]@{}
                Lifecycle = 'PayloadNotApproved'
            } | ConvertTo-Json -Depth 4 -Compress)
            break
        }

        $archive = Join-Path $Root 'artifacts\brat-loadtest-payload\WinbratLoadGen-win-x64.zip'
        $approvedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash.ToLower()
        $runId = [guid]::NewGuid().ToString('N')
        $remoteRoot = "C:\r4review\loadtest\$runId"
        $remoteArchive = "$remoteRoot\payload.zip"
        $s = New-VerifiedBratSession
        try {
            $fullUi = $false
            try {
                $full = Invoke-BratInteractive -Session $s -Mode 'uia' -Name 'подписка · полный||subscribe · full' -ControlType Text -UiaOperation Inspect -TimeoutSeconds 15
                $fullUi = $null -ne $full.Element
            }
            catch { $fullUi = $false }

            $preflight = Invoke-Command -Session $s -ArgumentList $TimeoutSeconds -ScriptBlock {
                param($timeoutSeconds)
                $hostName = 'loadtest.vpn.ninitux.com'
                $routeScope = 'Unknown'
                try {
                    $address = [System.Net.Dns]::GetHostAddresses($hostName) |
                        Where-Object { $_.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetwork } |
                        Select-Object -First 1
                    if ($address) {
                        $route = Find-NetRoute -RemoteIPAddress $address.IPAddressToString -ErrorAction Stop | Select-Object -First 1
                        $adapter = Get-NetAdapter -InterfaceIndex $route.InterfaceIndex -ErrorAction Stop
                        $routeScope = if ($adapter.Name -eq 'VPNRouter-TUN' -and $adapter.Status -eq 'Up') { 'Tunnel' } else { 'Direct' }
                    }
                }
                catch { $routeScope = 'Unknown' }
                $ready = $false
                if ($routeScope -eq 'Tunnel') {
                    Add-Type -AssemblyName System.Net.Http
                    $http = New-Object System.Net.Http.HttpClient
                    $http.Timeout = [TimeSpan]::FromSeconds($timeoutSeconds)
                    try {
                        $response = $http.GetAsync("https://$hostName/health").GetAwaiter().GetResult()
                        try { $ready = ([int]$response.StatusCode -eq 200) } finally { $response.Dispose() }
                    }
                    catch { $ready = $false }
                    finally { $http.Dispose() }
                }
                $corePath = 'C:\ProgramData\VPNRouter\bin\sing-box.exe'
                $coreCount = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
                    ([string]$_.ExecutablePath) -ieq $corePath
                }).Count
                $tun = Get-NetAdapter -Name 'VPNRouter-TUN' -ErrorAction SilentlyContinue | Select-Object -First 1
                $stats = if ($tun -and $tun.Status -eq 'Up') { Get-NetAdapterStatistics -Name 'VPNRouter-TUN' -ErrorAction SilentlyContinue } else { $null }
                [ordered]@{
                    RouteScope = $routeScope
                    Ready = $ready
                    CoreCount = $coreCount
                    TunUp = [bool]($tun -and $tun.Status -eq 'Up')
                    TunBytes = if ($stats) { [uint64]$stats.ReceivedBytes + [uint64]$stats.SentBytes } else { [uint64]0 }
                }
            }
            if (-not $fullUi -or -not $preflight.Ready -or $preflight.RouteScope -ne 'Tunnel' -or
                $preflight.CoreCount -ne 1 -or -not $preflight.TunUp) {
                $clean = [ordered]@{
                    Status = 'BLOCKED'; Profile = 'GameUdp'; RouteScope = [string]$preflight.RouteScope
                    FullTunnel = [bool]$fullUi; TunCorrelation = $false; DurationSeconds = 300
                    Caps = '20pps-256B-burst50pps'; Metrics = [ordered]@{}
                    Lifecycle = if (-not $preflight.Ready) { 'EndpointUnavailable' } else { 'MeasurementGated' }
                }
                Write-Output ($clean | ConvertTo-Json -Depth 4 -Compress)
                break
            }

            Invoke-Command -Session $s -ArgumentList $remoteRoot -ScriptBlock {
                param($dir)
                if (-not $dir.StartsWith('C:\r4review\loadtest\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid fixed load-test directory.' }
                New-Item -ItemType Directory -Path $dir -Force | Out-Null
            }
            Copy-Item -LiteralPath $archive -Destination $remoteArchive -ToSession $s -Force
            $result = Invoke-Command -Session $s -ArgumentList $remoteRoot, $remoteArchive, $approvedHash, ([uint64]$preflight.TunBytes) -ScriptBlock {
                param($dir, $zip, $expectedHash, $tunBefore)
                $exe = Join-Path $dir 'VPNRouter.Tools.WinbratLoadGen.exe'
                $stdout = Join-Path $dir 'result.json'
                $stderr = Join-Path $dir 'error.txt'
                $process = $null
                try {
                    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash.ToLower() -ne $expectedHash) {
                        return [ordered]@{ Success = $false; Failure = 'PayloadHashMismatch' }
                    }
                    Expand-Archive -LiteralPath $zip -DestinationPath $dir -Force
                    if (-not (Test-Path -LiteralPath $exe)) { return [ordered]@{ Success = $false; Failure = 'PayloadMissing' } }
                    $process = Start-Process -FilePath $exe -WorkingDirectory $dir -NoNewWindow -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
                    if (-not $process.WaitForExit(330000)) {
                        $process.Kill()
                        return [ordered]@{ Success = $false; Failure = 'PayloadTimeout' }
                    }
                    $process.WaitForExit()
                    $process.Refresh()
                    if (-not (Test-Path -LiteralPath $stdout)) { return [ordered]@{ Success = $false; Failure = 'PayloadOutputMissing' } }
                    if ((Get-Item -LiteralPath $stdout).Length -eq 0) { return [ordered]@{ Success = $false; Failure = 'PayloadOutputEmpty' } }
                    try { $metrics = Get-Content -LiteralPath $stdout -Raw | ConvertFrom-Json }
                    catch {
                        if ($process.ExitCode -ne 0) { return [ordered]@{ Success = $false; Failure = 'PayloadExitNonZero' } }
                        return [ordered]@{ Success = $false; Failure = 'PayloadResultInvalid' }
                    }

                    $corePath = 'C:\ProgramData\VPNRouter\bin\sing-box.exe'
                    $coreCount = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
                        ([string]$_.ExecutablePath) -ieq $corePath
                    }).Count
                    $tun = Get-NetAdapter -Name 'VPNRouter-TUN' -ErrorAction SilentlyContinue | Select-Object -First 1
                    $stats = if ($tun -and $tun.Status -eq 'Up') { Get-NetAdapterStatistics -Name 'VPNRouter-TUN' -ErrorAction SilentlyContinue } else { $null }
                    $tunAfter = if ($stats) { [uint64]$stats.ReceivedBytes + [uint64]$stats.SentBytes } else { [uint64]0 }
                    [ordered]@{
                        Success = $true
                        PayloadStatus = [string]$metrics.Status
                        CoreStable = [bool]($coreCount -eq 1 -and $tun -and $tun.Status -eq 'Up')
                        TunCorrelation = [bool]($tunAfter -gt [uint64]$tunBefore)
                        Sent = [int]$metrics.Sent; Received = [int]$metrics.Received; Loss = [int]$metrics.Loss
                        Duplicate = [int]$metrics.Duplicate; Reorder = [int]$metrics.Reorder
                        Corruption = [int]$metrics.Corruption; Unknown = [int]$metrics.Unknown
                        RttP50Ms = [double]$metrics.RttP50Ms; RttP95Ms = [double]$metrics.RttP95Ms
                        RttP99Ms = [double]$metrics.RttP99Ms; MaxAcknowledgedGapMs = [double]$metrics.MaxAcknowledgedGapMs
                    }
                }
                finally {
                    if ($process -and -not $process.HasExited) { try { $process.Kill() } catch { } }
                }
            }

            $metrics = [ordered]@{}
            $tunCorrelation = $false
            $passed = $false
            $lifecycle = 'PayloadFailed'
            if ([bool]$result.Success) {
                foreach ($name in @('Sent','Received','Loss','Duplicate','Reorder','Corruption','Unknown','RttP50Ms','RttP95Ms','RttP99Ms','MaxAcknowledgedGapMs')) {
                    $metrics[$name] = $result.$name
                }
                $tunCorrelation = [bool]$result.TunCorrelation
                $payloadStatus = [string]$result.PayloadStatus
                if ($payloadStatus -notin @('Completed', 'ReplyGap', 'CookieFailure', 'NetworkFailure', 'InternalFailure')) {
                    $payloadStatus = 'InternalFailure'
                }
                $passed = [bool]($payloadStatus -eq 'Completed' -and $result.CoreStable -and $tunCorrelation -and
                    $result.Received -gt 0 -and $result.Corruption -eq 0 -and $result.Unknown -eq 0)
                $lifecycle = $payloadStatus
            }
            else {
                $candidate = [string]$result.Failure
                if ($candidate -in @('PayloadHashMismatch', 'PayloadMissing', 'PayloadTimeout', 'PayloadExitNonZero', 'PayloadOutputMissing', 'PayloadOutputEmpty', 'PayloadFailed', 'PayloadResultInvalid')) {
                    $lifecycle = $candidate
                }
            }
            $clean = [ordered]@{
                Status = if ($passed) { 'PASS' } else { 'FAIL' }
                Profile = 'GameUdp'; RouteScope = 'Tunnel'; FullTunnel = $true
                TunCorrelation = $tunCorrelation; DurationSeconds = 300
                Caps = '20pps-256B-burst50pps'; Metrics = $metrics
                Lifecycle = $lifecycle
            }
            Write-Output ($clean | ConvertTo-Json -Depth 4 -Compress)
        }
        finally {
            try {
                Invoke-Command -Session $s -ArgumentList $remoteRoot -ScriptBlock {
                    param($dir)
                    if (-not $dir.StartsWith('C:\r4review\loadtest\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid fixed load-test cleanup directory.' }
                    if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
                    if (Test-Path -LiteralPath $dir) { throw 'Fixed load-test cleanup failed.' }
                }
            }
            finally { Remove-PSSession $s }
        }
    }

    'altclient' {
        if ($AltClient -ne 'AmneziaWG') { throw 'Only the fixed official AmneziaWG client is approved.' }
        if (-not (Test-Path -LiteralPath $OfficialAwgRemoteHelper -PathType Leaf)) {
            throw 'The fixed official AmneziaWG remote helper is missing.'
        }

        $startedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        $remoteRoot = 'C:\r4review\official-ab\current'
        $remoteMsi = Join-Path $remoteRoot 'amneziawg.msi'
        $remotePayload = Join-Path $remoteRoot 'payload.zip'
        $payloadArchive = Join-Path $Root 'artifacts\brat-loadtest-payload\WinbratLoadGen-win-x64.zip'
        $payloadApproved = Test-ApprovedWinbratLoadPayload

        function Write-LocalAltClientBlock {
            param([Parameter(Mandatory = $true)] [string]$Lifecycle)
            Write-Output ([ordered]@{
                Status = 'BLOCKED'; Client = 'AmneziaWG'; Profile = $AltProfile; Operation = $AltOperation
                Lifecycle = $Lifecycle; StartedUtc = $startedUtc; EndedUtc = [DateTimeOffset]::UtcNow.ToString('o')
                ManagementRouteIntact = $false; ExpectedAdapterRoute = $false
                AdapterByteCorrelation = $false; CleanTeardown = $true; Metrics = [ordered]@{}
            } | ConvertTo-Json -Depth 5 -Compress)
        }

        function Get-RequiredOfficialBoolean {
            param(
                [Parameter(Mandatory = $true)] [object]$InputObject,
                [Parameter(Mandatory = $true)] [string]$Name
            )
            $property = $InputObject.PSObject.Properties[$Name]
            if ($null -eq $property -or $property.Value -isnot [bool]) {
                throw 'Official-client helper returned an invalid boolean proof.'
            }
            return [bool]$property.Value
        }

        function ConvertTo-SafeOfficialMetrics {
            param(
                [object]$InputObject,
                [bool]$RequireComplete
            )
            if ($null -eq $InputObject) {
                if ($RequireComplete) { throw 'Official-client helper omitted the fixed aggregate schema.' }
                return [ordered]@{}
            }

            $safe = [ordered]@{}
            foreach ($name in @('Sent', 'Received', 'Loss', 'Duplicate', 'Reorder', 'Corruption', 'Unknown')) {
                $property = $InputObject.PSObject.Properties[$name]
                if ($null -eq $property) {
                    if ($RequireComplete) { throw 'Official-client helper omitted the fixed aggregate schema.' }
                    continue
                }
                if ($property.Value -isnot [ValueType] -or $property.Value -is [bool]) {
                    throw 'Official-client metrics contain an invalid counter.'
                }
                try { $value = [int64]$property.Value }
                catch { throw 'Official-client metrics contain an invalid counter.' }
                if ($value -lt 0 -or [decimal]$value -ne [decimal]$property.Value) {
                    throw 'Official-client metrics contain an invalid counter.'
                }
                $safe[$name] = $value
            }
            foreach ($name in @('RttP50Ms', 'RttP95Ms', 'RttP99Ms', 'MaxAcknowledgedGapMs')) {
                $property = $InputObject.PSObject.Properties[$name]
                if ($null -eq $property) {
                    if ($RequireComplete) { throw 'Official-client helper omitted the fixed aggregate schema.' }
                    continue
                }
                if ($property.Value -isnot [ValueType] -or $property.Value -is [bool]) {
                    throw 'Official-client metrics contain an invalid duration.'
                }
                try { $value = [double]$property.Value }
                catch { throw 'Official-client metrics contain an invalid duration.' }
                if ($value -lt 0 -or [double]::IsNaN($value) -or [double]::IsInfinity($value)) {
                    throw 'Official-client metrics contain an invalid duration.'
                }
                $safe[$name] = $value
            }
            if ($RequireComplete -and
                ($safe.Received -gt $safe.Sent -or $safe.Loss -ne ($safe.Sent - $safe.Received) -or
                 $safe.RttP50Ms -gt $safe.RttP95Ms -or $safe.RttP95Ms -gt $safe.RttP99Ms)) {
                throw 'Official-client metrics violate fixed aggregate invariants.'
            }
            return $safe
        }

        if ($AltOperation -eq 'Install' -and -not (Test-ApprovedOfficialAwgInstaller)) {
            Write-LocalAltClientBlock -Lifecycle InstallerNotApproved
            break
        }
        if ($AltOperation -eq 'Cycle' -and -not $payloadApproved) {
            Write-LocalAltClientBlock -Lifecycle PayloadNotApproved
            break
        }

        $s = New-VerifiedBratSession
        try {
            if ($AltOperation -in @('Install', 'Cycle')) {
                $preCleanRaw = @(Invoke-Command -Session $s -FilePath $OfficialAwgRemoteHelper -ArgumentList @('Cleanup', $AltProfile))
                $preCleanText = ($preCleanRaw -join [Environment]::NewLine).Trim()
                try { $preClean = $preCleanText | ConvertFrom-Json }
                catch { throw 'Official-client pre-cleanup returned invalid JSON.' }
                $preCleanTeardown = Get-RequiredOfficialBoolean -InputObject $preClean -Name CleanTeardown
                if ([string]$preClean.Status -ne 'PASS' -or [string]$preClean.Lifecycle -ne 'Cleaned' -or
                    [string]$preClean.Client -ne 'AmneziaWG' -or [string]$preClean.Operation -ne 'Cleanup' -or
                    -not $preCleanTeardown) {
                    throw 'Official-client pre-cleanup did not prove a clean state.'
                }
                Invoke-Command -Session $s -ScriptBlock {
                    $dir = 'C:\r4review\official-ab\current'
                    New-Item -ItemType Directory -Path $dir -Force | Out-Null
                }
                if ($AltOperation -eq 'Install') {
                    Copy-Item -LiteralPath $OfficialAwgMsi -Destination $remoteMsi -ToSession $s -Force
                }
                else {
                    Copy-Item -LiteralPath $payloadArchive -Destination $remotePayload -ToSession $s -Force
                }
            }

            $raw = $null
            try {
                $raw = @(Invoke-Command -Session $s -FilePath $OfficialAwgRemoteHelper -ArgumentList @($AltOperation, $AltProfile))
            }
            catch {
                if ($AltOperation -ne 'Cycle') {
                    throw 'Official-client helper failed before a live cycle completed.'
                }

                Remove-PSSession $s -ErrorAction SilentlyContinue
                $s = $null
                $deadline = [DateTimeOffset]::UtcNow.AddMinutes(12)
                $recovered = $false
                $watchdogFired = $false
                $cleanTeardown = $false
                $managementSafe = $false
                do {
                    try {
                        $s = New-VerifiedBratSession
                        $observedWatchdogFired = Invoke-Command -Session $s -ScriptBlock {
                            $state = 'C:\r4review\official-ab\current\watchdog-state.txt'
                            if (-not (Test-Path -LiteralPath $state -PathType Leaf)) { return $false }
                            try { return ((Get-Content -LiteralPath $state -Raw).Trim() -eq 'Fired') }
                            catch { return $false }
                        }
                        if ($observedWatchdogFired -isnot [bool]) { throw 'Official-client watchdog returned an invalid state.' }
                        if ($observedWatchdogFired) { $watchdogFired = $true }
                        $cleanupRaw = @(Invoke-Command -Session $s -FilePath $OfficialAwgRemoteHelper -ArgumentList @('Cleanup', $AltProfile))
                        $cleanupText = ($cleanupRaw -join [Environment]::NewLine).Trim()
                        try { $cleanup = $cleanupText | ConvertFrom-Json }
                        catch { throw 'Official-client recovery cleanup returned invalid JSON.' }
                        $cleanTeardown = Get-RequiredOfficialBoolean -InputObject $cleanup -Name CleanTeardown
                        $managementSafe = Get-RequiredOfficialBoolean -InputObject $cleanup -Name ManagementRouteIntact
                        $recovered = [string]$cleanup.Status -eq 'PASS' -and
                            [string]$cleanup.Lifecycle -eq 'Cleaned' -and
                            [string]$cleanup.Client -eq 'AmneziaWG' -and
                            [string]$cleanup.Operation -eq 'Cleanup' -and
                            $cleanTeardown -and $managementSafe
                    }
                    catch {
                        if ($s) { Remove-PSSession $s -ErrorAction SilentlyContinue; $s = $null }
                    }
                    if (-not $recovered) { Start-Sleep -Seconds 15 }
                } while (-not $recovered -and [DateTimeOffset]::UtcNow -lt $deadline)

                if (-not $recovered) {
                    throw 'WINBRAT did not recover within the fixed official-client watchdog budget.'
                }
                $raw = @([ordered]@{
                    Status = 'ABORTED'; Client = 'AmneziaWG'; Profile = $AltProfile; Operation = 'Cycle'
                    Lifecycle = if ($watchdogFired) { 'WatchdogFired' } else { 'TransportLost' }
                    StartedUtc = $startedUtc; EndedUtc = [DateTimeOffset]::UtcNow.ToString('o')
                    ManagementRouteIntact = $managementSafe; ExpectedAdapterRoute = $false
                    AdapterByteCorrelation = $false; CleanTeardown = $cleanTeardown; Metrics = [ordered]@{}
                } | ConvertTo-Json -Depth 5 -Compress)
            }

            $text = ($raw -join [Environment]::NewLine).Trim()
            try { $result = $text | ConvertFrom-Json }
            catch { throw 'Official-client helper returned invalid JSON.' }

            $statuses = @('PASS', 'FAIL', 'BLOCKED', 'ABORTED')
            $lifecycles = @(
                'Ready', 'InstallerNotApproved', 'ClientNotInstalled', 'ClientBinaryInvalid',
                'FixtureMissing', 'FixtureAttestationMissing', 'FixtureAclUnsafe', 'VpnRouterNotClean',
                'ManagementRouteUnsafe', 'DirtyClientState', 'InstallFailed', 'Installed',
                'EndpointUnavailable', 'RouteNotTunnel', 'TunnelStateUnavailable', 'PayloadNotApproved',
                'PayloadHashMismatch', 'PayloadMissing', 'PayloadTimeout', 'PayloadExitNonZero',
                'PayloadOutputMissing', 'PayloadOutputEmpty', 'PayloadResultInvalid', 'Completed',
                'ReplyGap', 'CookieFailure', 'NetworkFailure', 'PayloadIntegrityFailure', 'InternalFailure', 'WatchdogFired',
                'TransportLost', 'CleanupFailed', 'Cleaned'
            )
            if ([string]$result.Status -notin $statuses -or [string]$result.Lifecycle -notin $lifecycles -or
                [string]$result.Client -ne 'AmneziaWG' -or [string]$result.Profile -ne $AltProfile -or
                [string]$result.Operation -ne $AltOperation) {
                throw 'Official-client helper returned an unapproved enum.'
            }

            $started = [DateTimeOffset]::MinValue
            $ended = [DateTimeOffset]::MinValue
            $dateStyle = [System.Globalization.DateTimeStyles]::RoundtripKind
            $culture = [System.Globalization.CultureInfo]::InvariantCulture
            if (-not [DateTimeOffset]::TryParseExact([string]$result.StartedUtc, 'o', $culture, $dateStyle, [ref]$started) -or
                -not [DateTimeOffset]::TryParseExact([string]$result.EndedUtc, 'o', $culture, $dateStyle, [ref]$ended) -or
                $ended -lt $started) {
                throw 'Official-client helper returned an invalid evidence interval.'
            }

            $managementSafe = Get-RequiredOfficialBoolean -InputObject $result -Name ManagementRouteIntact
            $expectedRoute = Get-RequiredOfficialBoolean -InputObject $result -Name ExpectedAdapterRoute
            $byteCorrelation = Get-RequiredOfficialBoolean -InputObject $result -Name AdapterByteCorrelation
            $cleanTeardown = Get-RequiredOfficialBoolean -InputObject $result -Name CleanTeardown
            if ($AltOperation -eq 'Cycle') {
                if ([string]$result.Status -eq 'PASS' -and [string]$result.Lifecycle -ne 'Completed') {
                    throw 'Official-client PASS did not complete the fixed payload.'
                }
                if ([string]$result.Status -eq 'FAIL' -and [string]$result.Lifecycle -notin @('ReplyGap', 'CookieFailure', 'NetworkFailure')) {
                    throw 'Official-client FAIL was not a classified network measurement.'
                }
                if ([string]$result.Status -ne 'FAIL' -and [string]$result.Lifecycle -in @('ReplyGap', 'CookieFailure', 'NetworkFailure')) {
                    throw 'Official-client network lifecycle was not a measured failure.'
                }
                if (([string]$result.Lifecycle -eq 'PayloadIntegrityFailure') -ne ([string]$result.Status -eq 'ABORTED')) {
                    throw 'Official-client integrity lifecycle was not fail-closed.'
                }
                if ([string]$result.Status -in @('PASS', 'FAIL') -and
                    (-not $managementSafe -or -not $expectedRoute -or -not $byteCorrelation -or -not $cleanTeardown)) {
                    throw 'Official-client network result lost route, byte or teardown attribution.'
                }
            }

            $metricsProperty = $result.PSObject.Properties['Metrics']
            $cleanMetrics = ConvertTo-SafeOfficialMetrics -InputObject $(if ($metricsProperty) { $metricsProperty.Value } else { $null }) -RequireComplete ([string]$result.Status -in @('PASS', 'FAIL'))

            $sanitized = [ordered]@{
                Status = [string]$result.Status; Client = 'AmneziaWG'; Profile = $AltProfile; Operation = $AltOperation
                Lifecycle = [string]$result.Lifecycle; StartedUtc = $started.ToUniversalTime().ToString('o')
                EndedUtc = $ended.ToUniversalTime().ToString('o'); ManagementRouteIntact = $managementSafe
                ExpectedAdapterRoute = $expectedRoute; AdapterByteCorrelation = $byteCorrelation
                CleanTeardown = $cleanTeardown; Metrics = $cleanMetrics
            }
            Write-Output ($sanitized | ConvertTo-Json -Depth 5 -Compress)
        }
        finally { if ($s) { Remove-PSSession $s -ErrorAction SilentlyContinue } }
    }

    'lifecycle' {
        if (-not $SinceUtc) { throw 'lifecycle requires -SinceUtc in round-trip ISO-8601 format.' }
        $since = [DateTimeOffset]::MinValue
        if (-not [DateTimeOffset]::TryParseExact(
            $SinceUtc,
            'o',
            [System.Globalization.CultureInfo]::InvariantCulture,
            [System.Globalization.DateTimeStyles]::RoundtripKind,
            [ref]$since)) {
            throw 'SinceUtc must use round-trip ISO-8601 format.'
        }
        if ($since -gt [DateTimeOffset]::UtcNow.AddMinutes(1) -or
            $since -lt [DateTimeOffset]::UtcNow.AddHours(-24)) {
            throw 'SinceUtc must be within the last 24 hours and not in the future.'
        }

        $s = New-VerifiedBratSession
        try {
            $summary = Invoke-Command -Session $s -ArgumentList $since.ToString('o') -ScriptBlock {
                param($sinceText)
                $since = [DateTimeOffset]::ParseExact(
                    $sinceText,
                    'o',
                    [System.Globalization.CultureInfo]::InvariantCulture,
                    [System.Globalization.DateTimeStyles]::RoundtripKind)
                $logDir = 'C:\ProgramData\VPNRouter\logs'
                if (-not (Test-Path $logDir)) { throw 'Lifecycle log source is unavailable.' }

                $events = @()
                $counts = @{}
                $errorCount = 0
                $fatalCount = 0
                $unknownErrorCount = 0
                $recentCount = 0
                $maxLines = 50000
                $files = @(Get-ChildItem $logDir -Filter 'vpnrouter*.log' -File | Sort-Object LastWriteTime)
                if (-not $files) { throw 'Lifecycle log source is unavailable.' }

                foreach ($file in $files) {
                    $lines = @(Get-Content $file.FullName -Tail $maxLines)
                    $include = $false
                    $at = $null
                    foreach ($line in $lines) {
                        if ($line -match '^(?<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2})') {
                            $parsed = [DateTimeOffset]::MinValue
                            $ok = [DateTimeOffset]::TryParseExact(
                                $Matches.timestamp,
                                'yyyy-MM-dd HH:mm:ss.fff zzz',
                                [System.Globalization.CultureInfo]::InvariantCulture,
                                [System.Globalization.DateTimeStyles]::None,
                                [ref]$parsed)
                            $include = $ok -and $parsed -ge $since
                            $at = if ($include) { $parsed } else { $null }
                            if ($include) {
                                $recentCount++
                                if ($recentCount -gt $maxLines) {
                                    throw 'Lifecycle window exceeds the bounded line cap.'
                                }
                            }
                        }
                        if (-not $include -or -not $at) { continue }

                        $isErrorLine = $line -match '\[ERR\]|Exception|FATAL'
                        if ($isErrorLine) {
                            $errorCount++
                            if ($line -match 'FATAL') { $fatalCount++ }
                        }

                        $kind = $null
                        if ($line -match '\[VpnEngine\].*sing-box started|TUN interface ready|TUN ready') {
                            $kind = if ($line -match 'TUN') { 'TunReady' } else { 'CoreStarted' }
                        }
                        elseif ($line -match '\[HealthMonitor\] Started') { $kind = 'MonitorStarted' }
                        elseif ($line -match '\[HealthMonitor\] Stopped') { $kind = 'MonitorStopped' }
                        elseif ($line -match '\[HealthMonitor\] Health check failed') { $kind = 'HealthFailed' }
                        elseif ($line -match '\[HealthMonitor\] sing-box WEDGED') { $kind = 'CoreWedged' }
                        elseif ($line -match '\[HealthMonitor\] Restarting sing-box') { $kind = 'RestartRequested' }
                        elseif ($line -match '\[HealthMonitor\] sing-box restarted successfully') { $kind = 'RestartSucceeded' }
                        elseif ($line -match '\[HealthMonitor\].*requesting failover') { $kind = 'FailoverRequested' }
                        elseif ($line -match '\[AutoFailover\].*switched|AutoFailover.*commit') { $kind = 'FailoverCommitted' }
                        elseif ($line -match '\[HealthMonitor\] VPN is up') { $kind = 'HealthRecovered' }
                        if ($isErrorLine -and -not $kind) { $unknownErrorCount++ }

                        if ($kind) {
                            if (-not $counts.ContainsKey($kind)) { $counts[$kind] = 0 }
                            $counts[$kind]++
                            $events += [pscustomobject]@{ AtUtc = $at.ToUniversalTime().ToString('o'); Kind = $kind }
                        }
                    }
                }
                if ($recentCount -eq 0) { throw 'Lifecycle window contains no timestamped entries.' }

                [ordered]@{
                    SinceUtc          = $since.ToUniversalTime().ToString('o')
                    RecentEntryCount  = $recentCount
                    EventCounts       = @($counts.GetEnumerator() | ForEach-Object {
                        [pscustomobject]@{ Kind = [string]$_.Key; Count = [int]$_.Value }
                    })
                    Events            = $events
                    ErrorCount        = $errorCount
                    FatalCount        = $fatalCount
                    UnknownErrorCount = $unknownErrorCount
                }
            }
            $cleanCounts = [ordered]@{}
            foreach ($pair in @($summary.EventCounts)) {
                $cleanCounts[[string]$pair.Kind] = [int]$pair.Count
            }
            $cleanEvents = @($summary.Events | ForEach-Object {
                [ordered]@{ AtUtc = [string]$_.AtUtc; Kind = [string]$_.Kind }
            })
            $cleanSummary = [ordered]@{
                SinceUtc          = [string]$summary.SinceUtc
                RecentEntryCount  = [int]$summary.RecentEntryCount
                EventCounts       = $cleanCounts
                Events            = $cleanEvents
                ErrorCount        = [int]$summary.ErrorCount
                FatalCount        = [int]$summary.FatalCount
                UnknownErrorCount = [int]$summary.UnknownErrorCount
            }
            Write-Output ($cleanSummary | ConvertTo-Json -Depth 6 -Compress)
        }
        finally { Remove-PSSession $s }
    }

    'logs' {
        $s = New-VerifiedBratSession
        try {
            $sinceText = [DateTimeOffset]::Now.AddMinutes(-$LogWindowMinutes).ToString('o', [System.Globalization.CultureInfo]::InvariantCulture)
            $scan = Invoke-Command -Session $s -ArgumentList $sinceText -ScriptBlock {
                param($sinceText)
                $since = [DateTimeOffset]::ParseExact(
                    $sinceText,
                    'o',
                    [System.Globalization.CultureInfo]::InvariantCulture,
                    [System.Globalization.DateTimeStyles]::RoundtripKind)
                $dir = 'C:\ProgramData\VPNRouter\logs'
                if (-not (Test-Path $dir)) { return @{ Found = $false; File = $null; Hits = @(); Note = "no log dir at $dir" } }
                $allFiles = @(Get-ChildItem $dir -Filter 'vpnrouter*.log' -File | Sort-Object LastWriteTime)
                if (-not $allFiles) { return @{ Found = $false; File = $null; Hits = @(); Note = "no vpnrouter*.log in $dir" } }
                $files = @($allFiles | Where-Object { $_.LastWriteTimeUtc -ge $since.UtcDateTime })
                if (-not $files) {
                    return @{ Found = $false; File = $allFiles[-1].Name; Hits = @(); Note = "no log entries since $since" }
                }
                $hits = @()
                $recentEntryCount = 0
                $maxLines = 50000
                foreach ($f in $files) {
                    $lines = @(Get-Content $f.FullName -Tail $maxLines)
                    $include = $false
                    $oldestParsed = $null
                    foreach ($line in $lines) {
                        if ($line -match '^(?<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2})') {
                            $parsed = [DateTimeOffset]::MinValue
                            $parsedOk = [DateTimeOffset]::TryParseExact(
                                $Matches.timestamp,
                                'yyyy-MM-dd HH:mm:ss.fff zzz',
                                [System.Globalization.CultureInfo]::InvariantCulture,
                                [System.Globalization.DateTimeStyles]::None,
                                [ref]$parsed)
                            if ($parsedOk -and $null -eq $oldestParsed) { $oldestParsed = $parsed }
                            $include = $parsedOk -and $parsed -ge $since
                            if ($include) { $recentEntryCount++ }
                        }
                        if ($include -and $line -match '\[ERR\]|Exception|FATAL') { $hits += "$($f.Name): $line" }
                    }
                    if ($lines.Count -ge $maxLines -and ($null -eq $oldestParsed -or $oldestParsed -ge $since)) {
                        return @{ Found = $false; File = $f.Name; Hits = @(); Note = "verification window exceeds the $maxLines-line safety cap in $($f.Name)" }
                    }
                }
                if ($recentEntryCount -eq 0) {
                    return @{ Found = $false; File = ($files.Name -join ', '); Hits = @(); Note = "no log entries since $since" }
                }
                @{ Found = ($hits.Count -gt 0); File = ($files.Name -join ', '); Hits = $hits; Note = $null }
            }
            if (-not $scan.File) {
                throw "Cannot verify remote logs on $BratMachineName`: $($scan.Note). Failing closed."
            }
            if ($scan.Note) {
                throw "Cannot verify recent remote logs on $BratMachineName`: $($scan.Note). Failing closed."
            }
            if ($scan.Found) {
                Write-Host "[!] $($scan.Hits.Count) error pattern(s) in remote $($scan.File):" -ForegroundColor Red
                $scan.Hits | ForEach-Object { Write-Host "    $_" -ForegroundColor Yellow }
                exit 1
            }
            Write-Host "CLEAN: no [ERR]/Exception/FATAL in the last $LogWindowMinutes minute(s) of remote $($scan.File)." -ForegroundColor Green
        }
        finally { Remove-PSSession $s }
    }
}
