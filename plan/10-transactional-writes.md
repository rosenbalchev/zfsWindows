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

