# Testing the chunk store locally

Scripts for checking, by hand on your own machine, that archives convert to chunk manifests correctly.
The design is in [docs/p2p/README.md](../../docs/p2p/README.md).

| Script | What it does |
|---|---|
| `Start-TestServer.ps1` | Runs the server from source with its own data folder (`Data\P2PTest`, ignored by git) |
| `New-TestArchive.ps1` | Makes a test game zip covering every conversion case, in two versions |
| `Test-ChunkManifest.ps1` | Rebuilds every file of a zip from the chunk store and checks it matches exactly |
| `Test-ChunkEndpoints.ps1` | Does the same over HTTP: signs in, then downloads the manifest and every chunk from the server, as a launcher will |

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

7. **Check the downloads over HTTP:** in **Settings → Archives**, set **Download Format** to **Chunks
   available** and save. Copy an archive's full manifest hash with the copy button beside *Ready*, then:

   ```powershell
   .\Scripts\P2P\Test-ChunkEndpoints.ps1 -ArchivePath .\Data\P2PTest\game-v1.zip -ManifestHash <full v1 hash>
   ```

   It asks for the account you signed up with, and should end with **The server serves this archive
   correctly as chunks**. It also shows how much was transferred: less than the file data, because
   compressed chunks are sent compressed and shared chunks are downloaded once.

   Set the format back to **Archives only** and run it again: it should stop at once, saying the
   server offers archives only.

## Converting without clicking

With **Generate Chunk Manifests** on, uploads are converted automatically, and repacked or patched
archives are marked *Out of date* and converted again. Everything else waiting (archives from before the
setting, imports) is picked up by a sweep every 10 minutes, a few at a time.

**Settings → Archives** shows the progress (ready, not converted, out of date, converting, failed), how
much space the rest could take against what's free, and a **Build All Now** button that converts
everything waiting one archive at a time. Use **Refresh** to follow it.

Chunks no archive uses any more (after archives are deleted or rebuilt) are removed every night at 4:00
once they are a day old. **Clean Up Store** does it straight away. To see it work: convert `game-v1.zip`,
delete that archive, click **Clean Up Store**, and the chunks only it used disappear from
`Data\P2PTest\Chunks`, while chunks it shared with `game-v2.zip` stay.

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
