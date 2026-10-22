# Step 07 — Scale, stress, and benchmarks

Status: `complete`

Objective: validate directory traversal and resource behavior from 5,000 to
500,000 files, with 5,000,000 reserved for dedicated storage.

Implementation: streaming manifests, configurable fan-out/depth, concurrent
enumeration/read tests, memory/time tracking, and BenchmarkDotNet plus
end-to-end workload runners. Cached and uncached results must be separated.

Implemented profiles:

| Profile | Files | Default fan-out/depth | Sparse image size | Execution policy |
|---|---:|---:|---:|---|
| Small | 5,000 | 10 / 3 | 512 MiB | milestone and developer stress run |
| Medium | 50,000 | 10 / 3 | 2 GiB | milestone and developer stress run |
| Large | 500,000 | 10 / 4 | 32 GiB | dedicated storage, opt-in |
| Extreme | 5,000,000 | 10 / 5 | 256 GiB | dedicated storage, opt-in |

The Linux generator creates empty, 1–128 byte, binary, Unicode, near-limit
long, similar, and case-colliding names. It writes a streaming JSONL hash
manifest, exports and re-imports the pool, verifies every file independently,
checks the enumeration count, and records logical/allocated space, throughput,
elapsed time, peak working set, OpenZFS version, GUID, and immutable image hash.

Windows evidence (2026-10-22):

- Small: 5,000 files in 1,110 directories; 39.8 fully verified opens/s;
  latency p50 15.079 ms, p95 23.300 ms, p99 25.677 ms; 13,664,256-byte
  working-set delta. Linux created 9,803.3 files/s and verified 5,368.8/s
  after re-import.
- Medium: 50,000 files in 1,110 directories; 45.0 fully verified opens/s;
  latency p50 17.911 ms, p95 24.819 ms, p99 33.854 ms; 26,075,136-byte
  working-set delta. Linux created 12,482.5 files/s and verified 4,991.1/s
  after re-import.
- Both runs opened and SHA-256-checked every file, matched the complete
  recursively enumerated path set, rejected duplicates, repeated 1,000
  deterministic random opens, matched concurrent enumeration totals, and
  proved the image SHA-256 unchanged.
- BenchmarkDotNet 0.15.8 warm-cache microbenchmarks on the Small fixture:
  root enumeration mean 12.507 ms / 1,704 B allocated; tiny-file open/read
  mean 8.252 ms / 520 B allocated. These warm measurements are reported
  separately from the initial end-to-end traversal; no OS cache flush was
  claimed.

`scripts/run-stress-tests.ps1` runs an explicitly selected fixture and
`scripts/run-benchmarks.ps1` runs the microbenchmarks. Large and Extreme are
implemented but intentionally not ordinary milestone gates: at the measured
uncached traversal rate their exhaustive Windows passes require dedicated
multi-hour hosts and storage.

Commit: `tests: add scalable stress profiles and benchmarks`

