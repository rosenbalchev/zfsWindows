# zfswin and the ZfsWin PowerShell module

`zfswin.exe` and the `ZfsWin` PowerShell module are the supported ways to
administer OpenZFS for Windows. Both talk to the **ZfsWinBroker** service
over a local named pipe (`\\.\pipe\zfswin-broker`). The broker is the only
component that runs the OpenZFS tools (`zpool.exe`, `zfs.exe`) with
administrative rights. It checks every argument before running a tool, and
writes every change to the Application event log (source `ZfsWinBroker`).

## Who can do what

| Caller | Allowed |
| --- | --- |
| Any local, signed-in user | `pool list/status/get`, `dataset list/get`, `snapshot list`, `mount list`, `scrub status`, `version` |
| Elevated administrator | Everything above, plus every command that changes state, and `pool discover`, which reads raw disks |
| Remote (network) callers | Nothing: the pipe refuses network logons |

An administrator who has not elevated (a UAC-filtered token) is treated as a
standard user. Start the shell with **Run as administrator** for changes.

## Safety rules

- Nothing chooses a disk, pool or dataset for you. Every device is named
  explicitly, either as a fully qualified file path (`D:\pools\disk1.img`) or
  as `\\?\PhysicalDriveN`.
- Commands that overwrite devices or destroy data need `--yes` in zfswin.
  Without it they exit with code 6 and change nothing. In PowerShell these
  cmdlets have `ConfirmImpact = High`, so they prompt unless you pass
  `-Confirm:$false` or `-Force`, and they support `-WhatIf`.
- A pool's root dataset cannot be destroyed, and there is no pool-destroy
  command. To retire a pool, export it.
- Values that start with `-`, relative paths, UNC paths, wildcards and line
  breaks are rejected before any tool runs.

## Commands

```
zfswin [--json] [--timeout <seconds>] <command>
```

| Command | What it does |
| --- | --- |
| `pool list` | Imported pools, with size, allocation, capacity, fragmentation, dedup ratio and health |
| `pool status [<pool>]` | Pool state, scan (scrub or resilver) progress, the vdev tree and error counters |
| `pool get <pool> [<property>...]` | Pool properties (default: all) |
| `pool discover [--dir <dir>]...` | Pools that can be imported from disks, or from image files in `<dir>` (admin) |
| `pool import <pool\|guid> [--dir <dir>]... [--new-name <n>] [--no-mount] [--readonly] [--force]` | Import, and mount unless `--no-mount` (admin) |
| `pool export <pool> [--force]` | Unmount every dataset and export the pool (admin) |
| `pool create <pool> [--mirror] <device>... [-o p=v]... [-O p=v]... [--mountpoint <path>] --yes` | Create a single-device or mirror pool, overwriting the devices (admin) |
| `scrub status <pool>` | Scrub progress or result |
| `scrub start\|pause\|stop <pool>` | Control a scrub (admin) |
| `dataset list [<root>] [-r] [-d <depth>]` | File systems and volumes |
| `dataset get <dataset> [<property>...]` | Dataset properties (default: all) |
| `dataset set <dataset> <p>=<v>...` | Set properties, including user properties such as `com.example:owner=me` (admin) |
| `dataset create <dataset> [-o p=v]... [-p]` | Create a file system (admin) |
| `dataset destroy <dataset> [-r] --yes` | Destroy a file system, and with `-r` its children and snapshots (admin) |
| `dataset mount <dataset>` / `dataset unmount <dataset> [--force]` | Mount or unmount (admin) |
| `mount list` | Mounted datasets and their drive letters or paths |
| `snapshot list [<dataset>] [-r]` | Snapshots of a dataset, or of everything |
| `snapshot create <dataset>@<name> [-r]` | Take a snapshot (admin) |
| `snapshot destroy <dataset>@<name> [-r] --yes` | Destroy a snapshot (admin) |
| `vdev attach <pool> <device> <new-device> --yes` | Mirror `<device>` onto `<new-device>`; resilvering starts automatically (admin) |
| `vdev detach <pool> <device> --yes` | Remove one side of a mirror (admin) |
| `vdev replace <pool> <device> [<new-device>] --yes` | Replace a device and resilver onto the new one (admin) |
| `version` | Versions of zfswin, the broker protocol, and the OpenZFS userland and kernel module |

Snapshots are read-only under `<mountpoint>\.zfs\snapshot\<name>`.

## Exit codes

These codes are a stable contract.

