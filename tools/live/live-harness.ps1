# Live functional harness for the real VPNRouter desktop app (Windows, interactive session).
# Drives the running app through UI Automation, times what it does and writes a JSON report plus screenshots.
# Run it through live-run.ps1 (a scheduled task in the interactive session). Scenarios:
#   tabs     every main tab and every inner tab / section / segment, a screenshot of each, the app must stay alive
#   cycles   N connect -> connected -> disconnect -> stopped cycles with timings (and the public IP before/during/after in full-tunnel mode)
#   servers  connect to every server of the subscription one by one, timing each and checking the data plane
#   ping     Test all and Deep verify on the Subscribe tab with timings and per-row results, disconnected and connected
#   dump     write every visible control of one tab (-Tab servers|subscribe|settings|apps|tools|public) to dump-<tab>.txt, for debugging selectors
#   versions open Settings > Updates > Other versions on the experimental channel and read the list of older releases (never installs one)
#   modes    switch Selected apps / All traffic N times while connected; the window must never fall back to "Connect"
param(
    [Parameter(Mandatory = $true)][ValidateSet('tabs', 'cycles', 'servers', 'ping', 'modes', 'dump', 'versions')][string]$Scenario,
    [int]$Count = 3,
    [string]$OutDir = 'C:\android-build\live',
    [int]$MaxServers = 20,
    [string]$Tab = 'settings'
)

Set-StrictMode -Off
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Runtime.InteropServices;
public class LiveW32 {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
}
"@
[LiveW32]::SetProcessDPIAware() | Out-Null

$RunId = (Get-Date).ToString('yyyyMMdd-HHmmss') + '-' + $Scenario
$Run = Join-Path $OutDir $RunId
New-Item -ItemType Directory -Force $Run | Out-Null
$Started = Get-Date
$Steps = New-Object System.Collections.ArrayList
$ae = [System.Windows.Automation.AutomationElement]
$tree = [System.Windows.Automation.TreeScope]

function Add-Step($name, $ok, $ms, $detail, $shot) {
    [void]$Steps.Add([ordered]@{ name = $name; ok = [bool]$ok; ms = [int]$ms; detail = $detail; shot = $shot })
    $shotNote = if ($shot) { " [shot: $shot]" } else { '' }
    Add-Content -Encoding UTF8 (Join-Path $Run 'steps.log') ("{0} {1} {2} ms {3}{4}" -f ($(if ($ok) { 'OK  ' } else { 'FAIL' })), $name, [int]$ms, $detail, $shotNote)
}

$script:Proc = Get-Process VPNRouter.App -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $script:Proc) { Add-Step 'app running' $false 0 'VPNRouter.App is not running' $null }
else {
    $script:Hwnd = $script:Proc.MainWindowHandle
    [LiveW32]::ShowWindow($script:Hwnd, 9) | Out-Null
    [LiveW32]::SetForegroundWindow($script:Hwnd) | Out-Null
}
$script:ProcCond = if ($script:Proc) { New-Object System.Windows.Automation.PropertyCondition($ae::ProcessIdProperty, [int]$script:Proc.Id) } else { $null }
function Win { $ae::RootElement.FindFirst($tree::Children, $script:ProcCond) }

