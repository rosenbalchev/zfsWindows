# Step 11 — Failure injection and recovery

Status: `planned`

Objective: distinguish acknowledged durable data from uncommitted data under
I/O, allocation, metadata, mirror, and process-failure injection.

Implementation: controlled checkpoints, recovery fixtures, upstream import
validation, and isolated-VM abrupt termination tests.

Commit: `tests: add persistence and failure-recovery validation`

