# Patches to `third_party/openzfs-windows`

The submodule stays pinned to the upstream openzfsonwindows release.
`scripts/apply-openzfs-patches.ps1` (run by `build-write-core.ps1` and
`build-driver.ps1`) applies these patches idempotently before configuring.
0001, 0002 and 0004–0018 are bug fixes and candidates for upstream
submission. 0003 is test instrumentation.

## 0001 — libspl positional I/O without the shared file pointer

`wosix_pwrite()` and `pread_win()` emulated `pwrite`/`pread` as *seek, I/O,
restore seek* on a shared synchronous handle. libzpool issues vdev_file I/O
concurrently from taskq threads, so another thread could move the file
pointer between the seek and the transfer and the data landed at the wrong
offset.

Observed on a disposable pool: a 3 MiB file write produced a space-map block
written one 512-byte sector late (its LZ4 header at byte 0x200). Linux
OpenZFS then failed `space_map_load` with `ECKSUM` and could not mount either
dataset. The fix passes the offset in an `OVERLAPPED` structure so the
position belongs to the single `ReadFile`/`WriteFile` request.

The patch also makes `wosix_fdatasync()` flush (`FlushFileBuffers`) instead of
being a silent no-op.

## 0002 — libspl `fsync` reports failure POSIX-style

`wosix_fsync()` returned a positive `EIO` when `FlushFileBuffers()` failed.
`zfs_file_fsync()` treats only a negative result as failure, so a failed
vdev flush was reported as success, and ZFS would then write an uberblock
whose blocks might not be on stable storage. The function now returns `-1`
and sets `errno`.

## 0003 — libzpool `zfs_file` I/O observer

This adds an optional observer, `zfs_file_observer` (NULL by default), to
the user-space `zfs_file_pwrite()` and `zfs_file_fsync()`. It sees the start
and end of every vdev file write and flush. zfs-write-core uses it for the
power-loss journal in step 11 (`ZFS_WRITE_CORE_JOURNAL`). It sits at the
platform-neutral `zfs_file` layer, the boundary ztest already uses to
simulate torn writes. The only cost when unset is one pointer test per call.
Unlike 0001 and 0002, it is test instrumentation rather than a fix.

## 0004 — libzfs (Windows) mount when no parent has a drive letter

`do_mount()` walks up from a dataset to find a parent with a drive letter.
It accepts the pool's root dataset as that parent even when its
`driveletter` is unset (`-`). That happens when the root dataset is not
mounted, as in a Linux-created pool with `mountpoint=none` on the root and a
mountpoint on a child. No letter can then be found, and the placeholder `-`
was used as the drive letter: the dataset was "mounted" at
`\??\-:\var\tmp\...`, which never appears as a volume. The patch falls back
to the full mountpoint path on `C:`, which is what the function's comment
already describes. Found in plan step 12 with the genuine single-disk
fixture.

## 0005 — kernel: a read-only pool is a read-only mount

The Windows `zfs_is_readonly()` only tested the mount's `MNT_RDONLY` flag.
On a pool imported with `-o readonly=on`, that flag was clear when
directory enumeration first loaded the znodes of Linux-created files.
`zfs_attach_security()` then saved a generated NT security descriptor as an
xattr: `zfs_save_ntsecurity()` → `zpl_xattr_set()` → `zfs_log_setsaxattr()`.
That ended in `VERIFY(spa_writeable(zilog->zl_spa))` in `zilog_dirty()` and
bugcheck `0x00FFFFFF`. This was found in plan step 12 with the genuine
single-disk fixture under Driver Verifier; the kernel dump is analyzed in
the step file. `zfs_is_readonly()` now also reports read-only when the pool
is not writeable, which Linux tests separately with `spa_writeable()`. This
can only turn a would-be panic into a skipped write.

## 0006 — mmp: do not signal pools that are being exported

`spa_export_common()` sets `spa_export_thread` under the namespace lock,
then tears the pool down without that lock, destroying the MMP locks while
`spa_state` can still read `ACTIVE`. `mmp_signal_all_threads()`, called
from the `zfs_multihost_interval` setter, then locked a destroyed mutex.
This produced bugcheck `0x00FFFFFF`, "mutex not initialised". On Windows the
setter runs whenever the driver re-applies its tunables after any change
under its registry key, which made the race routine during export. The loop
now skips pools with `spa_export_thread` set. That field is written under
the namespace lock the loop holds, so the check cannot race. This is an
upstream bug, not specific to Windows.

## 0007 — Windows: no access-time transaction on a read-only file system