function Scan {
    # visible interesting elements: name, type, rectangle
    $w = Win
    if (-not $w) { return @() }
    $types = 'Button', 'ListItem', 'RadioButton', 'TabItem', 'CheckBox', 'Text'
    $conds = foreach ($t in $types) { New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::($t)) }
    $or = New-Object System.Windows.Automation.OrCondition($conds)
    $res = New-Object System.Collections.ArrayList
    $wr = $w.Current.BoundingRectangle
    $found = $null
    for ($try = 0; $try -lt 3 -and -not $found; $try++) { try { $found = $w.FindAll($tree::Descendants, $or) } catch { Start-Sleep -Milliseconds 400 } }
    if (-not $found) { return @() }
    foreach ($e in $found) {
        try {
            if ($e.Current.IsOffscreen) { continue }
            $r = $e.Current.BoundingRectangle
            # coordinates are relative to the window's top left corner
            [void]$res.Add([pscustomobject]@{ Name = $e.Current.Name; Type = $e.Current.ControlType.ProgrammaticName.Replace('ControlType.', ''); X = $r.X - $wr.X; Y = $r.Y - $wr.Y; W = $r.Width; H = $r.Height; El = $e })
        } catch { }
    }
    return $res
}
$script:PressError = ''
function Press($el) {
    $p = $null
    $script:PressError = 'no pattern to press with (not selectable, invokable or toggleable)'
    try {
        $sp = $null
        if ($el.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern, [ref]$sp)) { try { $sp.ScrollIntoView() } catch { } }
        if ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$p)) { $p.Select(); return $true }
        if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { $p.Invoke(); return $true }
        if ($el.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$p)) { $p.Toggle(); return $true }
    } catch { $script:PressError = $_.Exception.GetType().Name + ': ' + $_.Exception.Message }
    return $false
}
function Find-One($pattern, $scan) {
    if (-not $scan) { $scan = Scan }
    foreach ($e in $scan) { if ($e.Name -match $pattern) { return $e } }
    return $null
}
# The same control found again after the page may have rebuilt: same name and type, nearest to where it was (several controls can share a name, e.g. a tab and a segment).
function Find-Same($was) {
    $best = $null; $bestD = [double]::MaxValue
    foreach ($e in (Scan)) {
        if ($e.Name -ne $was.Name -or $e.Type -ne $was.Type) { continue }
        $d = [Math]::Abs($e.Y - $was.Y) + [Math]::Abs($e.X - $was.X)
        if ($d -lt $bestD) { $best = $e; $bestD = $d }
    }
    return $best
}
# Controls that scroll out of view (the lower area of the Subscribe page) are "offscreen" for Scan; these look through everything.
function Find-Any($typeName, $pattern) {
    $w = Win
    if (-not $w) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::($typeName))
    $found = $null
    try { $found = $w.FindAll($tree::Descendants, $cond) } catch { return $null }
    foreach ($e in $found) { try { if ($e.Current.Name -match $pattern) { return $e } } catch { } }
    return $null
}
function Alive { [bool](Get-Process -Id $script:Proc.Id -ErrorAction SilentlyContinue) }
function Shot($name) {
    try {
        Start-Sleep -Milliseconds 500
        $r = New-Object LiveW32+RECT
        [LiveW32]::GetWindowRect($script:Hwnd, [ref]$r) | Out-Null
        $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
        $bmp = New-Object System.Drawing.Bitmap $w, $h
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $hdc = $g.GetHdc(); [LiveW32]::PrintWindow($script:Hwnd, $hdc, 2) | Out-Null; $g.ReleaseHdc($hdc)
        $file = ($name -replace '[^0-9A-Za-z._-]', '_') + '.png'
        $bmp.Save((Join-Path $Run $file), [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
        return $file
    } catch { return $null }
}

# --- connection state -------------------------------------------------------------------------------------------------------------------
# the Advanced shell names its button with a leading glyph ("▶  Запустить VPN"): allow non-letters before the label
$ConnectPat = '^[^\p{L}]*(Connect|Подключить|Start VPN|Запустить VPN)$'
$DisconnectPat = '^[^\p{L}]*(Disconnect|Отключить|Stop VPN|Остановить VPN)$'
function Get-VpnState {
    $s = Scan
    if (Find-One $DisconnectPat $s) { return 'connected' }
    if (Find-One $ConnectPat $s) { return 'disconnected' }
    return 'busy'
}
function Wait-VpnState($want, $timeoutSec) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
        if ((Get-VpnState) -eq $want) { return [int]$sw.ElapsedMilliseconds }
        Start-Sleep -Milliseconds 300
    }
    return -1
}
function Click-Cta($pattern) {
    $e = Find-One $pattern $null
    if (-not $e) { return $false }
    return (Press $e.El)
}
function Public-Ip {
    foreach ($url in 'https://api.ipify.org', 'https://ifconfig.me/ip') {
        try {
            $o = (& curl.exe -s --max-time 8 $url) -join ''
            if ($o -match '^\d{1,3}(\.\d{1,3}){3}$') { return $o }
        } catch { }
    }
    return $null
}
function Singbox-Alive { [bool](Get-Process sing-box -ErrorAction SilentlyContinue) }

