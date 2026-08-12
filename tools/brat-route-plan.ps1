[CmdletBinding()]
param([switch]$SelfTest)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-OptionalProperty($value, [string]$name) {
    if ($null -eq $value) { return $null }
    $property = $value.PSObject.Properties[$name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Test-SafeExecutableLeaf([string]$name) {
    if ([string]::IsNullOrWhiteSpace($name) -or $name.Length -gt 128 -or
        -not $name.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($name) -cne $name -or
        $name.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) {
        return $false
    }
    return -not ($name.ToCharArray() | Where-Object { [char]::IsControl($_) } | Select-Object -First 1)
}

function Get-CanonicalProxyTag([string]$tag) {
    foreach ($canonical in @('proxy', 'proxy-udp')) {
        if ([string]::Equals($tag, $canonical, [StringComparison]::Ordinal)) { return $canonical }
    }
    return $null
}

function Test-ContainsOrdinal($values, [string]$expected) {
    foreach ($value in @($values)) {
        if ([string]::Equals([string]$value, $expected, [StringComparison]::Ordinal)) { return $true }
    }
    return $false
}

function Get-OutboundTypeByTag($outbounds, [string]$tag) {
    foreach ($outbound in @($outbounds)) {
        if ([string]::Equals(
            [string](Get-OptionalProperty $outbound 'tag'),
            $tag,
            [StringComparison]::Ordinal)) {
            return [string](Get-OptionalProperty $outbound 'type')
        }
    }
    return $null
}

function Test-ProxyCapableChain(
    [int]$MatchCount,
    [string[]]$Chains,
    [string]$ExpectedTag,
    $Outbounds) {
    if ($MatchCount -ne 1 -or -not (Test-ContainsOrdinal $Chains $ExpectedTag)) { return $false }

    $hasProxyTerminal = $false
    foreach ($tag in @($Chains)) {
        if ([string]::Equals([string]$tag, 'direct', [StringComparison]::Ordinal) -or
            [string]::Equals([string]$tag, 'block', [StringComparison]::Ordinal)) {
            return $false
        }
        $type = Get-OutboundTypeByTag $Outbounds ([string]$tag)
        if ([string]::Equals($type, 'direct', [StringComparison]::Ordinal) -or
            [string]::Equals($type, 'block', [StringComparison]::Ordinal)) {
            return $false
        }
        foreach ($proxyType in @(
            'vless', 'hysteria2', 'tuic', 'trojan', 'shadowsocks', 'wireguard',
            'socks', 'http', 'ssh', 'anytls', 'shadowtls', 'naive')) {
            if ([string]::Equals($type, $proxyType, [StringComparison]::Ordinal)) {
                $hasProxyTerminal = $true
                break
            }
        }
    }
    return $hasProxyTerminal
}

function Resolve-UdpProbePlan($config) {
    foreach ($rule in @($config.route.rules)) {
        $tag = Get-CanonicalProxyTag ([string](Get-OptionalProperty $rule 'outbound'))
        $action = [string](Get-OptionalProperty $rule 'action')
        $network = Get-OptionalProperty $rule 'network'
        $processNames = @(Get-OptionalProperty $rule 'process_name')
        if (-not $tag -or
            ($action -and -not [string]::Equals($action, 'route', [StringComparison]::Ordinal)) -or
            ($network -and -not (Test-ContainsOrdinal $network 'udp'))) {
            continue
        }

        $safeNames = @($processNames | Where-Object { Test-SafeExecutableLeaf ([string]$_) })
        $populatedSelectors = @($rule.PSObject.Properties | Where-Object {
            $_.Name -notin @('action', 'outbound', 'network', 'process_name') -and
            $null -ne $_.Value -and
            (-not ($_.Value -is [string]) -or -not [string]::IsNullOrWhiteSpace([string]$_.Value)) -and
            (-not ($_.Value -is [System.Collections.IEnumerable]) -or $_.Value -is [string] -or @($_.Value).Count -gt 0)
        })
        if ($populatedSelectors.Count -gt 0) { continue }

        if ($safeNames.Count -gt 0) {
            return [pscustomobject]@{ ExpectedTag = $tag; ProcessName = [string]$safeNames[0] }
        }
        if (-not (Get-OptionalProperty $rule 'process_name')) {
            return [pscustomobject]@{ ExpectedTag = $tag; ProcessName = $null }
        }
    }

    $routeFinal = Get-CanonicalProxyTag ([string](Get-OptionalProperty $config.route 'final'))
    if ($routeFinal) {
        return [pscustomobject]@{ ExpectedTag = $routeFinal; ProcessName = $null }
    }
    throw 'No unambiguous UDP route through a canonical proxy outbound was found.'
}

if ($SelfTest) {
    function New-Fixture([object[]]$rules, [string]$final) {
        [pscustomobject]@{ route = [pscustomobject]@{ rules = $rules; final = $final } }
    }
    function Assert-Plan($config, [string]$tag, $processName) {
        $plan = Resolve-UdpProbePlan $config
        if ($plan.ExpectedTag -cne $tag -or $plan.ProcessName -cne $processName) { throw 'RoutePlanFixtureFailed' }
    }

    $sniff = [pscustomobject]@{ action = 'sniff'; timeout = '300ms' }
    Assert-Plan (New-Fixture @($sniff, [pscustomobject]@{
        action = 'route'; outbound = 'proxy'; process_name = @('My App.exe')
    }) 'direct') 'proxy' 'My App.exe'
    $localizedName = (-join @(0x041F,0x0440,0x0438,0x043B,0x043E,0x0436,0x0435,0x043D,0x0438,0x0435 | ForEach-Object { [char]$_ })) + '.exe'
    Assert-Plan (New-Fixture @($sniff, [pscustomobject]@{
        action = 'route'; outbound = 'proxy-udp'; network = 'udp'; process_name = @($localizedName)
    }) 'direct') 'proxy-udp' $localizedName
    Assert-Plan (New-Fixture @($sniff) 'proxy') 'proxy' $null
    Assert-Plan (New-Fixture @($sniff, [pscustomobject]@{
        action = 'route'; outbound = 'proxy-udp'; network = 'udp'
    }) 'proxy') 'proxy-udp' $null
    Assert-Plan (New-Fixture @($sniff, [pscustomobject]@{
        action = 'route'; outbound = 'direct'; process_name = @('Excluded.exe')
    }) 'proxy') 'proxy' $null

    $proxyOutbounds = @(
        [pscustomobject]@{ tag = 'proxy-udp'; type = 'urltest' },
        [pscustomobject]@{ tag = 'selected-server'; type = 'hysteria2' },
        [pscustomobject]@{ tag = 'renamed-bypass'; type = 'direct' })
    if (-not (Test-ProxyCapableChain 1 @('proxy-udp', 'selected-server') 'proxy-udp' $proxyOutbounds)) { throw 'ProxyChainFixtureFailed' }
    if (Test-ProxyCapableChain 1 @('proxy-udp', 'direct') 'proxy-udp' $proxyOutbounds) { throw 'DirectChainFixtureFailed' }
    if (Test-ProxyCapableChain 1 @('proxy-udp', 'renamed-bypass') 'proxy-udp' $proxyOutbounds) { throw 'RenamedDirectChainFixtureFailed' }
    if (Test-ProxyCapableChain 2 @('proxy-udp', 'selected-server') 'proxy-udp' $proxyOutbounds) { throw 'AmbiguousChainFixtureFailed' }
    $softHyphen = "pro$([char]0x00ad)xy"
    try {
        Resolve-UdpProbePlan (New-Fixture @([pscustomobject]@{
            action = 'route'; outbound = $softHyphen; process_name = @('App.exe')
        }) 'direct') | Out-Null
        throw 'LookalikeTagFixtureFailed'
    }
    catch {
        if ($_.Exception.Message -eq 'LookalikeTagFixtureFailed') { throw }
    }
    Write-Output '{"Status":"PASS","Fixtures":10}'
}
