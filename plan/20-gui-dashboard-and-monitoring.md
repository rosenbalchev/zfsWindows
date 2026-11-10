# Step 20 — GUI dashboard, integrity and live rates (issue #17)

Status: `planned`

Prerequisites:
- Step 16: `ZfsWinGui.exe`, the broker operations it uses.
- Step 18: the user-mode ioctl bridge (`\\.\pipe\OpenZFS-UserMode`), which
  lets the same `zpool.exe`/`zfs.exe` and the broker serve both editions.

Source: GitHub issue #17, "User mode: ZFS gui".

## User story

> As the administrator of a Windows machine running OpenZFS for Windows,
> **kernel or user-mode edition**, I want the GUI to show me at a glance
> whether my pools are healthy, how much compression saves, how many files
> and folders I store, which files are damaged, how many copies of my data
> exist, how healthy each drive is, and the current read/write rates and ZFS
> memory use, refreshed every 5 seconds, so that I can spot trouble before it
> costs data, and so that I can create, restore and temporarily mount
> snapshots, without the command line.

Both editions must show the same tabs with the same numbers. Where an
edition gets a value differently, the difference is in the broker or the
bridge, never in the GUI.

## Acceptance criteria

### A. Overview (HTML dashboard)

1. A new first tab, **Overview**, shows a static HTML page that the GUI
   generates locally: inline SVG, no scripts from the network, no CDN.
2. Per pool, it shows:
   - a **health** badge (ONLINE / DEGRADED / FAULTED / SUSPENDED), the last
     scrub (date, repaired bytes, errors) and the count of damaged files;
   - a **capacity** ring: used, free, fragmentation;
   - **space saved**: `logicalused − used` per dataset, `compressratio`, the
     dedup ratio and block-cloning savings (`bclonesaved`), as bars;
   - **files and folders** per dataset (see C).
3. Hosting: WebView2 when its runtime is present. Windows 10/11 LTSC and
   Server editions do not include WebView2, so when it is missing the tab
   shows **Open in browser**. The GUI then writes the same page to
   `%LOCALAPPDATA%\OpenZFS\dashboard.html` every refresh, and the page
   reloads itself every 5 s (`<meta http-equiv="refresh">`).
4. **Save report…** writes the current page as a self-contained `.html`
   file.

### B. Integrity (damaged files)

1. A new tab, **Integrity**, lists the files ZFS reports as damaged
   (permanent errors, `zpool status -v`). It shows the path, the dataset,
   and the snapshot when the block is only in a snapshot. Objects without a
   path are shown as `<metadata>` or `dataset:<0xobject>`.
2. The page explains that ZFS checks every block against its checksum (the
   dataset's `checksum` property, fletcher4 or sha256 by default, not a
   CRC). The list covers only blocks read since the last clear. **Start
   scrub** reads every block, and its progress is shown on the same tab.
3. Per device: READ / WRITE / CKSUM error counters.
4. Actions:
   - **Open containing folder**;
   - **Copy list**;
   - **Clear error counters** (`zpool clear`), which asks for confirmation.
5. New broker operation `pool.errors` (read-only) and CLI parity:
   `zfswin pool errors <pool>`.

### C. Files and folders

1. ZFS keeps no file/folder split, so the counts come from a walk of each
   mounted dataset. It skips `.zfs` and reparse points, and the walk is cancellable.
