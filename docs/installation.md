# Installing, upgrading and removing OpenZFS for Windows

## Choose a variant

The release in `downloads/1.0/release/` (optimized) and
`downloads/1.0/debug/` (debug checks, file names ending in `-debug`) has two
packages. Install one of them; each refuses to install next to the other.

| | **User mode** (`…-x64-usermode.msi`) | **Kernel mode** (`…-x64-testsigned.msi`) |
| --- | --- | --- |
| Needs | [WinFsp](https://github.com/winfsp/winfsp) 2.1 or later | test signing on, Secure Boot off, `zfswindows-test.cer` trusted |
| Secure Boot | stays on | must be off |
| Our code runs as | a normal Windows service | a kernel driver |
| A bug can | stop the service (the volume disappears) | crash Windows |
| Speed | lower: every file operation crosses into user mode (see the benchmarks in plan step 15) | native |
| Pool creation, snapshots, `zfswin`/PowerShell/GUI administration | yes (through the service's ZFS bridge) | yes |
| Windows ACLs, alternate data streams, `.zfs\snapshot` | yes; ACLs enforced | yes; ACLs stored but not enforced on open |
| zvols | no | yes |

Both use the same OpenZFS engine and on-disk format. A pool written by
either variant imports anywhere OpenZFS 2.4 runs. The README's *User mode
or kernel mode? Feature matrix* lists every feature and whether it is
tested, implemented or missing in each edition.

First verify the download:

```powershell
cd downloads\1.0\release      # or downloads\1.0\debug
Get-Content SHA256SUMS | ForEach-Object {
    $hash, $name = $_ -split '\s+', 2
    if ((Get-FileHash $name -Algorithm SHA256).Hash -ne $hash) { "MISMATCH $name" } else { "ok $name" }
}
```

---

# User-mode variant

## Install

1. Install WinFsp from <https://github.com/winfsp/winfsp/releases> (the
   default "Core" feature is enough). WinFsp - Windows File System Proxy,
   Copyright (C) Bill Zissimopoulos.
2. Install `OpenZFS-for-Windows-<v>-x64-usermode.msi`, either by
   double-clicking it or with
   `msiexec /i OpenZFS-for-Windows-<v>-x64-usermode.msi /qn /l*v install.log`.
   Without WinFsp, setup refuses and logs a `REFUSED:` line that names
   WinFsp. It also refuses while the kernel variant is installed.

It installs the following:

| What | Where |
| --- | --- |
| `zfswin-um.exe`: the OpenZFS engine and the WinFsp file system, as the automatic **ZfsWinUserMode** service (LocalSystem, restarts on failure, started by setup) | `%ProgramFiles%\OpenZFS for Windows (user mode)\bin` |
| The **ZfsWinBroker** service, `zfswin.exe`, `ZfsWinGui.exe`, `zpool.exe`, `zfs.exe` (the same binaries as the kernel variant) | the same `bin` folder, added to the system `PATH` (`ADDTOPATH=0` to skip) |
| The `ZfsWin` PowerShell module | `%ProgramFiles%\WindowsPowerShell\Modules\ZfsWin\<version>` |

`zpool.exe` and `zfs.exe` find no ZFS driver here. They reach the engine
through the local pipe `\\.\pipe\OpenZFS-UserMode` (SYSTEM and elevated
Administrators only), which runs upstream OpenZFS's own ioctl handlers
(plan step 18). Everything in [`zfswin-cli.md`](zfswin-cli.md) therefore
works the same way in both variants.

## Create, import and mount

```powershell
fsutil file createnew D:\pools\a.img 1073741824
fsutil file createnew D:\pools\b.img 1073741824
zfswin pool create tank --mirror D:\pools\a.img D:\pools\b.img -O compression=lz4 --yes
zfswin dataset create tank/data
zfswin mount list
```

- **Mounting:** each mounted dataset is a WinFsp volume with the file
  system name `ZFS`.
  - The pool root gets a free drive letter, or the one in its
    `driveletter` property.
  - A child dataset whose mount point would be a folder inside another ZFS
    volume gets a drive letter of its own, because WinFsp cannot mount
    inside a WinFsp volume.
  - A mount point on NTFS, such as `C:\tank\data`, works as a folder mount.
- **At boot:** pools created or imported with `zpool`/`zfswin` are recorded
  under `HKLM\SOFTWARE\OpenZFS for Windows\UserMode\Pools`, and their mounts
  under `...\UserMode\Mounts\auto-<dataset>`. The service imports and mounts
  them again when it starts, including at boot. `zfswin pool export` removes
  both records.
- **Stopping:** `Stop-Service ZfsWinUserMode` unmounts and exports every
  pool, so the disks import anywhere. Starting the service brings them back.
- **Mounting by configuration:** you can also mount a dataset of a pool the
  service does not know, from its images or disk, with a key under
  `...\UserMode\Mounts\<name>`:
  - `Source`: an image folder, an image file, or a device; `;` separates
    several;
  - `Pool`, `Dataset`, `MountPoint`;
  - `ReadOnly` (1 opens the pool read-only; the image stays byte-identical).

  The GUI's **User mode** tab edits these keys. For a pool the service
  imports anyway, `Source` may be empty.
- **Status:** the Application event log has two sources:
  - `WinFsp` for mounts, unmounts, imports and engine errors;
  - `ZfsWinBroker` for every administrative command.

## Files

- **Windows ACLs:** stored in the pool as the self-relative security
  descriptor in the xattr `security.NTACL`, exactly where and how the kernel
  driver stores it.
  - WinFsp enforces them.
  - Files without one (written on Linux or before 1.0.5) get the descriptor
    the kernel driver would derive: inherited from the parent, with the
    driver's default at the dataset root.
- **Alternate data streams:** `file:stream` is stored as the file
  `stream:$DATA` in the file's xattr directory, as in the kernel driver.
- **Snapshots:** browsable read-only under `.zfs\snapshot\<name>`;
  `snapdir=visible` lists `.zfs` in the root.
- **Block cloning:** `zfswin clone <source> <target>` shares blocks instead
  of copying them.
  - WinFsp passes only custom control codes to a file system, so Windows'
    `FSCTL_DUPLICATE_EXTENTS_TO_FILE` (Explorer, `copy`) cannot reach this
    variant.
  - `zfswin clone` uses that FSCTL on the kernel variant, and this
    variant's own control code here.
- **Durability:** writes are durable after `FlushFileBuffers`, a
  write-through handle, or the service stopping. That is the same contract
  as any Windows volume, and the power-cut test checks it.
- **Not available:**
  - zvols;
  - `zfs send`/`receive` and `zpool events`: they hand file descriptors to
    the engine, which a pipe cannot carry;
  - file and directory names that are not valid UTF-8, or longer than 255
    UTF-16 characters, are not listed.

## A quick look without the service

`zfswin-um mount <source> <pool> <dataset> <mountpoint> [--readonly]` from
an elevated prompt, with the service stopped. Ctrl+C unmounts and exports.

## Upgrade, repair, uninstall

- **Upgrade:** run the newer MSI. Its build number is higher; see
  `VERSION.json`.
- **Uninstall:** use *Settings > Apps*, or `msiexec /x <msi> /qn`.
  - Uninstall stops both services, which unmounts and exports every pool,
    then removes the program files and the services.
  - It keeps the pool and mount records in the registry, WinFsp, and the
    pools.
---

# Kernel-mode variant

The setup package is a single offline MSI:

```
OpenZFS-for-Windows-<version>-<arch>-testsigned.msi
```

It installs the following:

| What | Where |
| --- | --- |
| The OpenZFS driver package (`OpenZFS.sys/.inf/.cat`, `OpenZVOL.sys/.inf/.cat`) | Staged in the Windows driver store; a copy is kept in `%ProgramFiles%\OpenZFS for Windows\driver` |
| `zfswin.exe`, `ZfsWinBroker.exe`, `zpool.exe`, `zfs.exe`, `zdb.exe`, `kstat.exe`, `zfsinstaller.exe` | `%ProgramFiles%\OpenZFS for Windows\bin`, added to the system `PATH` |
| The **ZfsWinBroker** service (automatic start, LocalSystem, restricted privileges, restarts on failure) | Service Control Manager |
| The `ZfsWin` PowerShell module | `%ProgramFiles%\WindowsPowerShell\Modules\ZfsWin\<version>` |
| Licenses, `VERSION.json` and symbols (PDBs, for crash-dump analysis) | `licenses`, `symbols` under the install folder |
| The event source `ZfsWinBroker` | Application log |

Setup never deletes, formats or rewrites pool data. That includes install,
repair, upgrade, uninstall and rollback.

## Requirements

Setup checks all of these before it touches the driver store. If one fails,
the log contains a `REFUSED:` line.

- Windows 10 version 1903 (build 18362) or later, or Windows Server 2019
  (build 17763) or later. The CPU architecture must match the package (x64 or
  ARM64).
- No reboot pending from servicing or Windows Update. To override, set
  `ZFSWIN_ALLOW_PENDING_REBOOT=1`.
- **Test-signed packages** (`-testsigned.msi`) have two more requirements:
  - test signing must be on (`bcdedit /set testsigning on`, then restart). That
    in turn requires Secure Boot to be off.
  - `zfswindows-test.cer` must be in the local machine's **Trusted Root
    Certification Authorities** and **Trusted Publishers** stores.

  Use test-signed packages only on test machines. A release package signed
  through the Microsoft Partner Center needs none of this (plan step 17).
- No OpenZFS driver that this package did not install may be present. Remove
  an existing one first (see the last section).

## Unattended use

```
msiexec /i OpenZFS-for-Windows-<v>-x64.msi /qn /norestart /l*v install.log [PROPERTY=value ...]
```

| Property | Default | Effect |
| --- | --- | --- |
| `INSTALLFOLDER` | `%ProgramFiles%\OpenZFS for Windows` | Install location |
| `ADDTOPATH` | `1` | Set `0` to leave the system `PATH` unchanged |
| `ZFSWIN_EXPORT_POOLS` | unset | `1`: on upgrade, repair or uninstall, export imported pools. Without it, setup refuses. Export is never forced; a busy pool still stops setup. |
| `ZFSWIN_ALLOW_PENDING_REBOOT` | unset | `1`: install even though a reboot is pending |
| `WIXFAILWHENDEFERRED` | unset | `1`: fail on purpose at the end of the install, to test rollback |

| Exit code | Meaning |
| --- | --- |
| 0 | Success |
| 3010 | Success; restart required (uninstall, and upgrades that replace a loaded driver) |
| 1603 | Failed or refused; everything this run changed was rolled back. Search the log for `REFUSED:` |
| 1618 | Another installation is running |

The log lists every setup step. Search it for `WixQuietExec`: each step's
output appears there, including `zfsinstaller` and the pool check.

- **Repair:** `msiexec /i <msi> REINSTALL=ALL REINSTALLMODE=amus /qn ZFSWIN_EXPORT_POOLS=1`.
  (`msiexec /fa` ignores property assignments, so it cannot pass
  `ZFSWIN_EXPORT_POOLS`.) Repair restores the files and the service
  configuration. A running driver of the same build is kept.
- **Upgrade:** run the newer MSI. The pools are checked first. They are
  exported (with `ZFSWIN_EXPORT_POOLS=1`), or setup refuses, before any file
  changes. The new version is then installed and the old one removed, in one
  transaction, so a failure restores the old version. A driver of the same
  build is kept running. A new driver build replaces the old one and takes
  effect after a restart. Then import your pools again.
- **Uninstall:** use *Settings > Apps*, or
  `msiexec /x <msi or product code> /qn ZFSWIN_EXPORT_POOLS=1`. Setup refuses
  while pools are imported. It removes only what it installed: its files,
  services, driver packages, the `PATH` entry, the module and its registry
  key. Pool disks and image files are left alone. A restart finishes
  unloading the driver.

## Formatting, importing and automount

See the README's *Using ZFS volumes* section (step 17) for creating a pool on
a disk, mounting it at a drive letter or folder, and having it mount at boot.

## Diagnostics

- `zfswin version` shows the versions of zfswin, the broker and the kernel
  module. `sc query OpenZFS` and `sc query ZfsWinBroker` show the two
  services.
- **Event Viewer > Windows Logs > Application**, source `ZfsWinBroker`:
  - service start and stop: event IDs 1 and 2;
  - every completed administrative command, with the user, the arguments and
    the duration: event 100;
  - every refused or failed one: event 101.
- `ZfsWinBroker.exe --console`, from an elevated prompt with the service
  stopped, runs the broker in the foreground and prints each command.
- Driver installation: `%WINDIR%\INF\setupapi.dev.log`. Driver crashes:
  `%WINDIR%\MEMORY.DMP`, analysed with the PDBs in the `symbols` folder.

## Recovery

- **zfswin reports `broker-unavailable` (exit 5):** start the service with
  `net start ZfsWinBroker`. It restarts automatically twice a day at most after
  a failure. In the meantime, an elevated `zpool.exe` and `zfs.exe` in the
  `bin` folder work directly.
- **Setup refused:** fix what the `REFUSED:` line says and run setup again.
  Nothing was changed.
- **A failed upgrade:** the old version is restored. Restart if the log asks
  you to, then import your pools again.
- **The driver does not start after an update:** boot into Safe Mode, and run
  `sc config OpenZFS start= disabled`. Restart, then repair or uninstall.

## Removing the driver by hand

Use this only when setup cannot run, for example after a damaged
installation:

1. Export every pool: `zpool export -a`, from an elevated prompt.
2. From the `bin` folder, run
   `zfsinstaller.exe uninstall ..\driver\OpenZFS.inf ..\driver\OpenZVOL.inf`.
3. Check that no OpenZFS package is left with
   `pnputil /enum-drivers | findstr /i "openzfs openzvol"`. Remove any that is
   left with `pnputil /delete-driver oemNN.inf /uninstall /force`.
4. Remove the service with `sc delete ZfsWinBroker`, then restart.
5. Delete `%ProgramFiles%\OpenZFS for Windows` and the `ZfsWin` module
   folder. Windows Installer still lists the product; remove the entry with
   `msiexec /x {product-code}`, or reinstall and uninstall.

Never delete `OpenZFS.sys` from `System32\drivers` while the service exists.
The pools themselves are untouched by all of this. Any OpenZFS
implementation of the same or a newer version can import them.
