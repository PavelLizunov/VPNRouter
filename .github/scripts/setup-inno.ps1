$ErrorActionPreference = 'Stop'

$compiler = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
if (!(Test-Path -LiteralPath $compiler -PathType Leaf)) {
  $setup = Join-Path $env:RUNNER_TEMP 'innosetup-6.7.4.exe'
  Invoke-WebRequest 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_4/innosetup-6.7.4.exe' -OutFile $setup
  if ((Get-FileHash -Algorithm SHA256 $setup).Hash -ne 'a549929319f9120716677c685535ca4671e4b937b5fdc87e2585f32d8eb30e8c') {
    throw 'Inno Setup installer checksum mismatch'
  }
  $toolsDir = Join-Path $env:RUNNER_TEMP 'inno-test-compiler'
  $install = Start-Process -FilePath $setup -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOICONS /DIR=`"$toolsDir`"" -Wait -PassThru
  if ($install.ExitCode -ne 0) { throw "Inno Setup installation failed with $($install.ExitCode)" }
  $compiler = Join-Path $toolsDir 'ISCC.exe'
}
if (!(Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'Inno Setup compiler is missing' }
$compiler
