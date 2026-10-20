# Step 09 — Snapshot reads and lifecycle

Status: `planned`

Objective: discover snapshots and read historical dataset contents using
copy-on-write metadata, not copied files.

Implementation: snapshot enumeration, historical dataset roots, read-only
snapshot paths, rollback and deletion semantics in disposable pools.

Commit: `native: add snapshot discovery and historical reads`

