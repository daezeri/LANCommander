# LAN Peer-to-Peer Downloads: Design

Status: **proposed design**. Step 1 (the server side) is implemented in branches `feature/p2p-01-…` to `feature/p2p-08-…`. Branch stack: `feature/p2p-00-design` (this document) → `feature/p2p-01-…` and up.

Open items for later are in [TODO.md](TODO.md).

## Goal

Let launchers on the same LAN download games from each other, from many peers at once, so a LAN party isn't
limited by one machine's upload or by the server's link. At many events the LANCommander server is hosted
remotely (the venue's Starlink or fibre), and every byte from it crosses that one link. Sharing over the LAN
avoids that. When the server is on the LAN, peers mostly spread the load across the switches.

## Ground rules

- **LAN only, never WAN.**
  - Peers find each other by UDP broadcast/multicast on the local subnet only (multicast TTL 1), by extending
    the existing beacon on UDP 35891.
  - The launcher's file server rejects any address outside the subnets of its own LAN adapters.
  - VPN and virtual adapters (Hamachi, Tailscale `100.64.0.0/10`, ZeroTier…) are excluded by default.
  - The server is **not** a tracker and never hands out peer addresses.
- **The server stays the authority.** It provides chunk manifests and download permissions. It is also the
  last-resort source for any chunk no peer has.
- **Out of scope: anything about internet use.** Tracking, budgets or caps on WAN traffic are for LAN
  administrators, not LANCommander.
- **Both systems live side by side.** Zip archives are never removed, and today's download path is unchanged.
  The chunk system is added alongside it.
- **No per-game overrides.** Every setting is global.

## Settings

### Server (admin)

| Setting | Values |
|---|---|
| Generate chunk manifests | On / Off |
| Download format | **Archives only** (today's behaviour) · **Chunks available** · **Chunks preferred** |

The server reports which formats it offers through a capability flag. Launchers that don't know about chunks,
and servers set to "Archives only", keep using zips. With "Archives only", the chunk endpoints refuse requests.

### Client (player)

| Setting | Values |
|---|---|
| Download source | **Automatic** (default) · **Prefer LAN peers** · **Server only** |
| Allow others on my LAN to download from this computer | Off by default (or ask once), with an upload cap, pause while in a game, max peers, and which games to share |

"Server only" skips all peer code: no discovery and no connections. It is the fallback for poor LAN hardware
(10/100 Mbps links, bad switches) or for when the peer system misbehaves. Sharing is a separate choice: a
player on "Server only" can still share.

## Content model: chunks

- Chunks are cut from the **extracted files**, not from the zip. Each file is split into fixed **4 MB** pieces.
  A chunk is named by the **SHA-256** of its raw (uncompressed) bytes.
- Chunk *n* of a file starts at *n* × 4 MB, so a client can write chunks in any order.
- Because chunks come from files, an installed game can itself be shared, and updates download only the chunks
  that changed.
- Manifests always come from the server over a signed-in connection, so they need no signature. Peers only
  supply chunks, and every chunk is checked against its hash.

### Manifest (format version 1)

```json
{
  "formatVersion": 1,
  "chunkSize": 4194304,
  "hash": "sha256",
  "directories": ["maps/", "sound/"],
  "files": [
    {
      "path": "maps/de_dust.bsp",
      "size": 9437184,
      "crc32": 2712847316,
      "modified": "2004-06-01T12:00:00+00:00",
      "chunks": ["9f86d0…", "60303a…", "fd61a0…"]
    }
  ]
}
```

- `crc32` (an unsigned 32-bit number) is kept so the existing `ValidateFilesAsync` and repair code keep
  working.
- The SDK code lives in `LANCommander.SDK/Chunking`:
  - `ChunkManifestBuilder` cuts files into chunks.
  - `ZipChunker` converts a whole zip.
  - `ChunkManifestSerializer` writes the exact bytes whose hash names the manifest.
  - `ChunkPath` rejects unsafe paths.
- The manifest is itself stored in the chunk store under its own hash, and the archive points to that hash.

### Server chunk store

The server keeps a **chunk store**: each chunk is written once, named by its hash, on a new
`StorageLocationType.Chunk` storage location. Serving a chunk is a plain file read. Reading chunks out of the
zip on every request was rejected, because it means decompressing a whole file to reach its last chunk.

- Each chunk is compressed on its own, or stored raw when compressing doesn't help (`.pak` files, video,
  audio). The method is recorded with the chunk.
- Chunks are written under a temporary name and then renamed, and spread over subfolders by hash prefix.
- Chunks shared between versions or games are stored once.

## Converting a zip to chunks

The server runs this as a background (Hangfire) job, for new uploads and existing archives alike.

1. Set the archive to `Pending` and open its zip from storage. The zip is a local file, so it can be read in
   any order.
2. For each entry, in order:
   - **Folders** are recorded, so empty folders are recreated.
   - **Files** are decompressed and read in 4 MB pieces. Each piece is hashed and written to the store if it
     isn't already there.
   - The entry's CRC32 is computed and compared with the zip's. A mismatch marks the archive `Failed` (damaged
     zip).
   - Empty files get size 0 and no chunks.
3. Write the manifest to the store.
4. In one database transaction: add the new `Chunk` rows, link the archive to its chunks in `ArchiveChunk`,
   and set the archive to `Ready` with its manifest hash.
   - Chunk files are written **before** the commit. Leftovers from a crash are harmless and are removed by
     the cleanup job.
5. A repack or patch marks the archive `Stale`, and it is rebuilt. Unchanged files keep the same hashes.

**Rejected archives:** paths with `../` or absolute paths, duplicate entries (including names that differ only
in case), and encrypted zips. Files over 4 GB (zip64) are fine.

**Costs:**
- One decompression and hashing pass per archive.
- Disk space of about the uncompressed size of each distinct file, minus compression and sharing.
- A known limit of fixed-size chunks: data inserted near the start of a file shifts every later chunk. The
  `formatVersion` field leaves room to move to content-defined chunking later.

## Client: deciding what to rebuild

- **Local record:** after writing a file, the launcher sets its modified time to the manifest's `modified`
  value and records the manifest, size and time in the game's `.lancommander` folder. This extends today's
  install history (`InstallHistoryHelper`).
- **Quick check** (sizes and dates, no reading): missing or wrong size → rebuild. Modified time differs from
  the record → full check of that file.
- **Full check:** hash each 4 MB piece and compare with the manifest. This finds exactly which chunks are bad,
  and only those are fetched. It replaces today's CRC-based `ValidateFilesAsync`.
- **Updates:** compare the old and new manifests.
  - Same chunk list → skip the file.
  - Only in the old version → delete it.
  - Changed → rebuild it from chunks already on disk plus the new chunks only.
- **Rebuilding:** write to a temporary file (`*.lcpart`), check it, then swap it in. This is never done in
  place, because a needed chunk may sit in the old copy of the same file.
- **Save paths are excluded** from verify, repair and update, as today.

| Situation | Check |
|---|---|
| Normal install | None needed: each chunk is checked as it's written |
| Before an update | Manifest comparison + quick check |
| Verify files / repair | Full check |
| Offering an install to peers | Full check once, then quick checks |
| After a crash or cancelled install | Quick check; temporary files are thrown away |

## Peer behaviour (step 4)

- Peers exchange maps of the chunks they hold, then announce new chunks as they get them.
- The client asks **different peers for different chunks in parallel**, with a few requests outstanding per
  peer. Faster peers get more requests.
- **Half-finished downloads share too:** any verified chunk can be shared before the install completes. This
  is what makes many people installing at once fast.
- The rarest chunks are fetched first. Near the end, the last chunks are requested from several peers, so one
  slow peer can't hold up the finish.
- A chunk that fails its hash check is discarded, and the peer that sent it is blocked for the session.
- **Server fetches are split between LAN clients**, so each chunk is fetched from the server once per LAN and
  then shared. Peers announce what they are fetching as well as what they hold.
- Permission: peers serve only clients holding a short-lived signed ticket from the server, the same idea as
  the relay tickets.

## Roadmap

1. **Server: manifests and chunk store** (step 1, below)
2. **Client: download by chunk**, from the server only, plus the download source setting
3. **LAN discovery, sharing settings, the launcher's file server**, and offering existing installs to peers
4. **Download from many peers at once**, with server fetches split between clients
5. **Cache mode** for organisers: pre-download games to a machine and share them without installing them

## Step 1: pull requests

Each PR merges on its own and changes nothing for users until the feature is switched on.

| # | Branch | Contents |
|---|---|---|
| 1 | `feature/p2p-01-manifest-format` | SDK manifest models and chunker (shared with the launcher later), unit tests |
| 2 | `feature/p2p-02-chunk-store` | `StorageLocationType.Chunk` and the chunk store service, tests |
| 3 | `feature/p2p-03-schema` | Archive manifest status (`None`/`Pending`/`Ready`/`Stale`/`Failed`) and manifest hash, plus `Chunk` and `ArchiveChunk` tables. Migrations for SQLite, MySQL and PostgreSQL. Nothing writes to them yet. |
| 4 | `feature/p2p-04-build-manifests` | Manifest service, Hangfire job, the "Generate chunk manifests" setting (off), and a "Rebuild manifest" button on the archive editor |
| 7 | `feature/p2p-07-endpoints` | `GET /api/Archives/{id}/ChunkManifest`, `GET /api/Chunks/{sha256}` (sign-in, `DownloadThrottle`, cache headers marking chunks as never changing), `GET /api/Chunks/Capabilities`, and the download format setting. The SDK's shared routes and headers are in `ChunkHttp`; the client that uses them is step 2's PR 9. |
| 5 | `feature/p2p-05-auto-triggers` | Build after upload. Mark `Stale` after repack and patch. Remove the manifest when an archive is deleted. |
| 6 | `feature/p2p-06-sweep` | Recurring sweep that converts existing archives: throttled, with a progress view, a "Build all now" button, and a disk space estimate |
| 8 | `feature/p2p-08-chunk-cleanup` | Delete chunks no archive links to, and leftover chunk files |

**Stack order** (each branch starts from the previous one, so the newest branch always holds everything built
so far): `main` → 00 → 01 → 02 → 03 → 04 → 07 → 05 → 06 → 08. PR 7 comes early so end-to-end testing can
start sooner. Branch names follow the project's `feature/` style, numbered so the order stays clear:
`feature/p2p-NN-name`.

**Submitting upstream:** a pull request to LANCommander can only target a branch that exists there, so
stacked branches go in one at a time. PR 01 targets `main`; once it's merged, the next one is rebased onto
`main` and opened, and so on. If the maintainers prefer, an upstream `feature/p2p` branch can collect them
instead.

**Where archives are created or changed** (manifests must follow these):
- **Upload completion:** `ArchiveService.CompleteUploadAsync`, from the Games, Redistributables and Tools
  endpoints.
- **Copying a local file:** `CopyFromLocalFileAsync`, called from `GameService`, `ToolService` and
  `RedistributableService`.
- **Imports:** `ArchiveImporter`.
- **Changes:** `RepackArchiveAsync` and `PatchArchiveAsync`.
- **Deletion:** `ArchiveService.DeleteAsync`.

The PR 6 sweep catches every path. PR 5 makes the common ones immediate and correct.

**Local testing:** see [Scripts/P2P/README.md](../../Scripts/P2P/README.md).
- From PR 4: run the server with its own data folder, convert test archives from the admin UI, and check with
  `Test-ChunkManifest.ps1` that the chunk store rebuilds every file exactly.
- From PR 7: request a manifest and its chunks over HTTP, and check that they reassemble into the original
  files.