function Live-LogFiles($since) {
    # NTFS does not refresh the last-write time of a file the app still holds open, so the files are picked by the date in their name
    $days = @($since.Date, $since.Date.AddDays(1), (Get-Date).Date) | ForEach-Object { $_.ToString('yyyyMMdd') } | Select-Object -Unique
    foreach ($d in $days) { Get-ChildItem 'C:\ProgramData\VPNRouter\logs' -Filter "vpnrouter$d*.log" -ErrorAction SilentlyContinue }
}
function Log-Findings {
    # warnings / errors the app wrote since the run started (the app logs unbuffered; several processes may share the day's file)
    $dir = 'C:\ProgramData\VPNRouter\logs'
    $noise = 'Skipping rule for|Split-tunnel:|StartSplittingProcess|StopSplittingProcess|no flow|did not shut down cleanly|driver not loaded|crash tail|\[singbox\]'
    $out = New-Object System.Collections.ArrayList
    foreach ($f in (Live-LogFiles $Started)) {
        try {
            $fs = [System.IO.File]::Open($f.FullName, 'Open', 'Read', 'ReadWrite')
            $sr = New-Object System.IO.StreamReader($fs, [Text.Encoding]::UTF8)
            while (($line = $sr.ReadLine()) -ne $null) {
                if ($line.Length -lt 30) { continue }
                $stamp = $null
                try { $stamp = [datetime]::ParseExact($line.Substring(0, 23), 'yyyy-MM-dd HH:mm:ss.fff', [Globalization.CultureInfo]::InvariantCulture) } catch { continue }
                if ($stamp -lt $Started) { continue }
                if ($line -match '\[(WRN|ERR|FTL)\]' -and $line -notmatch $noise) { [void]$out.Add($line.Substring(0, [Math]::Min(220, $line.Length))) }
            }
            $sr.Close(); $fs.Close()
        } catch { }
    }
    return $out
}
function Phase-Timeline($since) {
    # the app's own account of a connect: when the key steps happened, relative to the button press
    $dir = 'C:\ProgramData\VPNRouter\logs'
    $keys = 'Subscription\] Fetching|Fetched \d+ servers|SmartConnect|ServerHealthProbe\] \d+/\d+|ToggleConnectionAsync\.Connect|Firewall\] Created|Firewall block rules created|Starting sing-box|sing-box started|TUN ready|VpnEngine\] Connected|TrueSplit state="Active"|Stopping\.\.\.|VpnEngine\] Stopped|All VPNRouter firewall rules deleted|DNS leak lockdown disabled'
    $events = New-Object System.Collections.ArrayList
    foreach ($f in (Live-LogFiles $since)) {
        try {
            $fs = [System.IO.File]::Open($f.FullName, 'Open', 'Read', 'ReadWrite')
            $sr = New-Object System.IO.StreamReader($fs, [Text.Encoding]::UTF8)
            while (($line = $sr.ReadLine()) -ne $null) {
                if ($line.Length -lt 30 -or $line -notmatch $keys) { continue }
                $stamp = $null
                try { $stamp = [datetime]::ParseExact($line.Substring(0, 23), 'yyyy-MM-dd HH:mm:ss.fff', [Globalization.CultureInfo]::InvariantCulture) } catch { continue }
                if ($stamp -lt $since) { continue }
                $msg = $line.Substring(31); if ($msg.Length -gt 60) { $msg = $msg.Substring(0, 60) }
                [void]$events.Add([pscustomobject]@{ T = $stamp; M = $msg })
            }
            $sr.Close(); $fs.Close()
        } catch { }
    }
    $sorted = @($events | Sort-Object T)
    if ($sorted.Count -eq 0) { return 'no log lines visible' }
    return (($sorted | ForEach-Object { '+{0:N1}s {1}' -f (($_.T - $since).TotalSeconds), $_.M }) -join ' | ')
}
function Log-Tail($since, $pattern) {
    # app log lines since a moment that match a pattern: (time, text after the level)
    $lines = New-Object System.Collections.ArrayList
    foreach ($f in (Live-LogFiles $since)) {
        try {
            $fs = [System.IO.File]::Open($f.FullName, 'Open', 'Read', 'ReadWrite')
            $sr = New-Object System.IO.StreamReader($fs, [Text.Encoding]::UTF8)
            while (($line = $sr.ReadLine()) -ne $null) {
                if ($line.Length -lt 35 -or $line -notmatch $pattern) { continue }
                $stamp = $null
                try { $stamp = [datetime]::ParseExact($line.Substring(0, 23), 'yyyy-MM-dd HH:mm:ss.fff', [Globalization.CultureInfo]::InvariantCulture) } catch { continue }
                if ($stamp -lt $since) { continue }
                [void]$lines.Add([pscustomobject]@{ T = $stamp; Text = $line.Substring(31) })
            }
            $sr.Close(); $fs.Close()
        } catch { }
    }
    return @($lines | Sort-Object T)
}
function Wait-LogQuiet($since, $pattern, $quietSec, $maxSec) {
    # waits until matching lines appeared and none new came for $quietSec; returns them
    $sw = [Diagnostics.Stopwatch]::StartNew(); $last = 0; $lastChange = Get-Date
    while ($sw.Elapsed.TotalSeconds -lt $maxSec) {
        $n = (Log-Tail $since $pattern).Count
        if ($n -ne $last) { $last = $n; $lastChange = Get-Date }
        elseif ($n -gt 0 -and ((Get-Date) - $lastChange).TotalSeconds -ge $quietSec) { break }
        Start-Sleep -Milliseconds 700
    }
    return (Log-Tail $since $pattern)
}
function Probe-Summary($lines, $since) {
    # "[TcpTlsProbe] NAME HOST:PORT protocol=P status="S" latency=Nms err=E" -> one entry per server, plus the span of the run
    $rows = @{}
    foreach ($l in $lines) {
        if ($l.Text -match '\[TcpTlsProbe\] (.+?) \S+:\d+ protocol=(\S+) status="(\w+)" latency=(\d+)ms err=(.*)$') {
            $err = $Matches[5]; if ($err -eq '-') { $err = '' } elseif ($err.Length -gt 30) { $err = $err.Substring(0, 30) }
            $rows[$Matches[1]] = '{0}={1} {2}ms{3}' -f $Matches[1], $Matches[3], $Matches[4], $(if ($err) { " ($err)" } else { '' })
        }
    }
    if ($lines.Count -eq 0) { return 'no probe lines in the log' }
    $span = ($lines[-1].T - $since).TotalSeconds
    return ('{0} servers, last result +{1:N1}s: {2}' -f $rows.Count, $span, (($rows.Values | Sort-Object) -join '; '))
}
function Deep-Summary($lines, $since) {
    $rows = New-Object System.Collections.ArrayList
    foreach ($l in $lines) {
        if ($l.Text -match '\[VlessDeepVerifier\] (.+?): (PASS http=(\d+)ms bw=([\d.]+|-)|HTTP probe FAILED.*|.*never bound.*)') {
            [void]$rows.Add(('{0}={1}' -f $Matches[1], $(if ($Matches[3]) { "PASS $($Matches[3])ms $(if ($Matches[4] -eq '-') { 'bw not measured' } else { $Matches[4] + 'Mbps' })" } else { 'FAIL' })))
        }
    }
    if ($rows.Count -eq 0) { return 'no deep verify lines in the log' }
    return ('{0} results, last +{1:N1}s: {2}' -f $rows.Count, ($lines[-1].T - $since).TotalSeconds, (($rows | Sort-Object) -join '; '))
}
function Crash-Events {
    try {
        $ev = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $Started } -ErrorAction SilentlyContinue |
            Where-Object { $_.ProviderName -match 'Application Error|\.NET Runtime' -and $_.Message -match 'VPNRouter' }
        return @($ev | ForEach-Object { $_.TimeCreated.ToString('HH:mm:ss') + ' ' + $_.ProviderName })
    } catch { return @() }
}