`zfs_inactive()` wrote a pending access time (`z_atime_dirty`) in its own
transaction whenever the pool was not suspended. On a pool imported
read-only, that transaction dirtied the dataset. `dsl_dataset_dirty()`'s
hold is released only by a sync, and a read-only pool never syncs, so the
hold was never released and every later `zpool export` failed with "pool is
busy". A dump analysis showed the cause: `spa_refcount` 14 against
`spa_minref` 11, with the excess held by `zfswin_single/data`, whose only
`ds_dbuf` hold was the dataset's own dirty hold. The pending atime is now
dropped when `zfs_is_readonly()` (see 0005) is true. Linux never reaches
this state, because its VFS does not mark atime dirty on read-only mounts.

## 0008 — Windows: unmount releases the SA handles of lingering znodes

Upstream, an unmount waits until the VFS has evicted every inode before
`zfsvfs_teardown()` continues. The Windows port waits one round only
("windows can get stuck"), so znodes whose vnodes the Cache Manager still
holds survive the unmount with their SA handles, and therefore dnode
holds. `zfsvfs_free()` later unlinks such znodes and clears `v_data`, but
nothing releases the handles. The objset then never finishes evicting after
`dmu_objset_disown()`, and `zpool export` blocks forever in
`spa_evicting_os_wait()` while holding the pool namespace lock, which also
hangs every other `zpool` command.

This was found in plan step 12 after writing through the driver. A hang
dump analysis showed `zc_lz4/data`'s objset on `spa_evicting_os_list` with
two file dnodes still held, each by a live znode (`z_vnode` set, `z_sa_hdl`
holding bonus and spill buffers) whose vnode already had `v_data == NULL`
and no references. On unmount, after that single wait round, the patch
releases the SA handles of every remaining znode (`zfs_znode_dmu_fini()`,
under the teardown-inactive write lock). `zfs_vnop_reclaim()` already frees
a znode with a NULL `z_sa_hdl` without touching the DMU.

## 0009 — Windows: no security-descriptor attach under the teardown write lock

During unmount, `zfsvfs_teardown()` holds the teardown lock as writer and
calls `zil_close()`. Committing the remaining intent log can need a file's
data (`zfs_get_data()`), and therefore `zfs_zget()` of a znode that was
already reclaimed. On Windows, creating that znode's vnode attaches an NT
security descriptor: `zfs_attach_security()` → `zfs_load_ntsecurity()` →
`zpl_xattr_get()` → `zfs_enter()`. That re-enters the teardown lock as
reader on the same thread, and
`VERIFY(rrl->rr_writer != curthread)` in `rrw_enter_read_impl()` raised
bugcheck `0x00FFFFFF`. This was found in plan step 12 when unmounting after
writes through the driver. `zfs_attach_security()` now returns early when
the current thread holds the teardown lock as writer: an intent-log data
fetch needs no security descriptor, and Linux's `zfs_zget()` never reads
xattrs at all.

The function also returns early when the vnode has no znode (`VTOZ(vp)` is
NULL). A concurrent reclaim can clear it right after
`zfs_znode_getvnode()` publishes the vnode. The original code got away with
that only when a stored descriptor let it return before using the znode.
The teardown check above first moved that use earlier, and a step-13 power
cut test then hit it: bugcheck 0x3B in `zfs_attach_security`, from a
create's lookup.

## 0010 — SPL: a recycled vnode starts with zero file sizes

`vnode_create()` takes vnodes from a kmem cache, and the cache constructor
zeroes a vnode only when the cache first creates it. `vnode_create()` reset
the section object pointers, but not the `FSRTL_ADVANCED_FCB_HEADER` sizes,
and `FsRtlSetupAdvancedHeader()` does not reset them either. A recycled
vnode therefore kept the previous file's `FileSize`, `AllocationSize` and
`ValidDataLength`. `zfs_couplefileobject()` deliberately only *grows*
`FileSize` on open, so the stale larger value stayed. Cached reads, which
are bounded by `FileHeader.FileSize`, then ran past the real end of the new
file and returned zeros. A later size query refreshed the value, so a
re-read was correct.

This was found in the step-12 extended tests. After export and re-import,
between 0 and 20 of 15,036 files read wrong the first time in 4 of 5
mounts. Each bad read was the right content, zero-padded to another file's
length (11 bytes, 1 MiB, 4 MiB, 120 MiB), while Linux read every file
correctly from the same image. The fresh-boot read test never recycles
vnodes, so it could not see this. `vnode_create()` now zeroes the header
before `FsRtlSetupAdvancedHeader()`. With the patch, the extended run and
4 more reimport read rounds read every file correctly.

## 0011 — ctldir: wait briefly for a snapshot automount

The first lookup of `.zfs\snapshot\<name>` mounts the snapshot through
`zfs_vss_snapshot_mount()`, then retries `getzfsvfs()` exactly once. When
the new mount was not yet reachable, the lookup returned the ctldir stub,
which is an empty directory. The first open of a path inside a snapshot
just taken (`...\.zfs\snapshot\win1\windows-extended`) then failed with
"path not found", although later accesses worked. After a successful mount
call, the lookup now retries `getzfsvfs()` for up to 2 s (20 × 100 ms),
leaving the teardown lock between tries. Found in the step-12 extended
tests; with the patch, the snapshot is read through `.zfs` on first access.

