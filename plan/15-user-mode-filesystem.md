# Step 15 — User-mode filesystem (WinFsp bridge) and its installer

Status: `in-progress`

Prerequisites:
- Steps 10, 11 and 14 are complete: the user-mode `libzpool` write core
  (`src/native/ZfsWriteCore`) is verified against upstream OpenZFS, and the
  broker, `zfswin.exe` and the WiX installer pipeline exist.
- Step 16 (documentation and 1.0 distribution) follows this step and ships
  both variants.

## Why

Since Windows 10 1607, with Secure Boot on, Windows loads a new kernel-mode
driver only if Microsoft has signed it (attestation or WHQL). OpenZFS.sys
therefore cannot be installed on an ordinary machine without either a
Partner Center submission (EV certificate) or test signing with Secure Boot
off. A Windows filesystem cannot be a UMDF driver, so the user-mode route is
a filesystem-in-user-space framework whose kernel driver is already
Microsoft-signed:

- **WinFsp** (chosen): FUSE-style, Microsoft-signed `winfsp-x64.sys` /
  `winfsp-a64.sys`, native `FSP_FILE_SYSTEM_INTERFACE` API, ships x64 and
  ARM64, and is among the fastest Windows user-mode filesystem frameworks.
- Dokany was considered; it is equally usable but WinFsp has the more complete
  Windows semantics (security descriptors, reparse points, streams), so it is
  the one target.

Nothing in the user-mode variant needs a Microsoft signature: our code runs as
an ordinary service, and the MSI and binaries can be signed with any trusted
code-signing certificate.

## Requirements from the project owner

- **Portable code for user mode and kernel mode.** The shared layer
  (`src/native/ZfsUserMode/zum_zpl.c`, `zum_api.c`, `zum_impl.h`, `zum.h`)
  uses only OpenZFS kernel interfaces (DMU, ZAP, SA, SPL `kmem_*` and
  `cmn_err`), never the C runtime or Win32. The same source then builds into
  `libzpool` (user mode) and into the kernel module. Only these differ:
  - **Front ends:** `src/native/ZfsWinFsp/zum_winfsp.c` (WinFsp) in user
    mode, and the I/O-manager dispatch in the kernel.
  - **Pool lifetime:** `zum_pool_um.c` (`kernel_init`, the userland pool
    search, `spa_import`/`spa_export`) in user mode, and the driver's own
    import in the kernel.

  Windows metadata uses the kernel driver's formats and mappings
  (`zfs_getwinflags`/`zfs_setwinflags_xva`), so a pool moves between the
  variants unchanged.
- **Two installer artifacts.** `OpenZFS-for-Windows-<v>-<arch>-testsigned.msi`
  (kernel driver) and `OpenZFS-for-Windows-<v>-<arch>-usermode.msi`
  (WinFsp), both from `scripts/build-installer.ps1 -Variant kernel|usermode`.
  Each refuses to install next to the other.
- **The same test suite on both variants, with durations and performance
  reported side by side.** This uses `scripts/vm/guest-bench.ps1`, run by
  `Test-DriverInVm.ps1 -Phase bench` and `Test-UserModeInVm.ps1 -Phase bench`.

## Scope: a bridge, not a rewrite

The ZFS engine stays upstream OpenZFS `libzpool` (the same code `zdb` and
`ztest` run in user mode), plus the repository's write core that already
implements the ZPL operations on top of it. This step adds only:

1. **Engine library** — `src/native/ZfsUserMode/zfs_um_engine.{c,h}`.
   Refactor `zfs_write_core.c` so its znode helpers (lookup, create, remove,
   rename, truncate, write, stat, ACL masks) live in a library with a C API;
   the existing `zfs-write-core` CLI becomes a thin `main()` over it so the
   step-10/11 tests keep passing unchanged. Add what the CLI never needed:
   - pool open (import by device or image path, read-only or read-write),
     dataset open, clean close/export;
   - handle-based API (`zum_open`/`zum_read`/`zum_write`/`zum_readdir`/
     `zum_getattr`/`zum_setattr`/`zum_flush`/`zum_close`) instead of paths;
   - reads through `dmu_read`, directory enumeration through the ZAP cursor,
     snapshots read-only through `.zfs\snapshot\<name>`;
   - Windows metadata in the on-disk formats the kernel driver uses, so pools
     move between the two variants: ADS as `user.<name>:$DATA` xattrs,
     security descriptors as `windows:security.NTACL`, DOS attributes in the
     znode flags, case-insensitive lookups honouring `casesensitivity`.