function Go-Advanced {
    $s = Scan
    if (Find-One '^(Home|Главная)$' $s) { return $true }
    $e = Find-One '^(Расширенные настройки|Advanced settings)' $s
    if ($e) { Press $e.El | Out-Null; Start-Sleep -Milliseconds 1200; return $true }
    return $false
}
function Go-Tab($pattern) {
    $e = Find-One $pattern $null
    if (-not $e) { return $false }
    Press $e.El | Out-Null
    Start-Sleep -Milliseconds 900
    return $true
}
$TabNames = [ordered]@{
    servers = '^(Серверы|Servers)$'; subscribe = '^(Подписка|Subscribe)$'; settings = '^(Настройки|Settings)$'
    apps = '^(Приложения|Applications)$'; tools = '^(Инструменты|Tools)$'; public = '^(Публичные|Public)$'
}
function Page-Top {
    $s = Scan
    $max = 0
    foreach ($n in $TabNames.Values) { $e = Find-One $n $s; if ($e -and ($e.Y + $e.H) -gt $max) { $max = $e.Y + $e.H } }
    return $max
}

# --- scenarios ---------------------------------------------------------------------------------------------------------------------------
function Run-Tabs {
    if (-not (Go-Advanced)) { Add-Step 'enter advanced mode' $false 0 'no Advanced entry found' $null; return }
    $mainNames = ($TabNames.Values -join '|')
    foreach ($tab in $TabNames.Keys) {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $ok = Go-Tab $TabNames[$tab]
        Add-Step "tab $tab" ($ok -and (Alive)) $sw.ElapsedMilliseconds '' (Shot "tab-$tab")
        if (-not $ok) { continue }
        $top = Page-Top
        $s = Scan
        $targets = @()
        switch ($tab) {
            'servers'  { $targets = @($s | Where-Object { $_.Type -eq 'ListItem' -and $_.Y -lt ($top + 70) -and $_.Y -gt $top - 5 }) }
            'settings' { $targets = @($s | Where-Object { $_.Type -eq 'ListItem' -and $_.X -lt 240 -and $_.Y -gt $top - 5 }) }
            'apps'     { $targets = @($s | Where-Object { (($_.Type -eq 'ListItem' -and $_.X -lt 240 -and $_.Y -gt $top + 90) -or $_.Type -eq 'RadioButton') -and $_.Name -notmatch '^VPNRouter\.App\.' } | Select-Object -First 14) }
            'tools'    { $targets = @($s | Where-Object { $_.Type -eq 'ListItem' -and $_.Y -lt ($top + 70) -and $_.Y -gt $top - 5 }) }
            'public'   { $targets = @($s | Where-Object { $_.Type -eq 'ListItem' -and $_.Y -lt ($top + 70) -and $_.Y -gt $top - 5 }) }
        }
        $i = 0
        foreach ($t in $targets) {
            $i++
            $sw.Restart()
            $label = $t.Name
            # the page may have rebuilt its list since the scan: press a freshly found element, not the scanned handle
            $fresh = Find-Same $t
            $pressed = if ($fresh) { Press $fresh.El } else { $script:PressError = 'element no longer on the page'; $false }
            Start-Sleep -Milliseconds 700
            $why = if ($pressed) { $t.Type } else { "$($t.Type): $script:PressError" }
            Add-Step "$tab / $label" ($pressed -and (Alive)) $sw.ElapsedMilliseconds $why (Shot "$tab-$i")
            if ($tab -eq 'tools') {
                # the segments of the Zapret and Telegram pages
                $inner = @(Scan | Where-Object { $_.Type -eq 'RadioButton' })
                $j = 0
                foreach ($r in $inner) {
                    $j++
                    $sw.Restart()
                    $freshR = Find-Same $r
                    $p2 = if ($freshR) { Press $freshR.El } else { $script:PressError = 'element no longer on the page'; $false }
                    Start-Sleep -Milliseconds 600
                    $why2 = if ($p2) { 'RadioButton' } else { "RadioButton: $script:PressError" }
                    Add-Step "tools / $label / $($r.Name)" ($p2 -and (Alive)) $sw.ElapsedMilliseconds $why2 (Shot "tools-$i-$j")
                }
            }
        }
    }
    $homeBtn = Find-One '^(Home|Главная)$' $null
    if ($homeBtn) { Press $homeBtn.El | Out-Null; Start-Sleep -Milliseconds 900; Add-Step 'back to the home screen' (Alive) 0 '' (Shot 'home') }
}

