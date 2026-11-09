<#
.SYNOPSIS
    Runs the LANCommander server from source with its own data folder, for testing the chunk work.
.DESCRIPTION
    Sets LANCOMMANDER_DATA_DIR so the server keeps its settings, SQLite database, uploads and chunk
    store in a separate folder (Data\P2PTest at the repository root by default, which git ignores),
    then starts it with `dotnet run`. The server listens on http://localhost:1337.
.PARAMETER DataPath
    The data folder to use. Created if missing.
.PARAMETER Reset
    Deletes the data folder first, for a clean first-run setup.
.EXAMPLE
    .\Scripts\P2P\Start-TestServer.ps1
.EXAMPLE
    .\Scripts\P2P\Start-TestServer.ps1 -Reset
#>
[CmdletBinding()]
param(
    [string]$DataPath,
    [switch]$Reset
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')

if (-not $DataPath) {
    $DataPath = Join-Path $repoRoot 'Data\P2PTest'
}

if ($Reset -and (Test-Path $DataPath)) {
    Write-Host "Deleting $DataPath"
    Remove-Item -Recurse -Force $DataPath
}

New-Item -ItemType Directory -Force -Path $DataPath | Out-Null

$DataPath = (Resolve-Path $DataPath).Path

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet was not found. Install the .NET 10 SDK, then open a new terminal.'
}

$env:LANCOMMANDER_DATA_DIR = $DataPath

Write-Host "Data folder:  $DataPath"
Write-Host "Chunk store:  $(Join-Path $DataPath 'Chunks')"
Write-Host "Server:       http://localhost:1337"
Write-Host ''

Push-Location $repoRoot

try {
    dotnet run --project LANCommander.Server --launch-profile LANCommander
}
finally {
    Pop-Location
}
