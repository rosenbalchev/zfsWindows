# Step 19 — Release builds: verification and fixes

Status: `in progress`

Prerequisites:
- Step 17: the 1.0 distribution (`downloads/1.0/release` and `debug`).
- Step 18: the user-mode feature parity, verified on Debug builds.

Objective: the Release (optimized) builds pass the same VM suites as the
Debug builds. Release 1.0.8 was published in `f0aa60c` without having been
through the suites, and the README said so. This log records every run,
every failure and every fix, with evidence.

## Environment

| Item | Details |
| --- | --- |
| Host | x64 laptop, 12 logical CPUs, 64 GB |
| Toolchain | Visual Studio 18 (MSVC 14.51.36231, updated 2026-11-06 just before these builds); clang-cl from `C:\Program Files\LLVM` |
| Driver test VM | `zfsTest` (x64 Windows 11 Pro 26100, Secure Boot on, test signing on); PowerShell Direct only; checkpoints `clean`, `fixtures`, `usermode` |
| Linux reference | `UbuntuZFSTest` (`ubuntudev01`), OpenZFS 2.4.1-1ubuntu5.1, 12 CPUs from 2026-11-06 evening |
| ARM64 VM | `ZFSTestOnArm` on the user's ARM PC (`IvaNotebook`, Windows 11 IoT Enterprise LTSC ARM64, 12 logical CPUs, 15.6 GB). Reached over SSH (key `~/.ssh/zfswindows-arm_ed25519`, host-key alias `zfsarm-host`, fingerprint `SHA256:wG3UvUhaKehKIkUbpkWNZd0G/TnBXZWj2voG0qA+9iw`), then PowerShell Direct from that host |

The ARM64 VM is a fresh Windows 11 Enterprise ARM64 26200 install with
8 GB and Secure Boot on. Checkpoint `fresh-install` was taken before any
change.

## Run log

### 1. Release 1.0.8 user mode: engine assertion (fixed by patch 0026)

The `Test-UserModeInVm.ps1 -Configuration Release` run on 1.0.8 had these
results:

| Phase | Result |
| --- | --- |
| Setup (1,100 s) | Passed |
| Read (46 s) | Passed |
| Write | Passed |
| Bench | Passed |
| Power cut (419/419, 423/423) | Passed |
| Admin CLI stage | 12 failures |

The admin failures were:
- snapshot create exit 1;
- an empty clone;
- wrong snapshot counts;
- 0 files after remount;
- attach, detach and replace exit 3.

Reproducing in console mode in the VM, the engine died on the first writes
into a zstd dataset:

```
ASSERT at .../module/zfs/abd.c:121:abd_verify()
VERIFY3U(abd->abd_flags, ==, abd->abd_flags & (ABD_FLAG_LINEAR | ...)) failed
(18446744071796806544 == 912)
```

**Cause:** libzpool always builds with `ZFS_DEBUG`. The top-level
`CMakeLists.txt` defines it for the other userland libraries only in Debug.
`ZFS_DEBUG` changes `abd_t` (it adds `abd_parent` and `abd_children`) and
`zfs_refcount_t`. In Release, libzstd's compressors and libzpool therefore
disagreed about the `abd_t` layout.

**Fix:** patch `0026-cmake-zfs-debug-for-all-userland.patch`. `lib/` and
`cmd/` define `ZFS_DEBUG` in every configuration. The kernel `module/` keeps
upstream's per-configuration setting.

**Check:** the fixed Release `zfswin-um.exe` was run in console mode in the
VM through the following sequence:

| Step | Result |
| --- | --- |
| Mirror pool with lz4; zstd dataset | Created |
| 12 × 1 MiB files | Written |
| Snapshot | Taken |
| gzip-9, zstd-19, zle and lzjb datasets | Created |
| Unmount and remount | Files identical (SHA-256) |
| `zpool scrub -w` | `pool 'rtest' is healthy` |