function Ensure-Disconnected {
    if ((Get-VpnState) -eq 'connected') {
        Click-Cta $DisconnectPat | Out-Null
        [void](Wait-VpnState 'disconnected' 180)
    }
}
function Ensure-Home-Or-Bottom {
    # the bottom bar button (Advanced) and the home Connect button both match the patterns
}

function Run-Cycles {
    Ensure-Disconnected
    $ipBefore = Public-Ip
    Add-Step 'public ip before' ($null -ne $ipBefore) 0 "$ipBefore" $null
    for ($i = 1; $i -le $Count; $i++) {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $pressedAt = Get-Date
        $clicked = Click-Cta $ConnectPat
        $t = Wait-VpnState 'connected' 120
        Add-Step "cycle $i connect" ($clicked -and $t -ge 0 -and (Singbox-Alive)) $(if ($t -ge 0) { $t } else { $sw.ElapsedMilliseconds }) ("sing-box alive: $(Singbox-Alive). " + (Phase-Timeline $pressedAt)) (Shot "cycle-$i-connected")
        Start-Sleep -Seconds 6
        $ipDuring = Public-Ip
        $differs = ($ipBefore -and $ipDuring -and $ipBefore -ne $ipDuring)
        Add-Step "cycle $i data plane" $true 0 "ip during: $ipDuring (before: $ipBefore) changed: $differs (a split-tunnel run keeps the IP of non-listed apps)" $null
        $sw.Restart()
        $pressedAt = Get-Date
        $clicked = Click-Cta $DisconnectPat
        $t = Wait-VpnState 'disconnected' 180
        Add-Step "cycle $i disconnect" ($clicked -and $t -ge 0 -and -not (Singbox-Alive)) $(if ($t -ge 0) { $t } else { $sw.ElapsedMilliseconds }) ("sing-box alive: $(Singbox-Alive). " + (Phase-Timeline $pressedAt)) (Shot "cycle-$i-stopped")
        $ipAfter = Public-Ip
        Add-Step "cycle $i ip restored" ($ipAfter -eq $ipBefore) 0 "ip after: $ipAfter" $null
    }
}

