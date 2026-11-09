# Building OpenZFS for Windows

This repository builds two installable variants from one source tree:

| Variant | Package | How it reaches the file system |
| --- | --- | --- |
| **Kernel mode** | `OpenZFS-for-Windows-<v>-x64-testsigned.msi` | The OpenZFS kernel driver (`OpenZFS.sys`, `OpenZVOL.sys`), administered through the `ZfsWinBroker` service, `zfswin.exe` and the `ZfsWin` PowerShell module |
| **User mode** | `OpenZFS-for-Windows-<v>-x64-usermode.msi` | `zfswin-um.exe`, a service that runs upstream OpenZFS (`libzpool`) in user mode and serves datasets as Windows volumes through [WinFsp](https://github.com/winfsp/winfsp) (WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos) |

Both variants use the same OpenZFS engine and on-disk formats, so a pool
written by one can be imported by the other, by Linux, FreeBSD or macOS. The
user-mode ZPL layer (`src/native/ZfsUserMode`) is written only against
OpenZFS kernel interfaces. The same source can therefore also be built into
the kernel module.

Building never installs anything on the build machine. Driver and installer
tests run only in disposable Hyper-V VMs (`scripts/vm`).

## Versions

The version is `<prefix>.<build number>`, for example `1.0.7`:

- The prefix is in `build/ZfsWinVersion.props`.
- The build number is in `build/BUILD_NUMBER`.
- `scripts/build-release.ps1` increments the build number once per release
  build. It stamps the same version on everything: `zfswin.exe`,
  `ZfsWinBroker.exe`, the PowerShell module, `zfswin-um.exe` (its version
  resource and `--version`), both MSIs, `VERSION.json` and the SBOM.
- Individual build scripts reuse the current number.
- `scripts/Get-ZfsWinVersion.ps1` prints the current version.

Windows Installer compares the first three fields. A new build number is
therefore a newer product, and installing it upgrades the previous build in
place.

## Prerequisites

| Tool | Used for | Notes |
| --- | --- | --- |
| Windows 10/11 x64 | everything | the scripts are Windows PowerShell 5.1 |
| Visual Studio 2026 (18) with MSVC 14.5x, x64 and ARM64 C++ tools, CMake and Ninja | native code | the scripts find it with `vswhere` |
| Windows SDK and Windows Driver Kit (same version) | kernel driver, `signtool`, `inf2cat` | |
| LLVM `clang-cl` in `C:\Program Files\LLVM` | OpenZFS userland, `libzpool`, `zfswin-um` | upstream builds userland with clang-cl |
| OpenSSL 3 static libraries in `C:\Program Files\OpenSSL-Win64` (`lib\VC\x64\MTd`) | x64 `zpool`/`zfs`/`zdb`, `libzpool` | the Shining Light "Win64 OpenSSL" developer package |
| ARM64 OpenSSL in `out\vcpkg-openssl` | ARM64 builds | built with the vcpkg that ships with Visual Studio; see *ARM64* below |
| .NET 10 SDK | administration suite (NativeAOT), WiX builds | WiX 5.0.2 comes from NuGet |
| Python 3.12+ | upstream-validation tests | |
| git | submodules | `git submodule update --init --recursive` |

The WinFsp SDK is not installed. `scripts/fetch-winfsp.ps1` downloads the
pinned, Microsoft-signed WinFsp 2.1 MSI and checks its SHA-256 and signature.
It then unpacks the headers and import libraries with `msiexec /a`, which only
copies files and installs no driver.

## One-command release build

```powershell
git submodule update --init --recursive
powershell -ExecutionPolicy Bypass -File scripts\fetch-winfsp.ps1
powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1 -BuildDriver   # increments the build number
```

`build-release.ps1` builds every package twice, Release and Debug, into one
self-contained folder, `out\release\1.0.N\`:

```text
OpenZFS-for-Windows-1.0.N-x64-testsigned.msi   kernel-driver variant (Release: optimized native code)
OpenZFS-for-Windows-1.0.N-x64-usermode.msi     user-mode (WinFsp) variant
OpenZFS-for-Windows-1.0.N-x64.cdx.json         CycloneDX SBOM
OpenZFS-for-Windows-1.0.N-x64-*-debug.msi, -debug.cdx.json   the same, Debug
OpenZFS-for-Windows-1.0.N-arm64-*.msi, .cdx.json   the same for ARM64 (cross-built)
zfswindows-test.cer                            the test-signing certificate
VERSION.json                                   version, configurations, commit, OpenZFS base
SHA256SUMS                                     checksums of the above
```

Debug packages are built unoptimized with full debug checks, and their
names end in `-debug`, such as
`OpenZFS-for-Windows-1.0.N-x64-usermode-debug.msi`.

Packages are never committed (`downloads/` is ignored). CI builds the same
folder on every run (`.github/workflows/ci.yml`): a pull request builds
Debug only and keeps the packages as workflow artifacts; a push to `master`
builds Release and Debug and publishes them as the GitHub release
`v1.0.N`.

The native trees are built per configuration:

| Configuration | Driver build | Driver package | User-mode engine |
| --- | --- | --- | --- |
| Debug | `out\openzfs-driver-debug` | `out\driver-package` | `out\openzfs-write-debug` |
| Release | `out\openzfs-driver-release` | `out\driver-package-release` | `out\openzfs-write-release` |

The .NET tools are Release builds in both.

The user-mode engine keeps OpenZFS's own consistency checks (`ZFS_DEBUG`) in
Release too, as upstream libzpool always does. Only the compiler
optimization differs.

Options:

- `-BuildDriver` rebuilds the driver trees first.
- `-NoIncrement` rebuilds without changing the version.
- `-Configuration Release` or `-Configuration Debug` builds only one; the
  default is both.
- `build-installer.ps1 -Configuration` and the VM suites'
  `-Configuration` (the default is Debug) select the build to package or
  test.

## ARM64

ARM64 packages are cross-compiled on the x64 machine:
- the kernel driver with the MSVC ARM64 compiler and the WDK;
- userland and `zfswin-um` with `clang-cl --target=aarch64-pc-windows-msvc`;
- the administration suite with NativeAOT.

First build OpenSSL for ARM64 once. It goes into `out\` and installs nothing
system-wide:

```powershell
$vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -property installationPath
mkdir out\vcpkg-openssl; cd out\vcpkg-openssl
'{ "name": "zfswin-openssl", "version": "1.0.0", "dependencies": [ "openssl" ], "builtin-baseline": "29718cfec805208b4e2d80ed91e681143b488dd7" }' | Set-Content vcpkg.json
& "$vs\VC\vcpkg\vcpkg.exe" install --triplet arm64-windows-static --x-install-root=$PWD\installed    # about 11 minutes
cd ..\..
powershell -File scripts\build-driver.ps1 -Architecture arm64            # out\driver-package-arm64
powershell -File scripts\build-release.ps1                               # x64 and arm64 by default
```

Patches 0020–0023 make upstream's build cross-compile from x64:
- OpenSSL paths can be overridden;
- `tracewpp`, `signtool` and `stampinf` are chosen by the *host*
  architecture;
- the ARM64 Lua `setjmp` buffer type is fixed;
- the ARM64 exception-context registers are used.

The ARM64 packages are checked statically: machine type and signature of
every binary, and the MSI contents. They are also run in a Windows 11
ARM64 Hyper-V VM on a separate ARM PC (`scripts\vm\arm\`, results in plan
step 19).

## Building the parts

| Script | Output | What it does |
| --- | --- | --- |
| `scripts\apply-openzfs-patches.ps1` | `third_party\openzfs-windows` | applies `patches\openzfs-windows\*.patch` (the other scripts call it) |
| `scripts\build-driver.ps1` | `out\driver-package` | builds `OpenZFS.sys`, `OpenZVOL.sys`, `zpool`, `zfs`, `zdb`, `kstat` and `zfsinstaller`, then test-signs the package with `CN=zfsWindows Test Signing`. It creates that certificate in your user store the first time. |
| `scripts\build-admin.ps1 [-Architecture x64,arm64]` | `out\admin-<arch>` | runs the administration-suite tests, then publishes NativeAOT `zfswin.exe` and `ZfsWinBroker.exe` |
| `scripts\build-write-core.ps1 -Targets zfs-write-core,zfswin-um` | `out\openzfs-write-debug` | builds `libzpool`, the portable ZPL layer, the `zfs-write-core` test tool and `zfswin-um.exe` |
| `scripts\build-installer.ps1 -Variant kernel` | `out\release\<v>` | stages, signs and verifies the kernel payload, then builds the MSI, SBOM and `SHA256SUMS.txt` |
| `scripts\build-installer.ps1 -Variant usermode` | `out\release\<v>` | the same for the user-mode payload |
| `scripts\build-native.ps1`, `scripts\build-managed.ps1` | `out\` | the read-only ZFS reader and its .NET tests (plan steps 01–09) |

## Tests

| Command | Where | What it checks |
| --- | --- | --- |
| `scripts\run-tests.ps1` | host | reader unit and fixture tests |
| `dotnet test tests\Zfs.Admin.Tests` | host | CLI, protocol, validation, setup options, and the JSON schema against real `zpool`/`zfs` output |
| `python scripts\test-write-core.py <ubuntu-ip>` | host + Linux VM | write core vs upstream OpenZFS (step 10) |
| `python scripts\test-write-recovery.py <ubuntu-ip>` | host + Linux VM | power loss, kill, I/O errors, ENOSPC, mirrors and labels (step 11) |
| `scripts\vm\Test-DriverInVm.ps1` | Windows VM | kernel driver: read, write, extended, cache and bench phases (steps 12–13, 15) |
| `scripts\vm\Test-InstallerInVm.ps1` | Windows VM | kernel MSI: preflight, rollback, install, CLI, PowerShell, standard user, repair, upgrade, uninstall (step 14) |
| `scripts\vm\Test-UserModeInVm.ps1` | Windows VM with **Secure Boot on** | user-mode MSI: install refused without WinFsp, read, write, bench, power cut, uninstall (step 15) |
| `python scripts\vm\validate-*.py <ubuntu-ip>` | Linux VM | upstream OpenZFS imports every pool the Windows tests wrote, and checks hashes, scrub and `zdb -bcc` |

## Signing

- The kernel MSI is **test-signed**. It installs only where test signing is
  on (`bcdedit /set testsigning on`, Secure Boot off) and
  `zfswindows-test.cer` is trusted. A production kernel package needs
  Microsoft attestation signing through the Partner Center (EV certificate),
  which this repository cannot do.
- The user-mode MSI contains no kernel driver of ours. Its binaries are
  signed with the same project certificate, and nothing requires that
  signature to be trusted: Windows runs the service as an ordinary program,
  with Secure Boot on. WinFsp's own driver is Microsoft-signed.

## Licences

- This repository is MIT (`LICENSE.md`).
- OpenZFS is CDDL-1.0.
- WinFsp is GPLv3 with a FLOSS exception. The exception allows linking the
  WinFsp DLL from OSI-licensed software and redistributing its unmodified
  installer. It requires the notice "WinFsp - Windows File System Proxy,
  Copyright (C) Bill Zissimopoulos" and a link to
  <https://github.com/winfsp/winfsp> in the user interface and user-facing
  documentation. The user-mode MSI, its version resource and these documents
  carry both.