The engine stayed alive throughout.

### 2. Release 1.0.9 user mode: full suite passed

`Test-UserModeInVm.ps1 -Configuration Release` on 1.0.9 passed every phase:
setup, read, write, bench, power cut, admin and uninstall.

| Phase | Evidence |
| --- | --- |
| Setup | 1,261 s; GUI reports broker 1.0.9 |
| Read | 18 files, 8,894,469,635 bytes verified in 56 s |
| Write | Passed |
| Bench | seq write 61.9 MB/s, cached read 988 MB/s, unbuffered read 312.6 MB/s, 2,000 creates in 3.1 s |
| Power cut | Passed |
| Admin: CLI | 49/49 |
| Admin: ACLs, streams and `.zfs` | 33/33 |
| Admin: compression and dedup | 54/54 |
| Admin: cross-check of the kernel-written pool | 21/21 |
| Admin: restart | Passed |
| Admin: dumps | None |
| Uninstall | exit 0; image kept; volume gone; WinFsp kept |

**Linux** (`ubuntudev01`, OpenZFS 2.4.1), on the pool the Release user-mode
edition wrote:

```
PASS: 26 current files and 24 @s1 files verified by Linux OpenZFS; zfswin properties present; scrub clean; no leaks
PASS: 9 datasets (gzip-9, lz4, lzjb, off, off, zle, zstd, zstd, zstd-19) verified by Linux OpenZFS: settings, 25 files, dedup table; scrub clean; no leaks
```

### 3. Release 1.0.9 kernel: bugcheck on driver unload (fixed by patch 0027)

The kernel suite reached the untrusted-install preflight, which fails and
rolls back. The rollback unloaded the driver, and the VM bugchecked:

```
0x7E (0xffffffff80000003, 0xfffff8006f876fc3, ...)  — a breakpoint (int3) in OpenZFS.sys
```

The dump was analyzed inside the VM (`Analyze-GuestDump.ps1`, which now
takes `-Configuration Release`), and the offset was mapped with
`llvm-symbolizer` against the Release PDB. The breakpoint is in
`zvol_os_deregister_module()`, `zvol_os.c:367`.

**Cause:** the code tested `status` without ever setting it.
`ZwDeviceIoControlFile(OPENZVOL_DEREGISTER)`'s result was discarded. Reading
an uninitialized variable is undefined behaviour. In Release, clang treats
such a path as unreachable, and on Windows targets it compiles unreachable
code to `int3`.

**Fix:** patch `0027-kernel-uninitialized-values-trapping-in-release.patch`
corrects that, plus three similar cases that clang reported
(`-Wuninitialized`, `-Wsometimes-uninitialized`):

| Site | Defect |
| --- | --- |
| `zvol_os_deregister_module` | `status` tested unset; wrong `IO_STATUS_BLOCK` |
| `FileIdAllExtdBothDirectoryInformation` listing | next-entry offset written through the unset `fiebdi` (should be `fiaebdi`) |
| boot ereport | `ena` unset |
| `QueryDeviceRelations` | `goto out` from `TargetDeviceRelation` into the `BusRelations` block; frees the unset `StorportRelations` |

**Check:** in the next run (1.0.10), the untrusted preflight, rollback,
install and reboot all passed.

### 4. Release 1.0.10 kernel: general-protection fault in `zio_create` (fixed by patch 0028)

The VM bugchecked again, at the first pool creation after install (the
`installer-cli` stage). The first dump was lost: the user-mode suite that
followed restored a checkpoint. The rerun reproduced it:

```
0x7E (0xffffffffc0000005, ...)  KiGeneralProtectionFault  OpenZFS+0xa57cb
zio_create, zio.c:996:  zio->io_bp_orig = *bp;
1400a57cb: movaps %xmm0, 0x180(%rsi)     rsi = ffffad0310b31ab8
```