function Subscribe-ServerRows {
    if (-not (Go-Advanced)) { return @() }
    [void](Go-Tab $TabNames['subscribe'])
    $top = Page-Top
    $s = Scan
    # server rows: list items below the column header whose text contains a host:port
    $rows = @($s | Where-Object { $_.Type -eq 'ListItem' -and $_.Y -gt $top })
    return $rows
}

function Run-Servers {
    Ensure-Disconnected
    $ipBefore = Public-Ip
    $rows = Subscribe-ServerRows
    $names = @($rows | ForEach-Object { $_.Name } | Select-Object -First $MaxServers)
    Add-Step 'servers listed' ($names.Count -gt 0) 0 "$($names.Count) rows" (Shot 'subscribe-list')
    foreach ($name in $names) {
        [void](Go-Tab $TabNames['subscribe'])
        $row = Find-One ([regex]::Escape($name)) $null
        if (-not $row) { Add-Step "server $name" $false 0 'row not found' $null; continue }
        Press $row.El | Out-Null
        Start-Sleep -Milliseconds 700
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $clicked = Click-Cta $ConnectPat
        $t = Wait-VpnState 'connected' 120
        Start-Sleep -Seconds 5
        $ipDuring = Public-Ip
        Add-Step "server $name connect" ($clicked -and $t -ge 0) $(if ($t -ge 0) { $t } else { $sw.ElapsedMilliseconds }) "ip during: $ipDuring (before $ipBefore)" (Shot "server-$name")
        $sw.Restart()
        Click-Cta $DisconnectPat | Out-Null
        $t2 = Wait-VpnState 'disconnected' 180
        Add-Step "server $name disconnect" ($t2 -ge 0) $(if ($t2 -ge 0) { $t2 } else { $sw.ElapsedMilliseconds }) '' $null
    }
}

