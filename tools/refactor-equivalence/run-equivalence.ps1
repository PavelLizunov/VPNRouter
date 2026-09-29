# Runs the refactor-equivalence check for one commit on a task-owned worker checkout (see README.md).
# Usage: powershell -File run-equivalence.ps1 -Sha <full sha> -Name <short label> [-CorpusOnly] [-Root C:\android-build]
param([Parameter(Mandatory)][string]$Sha, [Parameter(Mandatory)][string]$Name, [switch]$CorpusOnly, [string]$Root = 'C:\android-build')
$ErrorActionPreference = 'Stop'
$R = $Root
$env:DOTNET_ROOT = "$R\dotnet"; $env:PATH = "$R\dotnet;$env:PATH"; $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:NUGET_PACKAGES = "$R\nuget"
$src = "$R\src-$Name"; $rec = "$R\rec-$Name.txt"
git init -q $src; git -C $src remote add origin https://github.com/PavelLizunov/VPNRouter.git  # the exact commit is fetched, never a branch tip
git -C $src fetch -q --depth 1 origin $Sha
git -C $src checkout -q --force FETCH_HEAD
# --- instrumentation (worker copy only) ---
$cs = @'
using System.Security.Cryptography;
using System.Text;
namespace VPNRouter.Core.Services;
internal static class EqRec
{
    private static readonly object Gate = new();
    private static readonly System.Text.RegularExpressions.Regex PathRx = new(@"[A-Za-z]:[\\/][^""]*", System.Text.RegularExpressions.RegexOptions.Compiled);
    internal static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PathRx.Replace(s, "<P>"))))[..16];
    internal static void Full(string key, string text)
    {
        var path = Environment.GetEnvironmentVariable("VPNROUTER_EQREC");
        if (string.IsNullOrEmpty(path)) return;
        var t = PathRx.Replace(text, "<P>").Replace("\r", "").Replace("\n", "\\n");
        lock (Gate) { System.IO.File.AppendAllText(path + ".full", key + "\t" + t + "\n"); }
    }
    internal static void Write(string line)
    {
        var path = Environment.GetEnvironmentVariable("VPNROUTER_EQREC");
        if (string.IsNullOrEmpty(path)) return;
        lock (Gate) { System.IO.File.AppendAllText(path, line + "\n"); }
    }
}
'@
Set-Content "$src\VPNRouter.Core\Services\EqRec.cs" $cs -Encoding UTF8
$f = "$src\VPNRouter.Core\Services\CustomConfigInjector.cs"
$t = Get-Content $f -Raw
$old = 'public static string Inject(string rawJson, IEnumerable<string> processNames, AppSettings settings)' + "`n" + '    {'
if (-not $t.Contains($old)) { $old = $old.Replace("`n", "`r`n") }
if (-not $t.Contains($old)) { throw 'Inject anchor missing' }
$nl = [Environment]::NewLine
$new = 'public static string Inject(string rawJson, IEnumerable<string> processNames, AppSettings settings)' + $nl + '    {' + $nl +
  '        try { var r = InjectOriginal(rawJson, processNames, settings); EqRec.Write("I " + EqRec.Hash(r)); EqRec.Full("I " + EqRec.Hash(r), r); return r; }' + $nl +
  '        catch (Exception ex) { EqRec.Write("IE " + ex.GetType().Name + " " + EqRec.Hash(ex.Message)); throw; }' + $nl + '    }' + $nl + $nl +
  '    private static string InjectOriginal(string rawJson, IEnumerable<string> processNames, AppSettings settings)' + $nl + '    {'
Set-Content $f $t.Replace($old, $new) -NoNewline
$f = "$src\VPNRouter.Core\Services\ConfigGenerator.cs"
$t = Get-Content $f -Raw
$old = 'public static SingBoxConfig Generate('
$idx = $t.IndexOf($old); if ($idx -lt 0) { throw 'Generate anchor missing' }
$t = $t.Substring(0, $idx) + 'public static SingBoxConfig Generate(Profile profile, IEnumerable<string> resolvedProcessNames, AppSettings settings, bool? strictDnsOverride = null, Func<VlessServerEntry, bool>? isServerAlive = null)' + $nl + '    {' + $nl +
  '        try { var r = GenerateOriginal(profile, resolvedProcessNames, settings, strictDnsOverride, isServerAlive); var gt = Serialize(r); EqRec.Write("G " + EqRec.Hash(gt)); EqRec.Full("G " + EqRec.Hash(gt), gt); return r; }' + $nl +
  '        catch (Exception ex) { EqRec.Write("GE " + ex.GetType().Name + " " + EqRec.Hash(ex.Message)); throw; }' + $nl + '    }' + $nl + $nl + '    private static SingBoxConfig GenerateOriginal(' + $t.Substring($idx + $old.Length)
Set-Content $f $t -NoNewline
Copy-Item "$R\EqCorpusTests.cs" "$src\VPNRouter.Tests\EqCorpusTests.cs" -Force
if (Test-Path "$R\EqCorpusVmTests.cs") { Copy-Item "$R\EqCorpusVmTests.cs" "$src\VPNRouter.Tests\EqCorpusVmTests.cs" -Force }
Set-Location $src
Remove-Item $rec, "$rec.full" -ErrorAction SilentlyContinue
$crec = "$R\corpus-$Name.txt"; $crec2 = "$R\corpus2-$Name.txt"
Remove-Item $crec, $crec2, "$crec.full", "$crec2.full" -ErrorAction SilentlyContinue
$ErrorActionPreference = 'Continue'
if (-not $CorpusOnly) {
  $env:VPNROUTER_EQREC = $rec
  & dotnet test VPNRouter.Tests\VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName!~EqCorpus" *> "$R\test-eq-$Name.log"
  "suite exit $LASTEXITCODE"
  "records: " + (Get-Content $rec | Measure-Object).Count
}
$env:VPNROUTER_EQREC = $crec
$noBuild = if ($CorpusOnly) { @() } else { @('--no-build') }
& dotnet test VPNRouter.Tests\VPNRouter.Tests.csproj -c Release @noBuild --filter "FullyQualifiedName~EqCorpus" *> "$R\test-corpus-$Name.log"
"corpus exit $LASTEXITCODE"
$env:VPNROUTER_EQREC = $crec2
& dotnet test VPNRouter.Tests\VPNRouter.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~EqCorpus" *> "$R\test-corpus2-$Name.log"
"corpus2 exit $LASTEXITCODE"
"corpus records: " + (Get-Content $crec | Measure-Object).Count + " / " + (Get-Content $crec2 | Measure-Object).Count
Set-Location $R
Remove-Item -LiteralPath $src -Recurse -Force -ErrorAction SilentlyContinue