**Cause:** `movaps` requires 16-byte alignment, but the zio sits at an
address that is only 8-byte aligned. Clang's record layout
(`-Xclang -fdump-record-layouts`) shows why the compiler expected more:
`struct zio` has `[sizeof=1344, align=64]`. Upstream marks
`io_queue_node ____cacheline_aligned`, and the Windows kernel SPL defines
that as `aligned(64)` (`include/os/windows/spl/sys/isa_defs.h`). Its
`kmem_cache`, however, guarantees only `KMEM_ALIGN` = 8.
- **Linux:** never affected, because its kernel builds without SSE.
- **Debug (`/Od`):** copies without aligned SSE.
- **Upstream userland libspl:** defines the macro as empty.

**Fix:** patch `0028-kernel-no-over-aligned-pointer-assumptions.patch`.
- `____cacheline_aligned` is empty in the Windows kernel SPL, as in
  userland. This is needed for types that carry the attribute themselves
  (`spa_config_lock_t` inside a `kmem_zalloc`'ed `spa_t`,
  `aggsum_bucket_t`), which `-fmax-type-align` cannot cover.
- `use_clang()` adds `-Xclang -fmax-type-align=8` to `WDK_COMPILE_FLAGS`,
  which kernel targets take. The flag is not applied to directory compile
  options, which `wdk_add_library` ignores.
- `ninja -t commands` shows the flag on `module/zfs/zio.c` but not on the
  MSVC `driver.c` nor on the userland libzpool objects.

**Check:** the Release `OpenZFS.sys` was disassembled with `llvm-objdump`,
counting aligned SSE (`movaps`) through a pointer that is not `rsp`, `rbp`
or `rip`:

| Build | Sites |
| --- | --- |
| Before the fix | 99, in `zio_create` (13), `zio_write*`, `dsl_pool_open_impl`, `spa_add`/`spa_config_lock_init`, `aggsum_*`, `brt_alloc` and others |
| Compiler flag only | 20, in `spa_config_lock_init`, `aggsum_value` and `aggsum_compare`, which use explicitly aligned types |
| Both changes | 11, all in the CRT's `memmove`/`memset`, which check alignment first |

Compiling `zio.c` on its own, `zio_create` went from 9 heap `movaps` to 0
(and from 26 to 36 `movups`).

The remaining `movdqa` uses (1,078 in `vdev_raidz_math_impl.h`, 2 in
`zfs_fletcher.c`) are upstream's deliberate SIMD code:
- RAID-Z works on ABD buffers that really are aligned.
- `fletcher_4_ops_t` objects are `aligned(64)` statics.

### 5. Release 1.0.11 kernel: no crash; intermittent `zpool export` hang (under investigation)

The kernel suite on 1.0.11 got through these stages without any bugcheck
(no System event 1001):

| Stage | Result |
| --- | --- |
| Untrusted install refused | Passed |
| Rollback | Passed |
| Install | Passed |
| Reboot | Passed |
| CLI | Passed |
| GUI | Passed |
| Samples | Passed |
| PowerShell module | Passed |
| Standard user | Passed |
| Filesystem | Passed, apart from the two known upstream gaps (ACLs not enforced on open; a fresh snapshot not readable through `.zfs`), as with 1.0.7 Debug |
| Cross-check | Passed, apart from the same `.zfs` snapshot gap |
| Repair | Passed |
| Refused uninstall and upgrade | Passed |
| Forced-failure upgrade rollback | Passed |

The final upgrade to 1.0.65000 then failed with 1603. From the MSI log:

```
WixQuietExec:  setup check-pools: zpool export did not finish within 600 s.
CustomAction ZfsWinCheckPools returned actual error code 1603
```

The uninstall checks that depend on the upgrade failed as consequences: 10
failures in total.

**Reproduction in the VM** (1.0.11 installed, pool `zwin` on
`C:\zfswin\installer\images`):