2. The walk runs in the background on request (**Count now**) and at most
   once per hour automatically. The result is cached with its time ("counted
   12 min ago").
3. A total object count is shown immediately, from `zfs userspace -Hp -o
   objused` when the `userobj_accounting` feature is active, and marked as
   "objects (files + folders + internal)".
4. New broker operation `dataset.count` and `zfswin dataset count`.

### D. Mirrors, copies and drive health

The existing **Mirrors and caches** tab becomes **Redundancy**:

1. Per top-level vdev: layout (single disk / n-way mirror / raidz), and its
   healthy members.
2. **Copies of your data** per dataset: the smallest number of healthy
   members across the data vdevs × the `copies` property. Example: "2-way
   mirror × copies=1 = 2 copies; 1 if a disk fails". A single-disk pool gets
   a warning: one bad block loses a file.
3. **Drive health** for each device:
   - ZFS state and error counters;
   - for physical disks, from Windows Storage WMI:
     - `MSFT_PhysicalDisk` (HealthStatus, OperationalStatus, media type);
     - `MSFT_StorageReliabilityCounter` (temperature, wear, read errors
       corrected/uncorrected, power-on hours), where the disk reports them;
   - for image files, the host volume and free space.
4. Resilver progress while a resilver runs.
5. The existing attach/detach and cache/log actions stay.

### E. Rates (live, every 5 s)

1. A new tab, **Rates**:
   - per pool and per device: read/write **IOPS** and **MB/s**;
   - a chart of the last 10 minutes (120 samples);
   - a configurable interval (1–60 s, default 5) and **Pause**;
   - no polling while the tab is hidden or the window is minimized.
2. **ZFS memory**:
   - ARC size, target, maximum, MRU/MFU split and hit ratio;
   - L2ARC size and hit ratio;
   - dedup table size.

   In the user-mode edition, it also shows the private memory of the
   `ZfsWinUserMode` service: the ARC lives in that process.
3. New broker operation `pool.iostat` (read-only). It returns cumulative
   counters, and the GUI takes the deltas, so no call blocks:
   `zpool get -Hp read_ops,write_ops,read_bytes,write_bytes <pool> all-vdevs`.
   These vdev properties exist in OpenZFS 2.4 (`zpool_prop.c`).
4. New broker operation `memory.stats`. It reads the kstat `zfs:0:arcstats`.

### G. Snapshots: create, restore, temporary mount

A new tab, **Snapshots**, lists the snapshots of the selected dataset with
their creation time, `used` and `referenced`, and whether one is mounted.

1. **Create:**
   - name the snapshot; the default is `gui-yyyyMMdd-HHmmss`;
   - option: include child datasets (`-r`);
   - uses the existing `snapshot.create`.
2. **Restore the whole dataset (roll back):**
   - new broker operation `snapshot.rollback` (destructive), and
     `zfswin snapshot rollback`;
   - the confirmation names the dataset and lists every newer snapshot that
     the rollback destroys (`-r`); without that confirmation, the broker
     refuses;
   - the dataset is unmounted and remounted around the rollback, to the
     same drive letter or folder.
3. **Restore single files:** mount the snapshot temporarily (4), then copy
   back in Explorer. **Open in Explorer** opens the temporary mount.
4. **Mount temporarily**, read-only, to a free drive letter or an empty
   folder:
   - new broker operations `snapshot.mount` and `snapshot.unmount`;
   - **one method for both editions:** a read-only clone
     `<pool>/.zfswin-tmp/<dataset>-<snapshot>` with
     `readonly=on`, `canmount=noauto`, and the user property
     `org.openzfs.windows:temporary=on`. It shares every block with the
     snapshot (no copy, no space used until something writes, which
     `readonly` prevents);
   - this avoids the kernel driver's `.zfs\snapshot` limitation recorded in
     step 18 (files of new snapshots not readable);
   - "temporary" means the clone is destroyed:
     - when the GUI's **Unmount** is used;
     - when the GUI closes, unless "keep mounted" is ticked;
     - at the next broker start, for any clone carrying the property
       (after a crash or reboot);
     - user mode does not record it under the service's `Mounts` key, so
       it does not come back at boot;
   - while a temporary mount exists, its snapshot cannot be destroyed;
     the GUI says so instead of failing with `dataset is busy`.
5. **Destroy** a snapshot: uses the existing `snapshot.destroy`, with
   confirmation.

### F. "The GUI works well"

1. Close step 16's open manual checks:
   - click through every tab on a desktop against a disposable pool, in
     both editions;
   - pick a real disk with the disk picker: attach a VHDX to the VM as a
     SCSI disk, so `MSFT_Disk` and physical-disk vdev paths are covered.
2. `ZfsWinGui.exe --screenshots <dir>` renders every tab to a PNG
   (`DrawToBitmap`, and `CapturePreviewAsync` for WebView2). The VM suites
   collect them as evidence. If PowerShell Direct cannot render without a
   desktop, record that and take them by hand.
3. Per-monitor DPI v2 in `app.manifest`. Refresh never blocks the window,
   and broker errors show in the status bar with the failed operation.
4. `--self-check` also runs `pool.errors`, `pool.iostat`,
   `memory.stats` and a snapshot mount/unmount round trip, and must succeed
   in both editions.

## Design: one GUI, two editions

| Data | Kernel edition | User-mode edition |
| --- | --- | --- |
| Pool and dataset properties, status | broker → `zpool`/`zfs` → `\\.\ZFS` | broker → `zpool`/`zfs` → bridge pipe (step 18) |
| Damaged files (`ZFS_IOC_ERROR_LOG`, `ZFS_IOC_OBJ_TO_PATH`) | driver | bridge, already compiled from upstream `zfs_ioctl.c` |
| Per-device counters (vdev properties) | driver | bridge |
| File/folder walk | broker walks the drive letter or mount point | the same; WinFsp mounts are ordinary volumes |
| Drive health (WMI) | Windows | Windows |
| Snapshot rollback, temporary mount (clone + mount) | driver | bridge (`ZFS_IOC_ROLLBACK`, `ZFS_IOC_CLONE`, mount ioctls → WinFsp) |
| ARC statistics | `KSTAT_IOC_READ` on `\\.\ZFS` (libkstat) | **new:** see below |

**The one gap: kstats in user mode.** In libspl, `kstat_create()` is a stub
that returns `NULL` (`lib/libspl/kstat.c`). So the engine inside
`zfswin-um.exe` never publishes `arcstats`. The fix keeps one client path
for both editions:

1. **Service:** a real in-process kstat registry for the user-mode build
   (`zum_kstat.c`). It allocates `ks_data` and calls `ks_update`, as the
   SPL does. `arc.c` is unchanged.
2. **Bridge:** the pipe answers `KSTAT_IOC_CHAIN_ID` and `KSTAT_IOC_READ`,
   next to the ZFS ioctls.
3. **Client (patch 0026):** when `\\.\ZFS` is absent, libkstat's open
   falls back to the pipe, as patch 0024 does for libzfs. `kstat.exe` and
   the broker then work unchanged in both editions.

**HTML generation** lives in `Zfs.Admin` (`DashboardHtml`): a pure function
from a status snapshot to HTML, which is unit-tested. The WinForms code only
hosts it. Charts are SVG computed in C#. Colours follow the Windows
light/dark setting.

**Packaging:** `Microsoft.Web.WebView2` adds about 0.2 MB per architecture
(the loader), in both MSIs. The runtime is not bundled.

## Verification

1. **Unit:**
   - `pool.errors` parsing: paths, `<metadata>`, snapshot objects, an
     empty list;
   - `pool.iostat` delta and rate maths, including a counter reset on
     re-import;
   - arcstats parsing;
   - the copies calculation for single, mirror, raidz and `copies=2`;
   - `DashboardHtml` output: no external URL, well-formed, and every value
     present;
   - CLI/broker parity for the new commands.
2. **Damaged-file test (both VMs):**
   - create a pool on one image file with `compression=off` and
     `checksum=fletcher4`;
   - write `victim.bin` with a known pattern, then export;
   - overwrite one occurrence of the pattern inside the `.img`, then
     import;
   - read the file: it must fail;
   - `pool.errors` must name `victim.bin`, and CKSUM > 0;
   - scrub, then clear.

   The same test on a 2-way mirror must self-heal: no damaged file, CKSUM
   > 0 on the damaged member, and the scrub repairs it.
3. **Rates (both VMs):** a 1 GiB sequential write. During it,
   `pool.iostat` deltas show > 0 write MB/s. `memory.stats` returns
   `size > 0` and `c_max > 0`, in user mode too (the new kstat bridge).
4. **Counts:** `dataset.count` on the fixture equals
   `Get-ChildItem -Recurse` (files and folders, `.zfs` excluded).
5. **Snapshots (both VMs):**
   - create `@a`, change and delete files, then create `@b`;
   - temporary mount of `@a`: the file hashes match `@a`, and a write is
     refused (read-only);
   - unmount: the clone is gone;
   - mount again, then kill the GUI and restart the broker: the leftover
     clone is removed;
   - roll back to `@a` without confirmation: refused;
   - with confirmation: `@b` is destroyed and the hashes match `@a`.
6. **Screenshots:** `--screenshots` in both suites; attach the PNGs here.
7. **Manual:** F.1, by a person, reported as such.

Commit: `gui: overview dashboard, damaged files, redundancy, live rates and snapshots (#17)`
