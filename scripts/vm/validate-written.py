#!/usr/bin/env python3
"""Upstream validation of the pool written through the OpenZFS kernel driver.

Test-DriverInVm.ps1 -Phase write leaves TestVolumes/vm/written/lz4.raw (the
Linux-created lz4 fixture after the driver wrote to it) and written.json
(SHA-256 of every file the driver wrote, as read back in the guest).  Linux
OpenZFS must import it, the original fixture files must still match
tests/fixtures/compression/lz4.json, the written files must match
written.json, scrub must be clean and `zdb -bcc` must report no leaks.

Usage: python scripts/vm/validate-written.py [vm-host]
"""
import json
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                '..'))
from zfstest import HOST, MANIFEST_PY, ROOT, log, scp, shell_quote, vm

POOL, DATASET = 'zc_lz4', 'zc_lz4/data'
REMOTE = '/root/zfswin-driver/written'
# TestVolumes/vm/written (kernel driver) or, with ZFS_WRITTEN_DIR,
# e.g. TestVolumes/vm/written-usermode (Test-UserModeInVm.ps1, step 15).
LOCAL = os.environ.get('ZFS_WRITTEN_DIR',
                       os.path.join(ROOT, 'TestVolumes', 'vm', 'written'))


def main():
    manifest = json.load(open(os.path.join(
        ROOT, 'tests', 'fixtures', 'compression', 'lz4.json')))
    written = json.load(open(os.path.join(LOCAL, 'written.json')))
    vm('zpool list -H -o name %s >/dev/null 2>&1 && zpool export %s; '
       'rm -rf %s && mkdir -p %s' % (POOL, POOL, REMOTE, REMOTE))
    scp(os.path.join(LOCAL, 'lz4.raw'), 'root@%s:%s/lz4.raw' % (HOST, REMOTE))
    out = vm('''set -e
zpool import -d %(r)s %(p)s
trap 'cd /; zpool export %(p)s 2>/dev/null || true' EXIT
mnt=$(zfs get -H -o value mountpoint %(ds)s)
python3 -c %(m)s "$mnt" > /tmp/zwd-manifest.json
zpool scrub -w %(p)s
zpool status -v %(p)s | grep -E 'state:|scan:|errors:'
zpool export %(p)s
trap - EXIT
zdb -e -p %(r)s -bcc %(p)s 2>&1 | grep -E 'leaked|No leaks'
cat /tmp/zwd-manifest.json
''' % {'r': REMOTE, 'p': POOL, 'ds': DATASET,
       'm': shell_quote(MANIFEST_PY)}, capture=True)
    lines = out.strip().splitlines()
    actual = json.loads(lines[-1])
    for line in lines[:-1]:
        log('  ' + line.strip())
    failures = []
    for f in manifest['files']:
        entry = actual.get(f['path'].lstrip('/'))
        if not entry or entry['sha256'].upper() != f['sha256'].upper():
            failures.append('fixture file %s: %s' % (f['path'], entry))
    for rel, sha in written.items():
        entry = actual.get('windows-written/' + rel)
        if not entry or entry['sha256'].upper() != sha.upper():
            failures.append('written file %s: %s' % (rel, entry))
    for gone in ('windows-written/deleted.txt', 'windows-written/temp.txt'):
        if gone in actual:
            failures.append('%s should not exist' % gone)
    if 'No leaks' not in out or 'with 0 errors' not in out:
        failures.append('scrub or zdb -bcc reported problems')
    if failures:
        raise SystemExit('FAIL:\n  ' + '\n  '.join(failures))
    log('PASS: %d fixture files and %d driver-written files verified by '
        'Linux OpenZFS; scrub clean; no leaks' % (len(manifest['files']),
                                                  len(written)))


if __name__ == '__main__':
    main()
