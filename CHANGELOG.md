# Changelog

From 1.0.19 on, every pull request merged into `master` is published by CI
as a [GitHub release](https://github.com/rosenbalchev/zfsWindows/releases)
`v1.0.N`, and its notes list the merged pull requests. This file records
the versions before that, and the verification runs that the release notes
do not show.

## 1.0.18 (2026-11-08)

- User mode: the ARC is sized and shrunk as in the kernel, and ABD chunks
  come from one allocation (patches 0032 and 0034, issue #3).
- User mode: the import scan takes partition paths, so pools on whole disks
  are imported again after a service restart or a reboot (patch 0033).

## 1.0.16 (2026-11-07)

- 1.0.15 plus patch 0031: the user-mode edition can create pools on whole
  physical disks (issue #1). That fix was checked on x64 and ARM64; the full
  VM suites ran on 1.0.15.

## 1.0.15 (2026-11-07)

- Release and Debug builds passed the VM suites on **x64 and ARM64**,
  kernel and user mode. On x64 that includes install, upgrade, repair and
  uninstall.
- Pools written on each platform and edition were read back correctly on
  the others and validated by upstream OpenZFS on Linux (plan step 19).
- 1.0.8–1.0.14 are superseded: Release-only optimizer bugs (patches
  0026–0029), a mount race with many datasets (0030), and two
  `zfswin`/broker bugs.
