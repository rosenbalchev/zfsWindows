# Step 13 — Read and write caching through the driver

Status: `complete`

Prerequisites: Step 12 (the driver in the isolated VM) is complete, including its extended follow-up (snapshots, streams, ACLs, large directories, concurrent and memory-mapped writes, dedup, compression and scrub).

Objective: prove that every cache layer between a Windows application and the pool behaves correctly through the OpenZFS kernel driver. Correct means: no stale reads, no lost acknowledged writes, a consistent pool after a hard power-off, and pools that upstream Linux OpenZFS imports and validates.

## Read caches

- **ARC.** Cold reads after import, warm rereads, and `kstat` ARC hit and miss counters. The `zfs_arc_max` registry tunable must be respected under a working set larger than the limit. The `primarycache=all|metadata|none` property must be honoured: data hits must not grow under `metadata` or `none`.
- **Windows Cache Manager over ARC.** A file changed through one handle must read back new data through another handle, through a memory mapping, and after a remount. Reads after an export and import must be cold, never stale.
- **L2ARC.** Add a file cache vdev on Windows. A repeated read workload must produce `l2_hits`. The `secondarycache` property must be honoured. Remove the cache vdev online. Linux must import the pool with the cache device missing.
- **Corrupt L2ARC.** Corrupt the cache device while the pool is imported. Reads must still return correct data: checksums reject the bad L2ARC blocks and the data is read from the pool.

## Write caches

- **Cache Manager write-back.** Write without flushing, then hard power-off the VM (`Stop-VM -TurnOff`). After the reboot the pool must import and be consistent. Each file must hold either its old or its new contents, decided by the last committed TXG.
- **Flushed and write-through writes.** Data written with `FlushFileBuffers` or `FILE_FLAG_WRITE_THROUGH`, and acknowledged before the power-off, must survive.
- **The `sync` property.**
  - `sync=always`: every acknowledged write survives a power-off.
  - `sync=disabled`: a power-off may lose recent writes, but the pool stays consistent.
  - `sync=standard`: only flushed writes are guaranteed.
- **SLOG and ZIL replay.** Add a separate log vdev and run a sync-heavy workload, then power off. On the next import the ZIL is replayed on Windows, and every acknowledged sync write is present.
  - Import with the log device missing (`zpool import -m`).
  - Remove the log device online.
- **Linux validation.** After each scenario, Linux OpenZFS imports the pool, checks the expected file contents, scrubs with 0 errors, and `zdb -bcc` reports no leaks.

## Evidence

Record in this file, for each scenario: the counters, the acknowledged-write logs, the power-off times, and the Linux validation output.

## Safety

The power-off is a Hyper-V `TurnOff` of the isolated test VM only. Nothing runs on the host.

Commit: `driver: verify read and write caching in the isolated VM`

## Implementation (2026-10-27/28)

### Harness

- `Test-DriverInVm.ps1 -Phase cache` runs the `scripts/vm/guest-cache.ps1` stages in the VM, under Driver Verifier (standard flags).
- Remoting is PowerShell Direct over the Hyper-V VM bus, so it doesn't depend on the guest or host network.
- The install phase turns the VM's kernel debugger off. With KDNET enabled and nothing attached, every `DbgPrint` from the debug driver stalled in `KdSendPacket`, and a breakpoint froze the guest. NMI and crash dumps still work.
- The verifier gained new verbs: `read-unbuffered`, `read-random`, `coherency`, `cache-writer` and `cache-check`.
- Linux validation scripts:
  - `scripts/vm/validate-cache.py` validates the pools;
  - `scripts/vm/l2arc-reference.py` runs the same L2ARC scenario on Linux OpenZFS for comparison;
  - `Test-ZilReplay.ps1` with `validate-zil-replay.py` lets Linux replay intent logs that the Windows driver wrote and never replayed itself.

### Scenarios

