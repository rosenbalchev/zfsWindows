# CI: self-hosted runners

`.github/workflows/ci.yml` runs on every push to `master`, on every pull
request, and on demand (Actions → CI → Run workflow, with Release or Debug
packages under test). The x64 unit tests run in every build. Coverage runs
on every push to `master` and on demand (*Measure the unit tests' code
coverage*). The ARM64 unit tests and the VM integration tests are disabled
for pushes and pull requests: they run only on demand, with *Run the ARM64
unit tests* or *Run the VM integration tests* ticked. It has six jobs:

| Job | Runner label | What it does |
| --- | --- | --- |
| `build` | `zfswin-x64` | Driver, user-mode engine, admin suite and the four MSIs (x64/ARM64 × kernel/user mode) in Release and Debug on `master`, Debug only for a pull request or a manual run on another branch (plus Release when the VM integration tests run on Release); uploads an artifact per architecture and configuration, `OpenZFS-for-Windows-<version>-<x64\|arm64>-<release\|debug>`, each with the kernel and user-mode MSIs, the SBOM, the test certificate, `VERSION.json` and `SHA256SUMS`; then the x64 unit tests (`scripts/test-usermode.ps1`: the user-mode engine in the test process on image files, no VM; Release when it was built, otherwise Debug), results as `unit-test-results-x64-<version>` |
| `release` | `zfswin-x64` | On a push to `master` only: publishes every package of the build as the GitHub release `v<version>` (notes generated from the merged pull requests); the README links to `releases/latest` |
| `unit-tests-arm64` | `zfswin-arm64` | The same unit tests on the ARM PC itself, no VM, with the ARM64 engine the build cross-compiled |
| `coverage` | `zfswin-x64` | The engine built instrumented (`build-write-core.ps1 -Coverage`: clang source-based coverage, `out\openzfs-write-coverage`, never packaged), the unit tests on it (`test-usermode.ps1 -Coverage`); the HTML report of our code and OpenZFS `module\zfs` as `coverage-html-<version>` |
| `vm-integration-x64` | `zfswin-x64` | `Test-UserModeInVm.ps1`, then `Test-InstallerInVm.ps1`, in the `zfsTest` VM |
| `vm-integration-arm64` | `zfswin-arm64` | `host-usermode-suite.ps1`, then `host-kernel-suite.ps1`, in the `ZFSTestOnArm` VM (in parallel with `vm-integration-x64`) |

GitHub-hosted runners cannot do any of this: they have no WDK and test-signing
certificate, and no Hyper-V for the test VMs. So both jobs run on our own
machines: the x64 PC (build and x64 VM) and the ARM PC (ARM64 VM).

**Test and coverage pages.** Each unit test job writes its results to the
run's summary page (Actions → the run → the job's summary): totals, a
table per test class, and every failed and skipped test with its message.
The coverage job adds the coverage of our code (`src\native`, per file),
of OpenZFS `module\zfs` and of the whole engine; the line-by-line view is
the `coverage-html-<version>` artifact (open `index.html`).  Locally the
same pages are `out\test-results\usermode-<arch>-<configuration>\summary.md`
and `out\coverage\summary.md`, `out\coverage\html\index.html`.

