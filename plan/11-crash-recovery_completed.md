# Step 11 — Failure injection and recovery

Status: `complete`

Objective: distinguish acknowledged durable data from uncommitted data under
I/O, allocation, metadata, mirror, and process-failure injection.

Implementation: controlled checkpoints, recovery fixtures, upstream import
validation, and isolated-VM abrupt termination tests.

Commit: `tests: add persistence and failure-recovery validation`


## Implementation

Every scenario starts from a fresh pool that Linux OpenZFS creates. zfs-write-core then
runs a `run-script` workload in a single import. It prints and journals
`ack <n>` only after step n's transaction groups are synced, which is the
adapter's durability acknowledgement. Linux OpenZFS 2.4.1 then imports the
result and judges it with these criteria:

1. The pool imports without rewind (`-f` only for an unexported image).
2. The namespace and every file's bytes equal the model after the
   acknowledged steps.
3. The one in-flight step may also be complete. A crash inside a shrinking
   truncate may leave the old size with a partly zeroed tail, which is what
   upstream `zfs_freesp()` does.
4. The unlinked set drains, scrub reports 0 errors, and `zdb -bcc` reports
   no leaks.

New mechanisms:

- **Power-loss journal.** `ZFS_WRITE_CORE_JOURNAL=<file>` records every
  vdev write together with the bytes it replaced, every flush, and every
  acknowledgement in one total order. It goes through patch 0003, an
  optional observer at libzpool's `zfs_file` layer.
  `scripts/zfs_crash_image.py` runs on the VM and rebuilds the devices as
  they would be after a power cut at any journal record:
  - writes that began after the cut are reverted;
  - a write is durable once it completed before a completed flush began;
  - every other write is volatile, and each of its 512-byte sectors
    survives or is lost (`random`), is lost (`prefix`), or survives
    (`all`).

  As a self-check, reverting every journaled write must reproduce the base
  image byte for byte.
- **Upstream fault injection.** `inject-write-errors <leaf|*>` and
  `clear-injections` script steps call `zio_inject_fault()` with
  `ZINJECT_DEVICE_FAULT` writes, as zinject does.
- **Fixes found by these tests.**
  - `rmnode`'s final transaction is now net-free, as in
    `dmu_free_long_object()`. Before, removing a file on a full pool failed
    with ENOSPC.
  - A reclaim failure no longer fails a remove whose unlink is already
    durable. As upstream, the znode stays on the unlinked set.
  - Patch 0002: libspl `fsync` returned a positive `EIO`, so
    `zfs_file_fsync()` treated a failed vdev flush as success.

## Evidence

`python scripts/test-write-recovery.py 192.168.195.63` (OpenZFS
`zfs-2.4.1-1ubuntu5.1`), run 2026-10-23:
`PASS (power-loss, win-recovery, kill, io-errors, enospc, mirror, labels)`
in 808 s. The power-loss and win-recovery scenarios were then rerun with
more lossy cuts (529 s). The workload has 25 steps:
- files of 1–4 MiB;
- an append and a mid-file overwrite;
- truncates that shrink and that extend;
- renames of files and directories, including a subtree;
- `mkdir`/`rmdir`;
- removal of Linux-created and Windows-created files.

- **power-loss.** The journal has 3,692 records: 1,759 writes, 74 flushes
  and 25 acknowledgements. It reverts to the base image exactly. The
  completed run verifies.
  - 47 power cuts: just before every third acknowledgement, 26 at random
    records (12 `random`, 10 `prefix`, 4 `all`), 10 right after flushes,
    plus the two ends.
  - 26 cuts had volatile writes; 22 lost sectors, 17,503 in total.
  - Every image imported without rewind and matched an allowed state.
    In 17 cuts the in-flight step was already complete. Every scrub was
    clean, every image had no leaks, and every unlinked set drained.
- **win-recovery.** Two `random`-mode power-cut images, at states 4 and 7,
  were reopened by Windows. Windows imported the unexported crashed pool,
  ran 5 more steps (`mkdir`, create, 2 MiB write, rename, remove) and
  exported. Linux matched the model exactly; scrub was clean and there were
  no leaks.
- **kill.** The process was terminated (TerminateProcess) 6 times: once
  during import and 5 times after 0–15 acknowledged steps plus 0–120 ms.
  Every image imported with `-f`, and each matched either the acknowledged
  state or the one with the in-flight step complete (2 of 6).
- **io-errors.** Write EIO on the only device after step 6: the pool
  suspended, the adapter hung and was killed, and no later step was
  acknowledged. Linux saw exactly the acknowledged state 7.
- **enospc.** On a 64 MiB pool, about 30 MiB is usable after the 32 MiB slop
  reserve. Step 12 (`create-file`) failed with ENOSPC (28) after 24 MiB and
  was not acknowledged. Three removes then reclaimed space, and a new 1 MiB
  file was written. The model matched, scrub was clean, and there were no
  leaks.
- **mirror.**
  - **Faulted member.** m2 was given write EIO for 12 steps; those included
    a 3 MiB file that is never rewritten. Because zinject reports EIO
    after the bytes reach the file, the journal was used to drop those
    writes from m2: 21,775 sectors. Before repair, m2 alone did not import
    ("insufficient replicas"). After the Linux pass, both members and each
    member alone matched the model.
  - **Missing member.** Windows ran the workload with m1 only, while the
    stale m2 differed from the model in 10 entries. Afterwards, both members
    and each member alone matched.
  - **Corrupted member.** 96 MiB of m1 was overwritten with random bytes
    before the Windows workload. Scrub repaired 41.5K, and each member alone
    then matched.
- **labels.** The front pair (L0/L1) or back pair (L2/L3) was zeroed.
  Windows imported, ran 10 steps and exported. `zdb -l` then showed
  `labels = 0 1 2 3`, and Linux matched the model.

The step 10 regression (`scripts/test-write-core.py`) still passes after
these changes: 73 cycles.

## Limits

- The power-loss model gives honest flushes and 512-byte sector atomicity.
  It does not model a disk that acknowledges a flush it did not perform.
  `ZINJECT_IGNORED_WRITES`, which models that, was rejected for this
  reason.
- Process termination happens on the Windows host, so the OS cache
  survives it; power loss is covered by the journal reconstruction. No
  whole-VM power-off was performed.
- I/O faults are injected on writes only. Read faults and checksum
  corruption are covered by the mirror corruption case and by step 8's
  reader tests.