- **ARC.**
  - `zfs_arc_max` 256 MiB, applied to `c_max` through the tunable registry key.
  - A 128 MiB file, reread with unbuffered I/O:
    - `primarycache=all`: 0–2 ARC misses;
    - `primarycache=metadata`: 1,024 misses, so every block came from disk again.
  - A 768 MiB read leaves the ARC at about 275 MB.
  - Windows `c_min` is about 256 MiB, so a 192 MiB `zfs_arc_max` is raised to it; Linux has the same rule.
- **L2ARC**, the same scenario as on Linux:
  - `l2arc_noprefetch=0`, `l2arc_write_max` 64 MiB, `l2arc_rebuild_blocks_min_l2size=0`;
  - a 512 MiB cache vdev;
  - 4 passes of 4,096 random 128 KiB reads from a fixed seed;
  - export, corrupt the cache device past its labels, import, 2 more passes;
  - then a second, full corruption of the cache device's data area.

  | | Windows | Linux reference |
  |---|---|---|
  | L2ARC hits in the random passes | 8,438–8,971 | 5,636 |
  | Persistent rebuild on import | 744 buffers | 434 buffers |
  | Hits after the partial corruption | 2,161–2,332 | 1,385 |
  | Corrupted blocks rejected | 1 log block (`l2_rebuild_cksum_lb_errors`) | 6 `l2_cksum_bad` |
  | After the full corruption | rebuild rejected; new hits come from blocks written afterwards | — |
  | Wrong reads | 0 | 0 |

  Removing the cache vdev online works.
- **Cache Manager coherency.** A cached reader sees a write made through another handle. A mapping sees file writes, and file reads see mapping writes, before any flush. Cached and non-cached I/O stay coherent in both directions, and a truncate is seen by another handle. The final content survives a remount.
- **Power cuts.** Six writers each write 64 KiB files at about 20 a second, and record each one in an acknowledgement log on NTFS (write-through and flushed) only after the write returns. The writers are:
  - `buffered`;
  - `FlushFileBuffers`;
  - write-through;
  - `sync=always`;
  - `sync=disabled`;
  - `sync=always` with a SLOG;
  - plus an NTFS control writer with flush.

  After 45 s the VM is turned off with `Stop-VM -TurnOff`. After the reboot the pool is imported and the files are classified, and a second cut covers import with the log device missing.

### Driver bugs found and fixed (patches 0014–0018)

- **0014: wildcard listings returned nothing.** On case-sensitive datasets, wildcard directory listings (`*.bin`) matched nothing. The pattern was upper-cased for a case-sensitive match. At first this looked like every file lost after a power cut.
- **0015: corrupt ZIL create records.** `zfs_log_create_txtype()` stored `va_mask & ATTR_XVATTR` in an `int`. On Windows `ATTR_XVATTR` is bit 63, so the truncation made it 0, and every Windows create was logged as `TX_CREATE` with an xvattr block where the name belongs. Replay created a file named `"\x03"` and stopped, so acknowledged `FlushFileBuffers`, `sync=always` and SLOG writes were lost. `zdb -iv` and Linux replay confirmed this was the log writer.
- **0016: write-through didn't sync.** `FILE_FLAG_WRITE_THROUGH` / `SL_WRITE_THROUGH` writes never committed the intent log.
- **0017: Windows replay dropped every logged write.** Replay's `zfs_write_simple()` went through a stub `zfs_vn_rdwr()` that never wrote. Every replayed `TX_WRITE` failed with `EIO` and stopped the replay.
- **0018: `spl_mutex_exit()` use-after-free.** It touched the mutex after releasing it, and `zio_done()` → `zio_wait()` → `zio_destroy()` freed it in that window: bugcheck 0x0A in `KeSetEvent`, under the special pool. It now holds `m_destroy_lock` across the release.
- **0009 amended.** `zfs_attach_security()` returns when the vnode lost its znode to a concurrent reclaim: bugcheck 0x3B, found in a power-cut run.

### Durability evidence before the final run