function Read-Pings {
    # Every server row of the page, scrolled out of view or not: its name and the ping/verdict text inside it (read from the row's own children).
    $w = Win
    $out = New-Object System.Collections.ArrayList
    if (-not $w) { return $out }
    $liCond = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $txCond = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    $items = $null
    try { $items = $w.FindAll($tree::Descendants, $liCond) } catch { return $out }
    foreach ($li in $items) {
        try {
            $name = $li.Current.Name
            if ($name -notmatch '\(\S+:\d+\)$') { continue }
            $texts = @($li.FindAll($tree::Descendants, $txCond) | ForEach-Object { $_.Current.Name })
            $ping = $texts | Where-Object { $_ -match '^(\d+ ms|UDP \?|×|TLS ×|<5 ms|—)$' } | Select-Object -First 1
            [void]$out.Add([ordered]@{ server = $name; ping = $ping })
        } catch { }
    }
    return $out
}
function Wait-Idle($busyPattern, $deadlineSec) {
    # busy while the button shows its running label (Cancel / Stop); idle again when that label has been gone twice in a row
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $seenBusy = $false; $quiet = 0
    while ($sw.Elapsed.TotalSeconds -lt $deadlineSec) {
        if (Find-Any 'Text' $busyPattern) { $seenBusy = $true; $quiet = 0 }
        else { $quiet++; if ($quiet -ge 2 -and ($seenBusy -or $sw.Elapsed.TotalSeconds -gt 4)) { return [int]$sw.ElapsedMilliseconds } }
        Start-Sleep -Milliseconds 400
    }
    return -1
}
function Format-Pings($pings) { ($pings | ForEach-Object { ($_.server -replace ' \(\S+:\d+\)$', '') + '=' + $_.ping }) -join '; ' }
$TestAllTip = 'TCP \+ TLS (probe to all|проверка всех)'
$TestAllIdle = '^(Test all|Проверить все)$'
$DeepTip = 'Spawn sing-box'
$DeepIdle = '^(Deep verify|Глубокая проверка)$'
function Run-Ping {
    Ensure-Disconnected
    if (-not (Go-Advanced)) { Add-Step 'enter advanced mode' $false 0 '' $null; return }
    [void](Go-Tab $TabNames['subscribe'])
    foreach ($phase in 'disconnected', 'connected') {
        if ($phase -eq 'connected') {
            $pressedAt = Get-Date
            $clicked = Click-Cta $ConnectPat
            $t = Wait-VpnState 'connected' 120
            Add-Step 'connect for the ping test' ($clicked -and $t -ge 0) $t "clicked: $clicked$(if (-not $clicked) { ' (' + $script:PressError + ')' }). $(Phase-Timeline $pressedAt)" (Shot 'ping-connect')
            if ($t -lt 0) { continue }
            [void](Go-Tab $TabNames['subscribe'])
        }
        # the buttons are named by their tooltip; the app log is the reliable account of what was probed and when
        $btn = Find-Any 'Button' $TestAllTip
        if (-not $btn) { Add-Step "test all ($phase)" $false 0 'button not found' (Shot "ping-$phase-nobutton"); continue }
        $t0 = Get-Date
        Press $btn | Out-Null
        $lines = Wait-LogQuiet $t0 '\[TcpTlsProbe\]' 3 120
        Add-Step "test all ($phase)" ($lines.Count -gt 0) $(if ($lines.Count -gt 0) { [int](($lines[-1].T - $t0).TotalMilliseconds) } else { -1 }) (Probe-Summary $lines $t0) (Shot "ping-$phase")
        $dv = Find-Any 'Button' $DeepTip
        if ($dv) {
            $t1 = Get-Date
            Press $dv | Out-Null
            $deepLines = Wait-LogQuiet $t1 '\[VlessDeepVerifier\] .*(PASS http|HTTP probe FAILED|never bound)' 12 300
            Add-Step "deep verify ($phase)" ($deepLines.Count -gt 0) $(if ($deepLines.Count -gt 0) { [int](($deepLines[-1].T - $t1).TotalMilliseconds) } else { -1 }) (Deep-Summary $deepLines $t1) (Shot "deep-$phase")
        }
    }
    Ensure-Disconnected
}

function Find-AllTexts($pattern) {
    $w = Win
    $out = New-Object System.Collections.ArrayList
    if (-not $w) { return $out }
    $cond = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    foreach ($e in $w.FindAll($tree::Descendants, $cond)) { try { if ($e.Current.Name -match $pattern) { [void]$out.Add($e.Current.Name) } } catch { } }
    return $out
}
function Run-Versions {
    Ensure-Disconnected
    if (-not (Go-Advanced)) { Add-Step 'enter advanced mode' $false 0 '' $null; return }
    [void](Go-Tab $TabNames['settings'])
    $upd = Find-One '^(Обновления|Updates)$' $null
    if ($upd) { Press $upd.El | Out-Null; Start-Sleep -Milliseconds 800 }
    Add-Step 'open Settings > Updates' ($null -ne $upd) 0 '' (Shot 'versions-updates')
    $cb = Find-Any 'CheckBox' 'prerelease|experimental|экспериментал'
    if (-not $cb) { Add-Step 'experimental channel checkbox' $false 0 'not found' $null; return }
    $tp = $null
    $wasOn = $false
    if ($cb.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$tp)) {
        $wasOn = ($tp.Current.ToggleState -eq 'On')
        if (-not $wasOn) { $tp.Toggle(); Start-Sleep -Milliseconds 600 }
    }
    Add-Step 'experimental channel on' $true 0 "was on before: $wasOn" $null
    $btn = $w = Win
    $btn = $w.FindFirst($tree::Descendants, (New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, 'UpdateVersionHistoryButton')))
    if (-not $btn) { Add-Step 'version history button' $false 0 'not found' (Shot 'versions-nobutton'); return }
    # the panel may be open from an earlier run: close it first so the press below always opens (and reloads) it
    if (@(Find-AllTexts '^(Hide versions|Скрыть версии)$').Count -gt 0) { Press $btn | Out-Null; Start-Sleep -Milliseconds 600 }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Press $btn | Out-Null
    $rows = @()
    while ($sw.Elapsed.TotalSeconds -lt 40) {
        Start-Sleep -Milliseconds 700
        $rows = @(Find-AllTexts '^v\d+\.\d+\.\d+')
        # the hint line shows "Loading versions..." until the GitHub answer is in
        if ((@(Find-AllTexts '^(Загружаю список|Loading versions)').Count -eq 0) -and $sw.Elapsed.TotalSeconds -gt 2) { break }
    }
    $hint = @(Find-AllTexts 'SHA-256|Подходящих|No eligible|Could not|Не удалось')
    Add-Step 'version list' ($rows.Count -ge 3) $sw.ElapsedMilliseconds "$($rows.Count) rows: $($rows -join ' | '). Hint: $($hint -join ' / ')" (Shot 'versions-list')
    if (-not $wasOn -and $tp) { try { $tp.Toggle() } catch { } ; Add-Step 'experimental channel restored' $true 0 '' $null }
    $close = $w.FindFirst($tree::Descendants, (New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, 'UpdateVersionHistoryButton')))
    if ($close) { Press $close | Out-Null }
}

