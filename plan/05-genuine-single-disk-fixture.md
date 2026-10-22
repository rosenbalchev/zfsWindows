# Step 05 — Genuine 5 GiB single-disk fixture

Status: `complete`

Progress: a reusable .NET 10 generator now creates the exact 5 GiB file vdev
through official Linux OpenZFS tools, writes more than 4 GiB of seeded logical
data using streaming buffers, and records whole-file plus deterministic range
SHA-256 values. The workload includes every required size class, compressible
and incompressible content, UTF-8 names, and an eight-level directory path.

Result: OpenZFS 2.4.1 generated and cleanly exported
`TestVolumes/single/disk01.raw` at exactly 5,368,709,120 bytes. Its 18 files
contain 4,447,150,593 logical bytes across 16 directories. The committed
manifest records pool GUID `17980794324421783239`, active/enabled features,
dataset and compression settings, exact file hashes, and 43 deterministic
range hashes. The Windows verifier enumerates every derived directory, streams
and hashes every file using bounded reads, checks every range sample, and
confirms the image remains byte-for-byte unchanged.

Evidence:

- image SHA-256 before and after:
  `B1954F223472CD523A0EE0670E21AADE8136EC9FB425031BF973054E0F48B946`;
- required size classes from 0 bytes through 512 MiB are present;
- Unicode path `/documents/Grüße-東京-📦.txt` and the eight-level nested path
  both pass exact SHA-256 verification;
- combined Step 03/04/05 run: 5 passed, 0 failed, 0 skipped in 66 seconds;
- native CMake build passes with MSVC `/W4 /WX`.

Objective: prove Linux/OpenZFS writes can be read by the Windows DLL.

Implementation:

- generate a deterministic 5 GiB raw image with official OpenZFS;
- populate the required multi-directory workload, including 0-byte through
  512 MiB files and Unicode/deep paths;
- write a JSON manifest containing pool identity, feature flags, sizes, paths,
  and independent SHA-256 hashes;
- verify all files and random offset reads from C#;
- hash the image before and after to prove read-only behavior.

Capacity exhaustion or unavailable Linux/OpenZFS tooling must be reported as a
blocker, never hidden by reducing the workload.

Commits: `tests: add OpenZFS fixture generator` and
`tests: verify genuine single-disk integrity fixture`.

