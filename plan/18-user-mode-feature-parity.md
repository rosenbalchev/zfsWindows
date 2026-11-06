# Step 18 — User-mode feature parity

Status: `in-progress`

Prerequisites:
- Step 15: the user-mode edition (libzpool engine, portable ZPL layer
  `src/native/ZfsUserMode`, WinFsp front end `zfswin-um.exe`).
- Step 14: the broker, `zfswin.exe`, the PowerShell module.
- Step 16: the GUI.

Objective: close the ❌ rows of the README's *User mode or kernel mode?*
matrix, in the user-mode edition, without rewriting what OpenZFS already
has:

| Area | Feature |
| --- | --- |
| Filesystem | Windows ACLs stored on the pool, in the kernel driver's format |
| Filesystem | Alternate data streams, in the kernel driver's format |
| Filesystem | Snapshots browsable in `.zfs\snapshot` |
| Filesystem | Zero-copy block cloning (`FSCTL_DUPLICATE_EXTENTS_TO_FILE`) |
| Administration | Create pools and datasets; change properties |
| Administration | Create, destroy and roll back snapshots |
| Administration | Scrub; add and remove mirror disks; replace a disk |
| Administration | Add and remove read cache (L2ARC) and write log (SLOG) |
| Administration | `zfswin` CLI, PowerShell module, GUI pool administration |
| Images | Pools on image files (`.img`): create, import, mount one by file name |

## Design

### Administration: the ioctl bridge

Every admin tool ends in `zpool.exe`/`zfs.exe`, which end in libzfs, which
sends ZFS ioctls (`zfs_cmd_t` plus nvlist buffers) through one function,
`lzc_ioctl_fd_os()`. In the kernel edition the driver's `zfs_ioctl.c`
answers them. In user mode the same engine runs inside the
`ZfsWinUserMode` service, so the bridge carries the same ioctls there:

- **Server:** `zfswin-um.exe` compiles upstream `module/zfs/zfs_ioctl.c`
  unchanged. A forced-include shim (`zum_ioctl_shim.h`) replaces the kernel
  VFS headers. `zum_ioctl_um.c` supplies the kernel entry points:
  - zfsvfs lookups report "not mounted by the kernel" (ESRCH), which is the
    truth in user mode;
  - zvols are not supported;
  - `copyin`/`copyout` are memory copies;
  - `zfs_create_fs` is ported into the portable layer.

  A named pipe `\\.\pipe\OpenZFS-UserMode` (SYSTEM and Administrators only,
  remote clients rejected) receives each `zfs_cmd_t` with its buffers and
  runs `zfsdev_ioctl_common()`.
- **Mount ioctls:** the Windows-only `ZFS_IOC_MOUNT`/`ZFS_IOC_UNMOUNT`
  become WinFsp mounts and unmounts. Each mount is recorded under the
  registry `Mounts` key, so it comes back at boot.
- **Client (patch 0024):** when `\\.\ZFS` (the driver) is absent, libspl's
  `open(ZFS_DEV)` opens the pipe instead. `lzc_ioctl_fd_os()` sends
  `zfs_cmd_t` over the pipe when its handle is a pipe. `getfsstat()` adds
  the bridge's mount table. The same `zpool.exe`/`zfs.exe` binaries serve
  both editions.
- **Pool lifetime:** the service owns the engine (`kernel_init()` once) and
  a pool cache file under `%ProgramData%`. Pools are imported or created by
  ioctl, or by a configured mount. Service stop unmounts and exports
  everything, as before.

### Filesystem features (portable layer, both editions' format)

- **xattrs:** `zum_xattr.c` reads and writes SA xattrs (`ZPL_DXATTR`) and
  xattr directories, with the kernel driver's naming rules:
  - the `windows:` prefix;
  - lower-case SA names on case-insensitive datasets;
  - SA first, falling back to the directory.

  Files with an xattr directory are now reclaimed with it, on delete and on
  rename over them.
- **ACLs:** the self-relative security descriptor is the xattr
  `windows:security.NTACL`, exactly as `zfs_save_ntsecurity()` writes it.
  - WinFsp gets `PersistentAcls` and checks access with it.
  - A new file's descriptor is derived from its parent's
    (`FspCreateSecurityDescriptor`).
  - Objects without the xattr (written on Linux, or before this step) get
    the kernel driver's default.
- **Streams:** `file:name` is the file `name:$DATA` in the xattr
  directory, as the kernel driver opens it. Streams are listed in
  `FileStreamInformation`.
- **`.zfs\snapshot`:** each snapshot is opened read-only on first access
  and closed with its last handle. The `snapdir` property decides whether
  `.zfs` is listed in the root.
- **Block cloning:** `FSCTL_DUPLICATE_EXTENTS_TO_FILE` reaches the service
  through WinFsp's `Control`. The source handle is duplicated from the
  calling process and identified by file index and volume serial.
  `dmu_read_l0_bps()`/`dmu_brt_clone()` share the blocks, as
  `zfs_clone_range()` does.

## Verification

1. **Unit:** build and the existing 138 tests; the shim compiles upstream
   `zfs_ioctl.c` with no source change.
2. **User-mode VM (`Test-UserModeInVm.ps1`, new phases):**
   - `admin`:
     - `zpool create` on `.img` files;
     - `zfs create` with mount to a drive letter;
     - properties (compression, dedup);
     - snapshot, rollback and destroy;
     - scrub;
     - mirror attach/detach and replace;
     - cache/log add/remove;
     - `zfswin` and PowerShell;
     - GUI self-check against the broker;
     - remount after a service restart.
   - `acl`: `icacls` grant/deny, enforced for a standard user, and kept
     across remount.
   - `streams`: write, list, read and delete, kept across remount.
   - `snapdir`: snapshot contents match the hashes at snapshot time.
   - `clone`: the 8 MiB clone is identical and `bcloneused` rises.
   - The existing phases still pass.
3. **Kernel VM (`Test-InstallerInVm.ps1`):** the full suite again, with the
   same binaries.
4. **Cross-edition:** a pool written in user mode (ACLs, streams) is read
   back by the kernel driver: same security descriptor (SDDL) and stream
   contents. Linux OpenZFS validates both pools (scrub, file hashes).
5. **README:** the feature matrix shows only what the tests above proved.

## Evidence

(pending)

Commit: `usermode: ACLs, streams, .zfs, block cloning and administration bridge`