function Run-Dump {
    if (-not (Go-Advanced)) { Add-Step 'enter advanced mode' $false 0 '' $null; return }
    [void](Go-Tab $TabNames[$Tab])
    $lines = Scan | ForEach-Object { '{0,-12} x={1,5:N0} y={2,5:N0} w={3,5:N0} h={4,4:N0}  {5}' -f $_.Type, $_.X, $_.Y, $_.W, $_.H, $_.Name }
    $lines | Set-Content (Join-Path $Run "dump-$Tab.txt") -Encoding UTF8
    Add-Step "dump $Tab" $true 0 "$(@($lines).Count) controls, page top $(Page-Top)" (Shot "dump-$Tab")
}

function Run-Modes {
    $e = Find-One '^(Home|Главная)$' $null
    if ($e) { Press $e.El | Out-Null; Start-Sleep -Milliseconds 900 }
    Ensure-Disconnected
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Click-Cta $ConnectPat | Out-Null
    $t = Wait-VpnState 'connected' 120
    Add-Step 'connect' ($t -ge 0) $t '' $null
    $targets = '^(All traffic|Весь трафик)$', '^(Selected apps|Приложения)$'
    for ($i = 1; $i -le $Count; $i++) {
        foreach ($pat in $targets) {
            $seg = Find-One $pat $null
            if (-not $seg) { Add-Step "mode $pat" $false 0 'segment not found' $null; continue }
            Press $seg.El | Out-Null
            $sw.Restart(); $sawConnect = $false
            while ($sw.Elapsed.TotalSeconds -lt 20) { if ((Get-VpnState) -eq 'disconnected') { $sawConnect = $true }; Start-Sleep -Milliseconds 400 }
            Add-Step "mode switch $i $pat" (-not $sawConnect -and (Singbox-Alive) -and (Get-VpnState) -eq 'connected') $sw.ElapsedMilliseconds "window showed Connect: $sawConnect; sing-box alive: $(Singbox-Alive)" $null
        }
    }
    Ensure-Disconnected
}

# --- run ---------------------------------------------------------------------------------------------------------------------------------
if ($script:Proc) {
    try {
        switch ($Scenario) {
            'tabs' { Run-Tabs }
            'cycles' { Run-Cycles }
            'servers' { Run-Servers }
            'ping' { Run-Ping }
            'modes' { Run-Modes }
            'dump' { Run-Dump }
            'versions' { Run-Versions }
        }
    } catch {
        Add-Step 'harness error' $false 0 ($_.Exception.Message) $null
    }
}
Add-Step 'app still running' (Alive) 0 '' $null
$findings = @(Log-Findings)
$crashes = @(Crash-Events)
$failed = @($Steps | Where-Object { -not $_.ok })
$report = [ordered]@{
    scenario = $Scenario; run = $RunId; started = $Started.ToString('o'); finished = (Get-Date).ToString('o')
    steps = $Steps; failed = $failed.Count; log_findings = $findings; crash_events = $crashes
}
$report | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $Run 'report.json') -Encoding UTF8
"LIVE-DONE $RunId failed=$($failed.Count) findings=$($findings.Count) crashes=$($crashes.Count)" | Set-Content (Join-Path $Run 'done.txt')
