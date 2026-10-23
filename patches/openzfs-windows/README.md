# Patches to `third_party/openzfs-windows`

The submodule stays pinned to the upstream openzfsonwindows release.
`scripts/build-write-core.ps1` applies these patches idempotently before
configuring. 0001 and 0002 are bug fixes and candidates for upstream
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
