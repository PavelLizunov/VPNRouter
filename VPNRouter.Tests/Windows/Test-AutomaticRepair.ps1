# Execute only the shared repair dispatch function; network, elevation, and process launch are fakes.
$ErrorActionPreference = 'Stop'
$root = (Get-Item -LiteralPath $PSScriptRoot).Parent.Parent.FullName
$source = Join-Path $root 'VPNRouter.GUI/repair-bootstrap.ps1'
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($source, [ref]$null, [ref]$errors)
if ($errors.Count) { throw ($errors.Message -join [Environment]::NewLine) }
foreach ($name in @('Invoke-AutomaticRepair','Invoke-ElevatedRepair','Set-RepairKnownFolders')) {
    $function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if (!$function) { throw "Missing repair step $name." }
    . ([scriptblock]::Create($function.Extent.Text))
}

$fixtureDirectory = [IO.Directory]::CreateDirectory((Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))))
$application = Join-Path $fixtureDirectory.FullName "Relocated install & O'Brien %"
$expectedInstaller = Join-Path $fixtureDirectory.FullName 'install.ps1'
$events = New-Object 'Collections.Generic.List[string]'
function Invoke-WebRequest {
    param([string]$Uri, [int]$MaximumRedirection, [switch]$UseBasicParsing, [string]$OutFile)
    if ($Uri -cne 'https://vpn.ninitux.com/install.ps1' -or $MaximumRedirection -ne 0 -or $OutFile -ne $expectedInstaller -or
        $env:TEMP -ne $fixtureDirectory.FullName -or $env:TMP -ne $fixtureDirectory.FullName) { throw 'Unexpected installer acquisition.' }
    $events.Add('download')
    if ($downloadFailure) { throw 'fixture download failure' }
    [IO.File]::WriteAllText($OutFile, 'fixture payload')
}
function Invoke-FixtureInstaller {
    $expected = @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$expectedInstaller,'-Repair','-RepairApplicationDir',$application)
    if ($channel) { $expected += '-Prerelease' }
    if (($args -join [char]0) -cne ($expected -join [char]0) -or [IO.File]::ReadAllText($expectedInstaller) -ne 'fixture payload') {
        throw 'Wrong repair target, channel, mode or payload.'
    }
    $events.Add('install')
    $global:LASTEXITCODE = $installResult
}
$expectedSystem = Join-Path $fixtureDirectory.FullName 'Windows/System32'
$expectedShell = Join-Path $expectedSystem 'WindowsPowerShell/v1.0/powershell.exe'
$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes('fixture'))
function Start-Process {
    param($FilePath,$Verb,$WorkingDirectory,$WindowStyle,[switch]$PassThru,$ArgumentList)
    if ($FilePath -ne $expectedShell -or $Verb -ne 'RunAs' -or  $WorkingDirectory -ne $expectedSystem -or $WindowStyle -ne 'Hidden' -or !$PassThru -or
        $ArgumentList -cne "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encoded") { throw 'Incorrect elevation handoff.' }
    $result = [pscustomobject]@{ ExitCode = 7 }
    $result | Add-Member ScriptMethod WaitForExit { $script:waitedForInstaller = $true }
    $result | Add-Member ScriptMethod Dispose {}
    $result
}
$oldTemp = $env:TEMP
$oldTmp = $env:TMP
try {
    Set-RepairKnownFolders
    foreach ($pair in @('ProgramData,CommonApplicationData','USERPROFILE,UserProfile','APPDATA,ApplicationData',
                          'LOCALAPPDATA,LocalApplicationData','ProgramFiles,ProgramFiles','ProgramFiles(x86),ProgramFilesX86')) {
        $parts = $pair.Split(',')
        if ([string][Environment]::GetEnvironmentVariable($parts[0]) -ne [Environment]::GetFolderPath($parts[1])) {
            throw "Incorrect known folder for $($parts[0])."
        }
    }
    $invocation = @($expectedShell, '-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-EncodedCommand',$encoded)
    if ((Invoke-ElevatedRepair $expectedShell $expectedSystem $invocation) -ne 7 -or !$waitedForInstaller) { throw 'Lost elevation result.' }
    $installResult = 0
    $downloadFailure = $false
    foreach ($channel in @($false, $true)) {
        $events.Clear()
        Invoke-AutomaticRepair $application $channel $fixtureDirectory.FullName 'Invoke-FixtureInstaller'
        if (($events -join ',') -ne 'download,install') { throw 'Dispatch order differs.' }
    }
    $channel = $false
    $installResult = 12
    $failed = $false
    try { Invoke-AutomaticRepair $application $channel $fixtureDirectory.FullName 'Invoke-FixtureInstaller' }
    catch { $failed = $_.Exception.Message -like '*code 12*' }
    if (!$failed) { throw 'Installer failure not reported.' }
    $events.Clear()
    $downloadFailure = $true
    $failed = $false
    try { Invoke-AutomaticRepair $application $channel $fixtureDirectory.FullName 'Invoke-FixtureInstaller' }
    catch { $failed = $_.Exception.Message -eq 'fixture download failure' }
    if (!$failed -or ($events -join ',') -ne 'download') { throw 'Installer was started after download failure.' }
    'Automatic repair: known folders, elevation, stable, prerelease, failed installer and failed download passed.'
} finally {
    $env:TEMP = $oldTemp
    $env:TMP = $oldTmp
    $fixtureDirectory.Delete($true)
}
