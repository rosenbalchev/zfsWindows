# Step 08 — Mirrored pools

Status: `planned`

Objective: read genuine two-device mirrors and validate checksum-based fallback.

Implementation: topology discovery, member loss, corruption of disposable
copies, valid-member recovery, and later replacement/resilver verification.

No automatic self-healing claim is allowed until an independently validated
resilver completes.

Commit: `native: add mirrored vdev reads and recovery tests`

