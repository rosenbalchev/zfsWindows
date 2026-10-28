#!/usr/bin/env python3
"""Upstream validation of the pools from Test-DriverInVm.ps1 -Phase cache.

TestVolumes/vm/cache/ holds the step-13 pools after the driver used them,
and the guest's JSON results:

- main.raw (zw_main): five datasets written until the first power cut;
- slogdata.raw (zw_slog): written with a SLOG until both power cuts, then
  imported without its log device (-m) and the log vdev removed;
- l2data.raw (zw_l2): read through an L2ARC that was corrupted and removed;
- coh.raw (zw_coh): the coherency file.

Linux OpenZFS must import every pool without the cache or log devices, see
exactly the files the guest found after each power cut (cache-check), the
L2ARC test file and the coherency file unchanged, scrub clean and report no
leaks with zdb -bcc.

Usage: python scripts/vm/validate-cache.py [vm-host]
"""
import json
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                '..'))
from zfstest import HOST, MANIFEST_PY, ROOT, log, scp, shell_quote, vm

LOCAL = os.path.join(ROOT, 'TestVolumes', 'vm', 'cache')
REMOTE = '/root/zfswin-driver/cache'
POOLS = {'zw_main': 'main.raw', 'zw_slog': 'slogdata.raw',
         'zw_l2': 'l2data.raw', 'zw_coh': 'coh.raw'}
# dataset -> guest result file whose result.files lists what must be there
CHECKS = {'zw_main/buffered': 'check-buffered', 'zw_main/flush': 'check-flush',
          'zw_main/writethrough': 'check-writethrough',
          'zw_main/syncalways': 'check-syncalways',
          'zw_main/syncdisabled': 'check-syncdisabled',
          'zw_slog/slog': 'check-slog', 'zw_slog/slog2': 'check-slog2'}


def result(name):
    with open(os.path.join(LOCAL, name + '.json'), encoding='utf-8-sig') as f:
        return json.load(f)['result']


def manifest(path):
    return json.loads(vm('python3 -c %s %s' % (shell_quote(MANIFEST_PY),
                                               shell_quote(path)),
                         capture=True))


def mountpoint(dataset):
    return vm('zfs get -H -o value mountpoint ' + dataset,
              capture=True).strip()


def main():
    failures = []
    vm('for p in %s; do zpool list -H -o name $p >/dev/null 2>&1 && '
       'zpool export $p; done; rm -rf %s && mkdir -p %s'
       % (' '.join(POOLS), REMOTE, REMOTE))
    for pool, image in POOLS.items():
        vm('mkdir -p %s/%s' % (REMOTE, pool))
        scp(os.path.join(LOCAL, image),
            'root@%s:%s/%s/%s' % (HOST, REMOTE, pool, image))

    for pool in POOLS:
        vm('zpool import -d %s/%s %s' % (REMOTE, pool, pool))
        try:
            for dataset, name in CHECKS.items():
                if not dataset.startswith(pool + '/'):
                    continue
                guest = result(name)
                want = {k: v.upper() for k, v in guest['files'].items()}
                got = {k[2:]: v['sha256'].upper() for k, v in
                       manifest(mountpoint(dataset)).items()
                       if k.startswith('w/') and v['type'] == 'file'}
                bad = sorted(k for k in set(want) | set(got)
                             if want.get(k) != got.get(k))
                log('  %s: %d files as the guest saw them after the power '
                    'cut (acked %d, exact %d, zero-filled %d, missing %d), '
                    '%d differ' % (dataset, len(got), guest['acked'],
                                   guest['exact'], guest['zeroFilled'],
                                   guest['missing'], len(bad)))
                failures += ['%s: %s differs' % (dataset, k) for k in bad[:10]]
            if pool == 'zw_l2':
                want = result('fill')['files']['random-0.bin'].upper()
                got = manifest(mountpoint('zw_l2') + '/data')['random-0.bin']
                log('  zw_l2 random-0.bin %s' % (
                    'matches' if got['sha256'].upper() == want else 'DIFFERS'))
                if got['sha256'].upper() != want:
                    failures.append('zw_l2: random-0.bin differs')
            if pool == 'zw_coh':
                want = result('coherency')['finalSha256'].upper()
                got = manifest(mountpoint('zw_coh') + '/coherency')
                ok = got['coherent.bin']['sha256'].upper() == want
                log('  zw_coh coherent.bin %s' % ('matches' if ok else 'DIFFERS'))
                if not ok:
                    failures.append('zw_coh: coherent.bin differs')
            out = vm('''set -e
zpool scrub -w %(p)s
zpool status -v %(p)s | grep -E 'state:|scan:|errors:'
''' % {'p': pool}, capture=True)
            for line in out.strip().splitlines():
                log('  %s %s' % (pool, line.strip()))
            if 'with 0 errors' not in out:
                failures.append('%s: scrub not clean' % pool)
        finally:
            vm('cd /; zpool export %s' % pool)
        out = vm('zdb -e -p %s/%s -bcc %s 2>&1 | grep -E "leaked|No leaks"'
                 % (REMOTE, pool, pool), capture=True)
        log('  %s %s' % (pool, out.strip().splitlines()[-1].strip()))
        if 'No leaks' not in out:
            failures.append('%s: zdb -bcc reported leaks' % pool)

    if failures:
        raise SystemExit('FAIL:\n  ' + '\n  '.join(failures))
    log('PASS: power-cut, SLOG, L2ARC and coherency pools verified by Linux '
        'OpenZFS; scrubs clean; no leaks')


if __name__ == '__main__':
    main()
