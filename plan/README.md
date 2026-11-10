# Ordered implementation plan

## How to use this plan

Execute files in numeric order. Each file is one implementation step and ends
with a commit boundary. The implementation agent must:

1. Read the current step and its prerequisites.
2. Inspect the repository and preserve unrelated user changes.
3. Implement only the step's scope.
4. Run every listed verification command.
5. Record actual results, counts, and blockers in the step file.
6. When the step is complete, rename its file with the `_completed` suffix
   (for example, `07-scale-stress-and-benchmarks_completed.md`). An unsuffixed
   file is unfinished even if its internal status was edited accidentally.
7. Commit the step using the prescribed commit message.
8. Continue only when the step is complete, or stop and mark it blocked.

The status vocabulary is:

- `planned`: not started;
- `in-progress`: currently being implemented;
- `complete`: implementation and required evidence are finished;
- `blocked`: a concrete external dependency or missing evidence prevents safe progress.

Compilation alone never completes a filesystem milestone. Genuine OpenZFS
images, independent SHA-256 verification, read-only image identity checks, and
upstream compatibility are required where specified.

## Recovery after interruption

When resumed, inspect `git log --oneline`, `git status`, and this directory.
The first numbered plan file without the `_completed` suffix is the
continuation point; confirm its internal status before proceeding.
Do not repeat a completed commit unless verification proves it is missing or
invalid. If a commit exists but its step is still `in-progress`, rerun the
verification and update the step rather than creating duplicate work.

## Step index

| Step | File | Scope | Initial status |
|---|---|---|---|
| 00 | `00-workflow_completed.md` | Planning and commit workflow | complete |
| 01 | `01-build-and-interop_completed.md` | VS/MSBuild, C ABI, C# tests | complete |
| 02 | `02-block-device-and-labels_completed.md` | Safe read-only device and labels | complete |
| 03 | `03-uberblocks-and-pool-recognition_completed.md` | Label nvlist and uberblocks | complete |
| 04 | `04-mos-dataset-and-file-reads_completed.md` | MOS, datasets, directories, files | complete |
| 05 | `05-genuine-single-disk-fixture_completed.md` | 5 GiB fixture and hashes | complete |
| 06 | `06-compression_completed.md` | Compression compatibility | complete |
| 07 | `07-scale-stress-and-benchmarks_completed.md` | Small-file scale and measurement | complete |
| 08 | `08-mirrors_completed.md` | Mirror reads, degradation, recovery | complete |
| 09 | `09-snapshots_completed.md` | Snapshot discovery and historical reads | complete |
| 10 | `10-transactional-writes_completed.md` | COW writes and persistence | complete |
| 11 | `11-crash-recovery_completed.md` | Failure injection and durability | complete |
| 12 | `12-windows-driver_completed.md` | WDK filesystem driver in VM | complete |
| 13 | `13-read-and-write-caching_completed.md` | ARC, L2ARC, Cache Manager, sync and SLOG through the driver | complete |
| 14 | `14-userspace-tools-and-installer_completed.md` | Administration tools and Windows setup package | complete |
| 15 | `15-user-mode-filesystem.md` | User-mode variant: libzpool engine bridged to WinFsp, service and installer; no Microsoft driver signature needed | in-progress |
| 16 | `16-gui-administration-tool.md` | ZfsWinGui: status, compression/dedup/sync, mirrors, read/write caches, user-mode mounts | in-progress |
| 17 | `17-documentation-and-1.0-distribution.md` | 1.0 docs, x64/ARM64 downloads of both variants, Windows support matrix, ZFS vs NTFS/ReFS | planned |
| 18 | `18-user-mode-feature-parity_completed.md` | User mode: ACLs, streams, `.zfs\snapshot`, block cloning, pool/dataset administration through an ioctl bridge, image files | complete |
| 20 | `20-gui-dashboard-and-monitoring.md` | GUI (issue #17): HTML overview, damaged files, redundancy and drive health, live rates and ARC memory, snapshot create/restore/temporary mount; both editions | planned |

