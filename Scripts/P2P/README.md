# Testing the chunk store locally

Scripts for checking, by hand on your own machine, that archives convert to chunk manifests correctly.
The design is in [docs/p2p/README.md](../../docs/p2p/README.md).

| Script | What it does |
|---|---|
| `Start-TestServer.ps1` | Runs the server from source with its own data folder (`Data\P2PTest`, ignored by git) |
| `New-TestArchive.ps1` | Makes a test game zip covering every conversion case, in two versions |
| `Test-ChunkManifest.ps1` | Rebuilds every file of a zip from the chunk store and checks it matches exactly |

The scripts run on Windows PowerShell 5.1 and PowerShell 7.

### "Running scripts is disabled on this system"

Windows blocks `.ps1` files until its execution policy allows them. To bypass the policy for one run,
without changing any setting, start the script through `powershell`:

```powershell
powershell -ExecutionPolicy Bypass -File .\Scripts\P2P\Start-TestServer.ps1 -Reset
```

## Prerequisites

- .NET 10 SDK and Node.js
- `npm ci` run once in `LANCommander.Server.UI` and in `LANCommander.Server`, otherwise the server's
  build fails at "npm run package"

## Walkthrough

1. **Start a clean server:**

   ```powershell
   .\Scripts\P2P\Start-TestServer.ps1 -Reset
   ```

   Open http://localhost:1337 and complete the first-run setup (SQLite is fine).

2. **Turn the feature on:** go to **Settings → Archives**, switch on **Generate Chunk Manifests**, and
   save.

3. **Make two versions of a test game:**

   ```powershell
   .\Scripts\P2P\New-TestArchive.ps1 -Path .\Data\P2PTest\game-v1.zip
   .\Scripts\P2P\New-TestArchive.ps1 -Path .\Data\P2PTest\game-v2.zip -Variant 2
   ```

4. **Upload them:** create a game, then on its **Archives** tab upload `game-v1.zip` as version 1.0 and
   `game-v2.zip` as version 2.0.

5. **Convert them:** on each archive, click **Build Chunk Manifest**. The **Chunks** column shows
   *Converting*, then *Ready* with the start of the manifest's hash. Refresh the page if it doesn't update
   by itself.

6. **Check them:**

   ```powershell
   .\Scripts\P2P\Test-ChunkManifest.ps1 -ArchivePath .\Data\P2PTest\game-v1.zip -ManifestHash <v1 hash>
   .\Scripts\P2P\Test-ChunkManifest.ps1 -ArchivePath .\Data\P2PTest\game-v2.zip -ManifestHash <v2 hash> -CompareWith <v1 hash>
   ```

   Each run should end with **Manifest matches the archive**. The comparison should show most chunks
   shared: only `game.exe` and the added `patch-notes.txt` differ between the versions.

   Add `-OutputPath .\Data\P2PTest\rebuilt` to also write out the rebuilt files.

## What to look for

- `data\level.pak` and `data\copy-of-level.pak` are identical, so their chunks are stored once.
- `data\strings.txt` compresses well, so its chunks are stored as `.deflate` files. `level.pak` is random
  data, so its chunks are stored as they are.
- `config\empty.cfg` has no chunks, and the empty `saves\` folder is listed in the manifest.
- The chunk store is at `Data\P2PTest\Chunks`, with files spread over `ab\cd\` subfolders.

## Failure cases worth trying

- Upload a zip containing a path like `..\evil.dll`: the archive should show **Failed** with the reason.
- Delete an archive's zip from `Data\P2PTest\Uploads` and build again: **Failed**, "The archive file is
  missing".
