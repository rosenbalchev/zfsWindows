# ZFS for Windows

This repository contains an independent Windows implementation around the
upstream OpenZFS source in [`OpenZFS/`](OpenZFS/). That directory is a git
submodule pinned to upstream tag `zfs-2.4.4` (branch `zfs-2.4-release`,
commit `71a9f957`). It is kept unchanged and is treated as the on-disk format
and algorithm reference. `third_party/openzfs-windows` is the pinned
OpenZFS-on-Windows port (`zfswin-2.4.1rc17`) used for the write core and the
kernel driver. Clone with `git clone --recurse-submodules`, or run
`git submodule update --init` in an existing clone; the submodules' own
nested submodules are not needed.

## Current status

Steps 00–11 of the plan are complete (read-only milestones, disposable-pool writes, and failure recovery). The repository now contains:

- a Visual Studio/MSBuild-friendly native CMake project;
- a read-only file-backed block-device abstraction;
- a small exported C ABI for opening an image, probing vdev labels, bounded
  block reads, and initial MOS/dataset/directory traversal;
- a .NET 10 interop project and xUnit test project;
- PowerShell build/test entry points;
- a fixture manifest contract and an implementation specification.

The native layer can traverse the genuine fixture into its populated child
dataset and return exact file bytes through C# using dnodes, metadata-driven SA
sizes, indirect block pointers, embedded or non-embedded LZ4 data, and sparse
hole expansion. The current fixture verifies both a 300,000-byte multi-block
file and a 300,000-byte sparse file. Managed path traversal resolves nested
micro-ZAP directories, enumerates typed directory entries, and reads
`/nested/nested.txt` exactly.

The 5 GiB single-disk acceptance fixture contains 4,447,150,593 logical bytes
across 18 files, including 512 MiB random and compressible files, UTF-8 names,
and an eight-level path. The integration test streams every file, verifies its
manifest SHA-256 and deterministic range samples, enumerates every directory,
and proves the image SHA-256 is unchanged.
Compatibility claims are made only for behavior exercised against a genuine
OpenZFS-generated image.

Compression compatibility is verified with fourteen genuine OpenZFS 2.4.1
images covering uncompressed blocks, LZJB, gzip levels 1–9, ZLE, LZ4, and
Zstandard. Tests stream and hash 448 MiB of file content, cover a compression
property change, report per-algorithm throughput, and prove every image remains
byte-for-byte unchanged.

The scalable small-file suite provides 5K, 50K, 500K, and 5M profiles with
streaming manifests and configurable fan-out/depth. The completed milestone
exhaustively validated the 5K and 50K profiles after Linux export/re-import,
including every file hash and size, exact recursive enumeration, repeated
random opens, concurrent enumeration, resource/latency reporting, and image
identity. The 500K and 5M profiles are opt-in dedicated-host workloads.
BenchmarkDotNet microbenchmarks and separate end-to-end stress runners keep
warm-cache measurements distinct from initial traversal results.

Read-only mirrored pools are supported through a two-member API. Genuine
OpenZFS mirror tests verify healthy reads, either member missing, and
checksum-based fallback from a deliberately corrupted member. The validated
5 GiB-per-member fixture contains more than 4 GiB logical data; its corruption
case exercised 2,389 peer fallbacks with exact final hashes. This is read
recovery only—no automatic repair or resilver is claimed.

Snapshot discovery and historical reads are supported for the selected data
dataset. The managed API enumerates snapshot names with dataset object,
creation TXG, and GUID metadata and opens each snapshot as an independent
read-only dataset. A genuine three-snapshot OpenZFS fixture verifies exact
current and historical namespaces, including modified and deleted files and
empty directories. Its generator also proves export/re-import, rollback, and
snapshot deletion semantics on a disposable copy; the Windows reader itself
performs no rollback or deletion.

Read-only recognition validates OpenZFS SHA-256 checksums on labels and
uberblocks and Fletcher-4 checksums on referenced non-embedded metadata before
using it.

## Development approach and resume flow

The implementation is executed as ordered steps documented in [`plan/`](plan/).
Each step has a dedicated Markdown file containing its objective, dependencies,
implementation tasks, verification commands, evidence requirements, and the
commit boundary. A completed step's filename ends in `_completed`; unsuffixed
step files are unfinished. Work is committed after each independently
reviewable step; the plan file is updated with the actual result and blockers.

If work is interrupted, call Codex again and say: “Continue the ZFS Windows
plan.” Codex should inspect `plan/README.md`, the last committed step, the
working tree, and the latest test results, then continue with the first
unfinished step. A step marked `blocked` must not be silently skipped. A
milestone is marked `complete` only after its stated genuine-ZFS evidence is
available.

The plan deliberately separates read-only compatibility, writable semantics,
and kernel-driver work. The driver is never loaded automatically on the host.

## Build in Visual Studio

From a *Developer PowerShell for VS* prompt:

```powershell
cmake -S src/native -B out/native -G "Visual Studio 17 2022" -A x64
cmake --build out/native --config Debug
dotnet test ZfsWindows.sln
```

