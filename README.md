# ZFS for Windows

This repository contains an independent Windows implementation around the
upstream OpenZFS source in `OpenZFS/`. That directory is a git submodule
pinned to upstream tag `zfs-2.4.4` (branch `zfs-2.4-release`, commit
[`71a9f957`](https://github.com/openzfs/zfs/tree/71a9f9578616a90c3c14bb59629fb4d31bfd68d1)). It is kept unchanged and is treated as the on-disk format
and algorithm reference. `third_party/openzfs-windows` is the pinned
OpenZFS-on-Windows port (`zfswin-2.4.1rc17`) used for the write core and the
kernel driver. Clone with `git clone --recurse-submodules`, or run
`git submodule update --init` in an existing clone; the submodules' own
nested submodules are not needed.

## Download and install (1.0)

> **Latest stable: 1.0.12** (2026-11-06), in
> [`downloads/1.0/release/`](downloads/1.0/release/). The x64 Release build
> passed every VM suite (kernel and user mode, including install, upgrade
> and uninstall) and the Linux validation of the pools it wrote (plan step
> 19). Its Debug build is in [`downloads/1.0/debug/`](downloads/1.0/debug/).
> 1.0.8–1.0.11 are superseded: their Release builds had optimizer-only
> bugs, fixed by patches 0026–0029.

Versions are `1.0.N`; the build number goes up with every release build.
Each version comes in two builds:

| Folder | Build | For |
| --- | --- | --- |
| [`downloads/1.0/release/`](downloads/1.0/release/) | **Release**: optimized native code; file names as below | normal use |
| [`downloads/1.0/debug/`](downloads/1.0/debug/) | **Debug**: unoptimized native code with full debug checks; file names end in `-debug` | diagnosing problems |

Each folder has its own `SHA256SUMS`, `VERSION.json` and the test
certificate. There are two packages, and you install one of them:

| Package | What it is | Requirements |
| --- | --- | --- |
| `OpenZFS-for-Windows-1.0.N-x64-usermode.msi` | OpenZFS in a Windows service that serves datasets as volumes through [WinFsp](https://github.com/winfsp/winfsp) (WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos). No kernel driver of ours, so it works with Secure Boot on. | WinFsp 2.1+ |
| `OpenZFS-for-Windows-1.0.N-x64-testsigned.msi` | The OpenZFS kernel driver with the `ZfsWinBroker` service, `zfswin.exe` and the `ZfsWin` PowerShell module | test signing on, Secure Boot off, `zfswindows-test.cer` trusted. Test machines only, until the driver is attestation-signed. |

Verify the download against `SHA256SUMS`. Then follow
[`docs/installation.md`](docs/installation.md) (install, mount, upgrade,
uninstall, recovery) and [`docs/zfswin-cli.md`](docs/zfswin-cli.md) (the
command reference). To build from source, including the release script and
all test suites, see [`docs/building.md`](docs/building.md).

Each package exists for **x64** and **ARM64** (`…-arm64-usermode.msi`,
`…-arm64-testsigned.msi`). After an interactive install, each opens its own
getting-started guide (`README.html` in the install folder).

Both packages include **OpenZFS for Windows**, a graphical administration
tool in the Start menu (`ZfsWinGui.exe`, needs the .NET 10 Desktop Runtime).
- **Both editions** (the user-mode one since 1.0.7, through its ZFS bridge):
  - pool status and the vdev tree;
  - scrubs;
  - compression, deduplication and sync per dataset;
  - adding and removing mirror disks, read caches (L2ARC) and write logs
    (SLOG).
- **User-mode edition, in addition:** its service and mounts.

What has been tested:
- **x64 Debug:** verified in disposable Windows 11 24H2 x64 VMs and against
  upstream OpenZFS on Linux (plan steps 12–18).
- **x64 Release (1.0.12):** the same VM suites and Linux validation, all
  passed (plan step 19).
- **ARM64 Release:** tested with 1.0.11 in a Windows 11 ARM64 VM (plan step
  19). The user-mode edition passed all of its administration checks. The
  kernel edition installed and passed its CLI and filesystem checks (same
  known gaps as x64), but an import/export loop hung. That hang is fixed in
  1.0.12 (patch 0029), and the fix is verified on x64 but **not yet on
  ARM64**. Upgrade, uninstall, the read and power-cut phases, and Linux
  validation of an ARM64-written pool are not done yet.
- Windows 10 and Windows Server have not been tested yet (plan step 17).

## User mode or kernel mode? Feature matrix

Both editions run the same OpenZFS engine, write the same on-disk format,
and use the same administration tools. A pool moves freely between them,
with its Windows ACLs and alternate data streams, and to Linux, FreeBSD or
macOS. They differ in how Windows reaches the file system.

✅ tested in this project's VM runs (release 1.0.7) or Linux validation ·
☑️ implemented, not yet tested here, or only partly works (see note) ·
❌ not available

| Area | Feature | Kernel mode (`…-testsigned.msi`) | User mode (`…-usermode.msi`) |
| --- | --- | --- | --- |
| **Install** | Works with Secure Boot on | ❌ needs test signing until the driver is Microsoft-signed | ✅ only WinFsp's Microsoft-signed driver loads |
| | Prerequisites | test signing, project certificate trusted | WinFsp 2.1+ |
| | Install, upgrade, repair, uninstall without touching pools | ✅ | ✅ install, uninstall (upgrade, repair ☑️) |
| | Getting-started page, Start-menu shortcuts | ✅ | ✅ |
| | x64 | ✅ | ✅ |
| | ARM64 | ☑️ built and checked statically, not run | ☑️ built and checked statically, not run |
| **Files** | Read and write files and folders | ✅ | ✅ |
| | Read-only mount (image stays byte-identical) | ✅ | ✅ |
| | Durable after flush or write-through (power-cut test) | ✅ | ✅ |
| | Case-insensitive datasets | ☑️ | ☑️ |
| | Windows ACLs stored on the pool (same format in both, cross-checked) | ✅ | ✅ |
| | Windows ACLs enforced on open (a deny entry, a read-only grant) | ❌ upstream checks only create and delete; opening an existing file is not checked | ✅ |
| | Alternate data streams (same format, cross-checked) | ✅ | ✅ |
| | Snapshots browsable in `.zfs\snapshot` | ☑️ listed; files readable in some snapshots only (in the 1.0.7 run, `@s1` after a re-import was, a new `@f1` was not) | ✅ contents, ACLs and streams as at snapshot time; read-only |
| | Zero-copy block cloning | ✅ `FSCTL_DUPLICATE_EXTENTS_TO_FILE` (Windows' own call) and `zfswin clone` | ✅ `zfswin clone` only: WinFsp does not pass Windows' clone call to a file system |
| | Unbuffered I/O (`FILE_FLAG_NO_BUFFERING`) | ✅ | ✅ |
| | Child dataset in a folder of its parent's drive | ✅ | ☑️ gets a drive letter of its own instead (WinFsp cannot mount inside a WinFsp volume) |
| | zvols (ZFS block devices) | ☑️ | ❌ |
| **Data features** | Read and write compressed datasets (lz4, zstd, gzip, …) | ✅ | ✅ measured on the same data: off 1.00×, lz4 2.65×, gzip-9 2.81×, zstd and zstd-19 2.98×, lzjb 2.80×, zle 1.01× (zero runs only); Linux reads the same bytes and ratios |
| | Read and write deduplicated datasets | ✅ | ✅ four identical 16 MiB writes: pool `dedupratio` 1.00 → 4.00; three deleted → 1.01, survivor intact; dedup + zstd; Linux sees the dedup table |
| | Mirrored pools: attach, resilver, detach, replace | ✅ | ✅ |
| | Native encryption | ☑️ not tested | ☑️ not tested |
| **Administration** | Create pools on image files; import and export | ✅ `zfswin`, GUI, PowerShell, `zpool` | ✅ the same tools, through the service's ZFS bridge |
| | Mount at boot | ☑️ | ✅ pools and mounts back after a service restart |
| | Create and destroy datasets | ✅ | ✅ |
| | Change compression, dedup, sync and other properties | ✅ | ✅ |
| | Create, destroy and roll back snapshots | ✅ create and destroy with `zfswin`; rollback with `zfs rollback` (step 12) | ✅ create and destroy with `zfswin`; rollback with `zfs rollback` ☑️ |
| | Scrub (verify and repair) | ✅ | ✅ |
| | Add and remove mirror disks; replace a disk | ✅ | ✅ |
| | Add and remove read cache (L2ARC) and write log (SLOG) | ✅ | ✅ |
| | `zfswin` CLI, `ZfsWin` PowerShell module | ✅ | ✅ |
| | Standard users may read, only elevated administrators change | ✅ | ✅ |
| | GUI (`ZfsWinGui.exe`) | ✅ status, scrub, properties, mirrors, read cache and write log (self-check tested; the windows themselves not yet clicked through) | ✅ the same, plus the service and its mounts (self-check tested) |
| | `zfs send`/`receive`, `zpool events` | ☑️ | ❌ they hand file handles to the engine, which the bridge cannot carry |
| **Behaviour** | A bug can… | crash Windows (bugcheck) | stop the service; the volume disappears, Windows keeps running |
| | Performance (Debug builds; step-15 and step-18 benchmarks) | faster cached and unbuffered reads (2.1 GB/s, 367 MB/s) | similar large writes (42 MB/s, disk-bound); reads 0.86 GB/s and 214 MB/s; creating small files costs more since each gets its own ACL (311 files/s) |
| | Interoperability with Linux OpenZFS | ✅ every pool written in the tests imported and verified | ✅ every pool written in the tests imported and verified |

**Which one?**
- **User mode** for ZFS on a normal, Secure-Boot machine today. It has the
  full administration toolset and enforces Windows ACLs. A bug stops a
  service, not Windows.
- **Kernel mode** for the fastest reads, zvols, Windows' own clone call
  (Explorer, `copy`), and child datasets mounted inside their parent's
  folder. Use it on a test machine until the driver is Microsoft-signed.
## Why ZFS on Windows? NTFS, ReFS and ZFS compared

NTFS and ReFS are Microsoft's file systems, and for most Windows machines
they are the right default. ZFS earns its place when your data must stay
correct over years, when you want instant snapshots, or when the same disks
must also work on Linux, FreeBSD, macOS or a NAS.

The table compares the file systems as shipped:
- **Measured:** results this project measured in its VM tests (plan steps
  06–15).
- **Not tested here:** features this project has not exercised itself.
- Everything else is documented behaviour of the respective file system.

| | NTFS | ReFS | ZFS (this project) |
| --- | --- | --- | --- |
| **Checksums** | none on data or metadata | metadata always; data only with *integrity streams*, off by default on most volumes | every block, data and metadata, verified on every read |
| **Silent-corruption repair** | no (chkdsk repairs structure, not file contents) | with integrity streams on a Storage Spaces mirror or parity | automatic from a mirror, raidz or `copies=2`. **Measured:** a scrub repaired 83.4 MB of deliberately corrupted mirror data (step 12). |
| **Snapshots** | Volume Shadow Copy (VSS), volume-wide | no general volume snapshot and rollback on client Windows | instant, read-only, per dataset; browse `.zfs\snapshot`, roll back, send elsewhere |
| **Replication** | none built in | none built in | `zfs send`/`receive`, full or incremental (not tested here) |
| **Copy-on-write / cloning** | no | block cloning (Dev Drive uses it for fast copies) | always copy-on-write; zero-copy block cloning: through the same Windows call as ReFS (`FSCTL_DUPLICATE_EXTENTS_TO_FILE`) in the kernel edition, through `zfswin clone` in both. **Measured:** an 8 MiB clone shared every block with its source (`bcloneused` 8 MiB) in both editions, confirmed by Linux OpenZFS for the kernel edition. |
| **Compression** | per-file LZNT1 or "compact" | Windows Server 2025 | inline lz4, zstd or gzip per dataset. **Measured:** 5.29× (zstd) and 6.34× (gzip) on text |
| **Deduplication** | Windows Server Data Deduplication role | Windows Server | inline, per dataset. **Measured:** 4.00× on duplicate data |
| **Pooling and RAID** | Storage Spaces (separate layer) | Storage Spaces | built in: mirror, raidz1/2/3, hot spares; datasets share one pool |
| **Encryption** | BitLocker, EFS | BitLocker | native per-dataset encryption (not tested here); BitLocker does not apply |
| **Same disk on Linux, FreeBSD, macOS** | Linux read/write through ntfs3 or ntfs-3g | Windows only, and the on-disk version changes with Windows releases | yes: every pool written in this project's tests was imported and verified by upstream OpenZFS on Linux |
| **Boot / system volume** | yes | no on client Windows | no |
| **Microsoft-supported** | yes | yes | no: open source, test-signed kernel driver or user mode through WinFsp |

### Async operations, like ReFS Dev Drive?

A **Dev Drive** is a ReFS volume that Windows trusts for development
workloads. Its "performance mode" lets Microsoft Defender scan files
*asynchronously*, instead of holding up every file open, and that is where
most of its speed-up comes from. Windows grants this only to ReFS Dev Drives.
**A ZFS volume does not get it:** Defender scans files on ZFS volumes the
normal, synchronous way, with both the kernel driver and the user-mode
edition. You can add a Defender exclusion for a ZFS path yourself, at your
own risk.

ZFS is asynchronous in a different place, its write path:
- Writes collect in memory and are committed together as one transaction
  group, every few seconds.
- An application that needs durability flushes the file or opens it
  write-through. Those calls wait for the ZFS intent log or the commit.
  **Measured:** after a hard power-off, every flushed and every write-through
  file was intact in both editions (steps 13 and 15).
- The `sync` property tunes this per dataset. `sync=standard` is the default.
  `sync=disabled` makes everything asynchronous, which is faster but loses
  the last seconds of writes on a power cut. `sync=always` makes every write
  durable before it returns.

### Zero-copy

**Block cloning (kernel edition).** The driver implements
`FSCTL_DUPLICATE_EXTENTS_TO_FILE`, the call ReFS and Dev Drive use for
instant copies. It reports `FILE_SUPPORTS_BLOCK_REFCOUNTING` on pools with
the `block_cloning` feature, which every pool created by OpenZFS 2.2 or later
has.
- A cloned range shares the existing blocks through ZFS's block reference
  table. Nothing is read or rewritten, and the copy uses no extra space until
  one side changes.
- **Measured (step 16):**
  - an 8 MiB clone was byte-identical to its source;
  - `bcloneused` went from 0 to 8,388,608 bytes;
  - Linux OpenZFS confirmed the shared blocks after import.
- Whether Explorer or `CopyFile` uses this automatically depends on the
  Windows version. Tools that call the FSCTL directly always can.

**Other forms:**
- **The user-mode edition (1.0.7)** clones the same way inside the engine.
  WinFsp, however, passes a file system only control codes of its own device
  type, so Windows' `FSCTL_DUPLICATE_EXTENTS_TO_FILE` (Explorer, `copy`)
  never arrives.
  - `zfswin clone <source> <target>` uses this edition's own control code
    instead, and the standard FSCTL on the kernel edition.
  - **Measured:** an 8 MiB clone was byte-identical, and `bcloneused` went
    from 0 to 8,388,608.
- Unbuffered I/O (`FILE_FLAG_NO_BUFFERING`) skips the Windows cache in both
  editions.
- OpenZFS's own Direct I/O, which also bypasses the ARC, is not wired up on
  Windows.

**Choose NTFS or ReFS** for:
- the system and boot volume;
- BitLocker-encrypted system disks;
- software that relies on NTFS-only features (EFS, transactional NTFS, some
  backup and antivirus products);
- Windows Server roles that need ReFS or Storage Spaces Direct;
- anything that must be a Microsoft-supported, WHQL-certified file system;
- small-memory machines (ZFS's ARC cache wants RAM);
- Dev Drive workloads that depend on Defender's asynchronous scanning.

**Choose ZFS** for:
- data disks where silent corruption is unacceptable;
- data that benefits from cheap snapshots and rollback;
- disks shared with Linux or a NAS;
- compression- and dedup-friendly datasets;
- home-lab and development pools.

## Current status

Steps 00–14 of the plan are complete: the read-only milestones, disposable-pool writes, failure recovery, the kernel driver in an isolated VM, read and write caching through the driver (including power-loss durability), and the administration tools and kernel-driver installer. Step 15, the user-mode WinFsp variant and its installer, works end to end; its remaining items are listed in the step file. Step 18 (complete, release 1.0.7) brings the user-mode edition to the kernel edition's features and tools: Windows ACLs (enforced), alternate data streams, `.zfs\snapshot`, block cloning, and full pool and dataset administration through a bridge to upstream `zfs_ioctl.c`. The repository now contains:

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
python scripts/test-write-core.py <vm-host>
```

`<vm-host>` is the Linux verification VM. It can also be given as
`ZFS_VM_HOST`; the default in `scripts/zfstest.py` is the last known
address. The VM gets its address by DHCP on a bridged switch, so after a
network change, find it by its MAC address with `Get-NetNeighbor`.

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
python scripts/test-write-recovery.py <vm-host> [scenario ...]
```

## Kernel driver in an isolated VM (step 12)

The OpenZFS-on-Windows kernel driver is built from the pinned
`third_party/openzfs-windows` submodule, with the repository patches
applied, by `scripts/build-driver.ps1`. That script uses MSVC, which the
kernel sources require, and test-signs `out/driver-package` with a
user-store certificate trusted only inside the VM. **It is never installed
on the development host.**

- `scripts/vm/New-DriverTestVm.ps1` (elevated, once) creates the Windows 11
  VM, with test signing enabled.
- `scripts/vm/Test-DriverInVm.ps1` (unelevated) runs the
  `prepare|install|read|write|extended|cache` phases under Driver Verifier.
  `-VerifierFlags 0xA6FBB` adds forced pending I/O, IRP logging and the
  extra DDI rules. Remoting uses PowerShell Direct (the Hyper-V VM bus), and
  the install phase turns the guest's kernel debugger off.
- `scripts/vm/validate-written.py`, `validate-extended.py` and
  `validate-cache.py` have Linux OpenZFS validate every pool the driver
  wrote.
- Kernel dumps from bugchecks, or from an NMI for a hang, are analyzed
  inside the VM with `scripts/vm/Analyze-GuestDump.ps1`, never on the host.

Verified through standard Windows file APIs, and then by Linux OpenZFS:
- **Reads:** the genuine 5 GiB fixture reads with exact hashes and an
  unchanged image.
- **Writes:** create, append, rename and delete. Linux checks the hashes,
  runs a scrub and finds no leaks with `zdb -bcc`.
- **Extended workload** (`-Phase extended`):
  - alternate data streams, which Linux sees as `user.<name>:$DATA`
    xattrs;
  - DACL edits, stored as `windows:security.NTACL`;
  - a 20,000-entry directory, and wildcard listings;
  - concurrent and memory-mapped writes;
  - snapshots read through `.zfs\snapshot`, and rollback;
  - `dedup=on` (4.00x), and `zstd`/`gzip` compression (5.29x/6.34x);
  - scrub;
  - a mirror created by the driver, whose scrub repaired 83.4 MB of
    deliberate corruption.

The driver bugs found this way are fixed as patches 0004–0018 (listed in
[`patches/openzfs-windows/README.md`](patches/openzfs-windows/README.md)):
crashes, hangs, wrong reads, failed wildcard listings and, in step 13, lost
writes after power loss.

## Read and write caching (step 13)

`Test-DriverInVm.ps1 -Phase cache` exercises every cache layer through the
driver:
- **ARC:** `zfs_arc_max` and `primarycache` are honored.
- **L2ARC:** checked against the same scenario on Linux
  (`scripts/vm/l2arc-reference.py`): hits, persistent rebuild, corrupted
  cache blocks rejected and correct data returned, and online removal.
- **Cache Manager coherency:** between handles, memory mappings, and cached
  and non-cached I/O.
- **Power cuts:** Hyper-V `TurnOff` of the VM while writers run in every
  sync mode and with a SLOG, plus import with the log device missing.

The power-cut tests found that, before patches 0015–0017, acknowledged
`FlushFileBuffers`, write-through, `sync=always` and SLOG writes could be
lost after a power failure:
- intent-log create records were malformed;
- write-through never synced;
- Windows replay dropped every logged write.

`scripts/vm/Test-ZilReplay.ps1` with `validate-zil-replay.py` now shows Linux
replaying Windows-written logs with no acknowledged durable write missing.
An NTFS control writer on the same virtual disk loses nothing either.

## Safety

The default native API opens images read-only. Only the experimental
`zfs-write-core` adapter writes, and only to the file-backed image directory it
is given. The kernel driver is installed only inside the isolated test VM, by
`scripts/vm/Test-DriverInVm.ps1` over remoting; nothing installs or loads it on
the development host. There is no code that formats a disk, repairs GPT, or
upgrades a pool.


## License

The code written for this repository is MIT-licensed. The files derived from
OpenZFS stay CDDL-1.0: `src/native/ZfsWriteCore/zfs_write_core.c`, the patches
in `patches/openzfs-windows/`, and both submodules. [`LICENSE.md`](LICENSE.md)
lists every exception and every third-party component, with its license, and
the obligations for binary distributions.
