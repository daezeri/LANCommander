<#
.SYNOPSIS
    Checks that a chunk manifest in the server's chunk store rebuilds an archive exactly.
.DESCRIPTION
    Reads the manifest from the chunk store, then for every file in the zip:
      - rebuilds the file from its chunks, checking each chunk's SHA-256 against its name and its length
        against the chunk size
      - compares the rebuilt file's SHA-256 with the zip entry's, and its size and CRC-32 with the manifest's
    It also checks that the manifest lists exactly the zip's files and empty folders, then prints how
    much space the chunks take compared with the files. Exits with 1 if anything doesn't match.

    Works on Windows PowerShell 5.1 and PowerShell 7.
.PARAMETER ArchivePath
    The zip that was uploaded to the server.
.PARAMETER ManifestHash
    The manifest's hash as the admin UI shows it. The first few characters (at least 4) are enough when
    they match only one stored file.
.PARAMETER DataPath
    The server's data folder. Defaults to Data\P2PTest at the repository root, as Start-TestServer.ps1 uses.
.PARAMETER ChunkStorePath
    The chunk store itself, if it isn't <DataPath>\Chunks.
.PARAMETER CompareWith
    Another manifest's hash (or prefix): reports how many chunks the two share, as when two versions of
    a game are converted.
.PARAMETER OutputPath
    Also writes the rebuilt files here, to look at by hand.
.EXAMPLE
    .\Scripts\P2P\Test-ChunkManifest.ps1 -ArchivePath .\Data\P2PTest\game-v1.zip -ManifestHash 3f9a1c
.EXAMPLE
    .\Scripts\P2P\Test-ChunkManifest.ps1 -ArchivePath .\Data\P2PTest\game-v2.zip -ManifestHash 77be02 -CompareWith 3f9a1c
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ArchivePath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{4,64}$')]
    [string]$ManifestHash,

    [string]$DataPath,

    [string]$ChunkStorePath,

    [ValidatePattern('^[0-9a-fA-F]{4,64}$')]
    [string]$CompareWith,

    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')

if (-not $ChunkStorePath) {
    if (-not $DataPath) {
        $DataPath = Join-Path $repoRoot 'Data\P2PTest'
    }

    $ChunkStorePath = Join-Path $DataPath 'Chunks'
}

if (-not (Test-Path $ChunkStorePath)) {
    throw "The chunk store wasn't found at $ChunkStorePath. Pass -DataPath or -ChunkStorePath."
}

$sha256 = [System.Security.Cryptography.SHA256]::Create()

# .NET Framework (Windows PowerShell 5.1) has no CRC-32 and no ZipArchiveEntry.Crc32, so compute it here
if (-not ('LANCommanderP2P.Crc32' -as [type])) {
    Add-Type -TypeDefinition @'
namespace LANCommanderP2P
{
    public static class Crc32
    {
        private static readonly uint[] Table = CreateTable();

        private static uint[] CreateTable()
        {
            var table = new uint[256];

            for (uint i = 0; i < 256; i++)
            {
                var c = i;

                for (var k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;

                table[i] = c;
            }

            return table;
        }

        public static uint Compute(byte[] data)
        {
            var crc = 0xFFFFFFFFu;

            foreach (var b in data)
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);

            return crc ^ 0xFFFFFFFFu;
        }
    }
}
'@
}

function Get-Hex([byte[]]$Bytes) {
    return ([System.BitConverter]::ToString($Bytes) -replace '-', '').ToLowerInvariant()
}

# Chunks live at <store>\ab\cd\abcd..., with ".deflate" when stored compressed
function Find-StoredChunk([string]$Prefix) {
    $Prefix = $Prefix.ToLowerInvariant()
    $folder = Join-Path (Join-Path $ChunkStorePath $Prefix.Substring(0, 2)) $Prefix.Substring(2, 2)

    if (-not (Test-Path $folder)) {
        return @()
    }

    return @(Get-ChildItem -File -Path $folder | Where-Object { $_.Name.StartsWith($Prefix) -and -not $_.Name.EndsWith('.tmp') })
}

function Read-Chunk([string]$Hash) {
    $found = Find-StoredChunk $Hash | Where-Object { $_.Name -eq $Hash -or $_.Name -eq "$Hash.deflate" }

    if (-not $found) {
        throw "Chunk $Hash is missing from the store"
    }

    $file = @($found)[0]
    $stored = [System.IO.File]::ReadAllBytes($file.FullName)

    if ($file.Name.EndsWith('.deflate')) {
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
    else {
        $data = $stored
    }

    $actual = Get-Hex $sha256.ComputeHash($data)

    if ($actual -ne $Hash) {
        throw "Chunk $Hash is damaged: its contents hash to $actual"
    }

    return [pscustomobject]@{ Data = $data; StoredSize = $file.Length }
}

function Resolve-Manifest([string]$Prefix) {
    $candidates = @(Find-StoredChunk $Prefix)

    if ($candidates.Count -eq 0) {
        throw "No stored file starts with $Prefix"
    }

    $hashes = @($candidates | ForEach-Object { $_.Name -replace '\.deflate$', '' } | Sort-Object -Unique)

    if ($hashes.Count -gt 1) {
        throw "$Prefix matches $($hashes.Count) stored files; give more of the hash"
    }

    $hash = $hashes[0]
    $json = [System.Text.Encoding]::UTF8.GetString((Read-Chunk $hash).Data)

    return [pscustomobject]@{ Hash = $hash; Manifest = ($json | ConvertFrom-Json) }
}

$failures = New-Object System.Collections.Generic.List[string]

function Fail([string]$Message) {
    $failures.Add($Message)
    Write-Host "  FAIL  $Message" -ForegroundColor Red
}

$resolved = Resolve-Manifest $ManifestHash
$manifest = $resolved.Manifest

Write-Host "Manifest $($resolved.Hash)"
Write-Host ("  Format {0}, {1} chunk size, {2} files, {3} folders" -f $manifest.formatVersion, $manifest.chunkSize, @($manifest.files).Count, @($manifest.directories).Count)

$chunkSize = [long]$manifest.chunkSize
$byPath = @{}

foreach ($file in @($manifest.files)) {
    $byPath[$file.path.ToLowerInvariant()] = $file
}

$zip = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path $ArchivePath).Path)
$checkedFiles = 0
$rawBytes = 0L
$storedBytes = 0L
$seen = @{}

