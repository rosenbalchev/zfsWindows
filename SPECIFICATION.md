# ZFS Windows implementation specification

## Scope

The product is split into three independently testable layers:

1. `ZfsCore`: portable C code for block I/O and ZFS on-disk interpretation.
2. `ZfsNativeApi`: the Windows user-mode DLL boundary used by managed tests.
3. `ZfsDriver`: a future WDK filesystem driver that consumes the same core
   contracts through kernel adapters.

The upstream OpenZFS tree is a reference dependency, not a source tree to
modify. Any reused code must retain its CDDL notices and be isolated behind
platform adapters.

## First interfaces

`ZfsCore` uses opaque handles and returns explicit error codes. All offsets and
sizes are unsigned 64-bit values and every operation checks overflow and the
device boundary before issuing I/O. The initial device mode is read-only.

The first probe reads all four standard vdev-label positions using the device
size rather than hardcoded end offsets. It checks that the positions are
addressable and records whether the label prefix is non-zero. This is only a
safe geometry/presence probe, not ZFS recognition. Full nvlist decoding,
uberblock verification, MOS traversal, dataset discovery, and file reads are
M2 work and must be added before the genuine-file acceptance test can pass.

## Milestones and evidence

- M1: build/interop infrastructure and safe block I/O.
- M2: genuine image recognition, uberblock selection, MOS and dataset reads.
- M3: single-disk fixture and SHA-256 file verification.
- M4-M7: compression, scale, mirrors, and snapshots.
- M8-M9: transactional writes, writable snapshots, and mirrors.
- M10: WDK driver in an isolated VM only.
- M11: stress and performance evidence.

A milestone is only complete when its native build, managed tests, and genuine
fixture evidence are recorded. A skipped fixture test is not a pass.

## Fixture contract

`tests/fixtures/manifest.schema.json` defines the metadata needed to prove
application-level file content independently of ZFS block checksums. Golden
images are immutable inputs; writable tests must operate on disposable copies.

