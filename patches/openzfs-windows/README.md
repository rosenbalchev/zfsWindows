# Patches to `third_party/openzfs-windows`

The submodule stays pinned to the upstream openzfsonwindows release.
`scripts/build-write-core.ps1` applies these patches idempotently before
configuring; each is a candidate for upstream submission.

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