2. **WinFsp bridge** — `src/native/ZfsUserMode/zfs_winfsp.c`. Implements
   `FSP_FILE_SYSTEM_INTERFACE` (GetVolumeInfo, GetSecurityByName, Create,
   Open, Overwrite, Cleanup, Close, Read, Write, Flush, GetFileInfo,
   SetBasicInfo, SetFileSize, CanDelete/SetDelete, Rename, GetSecurity,
   SetSecurity, ReadDirectory, GetStreamInfo) by calling the engine. It maps
   errno to NTSTATUS, FILETIME to ZFS timestamps, and enforces sharing and
   delete-on-close through WinFsp. Writes go into the open txg; `Flush` and
   `FILE_FLAG_WRITE_THROUGH` commit (`txg_wait_synced`) before returning.
   The engine is serialised behind one lock at first. Concurrency is widened
   only after the stress tests pass with the lock in place.
3. **Service** — `zfswin-um.exe`. It runs as a Windows service (one instance
   per mounted pool, `FspServiceRun`), mounts each dataset on a drive letter
   or folder, and exports the pool on stop and on shutdown. The step-14
   broker gains a `usermode` backend: `zfswin pool import/export` and
   `dataset mount/unmount` start and stop these instances. When the kernel
   driver is not installed, the broker runs `zpool`/`zfs` admin reads
   through the engine instead of the kernel driver.
4. **Installer** — `installer/ZfsWin.UserMode/`, producing
   `OpenZFS-for-Windows-<v>-<arch>-usermode.msi`. It installs the engine,
   bridge, service, broker, `zfswin.exe` and the PowerShell module. It
   requires WinFsp at or above a minimum version and refuses with a clear
   `REFUSED:` line if it is missing. A WiX Burn bundle
   (`...-usermode-setup.exe`) chains the unmodified, Microsoft-signed WinFsp
   MSI and then ours. It never touches the kernel-driver variant: the two
   MSIs refuse to install side by side.

## Licensing

- WinFsp is GPLv3 with a FLOSS exception (or a commercial licence). Confirm,
  and record in `third_party/README.md` and the SBOM, that the exception
  covers linking the CDDL `libzpool` and this MIT bridge, and that
  redistributing the unmodified WinFsp MSI inside the Burn bundle is
  permitted. If either cannot be confirmed, the bundle is not shipped and the
  MSI tells the user to install WinFsp from its official site.
- The WinFsp SDK headers and import library are taken from the official MSI
  through an administrative extraction (`msiexec /a`), which installs nothing
  on the build machine. The exact version and SHA-256 are pinned.

## Known limitations to document

- No zvols (block devices need a kernel driver).
- Throughput and metadata latency are below the kernel driver: every
  operation crosses kernel/user mode. Benchmarks are recorded against the
  step-13 kernel numbers.
- Mounts start with the service, after the core boot services, so a ZFS
  dataset cannot hold paths needed early in boot (page file, profiles,
  services that start before it).
- One engine lock at first: concurrent I/O to one pool is serialised.

## Verification

All installs run only in disposable VMs. The user-mode VM checkpoint has
**Secure Boot on and test signing off**, and no project certificate trusted
in the kernel-mode sense. That checkpoint is the proof that no Microsoft
signature on our code is required.

1. **Unit tests.** Engine tests run on the host, with no WinFsp, against
   copies of the genuine fixtures. They cover lookup, read, readdir and
   write/rename/truncate/remove, then export, then an upstream `zdb -bcc`
   check on Linux. The existing step-10/11 write-core tests still pass after
   the refactor.
