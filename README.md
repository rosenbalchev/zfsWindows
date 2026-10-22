# ZFS for Windows

This repository contains an independent Windows implementation around the
upstream OpenZFS source in [`OpenZFS/`](OpenZFS/). The upstream tree is kept
unchanged and is treated as the on-disk format and algorithm reference.

## Current status

Milestone M1 is in progress. The repository now contains:

- a Visual Studio/MSBuild-friendly native CMake project;
- a read-only file-backed block-device abstraction;
- a small exported C ABI for opening an image, probing vdev labels, bounded
  block reads, and initial MOS/dataset/directory traversal;
- a .NET 10 interop project and xUnit test project;
- PowerShell build/test entry points;
- a fixture manifest contract and an implementation specification.

The native layer can traverse the genuine fixture into its populated child
dataset and decode its root directory and file dnodes. General path lookup and
regular-file byte reads are not implemented yet. Compatibility claims are made
only for behavior exercised against a genuine OpenZFS-generated image.

## Development approach and resume flow

The implementation is executed as ordered steps documented in [`plan/`](plan/).
Each step has a dedicated Markdown file containing its objective, dependencies,
implementation tasks, verification commands, evidence requirements, and the
commit boundary. Work is committed after each independently reviewable step;
the plan file is updated with the actual result and any blockers.

If work is interrupted, call Codex again and say: “Continue the ZFS Windows
plan.” Codex should inspect `plan/README.md`, the last committed step, the
working tree, and the latest test results, then continue with the first
unfinished step. A step marked `blocked` must not be silently skipped. A
milestone is marked `complete` only after its stated genuine-ZFS evidence is
available.

The plan deliberately separates read-only compatibility, writable semantics,
and kernel-driver work. The driver is never loaded automatically on the host.

## Build in Visual Studio

From a *Developer PowerShell for VS* prompt:

```powershell
cmake -S src/native -B out/native -G "Visual Studio 17 2022" -A x64
cmake --build out/native --config Debug
dotnet test ZfsWindows.sln
```

The default managed tests do not require a driver, administrator rights, or
real disk writes. Genuine-image tests are enabled only by setting
`ZFS_TEST_IMAGE` and `ZFS_TEST_MANIFEST`.

## Safety

The default native API opens images read-only. There is no code that formats a
disk, repairs GPT, upgrades a pool, or loads a kernel driver.

