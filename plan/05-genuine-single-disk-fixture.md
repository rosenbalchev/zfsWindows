# Step 05 — Genuine 5 GiB single-disk fixture

Status: `planned`

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

Commit: `tests: add genuine single-disk integrity fixture`

