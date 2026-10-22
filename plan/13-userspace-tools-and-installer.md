# Step 13 — User-space administration tools and Windows setup

Status: `planned`

Prerequisites: Steps 10–12 are complete, the writable core and driver IOCTL
contract are stable, and the driver has passed isolated-VM verification. This
is the final implementation step.

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

Safety: package creation and static validation may run on the developer host,
but driver install/load/unload, upgrade, rollback, and uninstall tests run only
inside disposable Windows VMs. No setup test automatically targets this host.

Commit: `tools: add administration suite and signed Windows installer`
