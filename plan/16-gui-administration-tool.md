# Step 16 — GUI administration tool

Status: `in-progress`

Prerequisites:
- Step 14: the broker, the `zfswin/1` protocol and `zfswin.exe`.
- Step 15: the user-mode edition, its service and its mount configuration.
- Step 17 (documentation and 1.0 distribution) ships this tool in both
  packages.

Objective: a Windows GUI, `ZfsWinGui.exe`, that runs elevated and administers
ZFS without the command line:
- **Status:** pools, health, capacity, fragmentation, dedup ratio, the full
  vdev tree including log, cache, spare, special and dedup devices; scrub
  state and control.
- **Features per dataset:** compression, deduplication and sync, plus every
  property with its source.
- **Mirrors:** add a mirror member to a disk (attach, then resilver); remove
  one (detach).
- **Caches:** add and remove read cache (L2ARC) and write log (SLOG)
  devices. A log can be mirrored.
- **User-mode edition:** the `ZfsWinUserMode` service (start, stop, restart)
  and its mounts (add, edit, remove).

## Design

- **No new privileged path.** The GUI talks to `ZfsWinBroker` like
  `zfswin.exe` does. Every change is validated by the broker and written to
  its event log. Destructive operations require an explicit confirmation
  naming the devices, and only then pass `confirm: true`. The manifest
  requires elevation, because the broker accepts changes only from an
  elevated administrator.
- **New broker operations, also in `zfswin`:** `vdev.add` (`role` cache|log,
  `mirror` for logs) and `vdev.remove`. Data vdevs cannot be added this way:
  a mistaken top-level data vdev cannot always be removed again. `pool.status`
  gains `logs`, `caches`, `spares`, `special` and `dedup` (optional
  properties; `zfswin/1` stays compatible).
- **Non-visual logic** lives in `Zfs.Admin` and is unit-tested:
  - `AdminClient`: the typed client;
  - `AdminClient.DeviceArgument`: turns the vdev paths `zpool` reports
    back into broker arguments;
  - `UserModeMounts`: the registry configuration and its validation.

  The WinForms code only lays out and calls.
- **Device picker:** image files, or whole disks from the Storage WMI
  provider (`MSFT_Disk`). The Windows boot and system disks are never
  offered.
- **Framework-dependent single file:** about 0.8 MB, needing the .NET 10
  Desktop Runtime. WinForms cannot be trimmed or NativeAOT-compiled, and a
  self-contained copy would add about 60 MB per package, pushing the kernel
  MSIs past GitHub's 100 MB limit. Targeting plain `net10.0-windows` avoids
  the 23 MB WinRT projection that a versioned Windows TFM pulls in.
- **Packaging:** both MSIs install it in `bin` with Start-menu shortcuts (the
  tool and the getting-started page).
- **The user-mode edition** has no broker. There the GUI explains that pool
  changes need the kernel edition, and enables the User mode tab.

## Verification

1. **Unit tests:** `vdev.add`/`vdev.remove` argument building and refusals,
   CLI parity, status parsing of log and cache classes, `DeviceArgument`,
   `AdminClient` confirmation handling, and the `UserModeMounts` round trip
   and validation (HKCU test key).
2. **Kernel VM (`Test-InstallerInVm.ps1`):**
   - the CLI workflow adds a write log and a read cache, sees them in
     `pool status`, removes both, and sets `dedup=on`;
   - `ZfsWinGui.exe --self-check` runs the GUI's own queries against the
     installed broker and must see the pool and `zwin/docs` with dedup on;
   - the Start-menu shortcuts exist.
3. **User-mode VM (`Test-UserModeInVm.ps1`):**
   - the GUI self-check reports no broker (expected) and the service state;
   - the shortcuts exist;
   - an interactive (`/qb`) install runs the getting-started page action.
4. **Manual:** the windows themselves need a desktop session, which the
   automated VM tests (PowerShell Direct) do not have. Anything not clicked
   through by a person is reported as such.

## Evidence (2026-11-05, release 1.0.4)

Status: `in-progress`. Every automated check passes. Two manual checks
remain: clicking through the windows, and the getting-started page opening
on a desktop. Both need a desktop session, which the automated VM tests do
not have.

**Unit and protocol tests: 138/138 pass.**
- `CacheDeviceTests`: `vdev.add`/`vdev.remove` arguments, the refusal of
  unconfirmed, data-role, single-device-mirror and missing-device requests
  before any tool runs, the CLI mapping, and status parsing of the log and
  cache classes.
- `GuiBackendTests`: `DeviceArgument` for every path form `zpool` reports,
  the status tree with every device class, confirmation handling, and the
  `UserModeMounts` round trip and validation (HKCU test key).
- CLI/broker parity now covers the three new commands.

**Kernel VM (`Test-InstallerInVm.ps1`, `OpenZFS-for-Windows-1.0.4-x64-testsigned.msi`,
VM `zfsTest`): all checks pass, 1122 s.** The 1.0.3 run passed in 1281 s.
- CLI workflow:
  - `vdev add-log` without `--yes` is refused (exit 6);
  - write log and read cache added and listed (1/1), removed (0/0);
  - `dedup=on` set and read back;
  - **block cloning:** `FSCTL_DUPLICATE_EXTENTS_TO_FILE` returns 0, the
    8 MiB clone is byte-identical, and `bcloneused` goes from 0 to
    8,388,608;
  - 26 files after remount.
- `ZfsWinGui.exe --self-check` (.NET 10.0.14 Desktop Runtime) reaches
  broker 1.0.4 and sees `zwin` ONLINE, `zwin/docs` with zstd and dedup
  on. Exit 0.
- Start menu: "OpenZFS for Windows" and "OpenZFS for Windows - Getting
  started".
- Linux (`validate-installer.py`, OpenZFS 2.4.1): 26 current and 24 `@s1`
  files match; **`bcloneused=8388608`** (the clone's blocks are shared); the
  scrub is clean and there are no leaks.

**User-mode VM (`Test-UserModeInVm.ps1`, `…-1.0.4-x64-usermode.msi`):
every phase passes.**

| Phase | Result | Time |
| --- | --- | --- |
| setup | Secure Boot on, test signing off; GUI self-check exit 0 (reports "broker-unavailable", as expected, and service state); both Start-menu shortcuts | 1615 s |
| read | 5 GiB fixture exact | 122 s |
| write | passes | 137 s |
| bench | completed | 48 s |
| powercut | 301/301 flushed and 307/307 write-through files intact | 119 s |
| uninstall | passes | 104 s |

Linux validates the pool written in user mode.

**Getting-started page.**
- The first attempt put `OpenReadMe` in the execute sequence with
  `UILevel > 2`. On the server side, `UILevel` is always 2, so the action
  never ran.
- It now runs from the client-side `InstallUISequence`, right after
  `ExecuteAction`.
- The MSI tables of all four 1.0.4 packages were checked on the host:
  - UI sequence 1301 with `NOT Installed AND NOT REMOVE`;
  - absent from the execute sequence;
  - `WixShellExec` with the right per-architecture helper;
  - target `[#README.html]`.
- PowerShell Direct has no desktop, so Windows Installer downgrades `/qr` to
  basic UI there and never runs the UI sequence. Seeing the page open needs
  one interactive install by a person.

**Remaining**
- Manual: open the GUI on a desktop and use each tab against a disposable
  pool. Install a package by double-click and confirm the getting-started
  page opens.
- The GUI's disk picker (`MSFT_Disk`) and physical-disk vdev paths have not
  been exercised: the VM tests use image files only.

Commit: `gui: add ZfsWinGui administration tool, cache and log devices`
