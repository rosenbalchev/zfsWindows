# Step 09 — Snapshot reads and lifecycle

Status: `complete`

Objective: discover snapshots and read historical dataset contents using
copy-on-write metadata, not copied files.

Implementation: snapshot enumeration, historical dataset roots, read-only
snapshot paths, rollback and deletion semantics in disposable pools.

Commit: `native: add snapshot discovery and historical reads`

## Implemented

- Added native snapshot enumeration with dataset object, creation TXG, and GUID
  metadata, plus managed `GetSnapshots` and `OpenSnapshot` APIs.
- Carried the selected DSL dataset object through filesystem traversal, SA
  metadata lookup, directory enumeration, and ranged file reads so object
  handles resolve against either the live head or a historical snapshot.
- Accepted feature-zapified DSL directory and dataset dnodes by validating
  their authoritative bonus types, matching OpenZFS behavior.
- Added a Linux/OpenZFS fixture generator that creates three changing states,
  verifies export/re-import, and validates rollback and snapshot deletion on a
  disposable image copy.
- Added exact integration coverage for snapshot discovery, current and
  historical directory trees (including empty directories), file sizes and
  SHA-256 values, deleted-file visibility, missing snapshots, and unchanged
  source image identity.

## Evidence

- Genuine fixture: OpenZFS `zfs-2.4.1-1ubuntu5.1`, 1 GiB sparse image, pool
  `zfswin_snapshots`, three snapshots.
- Fixture SHA-256:
  `3353C5955CCCFDE6AAD1545F0029FF4BEEAF38DAFEA76A53B5BCA4CFF01283D1`.
- Generator verified export/re-import, rollback to `snapshot2`, destruction of
  `snapshot1` on a disposable copy, and all four recorded namespace states.
- Focused snapshot integration test: 1 passed in 8.6574 seconds.
- Full non-kernel/non-stress regression: 8 passed in 3.4331 minutes, including
  native device, sparse/multi-block, compression, 5 GiB single-disk, mirror
  degradation/checksum fallback, and snapshot cases.

