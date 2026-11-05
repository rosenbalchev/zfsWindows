# Step 14 — User-space administration tools and Windows setup

Status: `complete`

Prerequisites: Steps 10–13 are complete, the writable core and driver IOCTL
contract are stable, and the driver has passed isolated-VM verification. Step
15 adds the user-mode (WinFsp) variant, and step 17 documents and distributes
both.

Known issue to fix here: `zpool.exe` intermittently exits with 0xC0000005
after a successful import. The CRT's per-thread-data teardown at exit races
the detached libspl taskq worker threads, which are still exiting after
`taskq_destroy()` (step 12, dump `zpool.exe.7212.dmp`). The tools must
return reliable exit codes, so taskq workers must be joined before exit.

Objective: provide supported Windows tools for discovering and administering
ZFS pools, filesystems, and snapshots, plus a versioned setup package that can
safely install, upgrade, repair, and uninstall the driver and companion tools.

## User-space architecture

- Add a least-privilege Windows service as the single broker for privileged
  driver IOCTLs and raw-device discovery. Its local IPC protocol must be
  versioned, authenticated, bounded, cancellable, and safe against malformed
  messages.
- Add `zfswin.exe`, a scriptable CLI with stable exit codes and human-readable
  and JSON output. Cover pool discovery/import/export/status, dataset
  list/get/set/create/destroy, mount/dismount, snapshot list/create/destroy,
  scrub status/control, and mirror attach/detach/replace operations only where
  the corresponding core capability has already passed its earlier milestone.
- Add a signed PowerShell module exposing typed cmdlets over the same service
  API. Destructive commands require PowerShell `ShouldProcess`; neither CLI nor
  cmdlets may silently select a disk, create a pool, or destroy data.
- Keep diagnostic and read-only commands usable by standard users where ACLs
  permit. Restrict mutations and raw-device operations to administrators.
- Add command-level unit tests, mocked service-protocol tests, JSON schema
  compatibility tests, and isolated-VM end-to-end tests against disposable
  pools. Verify interoperability with upstream OpenZFS after every mutation.

## Setup package

- Produce x64 ARM64-aware WiX/MSI packages containing the signed driver
  (`.sys`, `.inf`, `.cat`), broker service, CLI, PowerShell module, licenses,
  symbols policy, and version metadata. Never package unsigned Release bits.
- Validate Windows version, architecture, Secure Boot/signing requirements,
  and pending-reboot state before changing the driver store. Use Windows driver
  package APIs or `pnputil` semantics; do not copy a driver directly into the
  system directory.
- Support unattended install/repair/upgrade/uninstall with documented MSI
  properties and deterministic logs. Stop and dismount safely before upgrade;
  preserve user configuration; rollback all package-owned changes on failure.
- Removal must refuse unsafe dismounts, remove only package-owned services and
  driver packages, and clearly report when a reboot is required. Pool data is
  never deleted by setup or uninstall.
- Build an optional bootstrapper for prerequisites, but keep the offline MSI
  self-contained for supported systems. Generate SHA-256 checksums and an SBOM
  for release artifacts.

## Verification and release evidence

1. Build clean Release packages from Visual Studio/MSBuild and CI.
2. Install, repair, upgrade from the previous package, and uninstall in fresh
   Windows 11 x64 and ARM64 VMs with reboot checkpoints and rollback tests.
3. Verify driver signature, service ACLs, file ACLs, PATH/PowerShell discovery,
   Event Log registration, and Add/Remove Programs metadata.
4. Exercise CLI and PowerShell read/write workflows on disposable images, then
   export and validate them with upstream OpenZFS on Linux.
5. Prove uninstall leaves pool images unchanged and removes all package-owned
   binaries, services, driver-store entries, and configuration.
6. Publish installation, command reference, recovery, diagnostics, and safe
   manual driver-removal documentation.

## Evidence (2026-11-05)

Status: `complete`, with the limitations below.

**Administration suite**
- `src/managed/Zfs.Admin` (protocol, validation, operations, JSON
  normalisers), `Zfs.Admin.Broker` (`ZfsWinBroker.exe`, service and
  `--setup` steps) and `Zfs.Admin.Cli` (`zfswin.exe`), all NativeAOT.
- `src/powershell/ZfsWin`: 26 cmdlets, with `ShouldProcess` on the
  destructive ones.
- `schemas/zfswin-output.schema.json` is the `zfswin/1` contract.
- `dotnet test tests/Zfs.Admin.Tests`: **127/127 pass**. They cover
  validation, the command line, the broker protocol over a real pipe with a
  fake runner, setup options, and **schema compatibility against real
  `zpool`/`zfs` output** captured in the VM (`tests/Zfs.Admin.Tests/samples`).

