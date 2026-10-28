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
