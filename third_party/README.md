# Upstream dependencies

Every upstream component the build uses, where its version is pinned, and
how it is updated. Last reviewed for issue #50 (2026-11-23): the policy is
the latest *stable* upstream release; a release candidate or beta only when
there is a concrete need.

## Source and binaries in the build

| Component | Version | Pinned in | Notes |
| --- | --- | --- | --- |
| OpenZFS on Windows | `zfswin-2.4.4rc10` (`c5cb4b9f`) | submodule `third_party/openzfs-windows` | The fork names every release `rcN`; rc10 is its GitHub release marked *Latest*, not a pre-release. Our fixes are `patches/openzfs-windows/*.patch`, applied at build time; the README there has the rebase notes. |
| OpenZFS | `zfs-2.4.4` (`71a9f957`) | submodule `OpenZFS` | Unmodified reference tree. LZ4 and Zstandard are compiled from it into `ZfsCore`. `zfs-2.4.99` is the development tag, not a release. |
| zlib | 1.4 | `third_party/zlib-1.4/` | Unmodified release from <https://zlib.net/>, `zlib14.zip`, SHA-256 `38BC7ED1C8AC3C870A159623DEACF74A46C393AE1AD23F1DCA1ED8E272DEB058` (as published on zlib.net). Built into `ZfsCore` (gzip decompression) as the `zlibstatic` target. |
| WinFsp | 2.2 (2.2.26323) | `scripts/fetch-winfsp.ps1` (version, SHA-256, URL) | Downloaded, not committed: the SDK builds `zfswin-um.exe` against `third_party/winfsp/sdk-2.2`, and the MSI is what the user-mode VM tests install. The script checks the SHA-256 and the Authenticode signature (Navimatics LLC). The user-mode package still accepts WinFsp 2.1 or later; the 2.1 → 2.2 API is unchanged. |
| OpenSSL, x64 | as installed (4.0.3 on the build host) | `C:\Program Files\OpenSSL-Win64` | Shining Light "Win64 OpenSSL" developer package, static libraries. Not pinned by the repository. |
| OpenSSL, ARM64 | vcpkg baseline `29718cfe` | `scripts/arm64-openssl.ps1` | Built with the vcpkg that ships with Visual Studio. |

The vendored libraries inside OpenZFS on Windows (its own zlib 1.2.13,
libpthread, libregex, libfetch, libuuid) come with the fork and are updated
with it.

## .NET packages (NuGet)

| Package | Version | Used by |
| --- | --- | --- |
| System.Management | 10.0.15 | GUI |
| System.ServiceProcess.ServiceController | 10.0.15 | broker, GUI |
| Microsoft.Web.WebView2 | 1.0.4370.26 | GUI |
| JsonSchema.Net | 9.7.0 | admin tests |
| Microsoft.NET.Test.Sdk | 18.12.0 | tests |
| xunit / xunit.runner.visualstudio | 2.9.3 / 4.0.1 | tests |
| BenchmarkDotNet | 0.15.8 | benchmarks |
| WixToolset.Sdk / WixToolset.Util.wixext | 5.0.2 | both MSIs |

`dotnet list <project> package --outdated` lists what is behind.

## CI (GitHub Actions)

`actions/checkout@v7`, `actions/upload-artifact@v7`,
`actions/download-artifact@v8`, `softprops/action-gh-release@v3`, in
`.github/workflows/ci.yml`. All need the Node 24 Actions runtime on the
self-hosted runners.

## Deliberately not on the newest version

- **WiX stays on 5.0.2.** WiX 6 and 7 are stable, but from 6 on the WiX
  project asks commercial users for its Open Source Maintenance Fee, and
  7 also needs its EULA accepted in the build. 5.0.2 is MS-RL with no fee,
  and the newest release in that line.
- **System.\* packages stay on the .NET 10 line (10.0.x).** The projects
  target .NET 10 (LTS); the 11.0.0 packages belong to .NET 11 (STS), and
  retargeting is a separate decision.
- **xunit stays on v2 (2.9.3, its last release).** xunit v3 is a different
  package (`xunit.v3`) and a migration, not an update.

## How to update

1. **OpenZFS on Windows:** check out the new tag in the submodule, then run
   `scripts/apply-openzfs-patches.ps1` on a clean tree. Refresh any patch
   that no longer applies (`git apply --3way`, then regenerate it with
   `git diff`), drop patches the fork now contains, and record both in
   `patches/openzfs-windows/README.md`. Update the version in `LICENSE.md`,
   `README.md`, `scripts/build-installer.ps1` (SBOM) and
   `scripts/build-release.ps1` (VERSION.json).
2. **OpenZFS:** move the submodule to the new `zfs-2.x.y` tag; check that
   the LZ4 and Zstandard sources `src/native/CMakeLists.txt` names still
   exist.
3. **zlib:** replace `third_party/zlib-<v>/` with the new release, unmodified,
   and update the path in `src/native/CMakeLists.txt`, the hash above and
   `LICENSE.md`.
4. **WinFsp:** change version, SHA-256 (from the release's build hashes) and
   URL in `scripts/fetch-winfsp.ps1`, the SDK folder in
   `src/native/ZfsWinFsp/CMakeLists.txt` and
   `scripts/build-installer-usermode.ps1`, and the MSI name in
   `scripts/vm/Test-UserModeInVm.ps1`, `scripts/vm/Test-PartitionInVm.ps1`
   and `scripts/vm/arm/host-usermode-suite.ps1`. Stage the new MSI in
   `C:\zfswin-arm\stage` on the ARM PC.
5. Run the host tests and the VM suites (`docs/building.md`, *Tests*).
