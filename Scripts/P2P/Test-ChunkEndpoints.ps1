<#
.SYNOPSIS
    Downloads a manifest and its chunks from a running server and checks they rebuild an archive exactly.
.DESCRIPTION
    Signs in, reads what the server offers (/api/Chunks/Capabilities), downloads the manifest by its
    hash and then every chunk it names, as a launcher will. Each chunk is decompressed as its
    X-Chunk-Compression header says and checked against its hash; then every file of the zip is
    rebuilt from the downloaded chunks and compared with the zip's own contents.

    The server's Download Format setting (Settings > Archives) must not be "Archives only".

    Works on Windows PowerShell 5.1 and PowerShell 7.
.PARAMETER ArchivePath
    The zip that was uploaded and converted.
.PARAMETER ManifestHash
    The archive's full manifest hash. The copy button beside "Ready" in the archive list copies it.
.PARAMETER ServerUrl
    The server. Default http://localhost:1337, as Start-TestServer.ps1 runs it.
.PARAMETER Credential
    The account to sign in with. Asked for when not given.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Scripts\P2P\Test-ChunkEndpoints.ps1 -ArchivePath .\Data\P2PTest\game-v1.zip -ManifestHash <hash>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ArchivePath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f]{64}$')]
    [string]$ManifestHash,

    [string]$ServerUrl = 'http://localhost:1337',

    [System.Management.Automation.PSCredential]$Credential
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Net.Http
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

if (-not $Credential) {
    $Credential = Get-Credential -Message "Sign in to $ServerUrl"
}

$sha256 = [System.Security.Cryptography.SHA256]::Create()

function Get-Hex([byte[]]$Bytes) {
    return ([System.BitConverter]::ToString($Bytes) -replace '-', '').ToLowerInvariant()
}

$http = New-Object System.Net.Http.HttpClient
$http.BaseAddress = New-Object System.Uri ($ServerUrl.TrimEnd('/') + '/')
$http.Timeout = [TimeSpan]::FromMinutes(5)

function Invoke-Get([string]$Path) {
    $response = $http.GetAsync($Path.TrimStart('/')).GetAwaiter().GetResult()

    if (-not $response.IsSuccessStatusCode) {
        throw "GET $Path answered $([int]$response.StatusCode) $($response.ReasonPhrase)"
    }

    return $response
}

# Sign in
$login = @{ Username = $Credential.UserName; Password = $Credential.GetNetworkCredential().Password } | ConvertTo-Json
$content = New-Object System.Net.Http.StringContent($login, [System.Text.Encoding]::UTF8, 'application/json')
$response = $http.PostAsync('api/Auth/Login', $content).GetAwaiter().GetResult()

if (-not $response.IsSuccessStatusCode) {
    throw "Signing in failed: $([int]$response.StatusCode) $($response.Content.ReadAsStringAsync().GetAwaiter().GetResult())"
}

$token = ($response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json).accessToken
$http.DefaultRequestHeaders.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue('Bearer', $token)

# What the server offers
$capabilities = (Invoke-Get 'api/Chunks/Capabilities').Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json

Write-Host "Server $ServerUrl"
Write-Host ("  Chunks available: {0}, preferred: {1}, format {2}, chunk size {3}" -f `
    $capabilities.available, $capabilities.preferred, $capabilities.formatVersion, $capabilities.chunkSize)

if (-not $capabilities.available) {
    throw 'The server offers archives only. Set Settings > Archives > Download Format to "Chunks available", save, and run this again.'
}

$transferred = 0L
$downloads = 0

# Downloads one chunk, decompresses it as its header says, and checks it against its hash
function Get-Chunk([string]$Hash) {
    $response = Invoke-Get "api/Chunks/$Hash"
    $stored = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()

    $script:transferred += $stored.Length
    $script:downloads++

    $compression = 'none'
    $values = $null

    if ($response.Headers.TryGetValues('X-Chunk-Compression', [ref]$values)) {
        $compression = @($values)[0]
    }

    if ($compression -eq 'deflate') {
        $source = New-Object System.IO.MemoryStream (, $stored)
        $deflate = New-Object System.IO.Compression.DeflateStream($source, [System.IO.Compression.CompressionMode]::Decompress)
        $output = New-Object System.IO.MemoryStream

        try {
            $deflate.CopyTo($output)
        }
        finally {
            $deflate.Dispose()
        }

        $data = $output.ToArray()
    }
    elseif ($compression -eq 'none') {
        $data = $stored
    }
    else {
        throw "Chunk $Hash came with unknown compression '$compression'"
    }

    $actual = Get-Hex $sha256.ComputeHash($data)

    if ($actual -ne $Hash) {
        throw "Chunk $Hash is damaged: its contents hash to $actual"
    }

    return , $data
}

$manifest = [System.Text.Encoding]::UTF8.GetString((Get-Chunk $ManifestHash)) | ConvertFrom-Json

Write-Host "Manifest $ManifestHash"
Write-Host ("  {0} files, {1} folders" -f @($manifest.files).Count, @($manifest.directories).Count)

$failures = New-Object System.Collections.Generic.List[string]
$byPath = @{}
$cache = @{}
$rawBytes = 0L
$checked = 0

foreach ($file in @($manifest.files)) {
    $byPath[$file.path.ToLowerInvariant()] = $file
}

$zip = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path $ArchivePath).Path)

try {
    $zipFiles = @($zip.Entries | Where-Object { -not ($_.FullName.EndsWith('/') -or $_.FullName.EndsWith('\')) })

    foreach ($entry in $zipFiles) {
        $path = $entry.FullName -replace '\\', '/'
        $file = $byPath[$path.ToLowerInvariant()]

        if (-not $file) {
            $failures.Add("$path is in the zip but not the manifest")
            continue
        }

        $byPath.Remove($path.ToLowerInvariant())

        $entryStream = $entry.Open()

        try {
            $expected = Get-Hex $sha256.ComputeHash($entryStream)
        }
        finally {
            $entryStream.Dispose()
        }

        $rebuilt = New-Object System.IO.MemoryStream

        foreach ($hash in @($file.chunks)) {
            # Each chunk is downloaded once, however many files use it
            if (-not $cache.ContainsKey($hash)) {
                $cache[$hash] = Get-Chunk $hash
            }

            $rebuilt.Write($cache[$hash], 0, $cache[$hash].Length)
            $rawBytes += $cache[$hash].Length
        }

        if ((Get-Hex $sha256.ComputeHash($rebuilt.ToArray())) -ne $expected) {
            $failures.Add("$path rebuilds to different contents")
        }
        else {
            $checked++
        }
    }

    foreach ($extra in $byPath.Values) {
        $failures.Add("$($extra.path) is in the manifest but not the zip")
    }
}
finally {
    $zip.Dispose()
    $http.Dispose()
}

Write-Host ("  {0} of {1} files rebuilt exactly from downloaded chunks" -f $checked, $zipFiles.Count)
Write-Host ("  {0} downloads, {1:N1} MiB transferred for {2:N1} MiB of file data" -f $downloads, ($transferred / 1MB), ($rawBytes / 1MB))
Write-Host ''

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) {
        Write-Host "  FAIL  $failure" -ForegroundColor Red
    }

    Write-Host "$($failures.Count) problem(s) found" -ForegroundColor Red
    exit 1
}

Write-Host 'The server serves this archive correctly as chunks' -ForegroundColor Green
exit 0