2. **Read fidelity.** In the VM, mount the Linux-created single, compression,
   mirror and snapshot fixtures read-only. Every file must match the fixture
   manifests (SHA-256), including `.zfs\snapshot`.
3. **Write fidelity.** Run the step-12 write workload (files, directories,
   rename, overwrite, truncate, delete, ADS, ACLs, attributes, large and
   sparse files) through the drive letter and export. Then Linux imports the
   pool, every hash matches, the scrub is clean and `zdb -bcc` reports no
   leaks (a `validate-usermode.py` like `validate-written.py`).
4. **Interchange.** A pool written by the user-mode variant imports in the
   kernel-driver VM, and the other way round, with identical manifests.
5. **Robustness.** Run the Windows tools (`robocopy`, Explorer copy, `fsutil`,
   `icacls`, `fc`) and a concurrent stress run. Kill the service mid-write:
   the pool still imports afterwards, and step-11 durability holds. Nothing
   synced is lost, and the ZIL is replayed.
6. **Installer.** Test fresh install, install refused without WinFsp, Burn
   bundle install, repair, upgrade, uninstall, and rollback, with the step-14
   state checks. Uninstall leaves pools untouched and WinFsp installed.
7. **Benchmarks.** Sequential and small-file throughput against the kernel
   driver on the same VM, recorded honestly.

## Evidence (2026-11-05, phase 1)

Status: `in-progress`. The bridge, the service and the user-mode MSI work end
to end and are verified. The items under *Remaining* are still open.

**What exists**
- **Portable layer:** `src/native/ZfsUserMode`
  - `zum_zpl.c`: the ZPL ported from the Linux ZPL, now case-insensitive and
    read-only aware;
  - `zum_api.c`: the file API, with Windows attributes as in
    `zfs_getwinflags`;
  - `zum.h` and `zum_impl.h`;
  - `zum_pool_um.c`: user-mode pool lifetime, with no pool cache and
    `cachefile=none`.
- **Front end:** `src/native/ZfsWinFsp/zum_winfsp.c`, built as
  `zfswin-um.exe`. It runs in console or service mode, with an SRW lock for
  mutations and a version resource.
- **The step-10 tool** `zfs-write-core.exe` is now a thin CLI over the same
  layer.
- **Installer:** `installer/ZfsWin.UserMode` produces
  `OpenZFS-for-Windows-<v>-x64-usermode.msi`.
- **Tests:** `scripts/vm/Test-UserModeInVm.ps1`, `scripts/vm/guest-bench.ps1`,
  and a `bench` phase in `Test-DriverInVm.ps1`.
- **WinFsp:** 2.1.25156, pinned and verified by `scripts/fetch-winfsp.ps1`.
  Its FLOSS exception covers MIT and CDDL; the notice and link are in the
  MSI, the version resource and the documentation.

**Regression of the shared layer, against Linux OpenZFS 2.4.1 (192.168.195.57)**
- `scripts/test-write-core.py`: **PASS**, 73 Windows import/export cycles,
  189 s. Every entry matches the model, the scrub is clean, `zdb` finds no
  leaks and the unlinked set is empty.
- `scripts/test-write-recovery.py`: **PASS**. Power-loss, win-recovery, kill,
  io-errors, enospc, mirror (three cases) and labels (two cases), 896 s.

**`Test-UserModeInVm.ps1`, on the release artifact
`OpenZFS-for-Windows-1.0.1-x64-usermode.msi`: PASS, 1179 s**

