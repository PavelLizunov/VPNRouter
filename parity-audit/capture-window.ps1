param(
    [Parameter(Mandatory=$true)][string]$ProcessName,
    [Parameter(Mandatory=$true)][string]$OutputPath
)

# Capture window of a named process to PNG via System.Drawing.
# Handles tray-minimized apps by enumerating ALL windows and restoring.

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Text;
public class Win32 {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

$procs = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue
if (-not $procs) {
    Write-Error "No process '$ProcessName' found."
    exit 1
}
$pids = $procs | Select-Object -ExpandProperty Id
Write-Host "Process IDs: $($pids -join ', ')"

# Enumerate ALL top-level windows belonging to the process
$found = @()
$enum = {
    param($hwnd, $lparam)
    $procId = 0
    [Win32]::GetWindowThreadProcessId($hwnd, [ref]$procId) | Out-Null
    if ($pids -contains $procId) {
        $len = [Win32]::GetWindowTextLength($hwnd)
        $sb = New-Object System.Text.StringBuilder ($len + 1)
        [Win32]::GetWindowText($hwnd, $sb, $sb.Capacity) | Out-Null
        $vis = [Win32]::IsWindowVisible($hwnd)
        $script:found += [pscustomobject]@{ HWnd = $hwnd; Title = $sb.ToString(); Visible = $vis; Pid = $procId }
    }
    return $true
}
$cb = [Win32+EnumWindowsProc]$enum
[Win32]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null

Write-Host "Windows found:"
$found | Format-Table | Out-String | Write-Host

# Pick a window: prefer titled + visible; else titled hidden; else first
$target = $found | Where-Object { $_.Title -ne "" -and $_.Visible } | Select-Object -First 1
if (-not $target) { $target = $found | Where-Object { $_.Title -ne "" } | Select-Object -First 1 }
if (-not $target) { $target = $found | Select-Object -First 1 }
if (-not $target) {
    Write-Error "No top-level windows for process."
    exit 2
}

$hwnd = $target.HWnd
[Win32]::ShowWindow($hwnd, 9) | Out-Null  # SW_RESTORE
[Win32]::ShowWindow($hwnd, 5) | Out-Null  # SW_SHOW
[Win32]::BringWindowToTop($hwnd) | Out-Null
[Win32]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 800

$rect = New-Object Win32+RECT
[Win32]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top

if ($w -le 0 -or $h -le 0) {
    Write-Error "Window has invalid dimensions: ${w}x${h} (left=$($rect.Left) top=$($rect.Top))"
    exit 3
}

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, [System.Drawing.Size]::new($w, $h))
$bmp.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose()
$bmp.Dispose()

Write-Host "OK: '$($target.Title)' ${w}x${h} @ ($($rect.Left),$($rect.Top)) -> $OutputPath"