## 0012 — Windows: volume PDOs complete the IRPs they do not handle

Each mounted dataset's device (DCB) is a PDO that the bus driver reports in
BusRelations, so it is the bottom of its own device stack. When a request
was not handled (`STATUS_INVALID_DEVICE_REQUEST`), `dispatcher()` passed
the IRP with `IoSkipCurrentIrpStackLocation()` and `IoCallDriver()` to the
*bus* FDO's lower device, which is in another stack, and the IRP has no
stack location for it. Under the standard Driver Verifier flags nothing
below looked. With forced pending I/O and IRP logging
(`verifier /flags 0xA6FBB`), Verifier's filter read past the IRP's stack
and raised bugcheck 0x50. Two dumps were analyzed inside the VM:

- `IRP_MN_QUERY_RESOURCE_REQUIREMENTS` during `PiProcessNewDeviceNode`,
  faulting in `nt!ViFilterDispatchPnp` (write to `0xffffffffffffffbb`);
- `IRP_MJ_DEVICE_CONTROL` from MountMgr
  (`FsRtlGetVirtualDiskNestingLevel`) for a newly arrived volume,
  faulting in `nt!ViFilterDispatchGeneric` (write to `0xfffffffffffffff0`).

On every non-bus device the patch now completes unhandled IRPs. PnP and
power IRPs keep their `IoStatus.Status`, as WDM requires of a PDO; every
other request completes with `STATUS_INVALID_DEVICE_REQUEST`. The bus FDO
still forwards down its own stack.

## 0013 — Windows: no zget spin while unmount holds the teardown lock

`zfs_windows_unmount_impl()` holds `vfs_main_lock` as writer and calls
`zfsvfs_teardown()`. That runs `zil_close()` → `zil_commit()` →
`zfs_get_data()` → `zfs_zget()` with the teardown lock held as writer by
the same thread. If the znode's vnode was still being attached, or was
being reclaimed, `zfs_zget_ext()` retried in a `kpreempt()` loop. But the
attach (`zfs_znode_asyncwait()` fails during teardown) and the reclaim
cannot finish while unmount holds those locks. Meanwhile the Cache
Manager's lazy writer, flushing that file
(`CcWriteBehind` → `zfs_AcquireForLazyWrite()` → `vfs_busy()`), blocked on
`vfs_main_lock` as reader. Export never finished.

This was found in plan step 12 under `verifier /flags 0xA6FBB` (forced
pending I/O), where the write phase's `zpool export` hung every time. A
resource-timeout breakpoint froze the guest. An NMI dump analyzed inside
the VM showed `zpool.exe` holding `OpenZFS!vfs_main_lock` exclusively in
`zfs_zget_ext+0x471` → `kpreempt`, and the lazy writer waiting for it
shared.

`zfs_zget_ext()` now fails with `EAGAIN` instead of spinning when the
current thread holds the teardown lock as writer. Upstream
`zfs_get_data()` already turns a failed `zfs_zget()` into `ENOENT`, which
the ZIL treats as "data not needed", and `zil_close()` then waits for the
TXG that contains the data. This is the same unmount path as patch 0009.

## 0014 — Windows: wildcard directory listings on case-sensitive datasets

`query_directory` always upper-cased a search pattern that contained
wildcards. It then matched with
`FsRtlIsNameInExpression(..., IgnoreCase = !(z_case == ZFS_CASE_SENSITIVE))`.
On a case-sensitive dataset, which is the default for Linux-created pools
and for Windows pools created without `-O casesensitivity=insensitive`,
the match is exact. So `*.bin` became `*.BIN` and matched none of the
lower-case names, and any wildcard containing a letter returned an empty
listing (`dir *.txt`, `Directory.GetFiles(dir, "*.bin")`). `*` alone still
worked, which is why the earlier tests did not see it.

This was found in plan step 13. After the power cuts, every dataset
appeared to have lost all its files, but an unfiltered listing showed 396
files where `GetFiles(dir, "w-*.bin")` returned 0, and the user-mode core
read them from the image. The pattern is now upper-cased only when the
match ignores case. The step-12 extended workload checks wildcard
listings, so this stays covered.

## 0015 — ZIL: create records keep the xvattr bit (lost writes after power loss)

`zfs_log_create_txtype()` stored `vap->va_mask & ATTR_XVATTR` in an `int`.
On Windows, `ATTR_XVATTR` is `1ULL << 63` (on Linux it is `1U << 31`), so
the truncation always gave 0. Every create carrying an xvattr was logged as
`TX_CREATE` / `TX_MKDIR`, and Windows creates carry one for the DOS
attributes. But `zfs_log_create()` tests the untruncated mask and wrote the
xvattr block where a plain `TX_CREATE` record keeps the file name.

