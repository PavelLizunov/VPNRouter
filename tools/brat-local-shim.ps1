# Lets the unchanged WINBRAT verifier scripts (tools/brat-verify.ps1, tools/brat-stability.ps1, deploy-to-testpc.ps1)
# run ON the verification machine itself: no WinRM, no credential. Dot-source it before calling them. The functions
# shadow the remoting cmdlets (a function wins over a cmdlet), and script blocks run locally in a non-strict child scope,
# like they would in a fresh remote session.
function New-PSSession { [CmdletBinding()] param($ComputerName, $Credential) [pscustomobject]@{ IsLocalShim = $true } }
function Remove-PSSession { [CmdletBinding()] param([Parameter(Position = 0)] $Session) }
function Invoke-Command {
    [CmdletBinding()]
    param($Session, [Parameter(Position = 0)] [scriptblock] $ScriptBlock, [object[]] $ArgumentList)
    & {
        Set-StrictMode -Off
        if ($ArgumentList) { & $ScriptBlock @ArgumentList } else { & $ScriptBlock }
    }
}
function Copy-Item {
    [CmdletBinding(PositionalBinding = $false)]
    param([Parameter(Position = 0)] [string[]] $Path, [string[]] $LiteralPath, [Parameter(Position = 1)] [string] $Destination,
          [switch] $Force, [switch] $Recurse, $ToSession, $FromSession)
    $p = @{ Destination = $Destination }
    if ($Path) { $p.Path = $Path }
    if ($LiteralPath) { $p.LiteralPath = $LiteralPath }
    if ($Force) { $p.Force = $true }
    if ($Recurse) { $p.Recurse = $true }
    Microsoft.PowerShell.Management\Copy-Item @p
}
