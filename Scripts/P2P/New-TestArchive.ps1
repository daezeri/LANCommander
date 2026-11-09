<#
.SYNOPSIS
    Creates a game archive (zip) that exercises every case of the chunk conversion.
.DESCRIPTION
    The archive holds:
      - data\level.pak        incompressible data spanning several 4 MiB chunks, plus a short last chunk
      - data\strings.txt      compressible text spanning more than one chunk
      - data\copy-of-level.pak an exact copy of level.pak, whose chunks are stored only once
      - game.exe, readme.txt   small files of one chunk each
      - config\empty.cfg       an empty file (no chunks)
      - saves\                 an empty folder
      - many\file-NNN.txt      many small files

    -Variant 2 makes the "next version" of the same game: level.pak is unchanged, game.exe is changed,
    readme.txt is removed and patch-notes.txt is added. Converting both versions shows the unchanged
    file's chunks being shared rather than stored twice.

    Contents are generated from fixed seeds, so the same parameters always give the same files.
.PARAMETER Path
    The zip to create. Overwritten if it exists.
.PARAMETER Variant
    1 (default) or 2.
.PARAMETER LargeFileMB
    The size of level.pak in MiB. Default 10, which is three chunks.
.PARAMETER SmallFileCount
    How many files to put in many\. Default 200.
.EXAMPLE
    .\Scripts\P2P\New-TestArchive.ps1 -Path .\Data\P2PTest\game-v1.zip
    .\Scripts\P2P\New-TestArchive.ps1 -Path .\Data\P2PTest\game-v2.zip -Variant 2
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Path,

    [ValidateSet(1, 2)]
    [int]$Variant = 1,

    [ValidateRange(1, 4096)]
    [int]$LargeFileMB = 10,

    [ValidateRange(0, 100000)]
    [int]$SmallFileCount = 200
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function New-RandomBytes([int]$Length, [int]$Seed) {
    $bytes = New-Object byte[] $Length
    (New-Object System.Random $Seed).NextBytes($bytes)
    return , $bytes
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("lc-test-archive-" + [guid]::NewGuid().ToString('N'))

New-Item -ItemType Directory -Force -Path $work | Out-Null

try {
    foreach ($folder in 'data', 'config', 'saves', 'many') {
        New-Item -ItemType Directory -Force -Path (Join-Path $work $folder) | Out-Null
    }

    # Several full chunks and a short last one, the same in both variants
    $level = New-RandomBytes ($LargeFileMB * 1MB + 12345) 1001
    [System.IO.File]::WriteAllBytes((Join-Path $work 'data\level.pak'), $level)
    [System.IO.File]::WriteAllBytes((Join-Path $work 'data\copy-of-level.pak'), $level)

    # Text compresses well, so its chunks are stored compressed
    $builder = New-Object System.Text.StringBuilder

    for ($i = 0; $builder.Length -lt 6MB; $i++) {
        [void]$builder.AppendLine("STRING_$i = `"The quick brown fox jumps over the lazy dog, line $i`"")
    }

    [System.IO.File]::WriteAllText((Join-Path $work 'data\strings.txt'), $builder.ToString())

    $exeSeed = if ($Variant -eq 1) { 2001 } else { 2002 }
    [System.IO.File]::WriteAllBytes((Join-Path $work 'game.exe'), (New-RandomBytes 300000 $exeSeed))

    if ($Variant -eq 1) {
        [System.IO.File]::WriteAllText((Join-Path $work 'readme.txt'), "Test game for the LANCommander chunk store.`r`n")
    }
    else {
        [System.IO.File]::WriteAllText((Join-Path $work 'patch-notes.txt'), "Version 2: new game.exe.`r`n")
    }

    [System.IO.File]::WriteAllBytes((Join-Path $work 'config\empty.cfg'), (New-Object byte[] 0))

    for ($i = 0; $i -lt $SmallFileCount; $i++) {
        $name = 'many\file-{0:D3}.txt' -f $i
        [System.IO.File]::WriteAllText((Join-Path $work $name), "Small file $i`r`n")
    }

    $Path = [System.IO.Path]::GetFullPath($Path)
    $parent = Split-Path $Path -Parent

    if ($parent) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }

    if (Test-Path $Path) {
        Remove-Item -Force $Path
    }

    # Entries are written one by one with forward slashes, as most zip tools write them; Windows
    # PowerShell's CreateFromDirectory would use backslashes. Empty folders get entries of their own.
    $zip = [System.IO.Compression.ZipFile]::Open($Path, [System.IO.Compression.ZipArchiveMode]::Create)

    try {
        $root = (Resolve-Path $work).Path.TrimEnd('\') + '\'

        foreach ($item in Get-ChildItem -Recurse -Force $work) {
            $name = $item.FullName.Substring($root.Length) -replace '\\', '/'

            if ($item.PSIsContainer) {
                if (-not (Get-ChildItem -Force $item.FullName)) {
                    [void]$zip.CreateEntry("$name/")
                }
            }
            else {
                [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $item.FullName, $name, [System.IO.Compression.CompressionLevel]::Optimal)
            }
        }
    }
    finally {
        $zip.Dispose()
    }

    $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)

    try {
        $files = @($zip.Entries | Where-Object { -not $_.FullName.EndsWith('/') })
        $folders = @($zip.Entries | Where-Object { $_.FullName.EndsWith('/') })
        $total = ($files | Measure-Object -Property Length -Sum).Sum

        Write-Host ("Created {0}" -f $Path)
        Write-Host ("  Variant {0}: {1} files, {2} empty folders, {3:N1} MiB uncompressed, {4:N1} MiB zipped" -f `
            $Variant, $files.Count, $folders.Count, ($total / 1MB), ((Get-Item $Path).Length / 1MB))
    }
    finally {
        $zip.Dispose()
    }
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