On replay, after a crash or power loss, the name was read from
`lr_attr_masksize` (3), giving a file called `"\x03"`. The next create
collided with it, replay stopped, and every later record was dropped. That
includes the writes acknowledged after `FlushFileBuffers`, write-through,
`sync=always` and a SLOG.

This was found in plan step 13. After Hyper-V power cuts, the durable modes
lost their last acknowledged writes. `zdb -iv` of the never-imported pool
showed every `TX_CREATE` named `^C`, and Linux OpenZFS replaying the same
log produced the same `\x03` file, which ruled out the replay side. The
type is now computed from the boolean result. This is an upstream bug on
any platform whose `ATTR_XVATTR` does not fit in an `int`.

## 0016 — Windows: write-through writes commit the intent log

Windows writes call `zfs_write()` with no sync flag. Cached writes reach
ZFS later, as paging I/O from the Cache Manager. A file opened with
`FILE_FLAG_WRITE_THROUGH` (`FO_WRITE_THROUGH`), or a request flagged
`SL_WRITE_THROUGH`, promises that the data is on stable storage when the
write completes, like `O_DSYNC` elsewhere. But its intent-log records stayed
asynchronous until the next TXG. In plan step 13, a write-through writer
lost its last ~2 s of acknowledged files after a power cut, the same as a
buffered writer, while `FlushFileBuffers` and `sync=always` lost none once
patch 0015 was applied.

After a successful non-paging write on such a file object,
`zfs_write_wrap()` now calls `zil_commit()` for the file, as an fsync does.
By then the Cache Manager has already flushed the write-through data
synchronously. `zil_commit()` honors `sync=disabled`.

## 0017 — Windows: intent-log replay writes data (no vnode stub)

Replaying a `TX_WRITE` record calls `zfs_write_simple()`. On Windows that
went through `zfs_vn_rdwr()`, a stub whose `VNOP_WRITE` is commented out.
It never wrote and left `resid` uninitialized, so `zfs_write_simple()`
returned `EIO` and `zil_replay()` stopped at the first write record. After
a crash or power loss, every write in the intent log was therefore dropped
on Windows. In the step-13 power cuts this showed as one 0-byte file (its
create replayed, its write not) followed by every later acknowledged file
missing, in all durable modes. Linux OpenZFS replayed the same log with
nothing missing (after patch 0015). The NTFS control writer on the same
disk lost nothing either.

`zfs_write_simple()` now does what Linux's does: it builds a `zfs_uio` and
calls `zfs_write()` on the znode. That needs no vnode, which a znode
created during replay may not have yet.

## 0018 — SPL: mutex_exit() no longer touches a mutex another thread freed

`spl_mutex_exit()` cleared `m_owner` first. Only afterwards did it read
`m_waiters`, take `m_destroy_lock` and `KeSetEvent()` the mutex's event.
In that window another thread could take the now-free mutex, drop it, and
destroy and free the memory around it. `zio_done()` wakes `zio_wait()`
with `mutex_exit(&zio->io_lock)`, and the woken thread calls
`zio_destroy()`, which is exactly that pattern. The exiting thread then
touched freed memory. Under Driver Verifier's special pool this was
bugcheck 0x0A (IRQL_NOT_LESS_OR_EQUAL) in `KeSetEvent` from
`spl_mutex_exit()` ← `zio_done()` ← a zio taskq thread, during the
step-13 L2ARC test. The `m_destroy_lock` guard existed for this case but
was taken too late.

`spl_mutex_exit()` now holds `m_destroy_lock` across the release, the
waiter check and the signal. `spl_mutex_destroy()` takes the same
spinlock, so it waits until the exiting thread no longer touches the
mutex. The wake-up protocol is unchanged: waiters register before their
final compare-and-swap, so reading `m_waiters` after the release cannot
miss one. This adds a spinlock acquire to every uncontended release.

## 0019 — libspl: taskq_destroy() joins its worker threads

User-space taskq workers were created detached. `taskq_destroy()` waited
only until `tq_nthreads` reached zero, which each worker decrements just
before `thread_exit()`. The workers were therefore still running their
exit path, including the C runtime's per-thread teardown, when
`taskq_destroy()` returned. `zpool import` creates and destroys such a
taskq to scan devices, and then exits. Intermittently a worker was still
in the CRT's fiber-local-storage teardown (`destroy_ptd`) while the main
thread ran the process-exit teardown, and `zpool.exe` exited with
0xC0000005 after a successful import (step 12, dump `zpool.exe.7212.dmp`).

Taskq workers are now created `TS_JOINABLE`, and `taskq_destroy()` joins
every one of them before it frees the taskq. Nothing else waits on these
threads, and `taskq_member()` still compares the same thread handles.