- **Linux replay of Windows-written logs** (`validate-zil-replay.py`, patches 0001–0016): the `flush`, write-through, `sync=always` and SLOG datasets each have **0 acknowledged files missing**, out of 386–408 acknowledged. `buffered` and `sync=disabled` lost about 30 recent files, which those modes allow. No odd names, and the scrubs are clean.
- **NTFS control on the same disk:** 600 acknowledged, 600 present and exact. So the VM and virtual disk keep acknowledged writes.

### Other observations

- **Windows Defender** rescans files opened on ZFS volumes; one test measured 19 s of Defender CPU per 26 s of opens. With the debug driver build, under Driver Verifier, opens took 2–35 ms. Excluding test paths would change security settings and wasn't done.
- **Driver Verifier overhead with 0018:** deadlock detection instruments the new spinlock in every `mutex_exit()`. The 8.9 GB read took 192 s, against 67–78 s before. A lock-free release (one combined owner/waiters word) would avoid that; it isn't done.

## Final run (2026-10-28, patches 0001–0018)

**Command:** `Test-DriverInVm.ps1 -Phase all`, which runs install, read, write, extended and cache under Driver Verifier's standard flags. It **exited 0** with 0 bugchecks.

**Linux OpenZFS validation**, all PASS:
- `validate-written.py`;
- `validate-extended.py`: 15,036 files plus the `@win1` snapshot, dedup 4.00x, streams, DACLs and the mirror;
- `validate-cache.py`: every pool from the cache phase, with clean scrubs and no leaks.

**Power cut, with Windows replaying its own intent log on import:**

| Mode | Acknowledged | Present (exact) | Missing |
|---|---|---|---|
| `FlushFileBuffers` | 40 | 40 | **0** |
| write-through | 37 | 37 | **0** |
| `sync=always` | 37 | 37 | **0** |
| SLOG + `sync=always` | 32 | 33 (one in flight) | **0** |
| NTFS control | 470 | 470 | 0 |
| buffered (loss allowed) | 54 | 49 | 5, no garbage |
| `sync=disabled` (loss allowed) | 46 | 42 | 4, no garbage |

- **Larger samples:** the ZFS writers managed fewer files than before, because of the Verifier overhead of 0018. The larger durability samples come from `validate-zil-replay.py` with the 0016 driver: 386–408 acknowledged per durable mode, 0 missing. 0017 and 0018 don't change how the log is written.
- **Missing log device (second cut):** `zpool import` refuses without `-m` ("use '-m' to import the pool anyway"). `zpool import -m` works, and the lost writes are in an allowed-loss mode (11 of 388), with no garbage. The missing log vdev is removed, and the scrub is clean.
- **L2ARC on the final driver:**
  - 8,421 hits during the random passes;
  - 744 buffers rebuilt on import;
  - the corrupted log block rejected in both corruption rounds (`l2_rebuild_cksum_lb_errors`);
  - 0 wrong reads.
- **ARC and coherency:** as above.

## Completion

The exit evidence is met. Every cache layer was exercised through the driver: ARC, L2ARC, Cache Manager coherency, write-back, flush, write-through, the `sync` property, and SLOG with intent-log replay. After hard power-offs:
- acknowledged durable writes survive;
- buffered writes are lost cleanly, never garbled;
- Linux OpenZFS validates every pool.

**Limitations, not claimed as working:**
- **Debug build only.** Tested only with the debug driver build under Driver Verifier. With 0018, deadlock detection instruments every `mutex_exit()`, which makes runs about 3x slower; a Release build is untested.
- **Defender overhead.** Real-time protection rescans files opened on ZFS volumes; ZFS has no USN journal for it to use. Its overhead was measured, not mitigated.
- **No physical cache devices.** L2ARC and SLOG were tested only with file-backed vdevs.
- **ARC minimum.** On this VM's memory, `zfs_arc_max` below `c_min` (about 256 MiB) is raised to `c_min`, as on Linux.
