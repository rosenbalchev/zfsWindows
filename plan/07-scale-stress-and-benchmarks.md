# Step 07 — Scale, stress, and benchmarks

Status: `planned`

Objective: validate directory traversal and resource behavior from 5,000 to
500,000 files, with 5,000,000 reserved for dedicated storage.

Implementation: streaming manifests, configurable fan-out/depth, concurrent
enumeration/read tests, memory/time tracking, and BenchmarkDotNet plus
end-to-end workload runners. Cached and uncached results must be separated.

Commit: `tests: add scalable stress profiles and benchmarks`

