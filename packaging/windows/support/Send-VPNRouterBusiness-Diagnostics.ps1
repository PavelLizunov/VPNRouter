#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ZipPath,
    [Parameter(Mandatory)][string]$UploadEndpoint,
    [string]$TokenFile
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

function Test-SupportUploadEndpoint([string]$Endpoint) {
    $uri = $null
    if (![Uri]::TryCreate($Endpoint, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -ne 'https' -or $uri.UserInfo -or $uri.Fragment) {
        throw 'An absolute HTTPS endpoint without credentials or fragment is required.'
    }
    return $uri
}

function Send-SupportBundle([string]$Path, [string]$Endpoint, [string]$BearerTokenFile, [Net.Http.HttpClient]$Client) {
    $uri = Test-SupportUploadEndpoint $Endpoint
    $file = [IO.FileInfo]::new([IO.Path]::GetFullPath($Path))
    if (!$file.Exists -or $file.Extension -ne '.zip' -or $file.Length -eq 0 -or $file.Length -gt 24MB) {
        throw 'A non-empty diagnostics ZIP of at most 24 MiB is required.'
    }
    $token = $null
    if ($BearerTokenFile) {
        $tokenInfo = [IO.FileInfo]::new([IO.Path]::GetFullPath($BearerTokenFile))
        if (!$tokenInfo.Exists -or $tokenInfo.Length -gt 4096) { throw 'Invalid token file.' }
        $tokenStream = [IO.File]::OpenRead($tokenInfo.FullName)
        try {
            if ($tokenStream.Length -gt 4096) { throw 'Invalid token file.' }
            $reader = [IO.StreamReader]::new($tokenStream)
            try { $token = $reader.ReadToEnd().Trim() } finally { $reader.Dispose() }
        } finally { $tokenStream.Dispose() }
        if ($token -notmatch '^[A-Za-z0-9._~+/-]+=*$') { throw 'Invalid bearer token.' }
    }
    $stream = $null
    $request = $null
    $response = $null
    try {
        $stream = [IO.File]::OpenRead($file.FullName)
        if ($stream.Length -eq 0 -or $stream.Length -gt 24MB) { throw 'ZIP size changed; upload refused.' }
        $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, $uri)
        $request.Content = [Net.Http.StreamContent]::new($stream)
        $request.Content.Headers.ContentType = [Net.Http.Headers.MediaTypeHeaderValue]::new('application/zip')
        $request.Content.Headers.ContentLength = $stream.Length
        [void]$request.Headers.TryAddWithoutValidation('X-VPNRouter-Report-Format', 'vpnrouter-business-support/1')
        if ($token) { $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $token) }
        $response = $Client.SendAsync($request, [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        if (!$response.IsSuccessStatusCode) { throw 'Receiver refused the report; ZIP retained.' }
        return [int]$response.StatusCode
    } finally {
        if ($response) { $response.Dispose() }
        if ($request) { $request.Dispose() }
        if ($stream) { $stream.Dispose() }
        $token = $null
    }
}

if ($MyInvocation.InvocationName -eq '.') { return }
$handler = $null
$client = $null
$priorTls = [Net.ServicePointManager]::SecurityProtocol
try {
    [void](Test-SupportUploadEndpoint $UploadEndpoint)
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $handler.UseCookies = $false
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(30)
    [void](Send-SupportBundle $ZipPath $UploadEndpoint $TokenFile $client)
    Write-Host 'Report accepted by the receiver. The local ZIP has been retained.'
    exit 0
} catch {
    # Never print an exception/response body: it may contain the URL or bearer token.
    Write-Host 'Report was not confirmed as received. The local ZIP has been retained for manual delivery.'
    exit 1
} finally {
    if ($client) { $client.Dispose() } elseif ($handler) { $handler.Dispose() }
    [Net.ServicePointManager]::SecurityProtocol = $priorTls
}
