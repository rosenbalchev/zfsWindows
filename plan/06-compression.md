# Step 06 — Compression compatibility

Status: `in progress`

Objective: read genuine blocks written with `off`, `lz4`, `gzip-1`, `gzip-9`,
`zstd`, and all additional supported algorithms discovered from OpenZFS.

Implementation: fixture generation, decompression dispatch, logical/physical
space reporting, property-change coverage, throughput capture, and hash
verification. Existing blocks must remain readable after a property change.

Progress: decompression dispatch now covers the full upstream on-disk table:
uncompressed, LZJB, gzip levels 1–9, ZLE, LZ4, and Zstandard. Gzip uses the
official zlib 1.3.2 source (archive SHA-256
`E8BF55F3017AA181690990CB58A994E77885DA140609FC8F94ABE9B65D2CAE28`),
and Zstandard uses the vendored OpenZFS decompressor sources. A reusable Linux
generator records compressible/moderate/random hashes, allocation metrics,
write throughput, and before/after compression-property coverage.

Commit: `native: add compression compatibility and tests`

