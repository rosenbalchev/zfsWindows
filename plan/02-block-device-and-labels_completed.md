# Step 02 — Read-only block device and vdev-label probe

Status: `complete`

Objective: implement safe file-backed reads without modifying an image.

Implementation:

- validate offsets, lengths, overflow, and device bounds;
- expose device size and sector geometry through opaque handles;
- probe all four vdev-label positions from the device size;
- support future GPT partition translation without hardcoded offsets;
- add byte-for-byte image identity checks around read-only tests.

Verification:

- native MSBuild and CMake builds;
- invalid path and out-of-range tests;
- disposable raw-file read test;
- image SHA-256 unchanged after every read.

Exit condition: the probe is safe and tested, but it must not be called ZFS
recognition until Step 03 validates actual nvlist and uberblock structures.

Result: native MSBuild and CMake builds passed; the managed suite passed 2
tests with 0 failures; the raw-file test verified bounded reads, reported
sector geometry, read-only mode, and unchanged SHA-256. Genuine ZFS
recognition remains intentionally deferred to Step 03.

Commit: `native: add safe read-only block device layer`

