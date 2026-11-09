# LAN Peer-to-Peer: TODO

Items deliberately left out of the current plan. See [README.md](README.md) for the design.

## Bundle small files into shared chunks

**Problem:** every file under 4 MB becomes its own chunk. A game with tens of thousands of tiny files means
tens of thousands of files in the server's chunk store, and as many requests on install.

**Idea:** pack small files together into shared 4 MB chunks, with the manifest giving each file's offset and
length inside its chunk. Large files keep their own chunks.

**Notes:**
- Needs a manifest `formatVersion` bump. Clients that only know version 1 must keep working, for example by
  the server offering both formats for a while.
- A change to one small file changes the hash of the bundle it's in, so the other files in that bundle are
  fetched again on update. Group files by folder to keep updates local.

## Content-defined chunking

Fixed 4 MB chunks shift whenever data is inserted near the start of a file, so every later chunk gets a new
hash. Content-defined chunking (for example FastCDC) keeps chunks stable around insertions. This would also be
a manifest `formatVersion` change.

## Retire zips

Both systems keep each game twice on the server (zip and chunks). Once chunk downloads are proven, zips could
be built from chunks only when needed (old launchers, exports), saving disk space.
