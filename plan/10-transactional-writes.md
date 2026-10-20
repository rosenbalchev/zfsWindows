# Step 10 — Transactional writes

Status: `planned`

Objective: implement disposable file-backed writes compatible with upstream
OpenZFS.

Implementation: create/write/append/overwrite/truncate/delete/rename,
directories, transaction groups, COW allocation, space accounting, flush
ordering, and reopen persistence. Every output image must import cleanly in
upstream OpenZFS.

Commit: `native: add crash-consistent disposable-pool writes`

