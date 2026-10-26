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

## Checkpoint: driver reads and writes genuine pools in the VM (Linux validation pending)

The VM (`scripts/vm/New-DriverTestVm.ps1`, `Complete-DriverTestVm.ps1`) runs
Windows 11 Pro 26100 with test signing, KDNET configuration and Secure Boot
off, on internal switch 10.77.0.0/24. `Test-DriverInVm.ps1` uses these
phases:

- `prepare` takes the checkpoint `fixtures`, with the fixtures already
  copied in.
- `install` always reverts to `fixtures`, installs the driver with
  `zfsinstaller`, enables `verifier /standard` for `OpenZFS.sys`, and
  asserts that the service is RUNNING after the reboot.
- `read` and `write` follow.

Driving the driver with genuine Linux-created pools under Driver Verifier
found six upstream-candidate bugs. They are fixed as patches 0004–0009 and
documented in `patches/openzfs-windows/README.md`:

| Patch | Symptom | Cause |
|---|---|---|
| 0004 | Linux pool mounted at `\??\-:\var\tmp\...` | parent with no drive letter used the placeholder `-`, and parent directories were never created |
| 0005 | bugcheck 0x00FFFFFF in `zilog_dirty()` | directory listing on a read-only pool saved an NT security descriptor (`zfs_is_readonly()` ignored `spa_writeable()`) |
| 0006 | bugcheck "mutex not initialised" at export | `mmp_signal_all_threads()` raced pool export (any registry change re-applies tunables on Windows) |
| 0007 | read-only pool: export "pool is busy" forever | `zfs_inactive()` atime transaction dirtied the dataset; read-only pools never sync |
| 0008 | export hung in `spa_evicting_os_wait()` after writes | single-round unmount left Cache-Manager-held znodes owning SA/dnode holds |
| 0009 | bugcheck `rrw_enter_read_impl` VERIFY at unmount | `zil_close()` → `zfs_get_data()` → `zfs_zget()` → security attach re-entered the teardown lock |

Debugging method: guest crash dumps were written by bugchecks or by
`Debug-VM -InjectNonMaskableInterrupt` (with `NMICrashDump=1`) and analyzed
offline with `kd -z` and `scripts/vm/spa-refs.js`. That script walks the
pool list, `spa_refcount` holders (with `reference_tracking_enable=1`), any
`zfs_refcount_t`, and objsets on `spa_evicting_os_list` with their remaining
dnodes. A live KDNET session from the user's host coincided with a host
crash and is no longer used.

Results:

- **Read** (2026-10-23): the genuine 5 GiB single-disk fixture was imported
  read-only. `Zfs.MountVerifier`, using standard .NET file APIs, checked 18
  files and 33 entries, 8.9 GB in 47.3 s: full SHA-256, sampled ranges,
  parallel reads and repeated opens. There were 0 failures, export
  succeeded on the first attempt, the image SHA-256 was unchanged, and there
  were 0 bugchecks.
- **Write** (2026-10-26): the Linux-created lz4 fixture was imported
  read-write. 4 files (64 MiB) verified with 0 failures. Through the driver
  the verifier wrote a 3 MiB file, appended twice, renamed one file,
  deleted another and created nested directories; the read-back hashes
  match. Export succeeded on the first attempt, and the image is in
  `TestVolumes/vm/written/`.
- **Pending:** upstream Linux validation of the written image
  (`scripts/vm/validate-written.py`), and the step's exit evidence.

There were two unexplained host incidents during VM work: a bugcheck 0x10E
and a later hang with no dump. They are recorded so that host stability is
watched in later runs.
