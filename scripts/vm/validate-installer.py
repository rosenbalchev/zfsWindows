#!/usr/bin/env python3
"""Upstream validation of the pool the installed admin suite built.

Test-InstallerInVm.ps1 leaves TestVolumes/vm/installer/{a,d}.img (the final
mirror of pool zwin after create, attach, replace and detach through
zfswin.exe) and manifest.json (SHA-256 of zwin/docs and of its @s1 snapshot,
as read back in the guest).  Linux OpenZFS must import the pool, see the
properties zfswin set, find @s1, match both manifests, scrub clean and
report no leaks.

Usage: python scripts/vm/validate-installer.py [vm-host]
"""
import json
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                '..'))
from zfstest import HOST, MANIFEST_PY, ROOT, log, scp, shell_quote, vm

POOL, DATASET = 'zwin', 'zwin/docs'
REMOTE = '/root/zfswin-driver/installer'
LOCAL = os.path.join(ROOT, 'TestVolumes', 'vm', 'installer')


def compare(label, expected, actual, failures):
    files = {k: v for k, v in actual.items() if v['type'] == 'file'}
    for f in expected:
        rel = f['path'].replace('\\', '/')
        entry = files.pop(rel, None)
        if not entry or entry['sha256'].upper() != f['sha256'].upper():
            failures.append('%s %s: %s' % (label, rel, entry))
    for rel in files:
        # Windows' mount manager keeps its own hidden system file on every
        # volume it mounts; the guest manifest skips hidden system files.
        if rel.startswith('System Volume Information/'):
            continue
        failures.append('%s %s: not in the guest manifest' % (label, rel))


def main():
    manifest = json.load(open(os.path.join(LOCAL, 'manifest.json'),
                              encoding='utf-8-sig'))
    vm('zpool list -H -o name %s >/dev/null 2>&1 && zpool export %s; '
       'rm -rf %s && mkdir -p %s' % (POOL, POOL, REMOTE, REMOTE))
    for name in ('a.img', 'd.img'):
        scp(os.path.join(LOCAL, name), 'root@%s:%s/%s' % (HOST, REMOTE, name))
    out = vm('''set -e
zpool import -d %(r)s %(p)s
trap 'cd /; zpool export %(p)s 2>/dev/null || true' EXIT
zpool status %(p)s | grep -E 'mirror|img'
zfs get -H -o property,value,source compression,atime,com.zfswin:test %(ds)s
zfs list -H -t snapshot -o name -r %(ds)s
mnt=$(zfs get -H -o value mountpoint %(ds)s)
python3 -c %(m)s "$mnt" > /tmp/zwi-current.json
python3 -c %(m)s "$mnt/.zfs/snapshot/s1" > /tmp/zwi-s1.json
zpool scrub -w %(p)s
zpool status -v %(p)s | grep -E 'state:|scan:|errors:'
zpool export %(p)s
trap - EXIT
zdb -e -p %(r)s -bcc %(p)s 2>&1 | grep -E 'leaked|No leaks'
cat /tmp/zwi-current.json; echo; cat /tmp/zwi-s1.json
''' % {'r': REMOTE, 'p': POOL, 'ds': DATASET,
       'm': shell_quote(MANIFEST_PY)}, capture=True)
    lines = [line for line in out.strip().splitlines() if line.strip()]
    snapshot, current = json.loads(lines[-1]), json.loads(lines[-2])
    for line in lines[:-2]:
        log('  ' + line.strip())
    failures = []
    compare('current', manifest['current'], current, failures)
    compare('@s1', manifest['s1'], snapshot, failures)
    for expect in ('compression\tzstd\tlocal', 'atime\toff\tlocal',
                   'com.zfswin:test\thello world\tlocal', '%s@s1' % DATASET):
        if expect not in out:
            failures.append('missing: %r' % expect)
    if '%s@s2' % DATASET in out:
        failures.append('@s2 was destroyed through zfswin but still exists')
    if 'No leaks' not in out or 'with 0 errors' not in out:
        failures.append('scrub or zdb -bcc reported problems')
    if failures:
        raise SystemExit('FAIL:\n  ' + '\n  '.join(failures))
    log('PASS: %d current files and %d @s1 files verified by Linux OpenZFS; '
        'zfswin properties present; scrub clean; no leaks'
        % (len(manifest['current']), len(manifest['s1'])))


if __name__ == '__main__':
    main()
