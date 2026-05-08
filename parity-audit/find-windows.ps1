Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public class Win32 {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr hWnd, StringBuilder s, int n);
}
"@

$rows = New-Object 'System.Collections.Generic.List[psobject]'
$cb = [Win32+EnumWindowsProc]{
    param($hwnd, $lparam)
    $procId = 0
    [Win32]::GetWindowThreadProcessId($hwnd, [ref]$procId) | Out-Null
    $proc = Get-Process -Id $procId -ErrorAction SilentlyContinue
    $procName = if ($proc) { $proc.ProcessName } else { "<gone>" }
    if ($procName -match "VPNRouter|sing-box|MainWindow|Avalonia") {
        $titleLen = [Win32]::GetWindowTextLength($hwnd)
        $sb = New-Object System.Text.StringBuilder ($titleLen + 2)
        [Win32]::GetWindowText($hwnd, $sb, $sb.Capacity) | Out-Null
        $cls = New-Object System.Text.StringBuilder 256
        [Win32]::GetClassName($hwnd, $cls, 256) | Out-Null
        $vis = [Win32]::IsWindowVisible($hwnd)
        $rows.Add([pscustomobject]@{
            HWnd = "0x$($hwnd.ToString('X'))"
            Pid = $procId
            Proc = $procName
            Title = $sb.ToString()
            Class = $cls.ToString()
            Visible = $vis
        }) | Out-Null
    }
    return $true
}
[Win32]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
$rows | Format-Table -AutoSize | Out-String | Write-Host
Write-Host "Total: $($rows.Count) windows"