| Sequence | Result |
| --- | --- |
| `zpool import`, then `zpool export` | 3 s import, 9 s export |
| Reboot, import, then `ZfsWinBroker --setup check-pools --export` (the installer's own call) | Exported in 77 s |
| `zfswin pool import`/`export` in a loop | Run 1: 3 s / 19 s; run 2: 7 s import, export **still running after 300 s** |

During the hang, a live kernel dump (`Get-StorageDiagnosticInfo
-IncludeLiveDump`, 1,273 MB) shows the stuck `zpool.exe` thread:

```
spl_cv_wait < dmu_tx_wait (dmu_tx.c:1358) < zfs_rmdir < delete_entry
  < zfs_fileobject_cleanup < fsDispatcher < IRP_MJ_CLEANUP ... NtClose
  < zfs_windows_unmount_impl (zfs_vnops_windows_mount.c:2503) < zfsdev_ioctl
```

`zfs_windows_unmount_impl` removes the reparse-point directory of a child
mount (for example `D:\docs`) in the parent dataset with delete-on-close.
That rmdir waits in `dmu_tx_wait` on

```c
while (dp->dp_dirty_total >= zfs_dirty_data_max)
        cv_wait(&dp->dp_spaceavail_cv, &dp->dp_lock);
```

Meanwhile both txg threads idle in `txg_thread_wait`. Of the 184 driver
threads, 164 are idle taskq workers. Nothing syncs, so nothing will ever
signal the wait.

The kernel debugger in the VM did not load the Release symbols. The values
were read as raw memory, with addresses computed from the Release PDB
(`llvm-pdbutil dump -publics`, section `.data` at RVA `0x3F0000`) and
member offsets from clang's record layouts for this build
(`dsl_pool_t`: `dp_lock` +432, `dp_spaceavail_cv` +480, `dp_dirty_total`
+544).

| Value | In the dump | Meaning |
| --- | --- | --- |
| `zfs_dirty_data_max` | `0x33318000` (819 MB) | Correct: 10% of 8 GB |
| `zfs_dirty_data_max_max` | `0x7ffbc000` | |
| `physmem` | `0x1ffef0` pages (8 GB) | |
| Waiting frame `rdi` / `rsi` | `…21b0` / `…21e0` | 48 bytes apart = `dp_lock` / `dp_spaceavail_cv`, so `dp` = `ffffc106d96d2000` (in `dnode_t`, `dn_mtx` and `dn_notxholds` are 240 bytes apart) |
| `dp->dp_spa` → `spa_name` | `"zwin"` | The right pool |
| `dp_dirty_total`, `dp_dirty_pertxg[0..3]`, `dp_long_free_dirty_pertxg[0..3]` | all 0 | |
| `dp_spaceavail_cv` | 1 waiter, the stuck thread (`Flink`/`Blink` → its stack entry), `cv_waiters_count` 1 | |
| Waiter state | event not signaled, `taken` = 0 | |
| `dmu_tx_dirty_over_max` (incremented each time the loop finds dirty ≥ max) | 5 | |

The Release code of the loop (`llvm-objdump`, `dmu_tx_wait`) does what
the source says:
1. `mutex_enter(&dp->dp_lock)`;
2. `cmp 0x220(dp)` against `zfs_dirty_data_max`, `lock inc` of the
   over-max statistic;
3. a loop around `spl_cv_wait` that re-reads `0x220(dp)`.

The thread therefore went to sleep after really reading a dirty total of
at least 819 MB on a small test pool, and it has not been woken although
the total is now 0. `dsl_pool_dirty_delta` wakes waiters with
`cv_signal` (one waiter) and relies on each woken thread dirtying again to
wake the next one. On an idle pool during export, no further dirtying
happens.

**Open questions:**
1. Why did the total read at least 819 MB? Candidates:
   - a transient underflow in `dp_dirty_total` (its
     `ASSERT3U(-delta, <=, dp_dirty_total)` is compiled out in Release);
   - real accounting of large frees.
2. Is the hang Release-only? The 1.0.7 Debug suite exported fine, but the
   hang is intermittent, so the Debug build needs the same import/export
   loop.

`zfs_dirty_data_max` is only set once, at startup (`sysctl_os_fix`,
`arc_init`), so the limit was not transiently small.

**Release only.** `scripts\vm\Repro-ExportHang.ps1` creates pool `zrep`
(mirror of two 1 GiB images, child datasets `docs` and `fs`) on a fresh
`fixtures` guest. It then runs 12 rounds of import and export. Results on
1.0.11:

| Build | Export times (s) |
| --- | --- |
| Debug | 6.4, 15.5, 15.3, 15.5, 15.1, 7.4, 7.4, 13.1, 3.2, 12.9, 2.1, 11.8 (max 15.5) |
| Release | 16.7, 11.3, 2.9, 2.3, **49.2**, 13.3, 17.9, 7.0, 17.1, **205.4**, **115.0**, 13.0 |

Here the stalled waiter was eventually woken by a later dirty change; in
the suite it was not (600 s timeout).

**Not an underflow.** `dp_dirty_total` and `dp_dirty_pertxg[]` are only
changed together, under `dp_lock`, in `dsl_pool_dirty_space` and
`dsl_pool_undirty_space`, and the undirty path clamps to the per-txg
value. The total therefore always equals the sum of the per-txg counters
and cannot wrap.

So in Release some path really accounts at least 819 MB of dirty space on
an idle pool during unmount and export. That points to a garbage `space`
argument. The txg sync later undirties the whole per-txg amount, and
`cv_signal` wakes only one of the waiters.

**Diagnostic builds** (local only, version 1.0.64999, reverted
afterwards):

| Build | Change | Result |
| --- | --- | --- |
| v1 | Traced `dsl_pool_dirty_space` bookings over 16 MB | The trace never fired, so it says nothing (the dump came after the stall had ended) |
| v2 | Used `RtlCaptureStackBackTrace` | Bugchecked the VM at boot (it is not a kernel-mode API); the VM was restored to `fixtures` |
| v3 | Used `RtlWalkFrameChain`; traced bookings of 1 MB or more, plus every entry into the dirty wait in `dmu_tx_wait` (total, max, `dp_dirty_pertxg[]`, call chain) | Caught a stall in round 3 |

The v3 trace showed eight waits. In all of them **`zfs_dirty_data_max`
was 0**, with dirty totals of only 671,744–852,480 bytes, and no single
booking of 1 MB or more. The waiters were:
- `zfs_rmdir` from `zfs_fileobject_cleanup` (the export hang);
- `zfs_mkdir` from `zfs_vnop_lookup`;
- `zfs_sa_set_xattr` from `zfs_save_ntsecurity` / `zfs_attach_security`;
- `zfs_trunc` from `set_file_endoffile_information`;
- `dmu_objset_space_upgrade` from the objset upgrade task.

**Root cause (Windows port, `module/os/windows/zfs/sysctl_os.c`):**
1. At first load, `sysctl_os_process` writes each tunable that is not
   yet in the Registry. `zfs_dirty_data_max`, `zfs_dirty_data_max_max`
   and `zfs_wrlog_data_max` are still 0 at that moment, because
   `sysctl_os_fix` computes them afterwards. The VM's Registry indeed
   holds 0 for all three (and for `zfs_arc_max`).
2. On every Registry change under the driver's key,
   `sysctl_os_registry_change` reapplies all tunables
   (`sysctl_os_all`). That sets those three to 0 until `sysctl_os_fix`
   runs at the end of the pass.
3. Each pass writes `zfs_version` (`ZT_FLAG_WRITEONLY`) and every callout
   tunable back with `ZwSetValueKey`, even when nothing changed. That
   fires the change notification again, so the passes run continuously
   and the zero window is open again and again.
4. A transaction that checks `dp_dirty_total >= zfs_dirty_data_max`
   inside that window sleeps on `dp_spaceavail_cv`, and only a later
   dirty change wakes it, via `cv_signal`, one waiter at a time. During
   an export nothing else dirties, so the export hangs.

Release code is faster, so it runs many more passes per second and hits
the window far more often. Debug did not stall in 12 rounds, but the bug
is the same there.

**Fix:** patch `0029-sysctl-registry-no-self-trigger-no-zero-dirty-max.patch`.
- `sysctl_os_write_registry` writes only when the type or data differ
  (`ZwQueryValueKey` + `RtlCompareMemory`, after string conversion). This
  ends the self-triggered loop.
- For the three tunables that `sysctl_os_fix` derives, a Registry value
  of 0 means "computed" and is never applied, so the variable never
  passes through 0.

All 29 patches apply to `e7c0984`.

**Check:** `Repro-ExportHang.ps1 -Configuration Release`, with the Release
driver plus patch 0029 (local build 1.0.64999), 20 rounds:

```
exports (s): 15.5 15.1 15.3 15.1 2.2 11.6 11.6 2.0 1.7 1.7 11.8 2.7 2.0 2.0 1.9 12.4 11.8 2.1 12.2 1.7 (max 15.5)
```

That is the same range as Debug. Without the fix, the same Release build
had stalled in round 3, and earlier at 49, 115 and 205 s.

### 6. Release 1.0.12 (patches 0026–0029): full verification, x64

Built 2026-11-06 21:14 UTC by `build-release.ps1 -BuildDriver` (exit 0),
together with the 1.0.65000 upgrade packages (Release and Debug, exit 0).
The two suites then ran one after the other on `zfsTest` (6 CPUs).

**Kernel suite** (`Test-InstallerInVm.ps1 -Configuration Release`, 1,335 s):

| Phase | Result |
| --- | --- |
| Untrusted certificate | Refused (1603) with the expected message |
| Forced rollback of a fresh install | 1603; state clean afterwards; **no bugcheck** |
| Install | Exit 0; reboot; services as expected |
| CLI | 49/49 |
| PowerShell | 19/19 |
| GUI, samples, standard user | Pass |
| Filesystem | 28/33. Only the known upstream gaps: `user-cannot-write-read-only-grant` and `user-denied-by-deny-ace` (ACLs not enforced on open); `snapshot-old-content`, `snapshot-acl` and `snapshot-stream` (a fresh snapshot is not readable through `.zfs`) |
| Cross-check: user-mode pool read by the kernel driver | 20/21. Only `snapshot-f1`, the same `.zfs` gap |
| Repair | Exit 0 |
| Uninstall/upgrade with a pool imported | Refused, as designed |
| Forced upgrade rollback | 1603; rolled back and rebooted cleanly |
| **Upgrade to 1.0.65000 with `ZFSWIN_EXPORT_POOLS=1`** | **Exit 0.** "Imported pools: zwin." → "Exported zwin." → preflight passed. The whole MSI took 24 s (21:34:46 → 21:35:10). With 1.0.11 this step hit the 600 s timeout. |
| Uninstall | 3010 (reboot needed); state clean afterwards |
| Pool images | The SHA-256 of all six images is unchanged before and after |

The script's exit code is 1 because it counts the two known-gap groups
above as failures. They are the same list as with 1.0.7 Debug and the
ARM64 1.0.11 run.

**User-mode suite** (`Test-UserModeInVm.ps1 -Configuration Release`,
1,888 s): "User-mode VM test passed: setup, read, write, bench, powercut,
admin, uninstall".

| Phase | Result |
| --- | --- |
| Setup | 1,120 s; install exit 0; Secure Boot on; WinFsp driver Microsoft-signed |
| Read | 18 files, 8.89 GB in 48.4 s |
| Write | 4 files; all hashes as expected; no failures |
| Bench | Sequential write 56.1 MB/s, cached read 1,196.3 MB/s, unbuffered read 292.2 MB/s; 619 small files created per second |
| Power cut | 406 acknowledged, 407 present, all exact; 0 zero-filled, 0 missing, 0 garbage |
| Admin | CLI 49/49, PowerShell 19/19, ACLs, streams and `.zfs` 33/33, compression and dedup 54/54, cross-check of the kernel-written pool 21/21; restart passed; no dumps |
| Uninstall | Exit 0; image kept; volume gone; WinFsp kept |

**Linux** (`ubuntudev01` at 192.168.195.63, OpenZFS 2.4.1):

```
kernel-written pool (TestVolumes\vm\installer):
PASS: 26 current files and 24 @s1 files verified by Linux OpenZFS; zfswin properties present; scrub clean; no leaks
user-mode-written pool (TestVolumes\vm\crossedition-usermode):
PASS: 26 current files and 24 @s1 files verified by Linux OpenZFS; zfswin properties present; scrub clean; no leaks
PASS: 9 datasets (gzip-9, lz4, lzjb, off, off, zle, zstd, zstd, zstd-19) verified by Linux OpenZFS: settings, 25 files, dedup table; scrub clean; no leaks
```

1.0.12 x64 Release is the first Release build that passed every suite. It
becomes the latest stable.

### zfsTest: 6 → 8 CPUs (2026-11-06, after the 1.0.12 suites)

The host has 12 logical processors. With the VM off, each of `clean`,
`fixtures` and `usermode` (all taken with the VM off, so no saved state)
was restored, set to 8 CPUs, renamed to `<name>-6cpu` and taken again
under its own name. Nothing was deleted. The scripts look checkpoints up
by exact name, so they now use the 8-CPU ones. A boot from the new
`usermode` checkpoint reported 8 logical processors in the guest.

## Releases built in this step

| Version | Change | Status |
| --- | --- | --- |
| 1.0.8 | First Release+Debug publication (`f0aa60c`) | Release user mode and kernel broken (see 1, 3, 4); Debug equal to 1.0.7 |
| 1.0.9 | Patch 0026 | Release user mode fully verified; kernel crashes on unload |
| 1.0.10 | Patch 0027 | Kernel: GP fault in `zio_create` |
| 1.0.11 | Patch 0028 | Kernel: no crash; export hang (see 5) |
| 1.0.12 | Patch 0029 | **x64: every suite and the Linux validation passed (see 6). Latest stable.** |

All 29 patches apply to pristine upstream `e7c0984` (checked after each
change with a fresh worktree).

## ARM64 (1.0.11 Release, `ZFSTestOnArm`)

A separate agent ran these tests, driven over SSH by the helper scripts in
`scripts\vm\arm\`.
- **Checkpoints created:** `usermode-installed`, `usermode-tested`,
  `testsigning-ready`. The user's `fresh-install` was left untouched.
- **ARM64 .NET Desktop Runtime:**
  `out\dotnet-runtime\windowsdesktop-runtime-10.0.14-win-arm64.exe`
  (Microsoft-signed).

**User-mode edition, Secure Boot on**

| Check | Result |
| --- | --- |
| Install | Refused without WinFsp (1603, correct message, nothing left). With WinFsp, the MSI and the runtime all exit 0. `zfswin-um.exe` is native ARM64 (`0xAA64`); the service is Auto, LocalSystem, Running. GUI self-check passed. |
| CLI | 49/49 |
| PowerShell | 19/19 |
| ACLs, streams and `.zfs` | 33/33 |
| Compression and dedup | 54/54, with the same ratios as x64: lz4 2.65, gzip-9 2.81, zstd and zstd-19 2.98, lzjb 2.80, zle 1.01, off 1.00; dedup 1 → 4.00 → 1.01 |
| Standard user | Can read; gets exit 4 / `PermissionDenied` when mutating |
| Service restart | Pool and all 12 mounts back; stream content intact |
| Cross-check: pool written by the x64 kernel driver | 21/21 |
| Export | Exit 0 in 1.9 s; no dumps |

**Kernel edition, Secure Boot off in this VM only, test signing on**

| Check | Result |
| --- | --- |
| Untrusted certificate | Refused (1603); nothing left |
| Forced rollback | 1603; no driver packages left; **no bugcheck on unload** (patch 0027 holds on ARM64) |
| Install | Exit 0. `OpenZFS.sys` is ARM64 and running; signatures valid; protocol 1 |
| CLI, PowerShell, GUI, standard user | 49/49, 19/19, pass, pass |
| Filesystem | 28/33. The failures match x64 exactly: the 2 ACL-enforcement gaps plus `snapshot-old-content`, `snapshot-acl`, `snapshot-stream` |
| Cross-check: ARM64 user-mode pool | 19/21. `snapshot-f1` is the known gap. `zpool.exe` crashed with 0xC0000409 during import, but the pool imported (dump `C:\zfswin\dumps\zpool.exe.4184.dmp` in the VM) |
| Bugchecks | None |
| Import/export loop | Exports took 14–24 s; cycle 5 **hung** (more than 300 s; live dump in the VM, 3.2 GB). The hang is not specific to x64. |

**Not done on ARM64:**
- **Linux validation of the ARM-written pool:** downloads from the ARM PC
  run at about 15 KB/s. The `scp` of the 118.6 MB archive failed at 21:45
  after 13.6 MB (`Connection closed`), so the partial
  `TestVolumes\vm\arm-usermode\arm-usermode-pool.zip` is unusable. It
  needs a resumable transfer or a manual copy (USB). The expected SHA-256
  values are:
  - a.img `575263DC3656FAF2878221DC1F7FE737253176CC34E4C850FD6D01949898880A`
  - d.img `B81013D9BD115B620C20EBE242E72F0259E99EB3155F25C17DC179DCDDBED936`
- **The read and power-cut phases** with the 5 GB fixture.
- **Repair, upgrade and uninstall:** blocked by the hung export, and no
  ARM64 upgrade package exists.
- **The kernel import/export loop with 1.0.12 (patch 0029):** prepared.
  `host-kernel-suite.ps1` now takes `$Version` and copies that MSI, the
  certificate and `guest-installer.ps1` from the stage folder into the
  guest after restoring `testsigning-ready`. At 2026-11-06 ~23:30 the ARM PC
  did not answer (`ssh: connect to host 192.168.195.59 port 22: Connection
  timed out`), so the run has not happened yet.

## Sporadic `zpool.exe` crashes (Release userland, open)

| Platform | Crash |
| --- | --- |
| x64 | In the first diagnostic run (driver 1.0.64999), `zpool create` exited with 0xC0000005. Windows Error Reporting: fault offset 0, faulting module unknown, i.e. a call through a NULL function pointer. The identical rerun created the pool normally. |
| ARM64 (1.0.11) | `zpool import` crashed with 0xC0000409 (fail-fast), although the import completed. |

Both crashes are intermittent, in the Release userland, and still to be
investigated.

**Crash hunt with 1.0.12 Release (2026-11-07, zfsTest at 8 CPUs).**
`Repro-ExportHang.ps1` now:
- sets WER LocalDumps (full dumps) for `zpool`, `zfs`, `zfswin` and
  `ZfsWinBroker`;
- flags NTSTATUS exit codes as crashes;
- copies any dumps to `out\vm-dumps\<time>`.

A run of 40 rounds (`-Iterations 40`) did `zpool create`, 2 × `zfs create`
and 41 imports and exports:
- no crash, no dump, no hang;
- imports took 2.3–5.3 s, exports 2.0–15.5 s.

The crash did not reproduce in that many runs, so it is still open. The
next chance of a dump is the ARM64 rerun, which keeps the same LocalDumps
settings. The loop still writes 0 files per round, so it exercises only
import, mount, unmount and export.
