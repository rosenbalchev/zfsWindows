# Step 08 — Mirrored pools

Status: `complete`

Objective: read genuine two-device mirrors and validate checksum-based fallback.

Implementation: topology discovery, member loss, corruption of disposable
copies, valid-member recovery, and later replacement/resilver verification.

No automatic self-healing claim is allowed until an independently validated
resilver completes.

Implementation: the native device now models one- and two-member topologies,
validates genuine mirror label nvlists (including the two-child topology),
selects consistent uberblocks across available members, and retries physical
block reads on the peer when a member is absent, out of bounds, unreadable, or
fails its block-pointer checksum. The managed API exposes topology, configured
and available member counts, and cumulative read fallbacks.

Evidence (2026-10-22): OpenZFS 2.4.1 created a genuine mirror from two 5 GiB
images. Its six-file workload contains 4,299,227,263 logical bytes, including
512 MiB incompressible data, 3.5 GiB compressible data, empty/small/Unicode
files, and nested directories. Linux export/re-import verified every hash;
allocated dataset space was 656,560,128 bytes.

The Windows acceptance test completed in 2m54s and proved:

- the healthy pair reports mirror topology, two configured/available members,
  the expected pool GUID, ashift 12, and no unsupported read features;
- every file streams to its independent SHA-256 with both members present;
- every file remains exact with only member 1, and again with only member 2;
- a disposable copy of member 1 with bytes 4–128 MiB zeroed caused 2,389
  checksum/read fallbacks to the untouched second member while every final
  file hash still matched;
- both golden 5 GiB image SHA-256 values were unchanged after all tests.

The corrupt copy is ignored test data. No block was repaired and no resilver
was attempted, so this milestone claims detection and read fallback only.

Commit: `native: add mirrored vdev reads and recovery tests`

