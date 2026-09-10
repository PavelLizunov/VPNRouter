#requires -Version 5.1
# Read-only window probe. Parent runs this in an Interactive Highest tester task.
# Never launches or stops the app; prints no window title, class text or contents.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$result = [ordered]@{ Success = $false; ProcessIdentity = $false; InteractiveTester = $false; WindowFound = $false; Visible = $false; GenericTitle = $false; AvaloniaClass = $false; MultipleWindows = $false }
try {
    if ([Environment]::MachineName -cne 'WINBRAT') { throw 'Wrong machine.' }
    $sid = ([Security.Principal.NTAccount]::new('WINBRAT', 'tester')).Translate([Security.Principal.SecurityIdentifier]).Value
    if ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -cne $sid -or [Diagnostics.Process]::GetCurrentProcess().SessionId -ne 1) { throw 'Interactive tester required.' }
    $result.InteractiveTester = $true
    $p = Get-CimInstance Win32_Process -Filter 'ProcessId=6324'
    if ($null -eq $p -or $p.Name -cne 'VPNRouter.App.exe' -or $p.ExecutablePath -ine 'C:\Program Files\VPNRouter\app\VPNRouter.App.exe' -or $p.SessionId -ne 1) { throw 'Wrong process.' }
    $owner = Invoke-CimMethod -InputObject $p -MethodName GetOwnerSid
    if ($owner.ReturnValue -ne 0 -or $owner.Sid -cne $sid) { throw 'Wrong owner.' }
    $result.ProcessIdentity = $true
    Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class PreviewWindowProbe {
 private delegate bool EnumProc(IntPtr window, IntPtr data);
 [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr data);
 [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
 [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int capacity);
 public static int[] Probe(uint process) {
  int count=0, visible=0, generic=0, avalonia=0, matching=0;
  bool ok=EnumWindows((window,data)=>{
   uint owner; GetWindowThreadProcessId(window,out owner);
   if(owner!=process) return true;
   count++;
   bool shown=IsWindowVisible(window);
   var title=new StringBuilder(1024); GetWindowText(window,title,title.Capacity);
   var cls=new StringBuilder(256); GetClassName(window,cls,cls.Capacity);
   bool titleOk=String.Equals(title.ToString(),"VPNRouter",StringComparison.Ordinal);
   bool classOk=cls.ToString().StartsWith("Avalonia",StringComparison.Ordinal);
   if(shown) visible++;
   if(titleOk) generic++;
   if(classOk) avalonia++;
   if(shown && titleOk) matching++;
   return true;
  },IntPtr.Zero);
  if(!ok) throw new InvalidOperationException();
  return new[]{count,visible,generic,avalonia,matching};
 }
}
'@
    $observed = [PreviewWindowProbe]::Probe(6324)
    $result.WindowFound = $observed[0] -gt 0
    $result.Visible = $observed[1] -gt 0
    $result.GenericTitle = $observed[2] -gt 0
    $result.AvaloniaClass = $observed[3] -gt 0
    $result.MultipleWindows = $observed[0] -gt 1
    # Require visible and generic title on the SAME top-level window.
    $result.Success = $observed[4] -gt 0
} catch { $result.Success = $false }
# stdout is the receipt. Parent may redirect it to a protected task-owned file.
$result | ConvertTo-Json -Compress
if (-not $result.Success) { exit 1 }
