# Step 06 — Compression compatibility

Status: `planned`

Objective: read genuine blocks written with `off`, `lz4`, `gzip-1`, `gzip-9`,
`zstd`, and all additional supported algorithms discovered from OpenZFS.

Implementation: fixture generation, decompression dispatch, logical/physical
space reporting, property-change coverage, throughput capture, and hash
verification. Existing blocks must remain readable after a property change.

Commit: `native: add compression compatibility and tests`