## 0020 — CMake: explicit OpenSSL libraries take precedence

The root `CMakeLists.txt` hard-codes the OpenSSL static libraries per
architecture with plain `set()` calls:
`C:/Program Files/OpenSSL-Win64/...` for x64 and
`C:/Program Files/OpenSSL-Win64-ARM/...` for ARM64. A `-D` option cannot
override a normal variable, so an ARM64 build could only use an OpenSSL
installed at exactly that system path. When `OPENSSL_CRYPTO_LIBRARY` and
`OPENSSL_SSL_LIBRARY` are both given on the command line, they are now
used as they are. The ARM64 build (plan step 17) passes a vcpkg
`arm64-windows-static` OpenSSL that way. Without them, nothing changes.
## 0021 — CMake: run host build tools when cross-compiling

`tracewpp`, `signtool` and `stampinf` were chosen from the WDK `bin`
folder of the *target* architecture (`CMAKE_SYSTEM_PROCESSOR`). An ARM64
build on an x64 machine therefore tried to run the ARM64 tools, and failed
("not compatible with the version of Windows you're running"). These tools
run on the build machine, so they are now selected by
`CMAKE_HOST_SYSTEM_PROCESSOR`. Native builds are unchanged.

## 0022 — Lua: ARM64 Windows kernel setjmp buffer type

On Windows ARM64 the kernel Lua uses the WDK's `setjmp`/`longjmp`, which
take a `jmp_buf` (an `unsigned __int64` array). `LUAI_TRY` and
`LUAI_THROW` passed `&(c)->b`, a `label_t *`. Current clang rejects
that as an error (`-Wincompatible-pointer-types`). They now pass the
array `(c)->b.val`, which has room for 64 values, more than the ARM64
`jmp_buf` needs. Other architectures are unchanged.

## 0023 — ARM64: exception context registers in the write fault filter

`zfs_write_fault_filter()` records the faulting instruction and stack
pointers for diagnosis. It read `ContextRecord->Rip` and `->Rsp`, which
exist only in the x64 `CONTEXT`. ARM64 uses `Pc` and `Sp` through a
small macro. The recorded values and their meaning are unchanged.
## 0024 — libzfs: the user-mode ZFS bridge client

The user-mode edition (plan step 18) runs the ZFS engine (libzpool and
upstream `zfs_ioctl.c`) inside the `ZfsWinUserMode` service instead of the
kernel. The service answers the same ZFS ioctls on the local named pipe
`\\.\pipe\OpenZFS-UserMode`. Access is limited to SYSTEM and Administrators,
and remote clients are rejected. With this patch, the same `zpool.exe` and
`zfs.exe` work against either edition:

- `lib/libspl/include/os/windows/sys/zfs_bridge.h` (new): the wire format. One
  request per connection: a header, then the `zfs_cmd_t` and the src, conf
  and history buffers it points at. The response carries the `zfs_cmd_t`, the
  history output and the dst buffer, bounded by the capacities the request
  named.
- `posix.c`: `open("\\.\ZFS")` opens the pipe when the driver's device does
  not exist. `zfs_bridge_call()` performs one round trip.
  `zfs_bridge_is_fd()` tells the two apart.
- `libzfs_core_ioctl.c`: `lzc_ioctl_fd_os()` sends `zfs_cmd_t` over the
  bridge when the descriptor is the pipe. Its own buffer pointers are
  restored afterwards. The result follows the driver path's convention
  (errno, -1).
- `getmntany.c`: `getfsstat()` adds the bridge's mount table, because the
  volumes the service mounts belong to WinFsp and do not answer
  `ZFS_IOC_GET_MOUNT`. That makes `zfs mount`, `zfs unmount` and
  `zpool export` see them.
- `libzfs_util_os.c`: `zfs_version_kernel()` reads the `zfs_version` that the
  user-mode service records under its own service key when there is no
  `OpenZFS` driver key. Without it, `zfs version` fails, and with it the
  broker's `version` operation.

With the driver present, nothing changes: `\\.\ZFS` opens as before and
no pipe is consulted, apart from one failed open per mount-table read when
no service listens.

## 0025 — libzpool: root file system hook for user space

`dsl_pool_create()` builds a new pool's root file system with
`zfs_create_fs()`, but only `#ifdef _KERNEL`. In libzpool the root dataset of
a pool created there has no ZPL at all, so it cannot be mounted. Its
children can, because `zfs create` goes through `zfs_create_cb()`. Step 18's
VM run hit exactly that: "cannot open zwin: error 2".

The fix cannot happen afterwards. Changing the root objset in a later
syncing transaction trips `dbuf_dirty()`'s assertion that only a brand-new
objset is written in syncing context. So user space gets a hook,
`dsl_pool_create_fs_hook`, called at the same place with the same
`zplprops`. It is NULL by default, so zdb and ztest are unchanged. The
user-mode engine points it at its port of `zfs_create_fs()`.

