# CI: self-hosted runners

`.github/workflows/ci.yml` runs on every push to `master`, on every pull
request, and on demand (Actions → CI → Run workflow, with Release or Debug
packages under test). The VM suites are off for now: they run only on
demand, with *Run the VM suites* ticked. It has three jobs:

| Job | Runner label | What it does |
| --- | --- | --- |
| `build` | `zfswin-x64` | Driver, user-mode engine, admin suite and the four MSIs (x64/ARM64 × kernel/user mode) in Release and Debug (Release only for a pull request); uploads an artifact per architecture and configuration, `OpenZFS-for-Windows-<version>-<x64\|arm64>-<release\|debug>`, each with the kernel and user-mode MSIs, the SBOM and the test certificate |
| `test-x64` | `zfswin-x64` | `Test-UserModeInVm.ps1`, then `Test-InstallerInVm.ps1`, in the `zfsTest` VM |
| `test-arm64` | `zfswin-arm64` | `host-usermode-suite.ps1`, then `host-kernel-suite.ps1`, in the `ZFSTestOnArm` VM (in parallel with `test-x64`) |

GitHub-hosted runners cannot do any of this: they have no WDK and test-signing
certificate, and no Hyper-V for the test VMs. So both jobs run on our own
machines: the x64 PC (build and x64 VM) and the ARM PC (ARM64 VM).

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

5. **Do not use the `zfsTest` VM by hand while a `test-x64` job runs**: every
   suite restores its checkpoints.

## 2. The ARM PC (label `zfswin-arm64`)

The `ZFSTestOnArm` VM, its checkpoint `testsigning-ready-updated`, and the
WinFsp and .NET installers in `C:\zfswin-arm\stage` are already there from
the manual ARM runs; the job uses the same folder.

1. **Git for Windows (ARM64)** from https://git-scm.com/download/win; the
   checkout action needs it. Nothing else: the job only runs PowerShell
   scripts and Hyper-V.

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

Actions → CI → *Run workflow* on the branch, with *Run the VM suites* off:
only `build` runs, and the artifact appears on the run's page. Then run it
again with the suites on.

Both runners must show as *Idle* under Settings → Actions → Runners. A job
waits ("Waiting for a runner to pick up this job") while its machine is
off.

## Security

Self-hosted runners run the workflow's code with your account. On a public
repository, keep *Settings* → *Actions* → *General* → *Fork pull request
workflows from outside collaborators* at **Require approval for all outside
collaborators**, so a stranger's pull request cannot run on these machines.