The unit tests' Linux reference-image checks (`LinuxImageTests`, two 5 GiB
pools hashed in full, about 2.5 minutes) are skipped in CI: the checkout
has no `TestVolumes\`. To run them in the build job, set
`ZFSWIN_LINUX_IMAGES` to `%ZFSWIN_CI_DATA%\TestVolumes` for that step.

A suite job fails only on a **new** failure. The checks every run reports
(the upstream ACL and snapshot gaps, and so on) are listed in
`scripts/ci/known-failures.txt`; `scripts/ci/Invoke-CiSuite.ps1` compares
against it and writes a summary to the job page. Remove a line there once
that check passes.

Every CI run gets its own version: build number = the committed
`build/BUILD_NUMBER` + the run number (`github.run_number`), written to the
runner's workspace only, never committed. Numbered releases are still made
by hand with `scripts\build-release.ps1`.

To keep the build short:
- `out\` stays in the runner's workspace between runs, so Ninja rebuilds
  only what changed. *Run workflow* with *Clean build* ticked discards it.
- CMake configures a tree only when it is new or its command line, the
  submodule commit or (for the engine) the version changed
  (`zfswin-configure.txt` in the tree); patch 0035 keeps a reconfigure
  from rewriting the headers every driver object includes.
- The submodules stay patched between runs: they are reset (and the patches
  re-applied) only when their pinned commits or `patches/` change, since the
  patched headers would otherwise recompile the whole driver
  (`out\ci-submodules.stamp`).
- The MSIs skip the ICE validation (`build-release.ps1 -NoValidation`).
- Packages: Debug MSIs carry the PDBs, Release MSIs have none, and both
  use fast compression (mszip; "high" is several times slower for about
  20% smaller packages). This is set in the `.wixproj` files, so it applies
  to hand-made releases too.

## 1. The x64 PC (label `zfswin-x64`)

It already has everything the build needs (Visual Studio 2022 with the x64
and ARM64 C++ tools, the WDK, the .NET SDK, WiX, CMake, Git) and the
`zfsTest` VM with its `clean` and `fixtures` checkpoints.

1. **Data folder.** Create `D:\zfswin-ci` and copy into it, keeping the
   paths:

   | Copy from `D:\Projects\zfsWindows\` | To `D:\zfswin-ci\` |
   | --- | --- |
   | `TestVolumes\` (VM credentials `vm\zfsTest.json`, fixture images) | `TestVolumes\` |
   | `out\dotnet-runtime\` | `out\dotnet-runtime\` |
   | `out\driver-tools\` | `out\driver-tools\` |
   | `out\release\1.0.65000\` (the upgrade test package) | `out\release\1.0.65000\` |
   | `out\vcpkg-openssl\installed\arm64-windows-static\` (ARM64 OpenSSL for the build) | `out\vcpkg-openssl\installed\arm64-windows-static\` |

   ```powershell
   foreach ($p in "TestVolumes", "out\dotnet-runtime", "out\driver-tools", "out\release\1.0.65000", "out\vcpkg-openssl\installed\arm64-windows-static") {
       robocopy "D:\Projects\zfsWindows\$p" "D:\zfswin-ci\$p" /E
   }
   ```

2. **Environment variable** (machine-wide, then restart the runner service
   if it is already installed):

   ```powershell
   [Environment]::SetEnvironmentVariable("ZFSWIN_CI_DATA", "D:\zfswin-ci", "Machine")
   ```

3. **Register the runner.** On GitHub: the repository → *Settings* →
   *Actions* → *Runners* → *New self-hosted runner* → *Windows*, *x64*.
   Copy the download commands from that page; for the configuration use
   a short folder (the build's paths are long) and the label:

   ```powershell
   mkdir C:\actions-runner; cd C:\actions-runner
   # ... the Invoke-WebRequest and Expand-Archive lines from the GitHub page ...
   .\config.cmd --url https://github.com/rosenbalchev/zfsWindows --token <token from the page> `
       --name zfswin-x64 --labels zfswin-x64 --work D:\w `
       --runasservice --windowslogonaccount "$env:COMPUTERNAME\rosen" --windowslogonpassword "<your password>"
   ```

   The service must run as **your account**, not as Network Service: the
   driver is signed with the test certificate in your
   `Cert:\CurrentUser\My`, and the suites need an administrator token for
   Hyper-V and PowerShell Direct (a service under an administrator account
   gets one). Use `--work D:\w`: Windows path limits break a deep work
   folder.

4. **Long paths and Git.** Once, as administrator:

   ```powershell
   git config --system core.longpaths true
   New-ItemProperty HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem -Name LongPathsEnabled -Value 1 -PropertyType DWord -Force
   ```

5. **Do not use the `zfsTest` VM by hand while a `vm-integration-x64` job runs**: every
   suite restores its checkpoints.

## 2. The ARM PC (label `zfswin-arm64`)

The `ZFSTestOnArm` VM, its checkpoint `testsigning-ready-updated`, and the
WinFsp and .NET installers in `C:\zfswin-arm\stage` are already there from
the manual ARM runs; the job uses the same folder.

1. **Git for Windows (ARM64)** from https://git-scm.com/download/win; the
   checkout action needs it. The VM integration tests need nothing else:
   they only run PowerShell scripts and Hyper-V. The unit tests
   (`unit-tests-arm64`) also need the **.NET 10 SDK (ARM64)**, from
   https://dotnet.microsoft.com/download/dotnet/10.0, to build and run the
   test project.

2. **Data folder** `C:\zfswin-arm\ci` with one file, `ZFSTestOnArm.json`:

   ```json
   { "vmName": "ZFSTestOnArm", "user": "<VM user>", "password": "<VM password>",
     "checkpoint": "testsigning-ready-updated" }
   ```

   (The same values as `vmName`, `user` and `password` in
   `TestVolumes\vm\ZFSTestOnArm.json` on the x64 PC.) Then:

   ```powershell
   [Environment]::SetEnvironmentVariable("ZFSWIN_CI_DATA", "C:\zfswin-arm\ci", "Machine")
   ```

3. **Register the runner**: *New self-hosted runner* → *Windows*, *ARM64*,
   then

   ```powershell
   mkdir C:\actions-runner; cd C:\actions-runner
   # ... the download lines from the GitHub page ...
   .\config.cmd --url https://github.com/rosenbalchev/zfsWindows --token <token> `
       --name zfswin-arm64 --labels zfswin-arm64 --work C:\w `
       --runasservice --windowslogonaccount "$env:COMPUTERNAME\iva" --windowslogonpassword "<password>"
   ```

   An administrator account, for Hyper-V and PowerShell Direct.

4. **Memory.** The VM's checkpoint has 8 GB, and Hyper-V starts it only
   when 8 GB are free; the PC has 16 GB. Close large programs before a run,
   or the job fails at `Start-VM` with "Not enough memory in the system".

## 3. Check it

Actions → CI → *Run workflow* on the branch, with *Run the VM integration tests* off:
only `build` runs, and the artifact appears on the run's page. Then run it
again with the VM integration tests on.

Both runners must show as *Idle* under Settings → Actions → Runners. A job
waits ("Waiting for a runner to pick up this job") while its machine is
off.

## Security

Self-hosted runners run the workflow's code with your account. On a public
repository, keep *Settings* → *Actions* → *General* → *Fork pull request
workflows from outside collaborators* at **Require approval for all outside
collaborators**, so a stranger's pull request cannot run on these machines.