## 0026 — cmake: ZFS_DEBUG for all userland code

libzpool always builds with `ZFS_DEBUG` (`lib/libzpool/CMakeLists.txt`),
as upstream libzpool does. The top-level `CMakeLists.txt` defines it for
everything else only in Debug. `ZFS_DEBUG` changes shared structures:
`abd_t` gains `abd_parent` and `abd_children`, and `zfs_refcount_t`
becomes a tracked list.

In a Release build, libzpool and the other userland libraries (libzstd's
compressors take `abd_t`) disagreed about those layouts. The first write
into a zstd dataset in the user-mode engine then failed `abd_verify()`
(step-18 follow-up, 1.0.8 Release VM run). `zdb` would hit the same
mismatch.

`lib/` and `cmd/` now define `ZFS_DEBUG` in every configuration. The
kernel module (`module/`) keeps upstream's per-configuration setting.

## 0027 — kernel: uninitialized values that trap in Release

Four uses of uninitialized values in the Windows kernel module are
undefined behaviour. Debug builds (`/Od`) tolerated them. In Release,
clang may treat such a path as unreachable, and on Windows targets it
compiles unreachable code to `int3`.

- `zvol_os_deregister_module()` tested `status` without setting it: the
  result of `ZwDeviceIoControlFile(OPENZVOL_DEREGISTER)` was discarded, and
  the wait used a different `IO_STATUS_BLOCK`. The 1.0.9 Release driver
  bugchecked on unload (`0x7E`, `0x80000003` at `zvol_os.c:367`) when
  a failed install rolled back in the VM suite.
- The `FileIdAllExtdBothDirectoryInformation` directory listing stored the
  next-entry offset through `fiebdi`, a pointer never set in that branch,
  instead of `fiaebdi`.
- The boot ereport posted an unset `ena`; it is now 0.
- In `QueryDeviceRelations()`, a failed allocation in the
  `TargetDeviceRelation` case used `goto out`. That label is inside the
  `BusRelations` block, so the jump freed the never-initialized
  `StorportRelations` and dereferenced the NULL list. The case now breaks
  and returns `STATUS_INSUFFICIENT_RESOURCES`.

The remaining `-Wsometimes-uninitialized` warnings (`metaslab.c`,
`range_tree.c`) are upstream switch defaults that `panic()`, so they are
harmless.

## 0028 — kernel: no over-aligned pointer assumptions

Upstream marks several members `____cacheline_aligned` (64 bytes), for
example `zio_t.io_queue_node`, `dsl_pool_t`, `metaslab_group_t`, `aggsum_t`
and `multilist_sublist_t`. Clang therefore assumes every pointer to those
structures is 64-byte aligned. The Windows SPL `kmem_cache` guarantees only
`KMEM_ALIGN` (8). Linux is safe because its kernel builds without SSE, and
Debug builds (`/Od`) copy without SSE as well.

An optimized build copies `blkptr_t` into `zio->io_bp_orig` with `movaps`.
On a zio at an address that is 8- but not 16-byte aligned, that raised a
general-protection fault: the 1.0.10 Release driver bugchecked with `0x7E`
(`c0000005`) at `zio_create` (`zio.c:996`) as soon as the VM suite created
a pool.

Two changes:

- **The macro:** the Windows kernel SPL now defines `____cacheline_aligned`
  as empty, as upstream's userland libspl already does
  (`lib/libspl/include/sys/feature_tests.h`). `-fmax-type-align` cannot
  cover types that carry the attribute themselves: `spa_config_lock_t`
  inside a `kmem_zalloc`'ed `spa_t`, and `aggsum_bucket_t`. The change only
  drops false-sharing padding.
- **The compiler option:** `use_clang()` adds `-Xclang -fmax-type-align=8` to
  `WDK_COMPILE_FLAGS`, so clang never assumes more than 8 bytes of alignment
  behind a pointer to any other over-aligned type. Kernel targets take only
  those flags. The MSVC-compiled `driver.c` and userland don't get the
  option (checked with `ninja -t commands`).

The result was checked by disassembling the Release `OpenZFS.sys` for
aligned SSE (`movaps`) through a pointer that is not `rsp`, `rbp` or `rip`:

| Build | Sites |
| --- | --- |
| Before | 99, in `zio_create`, `zio_write*`, `dsl_pool_open_impl`, `spa_add`, `aggsum_*` and others |
| Compiler option only | 20 |
| Both changes | only the CRT's `memmove`/`memset`, which check alignment first |

User-mode code is unchanged: its malloc returns 16-byte-aligned memory, and
it never had the attribute.

## 0029 — sysctl: no self-triggered Registry loop, no zero dirty limit

