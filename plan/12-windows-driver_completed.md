# Step 12 — Windows kernel filesystem driver

Status: `complete`

Objective: expose the validated core through a real WDK filesystem driver.

Implementation: kernel adapters, volume discovery, mount/dismount, IRP
dispatch, enumeration, cached reads, synchronization, metadata operations,
and eventually writes. Test only in a dedicated Windows 11 VM using Driver
Verifier and WinDbg. Never load the experimental driver automatically on the
developer host.

Exit evidence: standard .NET file APIs read validated fixture data through the
driver, and upstream tools independently validate the pool.

Commit: `driver: add isolated-VM filesystem driver milestone`


## Checkpoint: driver build, package and isolated-VM harness (not yet run)

- **Driver.** The driver is the pinned OpenZFS-on-Windows kernel driver
  (`OpenZFS.sys`, with `OpenZVOL.sys`, which `zfsinstaller` requires). It is
  built from the same submodule and patches as the step 10/11 core.
  `scripts/build-driver.ps1` configures a separate tree the way upstream CI
  does: Ninja with MSVC as the default compiler, because `driver.c` refuses
  clang; upstream's `use_clang()` still builds the userland. It packages the
  driver, `zfsinstaller`, `zpool`, `zfs`, `zdb` and `kstat` into
  `out\driver-package`. The package is test-signed with a self-signed
  certificate from the current user's store; the certificate is trusted
  only inside the VM. The build and signing succeed. Nothing is installed
  or loaded on the host.
- **VM.** `scripts/vm/New-DriverTestVm.ps1` (elevated, run once by the
  user) creates the VM:
  - Windows 11 Pro is applied straight from the ISO, with an unattended
    local admin account and PowerShell remoting;
  - an internal switch puts the host at 10.77.0.1 and the VM at 10.77.0.2,
    with no LAN access;
  - test signing and KDNET debugging to the host are set in the VM's BCD,
    and Secure Boot is off;
  - a `clean` checkpoint is taken.

  This session is not elevated and cannot manage Hyper-V, so the user has
  to run it.
- **Tests.** `scripts/vm/Test-DriverInVm.ps1` runs unelevated over
  remoting:
  - `install`: trusts the certificate in the guest, runs
    `zfsinstaller install`, enables `verifier /standard` for `OpenZFS.sys`,
    and reboots.
  - `read`: imports the genuine 5 GiB fixture read-only and runs
    `tools/Zfs.MountVerifier`, which uses standard .NET file APIs to check
    enumeration, full SHA-256 values, sampled ranges, parallel reads and
    repeated opens. It then proves the image hash is unchanged.
  - `write`: writes, appends, renames and deletes on a Linux-made fixture
    through the driver.

  `scripts/vm/validate-written.py` then has Linux OpenZFS validate the
  written pool: fixture and written hashes, scrub and `zdb -bcc`.

## Checkpoint: driver reads and writes genuine pools in the VM (Linux validation pending)

The VM (`scripts/vm/New-DriverTestVm.ps1`, `Complete-DriverTestVm.ps1`) runs
Windows 11 Pro 26100 with test signing, KDNET configuration and Secure Boot
off, on internal switch 10.77.0.0/24. `Test-DriverInVm.ps1` uses these
phases:

- `prepare` takes the checkpoint `fixtures`, with the fixtures already
  copied in.
- `install` always reverts to `fixtures`, installs the driver with
  `zfsinstaller`, enables `verifier /standard` for `OpenZFS.sys`, and
  asserts that the service is RUNNING after the reboot.
- `read` and `write` follow.

Driving the driver with genuine Linux-created pools under Driver Verifier
found six upstream-candidate bugs. They are fixed as patches 0004–0009 and
documented in `patches/openzfs-windows/README.md`:

| Patch | Symptom | Cause |
|---|---|---|
| 0004 | Linux pool mounted at `\??\-:\var\tmp\...` | parent with no drive letter used the placeholder `-`, and parent directories were never created |
| 0005 | bugcheck 0x00FFFFFF in `zilog_dirty()` | directory listing on a read-only pool saved an NT security descriptor (`zfs_is_readonly()` ignored `spa_writeable()`) |
| 0006 | bugcheck "mutex not initialised" at export | `mmp_signal_all_threads()` raced pool export (any registry change re-applies tunables on Windows) |
| 0007 | read-only pool: export "pool is busy" forever | `zfs_inactive()` atime transaction dirtied the dataset; read-only pools never sync |
| 0008 | export hung in `spa_evicting_os_wait()` after writes | single-round unmount left Cache-Manager-held znodes owning SA/dnode holds |
| 0009 | bugcheck `rrw_enter_read_impl` VERIFY at unmount | `zil_close()` → `zfs_get_data()` → `zfs_zget()` → security attach re-entered the teardown lock |