The default managed tests do not require a driver, administrator rights, or
real disk writes. Genuine-image tests use `ZFS_TEST_IMAGE` and
`ZFS_STEP04_IMAGE` for development fixtures, or `ZFS_SINGLE_IMAGE` and
`ZFS_SINGLE_MANIFEST` for the 5 GiB acceptance fixture. Set
`ZFS_COMPRESSION_IMAGES` to the directory containing compression `.raw` files
and `ZFS_COMPRESSION_MANIFESTS` to `tests/fixtures/compression` to run the
compression matrix. Set `ZFS_MIRROR_DIRECTORY` to the two-member fixture
directory and `ZFS_MIRROR_MANIFEST` to its committed manifest to run mirror
recovery tests.

Set `ZFS_SNAPSHOT_IMAGE` to the snapshot fixture image and
`ZFS_SNAPSHOT_MANIFEST` to `tests/fixtures/snapshots/snapshots.json` to run the
snapshot discovery and historical-read test.

## Disposable-pool writes (step 10)

`zfs-write-core.exe` is a user-mode adapter that writes to disposable,
file-backed pool images. It is built against the pinned openzfs-windows
libzpool (`third_party/openzfs-windows`, tag `zfswin-2.4.1rc17`) and calls the
upstream DMU/ZAP/SA code directly, so allocation, copy-on-write, transaction
groups, checksums, and space maps are the genuine OpenZFS implementation. The
namespace operations are ports of the upstream Linux ZPL code paths
(`zfs_mknode`, `zfs_link_create`/`destroy`, the unlinked set and
`zfs_rmnode`, `zfs_rename`, `zfs_freesp`, `zfs_write`) and are verified
against upstream Linux OpenZFS 2.4.1.

Every command has the form
`zfs-write-core <command> <image-directory> <pool> <dataset> ...`. Paths are
`/`-separated and relative to the dataset root:

- `probe`, `sync`, `stat <path>`;
- `create-file <path>`, `mkdir <path>`;
- `write-file <path> <offset|append> <payload-file>`, `truncate <path> <size>`;
- `remove <path>`, `rmdir <path>` (optionally `--defer-reclaim`, a test hook
  that leaves the znode on the unlinked set as a crash before reclaim would);
- `rename <from-path> <to-path>` (replaces an existing file or empty directory);
- `run-script <file>`: the commands above, one per line, in a single import,
  plus `inject-write-errors <leaf|*>` and `clear-injections`.

Each command imports the pool, commits and syncs its transaction groups, and
exports it, so Linux imports the result without `-f`. Unlinks follow the
upstream crash-consistent ordering. The name is removed and the znode is
added to the unlinked set in one transaction, and Linux drains that set on
mount if reclaim never ran.

Verified with `scripts/test-write-core.py`:
- 73 Windows import/export cycles exercise every command and 18 expected
  error cases.
- A Linux → Windows → Linux round trip ends with an exact match against a
  model of every file's bytes and every directory's size and link count.
- Scrub reports no errors, `zdb -bcc` reports no leaks, and the unlinked set
  is empty after mount.
- In zdb, Windows-created files and directories are identical to
  Linux-created ones.

The adapter refuses, with an error, cases it does not port:
- inheritable parent ACLs;
- xattr directories;
- normalized or case-insensitive datasets;
- long names.

Quotas, non-root ownership, hard links, and symlinks are not implemented.
Only operate on disposable copies of images.

Build and test (the test needs SSH access to a Linux VM with OpenZFS):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-write-core.ps1
python scripts/test-write-core.py 192.168.195.63
```

The build script applies the patches in
[`patches/openzfs-windows/`](patches/openzfs-windows/) to the submodule
before compiling, so the submodule shows as modified; this is expected. The
first patch fixes `pread`/`pwrite` in libspl. They used a shared file pointer,
so under concurrent vdev I/O they wrote blocks at the wrong offset; this
corrupted a space map and left Linux unable to mount the datasets. The debug
CRT's abort dialog is disabled, so a failed OpenZFS `VERIFY` returns an exit
code instead of blocking on a popup.

## Failure injection and recovery (step 11)

`scripts/test-write-recovery.py` checks durability. The adapter's
`run-script` command acknowledges each step (`ack <n>`) only after its
transaction groups are synced. After each failure, Linux OpenZFS 2.4.1 must
import the pool without rewind, and the pool must match the acknowledged
state, or the same plus the in-flight step completed. Scrub must be clean,
`zdb -bcc` must report no leaks, and the unlinked set must drain. The
failures are:

- **Power loss.** `ZFS_WRITE_CORE_JOURNAL` journals every vdev write (with
  the bytes it replaced), every flush, and every acknowledgement.
  `scripts/zfs_crash_image.py` then rebuilds the disk after a power cut at
  any chosen point. Writes covered by a completed flush survive; other
  writes survive or are lost sector by sector. 47 cuts pass, including
  ones losing thousands of sectors. Windows also reopens crashed images
  and keeps writing.
- Process kills at random points.
- Upstream `zio_inject` write faults:
  - on a single disk, the pool suspends and later steps are never
    acknowledged;
  - on one mirror member, the member really loses the faulted writes and
    Linux repairs it.
- ENOSPC on a full pool, followed by reclaim.
- A mirror member that is missing or 96 MiB-corrupted.
- Destroyed front or back vdev label pairs.

```powershell
python scripts/test-write-recovery.py 192.168.195.63 [scenario ...]
```

## Safety

The default native API opens images read-only. Only the experimental
`zfs-write-core` adapter writes, and only to the file-backed image directory it
is given. There is no code that formats a
disk, repairs GPT, upgrades a pool, or loads a kernel driver.