| Code | Meaning | `error.code` in `--json` |
| --- | --- | --- |
| 0 | Success | — |
| 1 | The ZFS command failed | `command-failed` |
| 2 | Bad command line or argument | `usage`, `invalid-argument` |
| 3 | Pool, dataset, snapshot or device not found | `not-found` |
| 4 | Access denied (elevation required) | `access-denied` |
| 5 | The broker service is not running | `broker-unavailable` |
| 6 | Confirmation (`--yes`) required | `confirmation-required` |
| 7 | Timed out (`--timeout`) or cancelled (Ctrl+C) | `timeout`, `cancelled` |
| 8 | Protocol mismatch between zfswin and the broker | `protocol-mismatch`, `bad-request` |
| 9 | Another administrative command is running, or the pool is busy | `busy` |
| 10 | Internal error | `internal` |

Ctrl+C or `--timeout` closes the connection. The broker then stops the ZFS
command it was running.

## JSON output

With `--json`, zfswin prints exactly one object. The schema is
[`schemas/zfswin-output.schema.json`](../schemas/zfswin-output.schema.json),
and its name is `zfswin/1`:

```json
{ "schema": "zfswin/1", "operation": "pool.list", "ok": true,
  "result": { "pools": [ { "name": "tank", "guid": "1234…", "health": "ONLINE",
                           "size": 266287972352, "allocated": 1052672, "free": 266286919680,
                           "capacityPercent": 0, "fragmentationPercent": 0, "dedupRatio": "1.00x" } ] } }
```

```json
{ "schema": "zfswin/1", "operation": "pool.export", "ok": false,
  "error": { "code": "access-denied", "message": "'pool.export' requires an elevated administrator.", "detail": null } }
```

Sizes are bytes and times are Unix seconds. A compatible change only adds
optional properties; any other change gets a new schema name. The
compatibility tests check the schema against real `zpool`/`zfs` output that
was captured in the test VM (`tests/Zfs.Admin.Tests/samples`).

## PowerShell

The module is installed in `%ProgramFiles%\WindowsPowerShell\Modules\ZfsWin`.
Windows PowerShell 5.1 and PowerShell 7 both find it there.

| Cmdlet | zfswin equivalent |
| --- | --- |
| `Get-ZfsPool [-Name <wildcard>]` | `pool list` |
| `Get-ZfsPoolStatus [-Name]` | `pool status` |
| `Get-ZfsPoolProperty -Name [-Property]` | `pool get` |
| `Find-ZfsPool [-SearchDirectory]` | `pool discover` |
| `Import-ZfsPool -Name [-SearchDirectory] [-NewName] [-NoMount] [-ReadOnly] [-Force]` | `pool import` |
| `Export-ZfsPool -Name [-Force]` | `pool export` |
| `New-ZfsPool -Name -Device [-Mirror] [-PoolProperty @{}] [-DatasetProperty @{}] [-MountPoint]` | `pool create` |
| `Get-ZfsScrub`, `Start-ZfsScrub`, `Suspend-ZfsScrub`, `Stop-ZfsScrub` | `scrub …` |
| `Get-ZfsDataset [-Name] [-Recurse] [-Depth]` | `dataset list` |
| `Get-ZfsProperty -Name [-Property]` / `Set-ZfsProperty -Name -Property @{}` | `dataset get` / `dataset set` |
| `New-ZfsDataset -Name [-Property @{}] [-CreateParents]` / `Remove-ZfsDataset -Name [-Recurse]` | `dataset create` / `dataset destroy` |
| `Get-ZfsMount`, `Mount-ZfsDataset`, `Dismount-ZfsDataset` | `mount list`, `dataset mount` / `unmount` |
| `Get-ZfsSnapshot`, `New-ZfsSnapshot`, `Remove-ZfsSnapshot` | `snapshot …` |
| `Add-ZfsMirrorDevice`, `Remove-ZfsMirrorDevice`, `Switch-ZfsPoolDevice` | `vdev attach`, `vdev detach`, `vdev replace` |
| `Get-ZfsWinVersion` | `version` |

Errors are terminating `ErrorRecord`s. Their category comes from the zfswin
error code: `not-found` becomes `ObjectNotFound`, `access-denied` becomes
`PermissionDenied`, and so on. `$_.Exception.Data['ZfsWinErrorCode']` holds
the code itself.

```powershell
Import-Module ZfsWin
New-ZfsPool tank -Device \\?\PhysicalDrive2, \\?\PhysicalDrive3 -Mirror -DatasetProperty @{ compression = 'lz4' } -WhatIf
Get-ZfsDataset tank -Recurse | Where-Object mounted
New-ZfsSnapshot tank/home@before-upgrade
Get-ZfsSnapshot tank/home | Sort-Object creation | Select-Object -First 1 | Remove-ZfsSnapshot -Confirm:$false
```
