# Step 15 — Documentation and the 1.0 x64/ARM64 distribution

Status: `planned`

Prerequisites:
- Steps 13 (read and write caching) and 14 (administration tools and signed installer) are complete.
- No driver bug found in the isolated VM is open without a documented workaround.

Objective: publish version 1.0 as downloadable, versioned x64 and ARM64 packages. The documentation must let a new user install the driver, create or import a pool, mount it, have it mount automatically at boot, and remove it again. It must also state honestly what is and is not supported.

## Distribution

- **Version.** `1.0.0` comes from a single version source. It is stamped into the driver INF `DriverVer`, the `.sys` and tool resources, the MSI `ProductVersion`, the PowerShell module manifest, and `zfswin --version`. The release also records:
  - the pinned upstream OpenZFS tag (`zfs-2.4.4`);
  - the openzfs-windows tag (`zfswin-2.4.1rc17`);
  - the list of repository patches applied.
- **Builds.** Clean Release builds of x64 and ARM64 from the step-14 pipeline. ARM64 is cross-compiled with the MSVC ARM64 toolset and the ARM64 OpenSSL libraries. Unsigned Release binaries are never packaged.
- **Signing.** A production release needs a Microsoft-attestation-signed (or WHQL) driver package. A test-signed build may be published only as a clearly labelled preview that requires test signing.
- **Layout.** All artifacts go under `downloads/1.0/`. Large binaries are attached to a GitHub release, and the folder holds the manifest and checksums.

  ```text
  downloads/1.0/
    zfswin-1.0.0-x64.msi
    zfswin-1.0.0-arm64.msi
    zfswin-1.0.0-x64.zip        (portable: driver package, tools, symbols)
    zfswin-1.0.0-arm64.zip
    SHA256SUMS
    zfswin-1.0.0.spdx.json      (SBOM)
    RELEASE-NOTES.md
  ```

## Minimum and tested Windows versions

- The driver catalog is built for Windows 10 version 1903 (build 18362, `10_19H1`) on x64 and ARM64, and for Windows Server 2019 (`ServerRS5_X64`). Treat that as the declared floor, not as a tested claim.
- Steps 12–14 have tested only Windows 11 Pro 24H2 (build 26100) x64 in a Hyper-V VM.
- Before 1.0, run the step-12 read, write and extended phases and the step-13 cache scenarios, in disposable VMs, on:
  - Windows 10 22H2 x64;
  - Windows 11 23H2 and 24H2 x64;
  - Windows 11 24H2 ARM64;
  - Windows Server 2022 x64.
- Publish a support table with three columns: *tested*, *expected to work*, and *unsupported* (anything older than 1903 or 32-bit x86).
- The installer refuses versions below the floor.

## README updates

- **Download and install.** Download links for `downloads/1.0/`, checksum verification, and installation:
  - interactive MSI;
  - unattended `msiexec /i … /qn /l*v`, with the documented properties;
  - the test-signing preview path: `bcdedit /set testsigning on` with Secure Boot off, and the risks.
- **Upgrade, repair and uninstall.** Uninstalling never touches pool data.
- **Create ("format") a pool.** Each command must refuse to proceed without an explicit disk.
  - Identify the disk: `Get-Disk` or `wmic diskdrive list brief`, which shows `\\.\PHYSICALDRIVEn`.
  - Create the pool: `zpool create -o ashift=12 -O casesensitivity=insensitive -O compression=lz4 -O atime=off tank PHYSICALDRIVE2`. Add `mirror` for two disks. Use `\\?\C:\pool.bin` for a file-backed pool.
  - Warn clearly that this destroys the disk's contents.
- **Mount.**
  - `zfs mount` lists datasets with their Windows paths.
  - `zfs set driveletter=Z tank` gives a drive letter; a `mountpoint` without a drive letter becomes a folder under `C:` (patch 0004).
  - `zfs unmount` and `zpool export` unmount and export.
  - Snapshots are read through `.zfs\snapshot\<name>`.
- **Automount at boot.**
  - Imported pools are recorded in the cache file.
  - Document the driver's boot-time import (`zpool set cachefile=…`) and the step-14 broker service's import-on-boot setting, and how to disable either.
  - Removable disks and a missing pool device at boot must not hang startup.
- **Linux and macOS interoperability.**
  - Pool feature flags, and keeping pools importable by upstream OpenZFS 2.4.
  - Case sensitivity.
  - ADS stored as `user.<name>:$DATA` xattrs, with Linux's 64 KiB xattr limit.
  - Windows ACLs stored as `windows:security.NTACL`.
  - The POSIX mode (0777) and gid that Windows-created files get.
- **Recovery, diagnostics and safe manual driver removal.**

## Section: ZFS vs NTFS and ReFS on Windows

Write this section for the README, with every claim either sourced or marked as this project's measurement:

- **Data integrity.**
  - ZFS checksums every block, data and metadata, and detects silent corruption.
  - With redundancy (a mirror, raidz or `copies=2`) a read or a scrub repairs it. Step 12 demonstrated this: a scrub repaired 83.4 MB of deliberately corrupted mirror data.
  - NTFS has no data checksums.
  - ReFS has metadata checksums; data integrity streams are optional, and automatic repair needs Storage Spaces mirror or parity.
- **Snapshots and rollback.**
  - ZFS snapshots are instant, read-only and space-efficient, and can be rolled back or sent with `zfs send`.
  - NTFS relies on Volume Shadow Copy.
  - ReFS has block cloning but no native snapshot/rollback workflow for general use.
- **Compression and deduplication.** Inline lz4/zstd/gzip compression and dedup are set per dataset; step 12 measured 5.29x (zstd) and 6.34x (gzip) on text and 4.00x dedup. NTFS has per-file LZNT1 compression. ReFS deduplication requires Windows Server.
- **Pooled storage.** ZFS mirrors and raidz are built in, and datasets share one pool with quotas and reservations. Windows needs Storage Spaces for the same.
- **Cross-platform pools.** The same pool can be imported by Linux, FreeBSD, macOS and Windows. Upstream OpenZFS verified every pool written in steps 10–12. NTFS is read/write on Linux only through ntfs3 or ntfs-3g. ReFS is Windows-only and its on-disk version changes between Windows releases.
- **Where NTFS or ReFS is the better choice.** State plainly:
  - the system or boot volume (ZFS cannot be the Windows boot volume);
  - BitLocker;
  - features that depend on NTFS: EFS, transactional NTFS, and some backup and antivirus products;
  - Windows Server roles that need ReFS or Storage Spaces Direct;
  - when a WHQL-certified, Microsoft-supported filesystem is required;
  - memory overhead: ARC sizing on small machines.
- **Why this driver makes sense.**
  - Data disks that need integrity and snapshots.
  - Disks shared with Linux or NAS systems.
  - Home-lab and development pools.
  - Each scenario is backed by the genuine-OpenZFS evidence in steps 05–14.

## Verification

1. Fresh x64 and ARM64 VMs follow the README steps exactly, from download to checksum, install, create, mount, reboot (automount), write, export, Linux import and validation, and uninstall. Any step that does not work as written fails this step.
2. `SHA256SUMS` matches every artifact, the signatures verify (`signtool verify /kp /v`), and the SBOM lists every bundled component and license.
3. The support table matches the VM matrix results, and the version string is identical in every artifact.
4. A documentation review against the step-12/13/14 known-limitations lists: none are omitted from the README.

## Safety

- Install, create and automount tests run only in disposable VMs.
- The documentation never suggests running `zpool create` against a disk without first identifying it, and warns before every destructive command.

Commit: `docs: 1.0 release documentation and x64/ARM64 distribution`
