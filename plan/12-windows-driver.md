# Step 12 — Windows kernel filesystem driver

Status: `in-progress`

Objective: expose the validated core through a real WDK filesystem driver.

Implementation: kernel adapters, volume discovery, mount/dismount, IRP
dispatch, enumeration, cached reads, synchronization, metadata operations,
and eventually writes. Test only in a dedicated Windows 11 VM using Driver
Verifier and WinDbg. Never load the experimental driver automatically on the
developer host.

Exit evidence: standard .NET file APIs read validated fixture data through the
driver, and upstream tools independently validate the pool.

Commit: `driver: add isolated-VM filesystem driver milestone`


## Checkpoint: driver build, package and isolated-VM harness (not yet run)

- **Driver.** The driver is the pinned OpenZFS-on-Windows kernel driver
  (`OpenZFS.sys`, with `OpenZVOL.sys`, which `zfsinstaller` requires). It is
  built from the same submodule and patches as the step 10/11 core.
  `scripts/build-driver.ps1` configures a separate tree the way upstream CI
  does: Ninja with MSVC as the default compiler, because `driver.c` refuses
  clang; upstream's `use_clang()` still builds the userland. It packages the
  driver, `zfsinstaller`, `zpool`, `zfs`, `zdb` and `kstat` into
  `out\driver-package`. The package is test-signed with a self-signed
  certificate from the current user's store; the certificate is trusted
  only inside the VM. The build and signing succeed. Nothing is installed
  or loaded on the host.
- **VM.** `scripts/vm/New-DriverTestVm.ps1` (elevated, run once by the
  user) creates the VM:
  - Windows 11 Pro is applied straight from the ISO, with an unattended
    local admin account and PowerShell remoting;
  - an internal switch puts the host at 10.77.0.1 and the VM at 10.77.0.2,
    with no LAN access;
  - test signing and KDNET debugging to the host are set in the VM's BCD,
    and Secure Boot is off;
  - a `clean` checkpoint is taken.

  This session is not elevated and cannot manage Hyper-V, so the user has
  to run it.
- **Tests.** `scripts/vm/Test-DriverInVm.ps1` runs unelevated over
  remoting:
  - `install`: trusts the certificate in the guest, runs
    `zfsinstaller install`, enables `verifier /standard` for `OpenZFS.sys`,
    and reboots.
  - `read`: imports the genuine 5 GiB fixture read-only and runs
    `tools/Zfs.MountVerifier`, which uses standard .NET file APIs to check
    enumeration, full SHA-256 values, sampled ranges, parallel reads and
    repeated opens. It then proves the image hash is unchanged.
  - `write`: writes, appends, renames and deletes on a Linux-made fixture
    through the driver.

  `scripts/vm/validate-written.py` then has Linux OpenZFS validate the
  written pool: fixture and written hashes, scrub and `zdb -bcc`.
