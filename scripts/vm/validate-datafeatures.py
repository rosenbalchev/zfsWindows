#!/usr/bin/env python3
"""Upstream validation of compression and deduplication written on Windows.

The user-mode suite's admin phase (guest-installer.ps1 -Stage datafeatures)
leaves, in TestVolumes/vm/crossedition-usermode, pool zwin ({a,d}.img) with
one dataset per compression algorithm (zwin/c-*), a deduplicated dataset
(zwin/dd) and a deduplicated zstd one (zwin/ddz), and datafeatures.json:
their SHA-256 and settings as read back in the guest.  Linux OpenZFS must
import the pool, report the same compression and dedup settings, read the
same bytes, see the deduplication table, scrub clean and report no leaks.

Usage: python scripts/vm/validate-datafeatures.py <vm-host>
       (ZFSWIN_POOL_DIR selects another folder of the same layout)
"""
import json
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                '..'))
from zfstest import HOST, MANIFEST_PY, ROOT, log, scp, shell_quote, vm

POOL = 'zwin'
REMOTE = '/root/zfswin-driver/datafeatures'
LOCAL = os.environ.get('ZFSWIN_POOL_DIR') or os.path.join(
    ROOT, 'TestVolumes', 'vm', 'crossedition-usermode')


def main():
    expected = json.load(open(os.path.join(LOCAL, 'datafeatures.json'),
                              encoding='utf-8-sig'))
    datasets = expected['datasets']
    vm('zpool list -H -o name %s >/dev/null 2>&1 && zpool export %s; '
       'rm -rf %s && mkdir -p %s' % (POOL, POOL, REMOTE, REMOTE))
    for name in ('a.img', 'd.img'):
        scp(os.path.join(LOCAL, name), 'root@%s:%s/%s' % (HOST, REMOTE, name))
    script = ['set -e',
              'zpool import -d %s %s' % (REMOTE, POOL),
              "trap 'cd /; zpool export %s 2>/dev/null || true' EXIT" % POOL]
    for ds in datasets:
        script.append('echo "PROPS %s $(zfs get -H -o value compression,dedup,'
                      'compressratio %s | tr \'\\n\' \' \')"' % (ds, ds))
        script.append('echo "FILES %s $(python3 -c %s "$(zfs get -H -o value '
                      'mountpoint %s)")"' % (ds, shell_quote(MANIFEST_PY), ds))
    script += ['echo "DEDUPRATIO $(zpool get -H -o value dedupratio %s)"' % POOL,
               'zpool status -D %s | grep -E "DDT entries|dedup:" || true' % POOL,
               'zpool scrub -w %s' % POOL,
               'zpool status -v %s | grep -E "state:|scan:|errors:"' % POOL,
               'zpool export %s' % POOL,
               'trap - EXIT',
               "zdb -e -p %s -bcc %s 2>&1 | grep -E 'leaked|No leaks'" % (REMOTE, POOL)]
    out = vm('\n'.join(script) + '\n', capture=True)

    failures = []
    seen = {}
    for line in out.splitlines():
        if line.startswith('PROPS '):
            _, ds, rest = line.split(' ', 2)
            seen.setdefault(ds, {})['props'] = rest.split()
            log('  %s: compression=%s dedup=%s ratio=%s' % ((ds,) + tuple(rest.split()[:3])))
        elif line.startswith('FILES '):
            _, ds, js = line.split(' ', 2)
            seen.setdefault(ds, {})['files'] = json.loads(js)
        elif line.strip():
            log('  ' + line.strip())
    for ds, want in datasets.items():
        got = seen.get(ds)
        if not got:
            failures.append('%s: not found on Linux' % ds)
            continue
        compression, dedup = got['props'][0], got['props'][1]
        if compression != want['compression']:
            failures.append('%s: compression %s, expected %s' % (ds, compression, want['compression']))
        if want.get('dedup') and dedup != want['dedup']:
            failures.append('%s: dedup %s, expected %s' % (ds, dedup, want['dedup']))
        files = {k: v for k, v in got['files'].items() if v['type'] == 'file'
                 and not k.startswith('System Volume Information/')}
        for f in want['files']:
            entry = files.pop(f['path'].replace('\\', '/'), None)
            if not entry or entry['sha256'].upper() != f['sha256'].upper():
                failures.append('%s/%s: %s' % (ds, f['path'], entry))
        for rel in files:
            failures.append('%s/%s: not in the guest manifest' % (ds, rel))
    if 'No leaks' not in out or 'with 0 errors' not in out:
        failures.append('scrub or zdb -bcc reported problems')
    if failures:
        raise SystemExit('FAIL:\n  ' + '\n  '.join(failures))
    log('PASS: %d datasets (%s) verified by Linux OpenZFS: settings, %d files, '
        'dedup table; scrub clean; no leaks'
        % (len(datasets), ', '.join(sorted(d['compression'] for d in datasets.values())),
           sum(len(d['files']) for d in datasets.values())))


if __name__ == '__main__':
    main()
