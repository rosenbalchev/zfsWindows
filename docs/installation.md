# Installing, upgrading and removing OpenZFS for Windows

## Choose a variant

The release in `downloads/1.0/` has two packages. Install one of them; each
refuses to install next to the other.

| | **User mode** (`…-x64-usermode.msi`) | **Kernel mode** (`…-x64-testsigned.msi`) |
| --- | --- | --- |
| Needs | [WinFsp](https://github.com/winfsp/winfsp) 2.1 or later | test signing on, Secure Boot off, `zfswindows-test.cer` trusted |
| Secure Boot | stays on | must be off |
| Our code runs as | a normal Windows service | a kernel driver |
| A bug can | stop the service (the volume disappears) | crash Windows |
| Speed | lower: every file operation crosses into user mode (see the benchmarks in plan step 15) | native |
| Pool creation, snapshots, `zfswin`/PowerShell administration | not yet: pools are created and administered elsewhere (kernel variant, Linux, FreeBSD, macOS) | yes |
| zvols, ADS, Windows ACLs | no | yes |

Both use the same OpenZFS engine and on-disk format. A pool written by
either variant imports anywhere OpenZFS 2.4 runs.

First verify the download:

```powershell
cd downloads\1.0
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

This installs `zfswin-um.exe` in `%ProgramFiles%\OpenZFS for Windows (user mode)\bin`.
It runs as the automatic **ZfsWinUserMode** service, as LocalSystem, and
restarts on failure.

## Mount a pool

Mounts are configured in the registry, one subkey per dataset, and are
mounted whenever the service starts, including at boot:

```powershell
$key = "HKLM:\SOFTWARE\OpenZFS for Windows\UserMode\Mounts\tank"
New-Item -Force $key | Out-Null
Set-ItemProperty $key Source     'D:\pools'            # image directory, or a device such as \\?\PhysicalDrive2
Set-ItemProperty $key Pool       'tank'
Set-ItemProperty $key Dataset    'tank/data'
Set-ItemProperty $key MountPoint 'Z:'                  # a drive letter, or an empty folder that does not exist yet
New-ItemProperty $key -Name ReadOnly -PropertyType DWord -Value 0 -Force | Out-Null
Restart-Service ZfsWinUserMode
```

- **Unmount:** `Stop-Service ZfsWinUserMode` unmounts every dataset, syncs and
  **exports** the pools, so another system can import them.
- **Status:** the Application event log, source `ZfsWinUserMode`, records
  every mount and unmount (with the export result) and every failure.
- **Writes and durability:** writes are durable after `FlushFileBuffers`, a
  write-through handle, or the service stopping. That is the same contract
  as any Windows volume. The power-cut test in step 15 checks it.
- **A quick look without the service:**
  `zfswin-um mount <source> <pool> <dataset> <mountpoint> [--readonly]`
  from an elevated prompt. Ctrl+C unmounts and exports.

## Not available in user mode (yet)

- Creating pools, datasets or snapshots; properties are read-only.
- Windows ACLs: every file shows the same permissions (SYSTEM and
  Administrators full control, Authenticated Users modify).
- Alternate data streams.
- `.zfs\snapshot`.
- zvols.
- `zfswin.exe` and the PowerShell module.
- Files and directories whose names are not valid UTF-8, or longer than 255
  UTF-16 characters, are not listed.
- Only the dataset named in a mount is shown; child datasets need mounts of
  their own.

## Upgrade, repair, uninstall

- **Upgrade:** run the newer MSI. Its build number is higher; see
  `VERSION.json`.
- **Uninstall:** use *Settings > Apps*, or `msiexec /x <msi> /qn`. This
  stops the service, which unmounts and exports every pool, then removes
  the program files and the service. Your mount configuration in the
  registry, WinFsp and the pools are left alone.

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
  through the Microsoft Partner Center needs none of this (plan step 16).
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

See the README's *Using ZFS volumes* section (step 16) for creating a pool on
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