**Kernel-driver MSI**
- `installer/ZfsWin.Installer` and `scripts/build-installer.ps1`. The MSI is
  test-signed, SHA-256 checksummed and has a CycloneDX SBOM. It refuses to
  package unsigned or foreign-signed files.

**`scripts/vm/Test-InstallerInVm.ps1`: every check passes**
- Release artifacts: `OpenZFS-for-Windows-1.0.1-x64-testsigned.msi`, upgrading
  to a 1.0.65000 package, **941 s** (results.jsonl, `installer-*`, 2026-11-05
  16:2x). The previous build passed in 1072 s.
- Preflight refuses an untrusted test certificate before touching the driver
  store.
- `WIXFAILWHENDEFERRED=1` rollback of a fresh install leaves no service,
  file or driver package.
- Install: the driver runs; the broker runs as LocalSystem with restricted
  privileges, a service SID and restart-on-failure; PATH, module, event
  source, ARP and pipe are in place; the state survives a reboot.
- `zfswin` workflow on file vdevs: create (mirror), datasets, properties
  (including user properties), data, snapshots, scrub, attach, resilver,
  detach, replace, error codes, export, discover, import, data after
  reimport.
- The PowerShell module workflow passes, including `-WhatIf`.
- A standard user can read but is refused mutations (`access-denied`).
- Repair keeps the running identical driver.
- Uninstall and upgrade are refused while a pool is imported.
- A rolled-back upgrade leaves the old version working after a reboot.
- Upgrade with `ZFSWIN_EXPORT_POOLS=1` exports, upgrades and reimports.
- Uninstall removes every package-owned item.
- Pool images are byte-identical before and after uninstall.

**Linux validation**
- `scripts/vm/validate-installer.py` on 192.168.195.57 (OpenZFS 2.4.1) of
  the pool the CLI built: **PASS**. 24 current and 24 `@s1` files match;
  `compression=zstd`, `atime=off` and `com.zfswin:test` are present; the
  final mirror (a + d) is ONLINE; the scrub is clean and `zdb -bcc` reports
  no leaks.

**Bugs found and fixed by the VM runs**
- `--setup` options were keyed by value. Driver install could not find
  `--driver-dir`, and preflight skipped the certificate check.
- `zpool` takes file vdevs only as `\\?\X:\…`.
- `zpool list -j` prints nothing when no pool is imported.
- Repair reinstalled the running driver, which the zvol device vetoed until
  a reboot.
- MSI error 2613 (`RemoveExistingProducts` after a deferred action):
  upgrades now use `afterInstallExecute`, and the pool check is the first
  deferred action.
- A refused removal left OpenZFS start type Disabled, which the
  keep-identical-driver path must not accept.
- `zfsinstaller` left `openzvol.inf` staged on rollback; it is now removed
  with `pnputil /delete-driver /uninstall`.
- `msiexec /fa` drops properties; repair now uses
  `/i … REINSTALL=ALL REINSTALLMODE=amus`.
- The harness itself: argument passing, the batch-logon right for the
  standard-user task, and an empty-object check.

**`zpool.exe` exit crash (patch 0019)**
- The root cause was confirmed from dump `zpool.exe.7212.dmp`: detached
  taskq workers raced the CRT's FLS teardown.
- Patch 0019 makes `taskq_destroy()` join its workers.
- With the patched tools, every installer run since used `zpool`/`zfs`
  through the broker hundreds of times. Every exit code matched its
  expectation; a crash would have surfaced as `0xC0000005`.
- `guest-exitcodes.ps1` did not reproduce the race on demand, even with the
  old tools (110 cycles, 0 crashes). So this is evidence of no regression,
  not a reproduction of the fix.

**Documentation**
- `docs/installation.md`, `docs/zfswin-cli.md` and `docs/building.md`.

**Limitations (carried to step 17)**
- Debug builds only.
- x64 only: the ARM64 admin suite builds, but there is no ARM64 driver build
  (no ARM64 OpenSSL yet) and no ARM64 VM.
- Tested only on Windows 11 24H2 x64.
- Test-signed driver: attestation signing needs a Partner Center account.
- No CI pipeline, and no prerequisite bootstrapper (the offline MSI is
  self-contained).

Safety: package creation and static validation may run on the developer host,
but driver install/load/unload, upgrade, rollback, and uninstall tests run only
inside disposable Windows VMs. No setup test automatically targets this host.

Commit: `tools: add administration suite and signed Windows installer`
