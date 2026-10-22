# Step 10 — Transactional writes

Status: `in-progress`

Objective: implement disposable file-backed writes compatible with upstream
OpenZFS.

Implementation: create/write/append/overwrite/truncate/delete/rename,
directories, transaction groups, COW allocation, space accounting, flush
ordering, and reopen persistence. Every output image must import cleanly in
upstream OpenZFS.

Commit: `native: add crash-consistent disposable-pool writes`

## Current implementation checkpoint

The Windows native adapter and its pinned OpenZFS build are now wired through
Visual Studio CMake/clang-cl.  The adapter currently provides two safe
lifecycle commands:

```text
zfs-write-core.exe probe <image-directory> <pool> <dataset>
zfs-write-core.exe sync  <image-directory> <pool> <dataset>
```

`sync` waits for the dataset pool TXG and `close_context` repeats the wait
before disowning the dataset.  The build explicitly enables the OpenZFS
assembly path for ASM sources only; this resolves the Windows user-mode ICP
link gap without changing the C ABI.  The corresponding checkpoint commit is
`2a8c877`.

The step remains in progress.  The remaining mutation work must use the
OpenZFS DMU/ZAP/SA interfaces directly because the Windows vnode operation
sources are part of the KMDF kernel target and are not linkable into this
user-mode adapter.  No host filesystem staging is acceptable as a substitute:
the test must verify the resulting image with upstream Linux OpenZFS.

The next low-level primitive is now implemented as `write-object`: it reads a
bounded payload, validates the target object, holds and assigns a DMU
transaction, calls `dmu_write`, commits it, and waits for the resulting TXG.
This is deliberately object-addressed while pathname namespace creation is
being built on top of the same transaction machinery.

The adapter also now exposes `create-file <image-directory> <pool>
<dataset> <name>`.  It allocates a `DMU_OT_PLAIN_FILE_CONTENTS` object with
SA bonus metadata and inserts a regular-file dirent into the dataset root in
the same transaction.  This checkpoint is compile-verified; cross-platform
image verification is still pending because the configured Ubuntu VM is not
currently accepting the available SSH key.

Live testing is now possible with the project SSH key.  The first disposable
fixture test currently stops in the OpenZFS debug invariant at
`dnode_sync.c:683` (`DNODE_FLAG_USERUSED_ACCOUNTED`) during object creation.
The image copy was disposable and the failed implementation is not being
promoted to a completed step; accounting/ownership initialization must be
fixed before proceeding to additional mutation operations.

## Checkpoint: ZPL-compatible create and write (verified)

The `DNODE_FLAG_USERUSED_ACCOUNTED` invariant was resolved by registering
the ZPL file-info decoder (`dmu_objset_register_type`), which libzpool does
not do because it never runs `zfs_init()`.  Further defects found and fixed
while driving genuine images:

- `sa_handle_destroy()` already releases a shared handle's bonus hold; the
  extra `sa_buf_rele()` underflowed the dbuf refcount.
- `create-file` now mirrors `zfs_mknode()`/`zfs_link_create()`: non-zero
  generation (Linux `zfs_znode_alloc()` rejects generation 0), current
  timestamps, `ZFS_ARCHIVE|ZFS_AV_MODIFIED|ZFS_ACL_TRIVIAL`, `ZFS_PROJID` and
  `projid` when project quota is active, the three-ACE trivial DACL, the
  upstream SA attribute order, and parent size/mtime/ctime updates.  Parents
  needing ACL inheritance are refused with `ENOTSUP`.
- `write-file <name> <offset|append> <payload>` replaces the raw
  `write-object` command and ports `zfs_write()`: single-block growth up to
  `recordsize`, `DMU_MAX_ACCESS/2` chunking, and per-chunk SA size, mtime,
  ctime and flags updates.
- The adapter now exports the pool on close, so Linux imports without `-f`.
- **Pinned libspl bug:** `wosix_pwrite()`/`pread_win()` implemented
  positional I/O with a shared file pointer, and concurrent vdev_file I/O
  wrote a space-map block one sector late (Linux: `ECKSUM`, datasets
  unmountable).  Fixed via `patches/openzfs-windows/0001-*.patch`, which
  `build-write-core.ps1` applies idempotently.

Evidence (`scripts/test-write-core.sh`, OpenZFS `zfs-2.4.1-1ubuntu5.1` VM):
fresh 256 MiB lz4 pool; Windows creates four files, writes 26 B, 3 MiB +
append + 5,000 B mid-file overwrite, and a sparse write at 1 MiB, and rejects
a duplicate create.  Linux imports cleanly, the namespace and root directory
size match, all four SHA-256 values match, Linux appends to a created file,
scrub repairs 0 B with 0 errors, `zdb -bcc` reports no leaks, and zdb
verifies the feature refcounts.

Remaining for this step: truncate, delete (including the unlinked set),
rename, mkdir/rmdir, nested paths, a C ABI/C# integration test, and
reopen/persistence across several import cycles.