On every change under the driver's Registry key,
`sysctl_os_registry_change` reapplies all tunables. The pass wrote
`zfs_version` and the callout tunables back unconditionally. Every such
write fired the notification again, so the passes never stopped.

Each pass also applied the 0 that first load had stored for
`zfs_dirty_data_max`, `zfs_dirty_data_max_max` and `zfs_wrlog_data_max`.
That stored value dates from before `sysctl_os_fix()` computes them from
memory. Until the end of the pass, `zfs_dirty_data_max` was therefore 0.
A transaction arriving then waited in `dmu_tx_wait` for
`dp_dirty_total < 0`, and nothing woke it on an idle pool. The 1.0.11
Release build showed this as `zpool export` hanging in `zfs_rmdir` of a
child mountpoint during unmount, on x64 and on ARM64. A diagnostic trace
recorded `zfs_dirty_data_max == 0` at every such wait.

The fix has two parts:
- `sysctl_os_write_registry()` writes only when the type or data differ;
- 0 in the Registry for those three tunables means "computed", and is
  not applied.
## 0030 — libzfs mounts one dataset at a time on Windows; MountMgr status

`zfs_foreach_mountpoint()` mounts the datasets of an imported pool with up
to 512 threads (`zpool import`, `zfs mount -a`). In the Windows driver,
every mount below a parent finishes on a taskq (`NotifyMountMgr_impl()`):
it creates the reparse point on the parent volume, announces it to
MountMgr and signals userland. When these run at the same time, some
mounts never reach MountMgr. They stay mounted in the kernel, but
`getmntent()` (which walks MountMgr's volume list) cannot see them, so
`zfs mount`, `zfs list -o mounted` and `zpool export` miss them. The export
then fails with "pool is busy" with nothing visibly mounted.

On the ARM64 VM (12 CPUs) the ARM64 user-mode pool, with 12 datasets,
showed this on every import in the kernel suite's cross-check: 3 of 12
children were missing from `mountvol`. With `ZFS_SERIAL_MOUNT=1` the same
import mounted all 12 in 1 s instead of 10.7 s.

The fix has two parts:
- **Serial mounting:** `zfs_foreach_mountpoint()` mounts serially under
  `_WIN32`. That is the code path upstream already has for
  `ZFS_SERIAL_MOUNT`, and it is the same for every Windows architecture.
- **MountMgr status:** `SendIoctlToMountManager()` read `iosb.Status`
  even when `IoCallDriver()` had not pended, so the I/O status block
  could still be unwritten. The driver log showed this as "IoCallDriver
  failed: 0xeaa30690". The status now comes from `IoCallDriver()` unless
  the request pended, as Microsoft documents.
## 0031 — libzpool: whole-disk vdevs (the ZFS partition of a disk) on Windows

`zpool create <pool> \\?\PhysicalDriveN` labels the disk with GPT (a ZFS
partition from 1 MiB, a small reserved partition at the end). It then names
the vdev `#<offset>#<length>#\\?\PhysicalDriveN`. The kernel driver opens
that form in `vdev_disk.c`. The user-mode engine is libzpool, where disk
vdevs use `vdev_file.c`, and there it failed (GitHub issue #1):

- **The path was rejected:** `vdev_file_open()` accepted only paths starting
  with `/` or `\` on Windows. `zpool create` ended in "cannot create
  '<pool>': I/O error" after writing the label.
- **The size was the whole disk's:** `zfs_file_getattr()` returned the size
  of the disk, not of the partition. Only the start offset was honoured
  (read back from the file pointer `wosix_open()` had moved), so ZFS would
  have used the space up to the end of the disk, which holds the reserved
  partition and the backup GPT.

The fix has two parts:
- `vdev_file_open()` accepts `#` paths on Windows.
- `zfs_file_open()` takes the offset and length from the path into
  `f_win_offset` and the new `f_win_length`. `zfs_file_getattr()` reports
  the partition length.

Checked on a fresh 8 GiB VHDX in the x64 VM, user mode:
- the issue's command creates the pool, 7.50 GiB, as the kernel edition does;
- 40 files survive export and import;
- the scrub is clean;
- both GPT partitions are intact afterwards.

The same on ARM64, in user mode in the ARM64 VM: the shipped 1.0.15 engine
failed with the issue's I/O error, the fixed one created the pool
(7.50 GiB), 40 files survived export and import, the scrub was clean, and
both GPT partitions were intact.

The kernel edition had no problem (same test: created, exported,
imported).

## 0032 — libzpool: the user-mode ARC sized and shrunk as in the kernel

libzpool's ARC memory functions (`lib/libzpool/arc_os.c`) are the ones
for ztest. The user-mode edition's engine, `zfswin-um.exe`, is libzpool,
and it got the same values (GitHub issue #3, "most of the system memory
after 15–20 GB written, dedup on"):

- **ARC ceiling:** half the memory minus 1 GiB (`arc_all_memory()` is half
  of it, `arc_default_max()` takes 5/8 or all but 1 GiB of that).
- **ARC floor:** half the ceiling (`arc_init()`, userland branch), so a
  quarter of the memory once filled.
- **No memory pressure:** `arc_available_memory()` reports plenty, except
  a random -1 KiB on one call in a hundred; `arc_free_memory()` is a random
  number, and `arc_sys_free` is 0. The ARC never shrank, however little
  memory Windows had left.

On Windows the patch uses the kernel driver's sizing instead:
- `arc_all_memory()` is all of the memory, `arc_default_max()` a quarter
  of it, so that the whole process (the ARC, and the dbufs, dnodes and
  heap around it) stays within the third the kernel driver gives its ARC;
- the floor stays at 1/32 (`arc_set_limits()`);
- `arc_free_memory()` is the memory Windows reports available (free and
  standby pages, `sysconf(_SC_AVPHYS_PAGES)`, new in libspl), read at most
  every 10 ms because `arc_adapt()` asks for every buffer;
- `arc_sys_free` is 1/16 of the memory: below that, the ARC gives memory
  back.

Other platforms' userland (ztest, zdb) is unchanged.

## 0033 — libzutil: the import scan takes a partition path

A pool on a whole disk has vdevs named `#<offset>#<length>#\?\PhysicalDriveN`
(0031). The user-mode service records them as the pool's sources and, when
it starts, imports the pool from them with `zpool_find_config()`. The scan
`stat()`ed each source, found no such file and skipped it silently. The
pool did not come back after a service restart; the GUI's mount editor
restarts the service, so changing a drive letter there made the pool
disappear (its data was intact; `zpool import` brought it back).

`zpool_find_import_scan()` now adds a `#` path as a slice of its own, and
`zpool_open_func()` already reads the labels at that offset.

The engine (`src/native/ZfsUserMode/zum_pool_um.c`) also looks for a pool
recorded on disks on all disks by name when it is not at the recorded path
(disk numbers change when disks are added or removed), and the service
records the new path.

The engine recorded each leaf's `vdev_path`, which for a disk is the
display name `/dev/physicaldriveN` that no scan can open; the path a disk is
opened by is its physpath. `zum_pool_sources()` now records the physpath
when it has the `#` form, and a pool recorded as `/dev/physicaldriveN` (by
1.0.16) is looked for on all disks and recorded again.

## 0034 — libzpool: an ABD's chunks in one allocation

libzpool simulates the kernel's scatter ABDs with one
`umem_alloc_aligned(4096, 4096)` per 4 KiB chunk. On Windows that is
`_aligned_malloc()`, which takes size + alignment + a pointer from the heap:
about 8 KiB per 4 KiB page. Every byte the ARC held cost two in
`zfswin-um.exe` (GitHub issue #3: a process far above the ARC ceiling).

On Windows `abd_alloc_chunks()` now allocates one region of n × 4 KiB per
ABD, 64-byte aligned (what the SIMD checksum and RAID-Z code asks of a
mapped chunk), and points the iovecs into it; `abd_free_chunks()` frees it.
Only the owning ABD frees chunks; offset views copy the iovecs. Chunk
addresses were never used for I/O: a scatter ABD is borrowed into a linear
buffer for it. Other platforms' userland is unchanged.

### 0032–0034: verified (1.0.18, Release, user mode)

`scripts\vm\Test-UserModeInVm.ps1 -Phase memory,wholedisk,reboot` (x64) and
`scripts\vm\arm\Test-ArmSuite.ps1 -Edition usermode -Only memory,wholedisk`
(ARM64), 8 GiB guests, 16 GiB of unique data written to a dedup pool:

| | x64 | ARM64 |
| --- | --- | --- |
| engine peak (limit RAM/3 + 256 MiB = 2986 MiB) | 2522 MiB | 2550 MiB |
| level, first half / last quarter | 2430 / 2517 MiB | 2397 / 2550 MiB |
| under memory pressure | 2353 → 700 MiB | 1816 → 677 MiB |
| whole-disk pool after a mount edit + restart, a renumbered disk, the path recorded again | passed | passed |
| whole-disk SCSI pool after a guest restart, at its new drive letter | 13 s after boot | (not run) |

1.0.16 failed the same stages: the pool was not imported after a restart,
and the engine's private bytes were twice the ARC (0034).

The first runs of the memory stage stopped writing after 4.3 GiB on a 4 GiB
pool: the pool was 98% allocated with 1.5 GiB live, and suspended
(`ZIO_SUSPEND_IOERR`, allocating writes failed with ENOSPC). Frees of
deduplicated blocks go through the DDT log and land many txgs later, and
the writer was let in. The stage now uses a 16 GiB pool; a full dedup pool
suspending instead of failing writes with ENOSPC is upstream behaviour,
not addressed here.