try {
    $zipFiles = @($zip.Entries | Where-Object { -not ($_.FullName.EndsWith('/') -or $_.FullName.EndsWith('\')) })
    $zipFolders = @($zip.Entries | Where-Object { $_.FullName.EndsWith('/') -or $_.FullName.EndsWith('\') } |
        ForEach-Object { ($_.FullName -replace '\\', '/').TrimEnd('/') + '/' })

    foreach ($entry in $zipFiles) {
        $path = ($entry.FullName -replace '\\', '/')
        $file = $byPath[$path.ToLowerInvariant()]

        if (-not $file) {
            Fail "$path is in the zip but not the manifest"
            continue
        }

        $byPath.Remove($path.ToLowerInvariant())

        # The zip's own bytes, for comparison
        $entryStream = $entry.Open()

        try {
            $expected = Get-Hex $sha256.ComputeHash($entryStream)
        }
        finally {
            $entryStream.Dispose()
        }

        $rebuilt = New-Object System.IO.MemoryStream
        $chunks = @($file.chunks)

        for ($i = 0; $i -lt $chunks.Count; $i++) {
            $chunk = Read-Chunk $chunks[$i]
            $isLast = $i -eq $chunks.Count - 1

            if (-not $isLast -and $chunk.Data.Length -ne $chunkSize) {
                Fail "$path chunk $i is $($chunk.Data.Length) bytes, not $chunkSize"
            }

            $rebuilt.Write($chunk.Data, 0, $chunk.Data.Length)
            $rawBytes += $chunk.Data.Length

            if (-not $seen.ContainsKey($chunks[$i])) {
                $seen[$chunks[$i]] = $true
                $storedBytes += $chunk.StoredSize
            }
        }

        $bytes = $rebuilt.ToArray()
        $actual = Get-Hex $sha256.ComputeHash($bytes)

        if ($actual -ne $expected) {
            Fail "$path rebuilds to different contents"
        }
        elseif ([long]$file.size -ne $bytes.Length) {
            Fail "$path is $($bytes.Length) bytes but the manifest says $($file.size)"
        }
        elseif ([uint32]$file.crc32 -ne [LANCommanderP2P.Crc32]::Compute($bytes)) {
            Fail "$path has CRC $([LANCommanderP2P.Crc32]::Compute($bytes)) but the manifest says $($file.crc32)"
        }
        elseif ($null -ne $entry.PSObject.Properties['Crc32'] -and [uint32]$file.crc32 -ne $entry.Crc32) {
            # Only .NET (PowerShell 7) exposes the CRC the zip records
            Fail "$path has CRC $($entry.Crc32) in the zip but $($file.crc32) in the manifest"
        }
        else {
            $checkedFiles++
        }

        if ($OutputPath) {
            $target = Join-Path $OutputPath $path
            New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
            [System.IO.File]::WriteAllBytes($target, $bytes)
        }
    }

    foreach ($extra in $byPath.Values) {
        Fail "$($extra.path) is in the manifest but not the zip"
    }

    $manifestFolders = @($manifest.directories)

    foreach ($folder in $zipFolders) {
        if ($manifestFolders -notcontains $folder) {
            Fail "Folder $folder is in the zip but not the manifest"
        }
    }
}
finally {
    $zip.Dispose()
}

Write-Host ("  {0} of {1} files rebuilt exactly" -f $checkedFiles, $zipFiles.Count)
Write-Host ("  {0:N1} MiB of file data in {1} distinct chunks taking {2:N1} MiB in the store ({3:P0})" -f `
    ($rawBytes / 1MB), $seen.Count, ($storedBytes / 1MB), $(if ($rawBytes -gt 0) { $storedBytes / $rawBytes } else { 0 }))

if ($CompareWith) {
    $other = Resolve-Manifest $CompareWith
    $mine = @($manifest.files | ForEach-Object { $_.chunks } | Sort-Object -Unique)
    $theirs = @($other.Manifest.files | ForEach-Object { $_.chunks } | Sort-Object -Unique)
    $shared = @($mine | Where-Object { $theirs -contains $_ })

    Write-Host ''
    Write-Host "Compared with manifest $($other.Hash)"
    Write-Host ("  {0} of this manifest's {1} chunks are shared, so a client updating from the other version downloads {2}" -f `
        $shared.Count, $mine.Count, ($mine.Count - $shared.Count))
}

Write-Host ''

if ($failures.Count -gt 0) {
    Write-Host "$($failures.Count) problem(s) found" -ForegroundColor Red
    exit 1
}

Write-Host 'Manifest matches the archive' -ForegroundColor Green
exit 0
