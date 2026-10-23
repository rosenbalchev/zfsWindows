# Step 10 — Transactional writes

Status: `complete`

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

At this checkpoint truncate, delete, rename, mkdir/rmdir, nested paths and
multi-cycle persistence were still open; they are completed below.

## Completion: namespace operations, crash consistency, persistence

The adapter now carries a minimal znode layer (`wznode_t`, loaded from and
written back to SA in the same transaction) and ports the Linux ZPL routines
onto it: `zfs_link_create`, `zfs_link_destroy`, `zfs_drop_nlink_locked`,
`zfs_unlinked_add`, `zfs_rmnode`, `zfs_mknode` (files and directories),
`zfs_rename` (with `zfs_rename_lock`'s descendant check), `zfs_freesp`,
`zfs_trunc`, `zfs_extend`, `zfs_grow_blocksize`, `zfs_write` and
`zfs_tstamp_update_setup`.  Commands take `/`-separated paths relative to the
dataset root:

```text
zfs-write-core <cmd> <image-directory> <pool> <dataset> ...
  stat <path> | create-file <path> | mkdir <path>
  write-file <path> <offset|append> <payload-file> | truncate <path> <size>
  remove <path> [--defer-reclaim] | rmdir <path> [--defer-reclaim]
  rename <from-path> <to-path>
```

Crash consistency follows upstream ordering: an unlink removes the name,
drops the link and adds the znode to the unlinked set (`DELETE_QUEUE`) in one
transaction; reclaim then frees data in bounded transactions and removes the
set entry together with the object.  `--defer-reclaim` stops after the first
transaction, producing the on-disk state of a crash before reclaim.  Each
command imports, syncs its TXGs (`txg_wait_synced`) and exports, so every
command is a full reopen cycle.

Upstream-compatibility fixes found by comparing zdb output with Linux-created
znodes: new directories carry `ZFS_ACL_AUTO_INHERIT` (from
`zfs_acl_ids_create()`), and `ZFS_NO_EXECS_DENIED` is set when all execute
bits are allowed (from `zfs_mode_compute()`).

Deliberately refused with an error rather than approximated: parents with
inheritable ACEs (`ENOTSUP`), objects with an xattr directory (`ENOTSUP`),
normalized or case-insensitive datasets (`ENOTSUP`), names of
`ZAP_MAXNAMELEN` bytes or more (`ENAMETOOLONG`, longname feature), pre-SA
znodes (`ENOTSUP`), and `.`/`..` path components (`EINVAL`).  Quotas,
ownership other than root, hard links, symlinks and RENAME_EXCHANGE/WHITEOUT
are not implemented.  The adapter is an executable; exposing writes through
the C ABI/C# layer is left to the userspace-tools step (13), and abrupt
process termination and I/O failure injection belong to step 11.

### Evidence

`python scripts/test-write-core.py 192.168.195.63` (Ubuntu VM, OpenZFS
`zfs-2.4.1-1ubuntu5.1`; replaces `test-write-core.sh`), run 2026-10-23,
`PASS (73 Windows import/export cycles)`:

1. Linux creates a 256 MiB lz4 pool with `existing.txt`, `dir/` and
   `linux/victim.bin` (2 MiB).
2. Windows phase 1, 63 cycles: files (small, 3 MiB + append + mid-file
   overwrite, sparse, empty); `mkdir` and nested creates, including inside
   the Linux-made directory; truncate shrinking across blocks, within a
   block, extending to a sparse tail and to zero; removal of Linux-made and
   Windows-made files; deferred-reclaim `remove` and `rmdir`; `rmdir`;
   rename within a directory, across directories, of a directory subtree,
   and onto an existing file and an existing empty directory.  It also checks
   18 expected failures: `EEXIST`, `ENOENT`, `ENOTDIR`, `EINVAL` (including a
   directory moved below itself), `EISDIR`, `EPERM` and `ENOTEMPTY`.  Finally
   it checks that `stat` sizes persist across cycles.
3. Crash state on the Windows output: the unlinked set holds exactly 2
   entries, and `zdb -bcc` reports no leaks.
4. Linux import: all 14 entries match the model (type, size, link count,
   SHA-256, directory sizes and link counts). Linux mounting drains the
   unlinked set to 0 entries. Scrub repairs 0 B with 0 errors, and
   `zdb -bcc` reports no leaks.
5. Linux mutates the pool (append, create, `mkdir -p`, `rm`, `mv`), then
   re-verifies 17 entries. zdb shows Windows `mkdir`/`create-file` znodes
   identical to Linux-made ones: bonus 176, mode 40755/100644, pflags
   `840800000144`/`840800000004`.
6. Windows phase 2 reopens the Linux-written image: it reads the size of the
   Linux file, appends to it, removes Linux-made directories with `rmdir`,
   renames across directories, truncates, removes, and creates nested
   entries.  The final Linux verification matches all 16 entries; scrub is
   clean, `zdb -bcc` reports no leaks, and the unlinked set is empty.
