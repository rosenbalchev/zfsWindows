# Ordered implementation plan

## How to use this plan

Execute files in numeric order. Each file is one implementation step and ends
with a commit boundary. The implementation agent must:

1. Read the current step and its prerequisites.
2. Inspect the repository and preserve unrelated user changes.
3. Implement only the step's scope.
4. Run every listed verification command.
5. Record actual results, counts, and blockers in the step file.
6. Commit the step using the prescribed commit message.
7. Continue only when the step is complete, or stop and mark it blocked.

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
The first plan file whose status is not `complete` is the continuation point.
Do not repeat a completed commit unless verification proves it is missing or
invalid. If a commit exists but its step is still `in-progress`, rerun the
verification and update the step rather than creating duplicate work.

## Step index

| Step | File | Scope | Initial status |
|---|---|---|---|
| 00 | `00-workflow.md` | Planning and commit workflow | complete |
| 01 | `01-build-and-interop.md` | VS/MSBuild, C ABI, C# tests | complete |
| 02 | `02-block-device-and-labels.md` | Safe read-only device and labels | complete |
| 03 | `03-uberblocks-and-pool-recognition.md` | Label nvlist and uberblocks | complete |
| 04 | `04-mos-dataset-and-file-reads.md` | MOS, datasets, directories, files | complete |
| 05 | `05-genuine-single-disk-fixture.md` | 5 GiB fixture and hashes | complete |
| 06 | `06-compression.md` | Compression compatibility | in-progress |
| 07 | `07-scale-stress-and-benchmarks.md` | Small-file scale and measurement | planned |
| 08 | `08-mirrors.md` | Mirror reads, degradation, recovery | planned |
| 09 | `09-snapshots.md` | Snapshot discovery and historical reads | planned |
| 10 | `10-transactional-writes.md` | COW writes and persistence | planned |
| 11 | `11-crash-recovery.md` | Failure injection and durability | planned |
| 12 | `12-windows-driver.md` | WDK filesystem driver in VM | planned |