Debugging method: guest crash dumps were written by bugchecks or by
`Debug-VM -InjectNonMaskableInterrupt` (with `NMICrashDump=1`) and analyzed
offline with `kd -z` and `scripts/vm/spa-refs.js`. That script walks the
pool list, `spa_refcount` holders (with `reference_tracking_enable=1`), any
`zfs_refcount_t`, and objsets on `spa_evicting_os_list` with their remaining
dnodes. A live KDNET session from the user's host coincided with a host
crash and is no longer used.

Results:

- **Read** (2026-10-23): the genuine 5 GiB single-disk fixture was imported
  read-only. `Zfs.MountVerifier`, using standard .NET file APIs, checked 18
  files and 33 entries, 8.9 GB in 47.3 s: full SHA-256, sampled ranges,
  parallel reads and repeated opens. There were 0 failures, export
  succeeded on the first attempt, the image SHA-256 was unchanged, and there
  were 0 bugchecks.
- **Write** (2026-10-26): the Linux-created lz4 fixture was imported
  read-write. 4 files (64 MiB) verified with 0 failures. Through the driver
  the verifier wrote a 3 MiB file, appended twice, renamed one file,
  deleted another and created nested directories; the read-back hashes
  match. Export succeeded on the first attempt, and the image is in
  `TestVolumes/vm/written/`.
- **Upstream validation** (2026-10-26):
  `python scripts/vm/validate-written.py 192.168.195.51` on Ubuntu
  `zfs-2.4.1-1ubuntu5.1`. Linux imported the driver-written pool cleanly.
  The 4 fixture files and the 4 driver-written files matched by SHA-256,
  and the deleted and renamed-away files were absent. Scrub repaired 0 B
  with 0 errors, and `zdb -bcc` reported no leaks.

There were two unexplained host incidents during VM work: a bugcheck 0x10E
and a later hang with no dump. They are recorded so that host stability is
watched in later runs.

## Completion

The exit evidence is met:

1. Standard .NET file APIs (`tools/Zfs.MountVerifier`) read validated
   fixture data through the kernel driver: the genuine 5 GiB single-disk
   fixture, with exact hashes and an unchanged image.
2. Upstream Linux OpenZFS independently validates the pool written through
   the driver: hashes, scrub and `zdb -bcc`.

All runs were in the isolated VM under Driver Verifier (standard flags).
The driver was never loaded on the host.

Known limitations, not claimed as working:

- Only the scenarios above are exercised. Out of scope: snapshots or
  `.zfs`, xattr/ADS, ACL editing, large-scale enumeration, memory-mapped
  writes, concurrent writers, and Driver Verifier's additional flags (low
  resources, pending I/O, IRP logging).
- `zpool.exe import` intermittently exits with an access violation
  (`0xC0000005`) after the pool has been imported. This is a userland issue
  and has not been investigated.
- After heavy reads Windows can briefly hold file objects, so the harness
  retries `zpool export` and records every attempt; every recorded export
  succeeded on the first attempt.
- Two host incidents happened during VM work: a bugcheck 0x10E in the
  graphics memory manager, and a later hang without a dump. The host dumps
  need elevation to analyze. The cause is undetermined and they may be
  unrelated.

## Follow-up: previously uncovered cases (2026-10-26/27)

At the user's request, the limitations above were tested. `Test-DriverInVm.ps1 -Phase extended` runs `scripts/vm/guest-extended.ps1`, and `scripts/vm/validate-extended.py` then validates the result on Linux.

**Workload on the Linux-created `zc_lz4` fixture:**

- **Alternate data streams:** a 100 B stream and a 300 KB stream, listed with `FindFirstStreamW`.
- **DACL edits:** BUILTIN\Users is granted Modify on a file and on a directory.
- **Large directories:**
  - 20,000 files in one directory, then 5,000 deleted and 1,000 renamed;
  - 2,000 subdirectories.
- **Concurrent writes:** 8 threads with 4 files each, plus 8 threads writing disjoint ranges of one shared file.
- **Memory-mapped writes:** create, map, write and flush; then a second mapping patches the file mid-way.
- **Dedup and compression:** `zfs create` of `dedup=on`, `compression=zstd` and `compression=gzip-6` children, filled with duplicate or compressible data.
- **Snapshots:**
  - `@win1`, then mutate (overwrite, delete, add, rename, rmdir), then read `.zfs\snapshot\win1`;
  - `@win2`, then mutate, then `zfs rollback` to `@win2`.
- **Scrub, export and re-import:** everything is verified again after re-import.

**Workload on a mirror the driver creates (`zpool create … mirror`) from two file vdevs:**

1. Fill it with 120 MiB.
2. Export it and overwrite 160 MiB of member b.
3. Import it and run `zpool scrub`.
4. Run `zpool clear`, then a second scrub, which must be clean.

### Results

Three runs, under Driver Verifier standard flags:

| Run | Bugs found |
|---|---|
| First run (2026-10-26) | 0010 and 0011 |
| Second run, with 0010 and 0011 (2026-10-27) | none: every check passed |
| Four further re-import read rounds of the 15,036-file tree | none: 0 failures, where 3 of 4 rounds failed before 0010 |

**Linux validation:** `python scripts/vm/validate-extended.py`, PASS on Ubuntu `zfs-2.4.1-1ubuntu5.1`:

- **Live dataset:** 15,036 files and 2,003 directories match.
- **`@win1`:** read through `.zfs/snapshot`, 15,036 files and 2,004 directories match the pre-mutation state.
- **Dedup and compression children:** match. `dedupratio` is 4.00x, and `zdb -DD` shows 32 duplicate DDT entries.
- **Compression ratios:** `compressratio` is 5.29x for zstd and 6.34x for gzip.
- **Alternate data streams:** each stream is a `user.<name>:$DATA` xattr. The 100 B stream matches byte for byte. The 300 KB stream is listed, but its value is over Linux's 64 KiB xattr limit, so Linux cannot read it.
- **DACLs:** the security descriptor is stored as `windows:security.NTACL`, which Linux shows as `user.windows:security.NTACL`, and it contains the BUILTIN\Users grant.
- **Scrub and leaks:** scrubs are clean on both pools, and `zdb -bcc` reports no leaks.
- **Mirror:** the Windows scrub repaired 83.4 MB with 0 errors, and the second scrub was clean. Linux imported the mirror, scrubbed it clean, and the data matched.

### Bugs found and fixed

The patches are documented in `patches/openzfs-windows/README.md`.

- **0010, recycled vnodes kept stale file sizes.**
  - **Symptom:** after re-import, a few files' first reads returned their real content zero-padded to another file's length; a re-read was correct.
  - **Fix:** `vnode_create()` now zeroes `FileHeader`.
- **0011, first access to a new snapshot.**
  - **Symptom:** the first access to `.zfs\snapshot\<new>` returned the empty ctldir stub, so paths inside it failed.
  - **Fix:** a bounded wait for the automount.
- **0012, volume PDOs forwarded unhandled IRPs to another device stack.** Found only under stricter Driver Verifier flags; two dumps:
  - `IRP_MN_QUERY_RESOURCE_REQUIREMENTS`, bugcheck 0x50 in `ViFilterDispatchPnp`;
  - a MountMgr `IRP_MJ_DEVICE_CONTROL`, bugcheck 0x50 in `ViFilterDispatchGeneric`.
- **0013, unmount deadlocked the lazy writer.** `zfs_zget_ext` spun during `zil_close` while holding `vfs_main_lock`, and the lazy writer waited for that lock. Diagnosed from an NMI dump analyzed inside the VM.

### Stricter Driver Verifier

`-VerifierFlags 0xA6FBB` adds the following to the standard flags:

- forced pending I/O;
- IRP logging;
- invariant MDL checking (stack and driver);
- the additional DDI compliance rules.

Patches 0012 and 0013 came from running the read/write/extended phases under these flags. With both applied, all three phases pass under them. The only failure was the known `zpool.exe` exit crash after a successful mirror import, which is now recorded separately; there were 0 bugchecks.

**Final regression (2026-10-28, patches 0001–0018).** `Test-DriverInVm.ps1 -Phase all`, under the standard flags, exited 0 with 0 bugchecks, and Linux validated the written, extended and cache pools (see step 13). Step 13 also found:
- 0014: wildcard listings;
- 0015–0017: intent-log durability and replay;
- 0018: `spl_mutex_exit()` use-after-free;
- an amendment to 0009.

### Other items

- **`zpool.exe` exit crash.**
  - **Diagnosis:** a WER user dump (`zpool.exe.7212.dmp`) shows the crash happens at process exit, after the import succeeded. The CRT's per-thread-data teardown (`destroy_ptd`) races about 200 detached libspl taskq worker threads that are still exiting after `taskq_destroy()`.
  - **Status:** the fix is assigned to step 14, where the tools must return reliable exit codes.
- **Host incidents.** The host's event log shows 16 unexpected shutdowns since 2026-09-21, which predate the VM (created 2026-10-23). Two of them are bugchecks, both 0x10E/0x2D (VIDEO_MEMORY_MANAGEMENT_INTERNAL):
  - one during a live KDNET session;
  - one during an offline `kd -z` with connected standby in progress.

  The driver never ran on the host. All dump analysis now runs inside the VM (`scripts/vm/Analyze-GuestDump.ps1`).
- **Not tested:** randomized low-resources simulation (`0x209BF`).
- **Test environment:**
  - The VM's kernel debugger is now off during tests: KDNET with nothing attached stalled every `DbgPrint` and froze the guest on breakpoints.
  - Remoting uses PowerShell Direct, because the host's internal-switch address was lost on 2026-10-27.
