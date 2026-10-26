# Patches to `third_party/openzfs-windows`

The submodule stays pinned to the upstream openzfsonwindows release.
`scripts/apply-openzfs-patches.ps1` (run by `build-write-core.ps1` and
`build-driver.ps1`) applies these patches idempotently before configuring.
0001, 0002 and 0004–0009 are bug fixes and candidates for upstream
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