| Phase | Result | Time |
| --- | --- | --- |
| setup | `Confirm-SecureBootUEFI` = True and test signing off. Our MSI without WinFsp: 1603 with `REFUSED: WinFsp is not installed…`, and no service left. WinFsp 2.1 installs, its driver signed by "Microsoft Windows Hardware Compatibility Publisher". Our MSI then installs; `ZfsWinUserMode` is automatic, LocalSystem. Our own binary's signature is *not* trusted on that machine (`UnknownError`), and nothing needs it to be. | 803 s (mostly copying the 5 GiB fixture) |
| read | The genuine 5 GiB single-disk fixture, read-only: 18 files and 33 entries exact, 8.89 GB read with samples, parallel reads and repeated opens. The image is byte-identical, and the read-only pool exports (`export: 0`). | 76 s |
| write | lz4 fixture, read-write: all fixture files verify; create, append, rename, delete, nested directories and read-back; exported on service stop | 93 s |
| bench | see below | 38 s |
| powercut | Hyper-V TurnOff while writers ran. **426/426** files acknowledged after `FlushFileBuffers` and **430/430** acknowledged after write-through were exact after the restart. None missing, zero-filled or garbage; the remount took 0.4 s. | 94 s |
| uninstall | With a dataset mounted: exit 0, service and files removed, volume unmounted, image and WinFsp kept | 74 s |

**Upstream validation of what the user-mode variant wrote**
- `validate-written.py` with `ZFS_WRITTEN_DIR=TestVolumes/vm/written-usermode`:
  **PASS**. Fixture and Windows-written files match, the scrub is clean, no
  leaks, and the pool imports without `-f`.
- The image left after uninstall imports on Linux without `-f`. The file
  written before uninstall is intact and the scrub is clean.

**Kernel driver vs user mode, same workload**

Both are Debug builds, on the same VM and the same lz4 fixture copy:
`guest-bench.ps1` with 256 MiB of random data and 2000 × 4 KiB files. The
kernel run had Driver Verifier off. With Verifier on, the kernel numbers
were 1.5–11 × worse.

| Metric | Kernel driver | User mode (WinFsp) |
| --- | --- | --- |
| Sequential write + flush | 34.9 MB/s | 36.7 MB/s |
| Sequential read (cached) | 2064 MB/s | 905 MB/s |
| Sequential read (unbuffered) | 367 MB/s | 305 MB/s |
| Create 2000 files | 3.56 s (563/s) | 2.55 s (785/s) |
| Enumerate 2000 | 0.035 s | 0.034 s |
| Read 2000 files | 19.2 s | 16.8 s |
| Rename 2000 | 5.02 s | 2.31 s |
| Delete 2000 | 1.89 s | 1.40 s |
| Mount (import), unmount (export) | 4.1 s, 4.3 s | 0.4–0.8 s, 1.0 s |

Reading the table:
- Large writes are disk-bound and equal.
- The kernel driver is faster wherever the Windows cache or the per-request
  path dominates: cached and unbuffered reads.
- User mode is faster here on metadata operations. This is a single-run
  measurement on Debug builds. The kernel driver logs heavily in Debug and
  goes through the full I/O-manager/ZPL path. The user-mode layer skips
  per-operation ZIL and ACL work: it has no ACLs yet, and its writes become
  durable at Flush, write-through or txg sync. Release-build numbers are a
  step-16 task.
- The read of 2000 small files is slow on both: per-file open and close
  through PowerShell dominates it.

**Remaining**
- Windows ACLs (`windows:security.NTACL`), ADS and `.zfs\snapshot`.
- Pool, dataset and snapshot creation; `zfswin`/PowerShell administration
  through a user-mode broker backend.
- Interchange: a pool written by user mode imported by the kernel variant
  (Linux import is verified).
- Robustness with robocopy, Explorer, `fsutil` and `icacls`, plus a
  concurrent stress run.
- Engine unit tests without WinFsp.
- A WiX Burn bundle that chains WinFsp. Redistribution is permitted by the
  FLOSS exception.
- Release builds and ARM64.

## Safety

- WinFsp is installed only in disposable VMs, never on the development host.
- The service opens only devices and image files it is explicitly given. It
  never scans or auto-imports disks it was not told about.
- `OpenZFS/` and `third_party/openzfs-windows` are not modified outside the
  numbered patch series.

Commit: `usermode: add WinFsp user-mode filesystem and installer`
