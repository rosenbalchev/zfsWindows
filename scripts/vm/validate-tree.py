#!/usr/bin/env python3
"""Upstream validation of the "tree" pools (guest-installer.ps1 stage tree).

Each directory holds tree.img (pool ztree, written on Windows) and
tree-manifest.tsv.  Linux OpenZFS imports a copy of the pool and must find
every file with its size, SHA-256 and modification time, and the same file
and directory count per dataset; then a scrub and zdb -bcc.  (Not
read-only: the child datasets' mountpoint directories do not exist in the
parent dataset, since Windows removes them on unmount, so a read-only
import cannot mount the children.)

Usage: python scripts/vm/validate-tree.py [vm-host] <dir> [<dir>...]
"""
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                '..'))
from zfstest import HOST, log, scp, vm

POOL = 'ztree'
REMOTE = '/root/zfswin-driver/tree'
ALTROOT = '/tmp/ztree-check'

# Run on the VM: one line per file and per dataset count, tab-separated.
WALK_PY = r'''
import hashlib, os, subprocess, sys
for index, ds in enumerate(sys.argv[1:]):
    top = subprocess.check_output(['zfs', 'get', '-H', '-o', 'value', 'mountpoint', ds]).decode().strip()
    files = dirs = 0
    for base, subdirs, names in os.walk(top):
        # Windows' own folders (the Windows-side count skips them too).
        subdirs[:] = [d for d in subdirs if d not in ('System Volume Information', '$RECYCLE.BIN')]
        dirs += len(subdirs)
        for name in names:
            path = os.path.join(base, name)
            st = os.lstat(path)
            data = open(path, 'rb').read()
            rel = os.path.relpath(path, top).replace(os.sep, '/')
            print('F\t%d\t%s\t%d\t%s\t%d' % (index, rel, st.st_size, hashlib.sha256(data).hexdigest().upper(), int(st.st_mtime)))
            files += 1
    print('C\t%d\t%d\t%d' % (index, files, dirs))
'''


def read_manifest(path):
    datasets, counts, files = [], {}, {}
    with open(path, encoding='utf-8') as f:
        for line in f:
            line = line.rstrip('\n')
            if not line:
                continue
            fields = line.split('\t')
            if fields[0] == '#dataset':
                datasets.append(fields[2])
                counts[int(fields[1])] = (int(fields[3]), int(fields[4]))
                continue
            files[(int(fields[0]), fields[1])] = (int(fields[2]), fields[3].upper(), int(fields[5]))
    return datasets, counts, files


def validate(local):
    datasets, counts, expected = read_manifest(os.path.join(local, 'tree-manifest.tsv'))
    vm('zpool list -H -o name %(p)s >/dev/null 2>&1 && zpool export %(p)s; '
       'rm -rf %(r)s && mkdir -p %(r)s' % {'p': POOL, 'r': REMOTE})
    scp(os.path.join(local, 'tree.img'), 'root@%s:%s/tree.img' % (HOST, REMOTE))
    out = vm('''set -e
zpool import -R %(a)s -d %(r)s %(p)s
trap 'cd /; zpool export %(p)s 2>/dev/null || true' EXIT
python3 -c %(w)s %(ds)s
zpool scrub -w %(p)s
zpool status -v %(p)s | grep -E 'state:|scan:|errors:'
zpool export %(p)s
trap - EXIT
zdb -e -p %(r)s -bcc %(p)s 2>&1 | grep -E 'leaked|No leaks'
''' % {'a': ALTROOT, 'r': REMOTE, 'p': POOL, 'ds': ' '.join(datasets),
       'w': "'" + WALK_PY.replace("'", "'\\''") + "'"}, capture=True)
    actual, seen_counts, other = {}, {}, []
    for line in out.splitlines():
        fields = line.split('\t')
        if fields[0] == 'F' and len(fields) == 6:
            actual[(int(fields[1]), fields[2])] = (int(fields[3]), fields[4], int(fields[5]))
        elif fields[0] == 'C' and len(fields) == 4:
            seen_counts[int(fields[1])] = (int(fields[2]), int(fields[3]))
        elif line.strip():
            other.append(line.strip())
    for line in other:
        log('  ' + line)
    failures = []
    for key, want in expected.items():
        got = actual.get(key)
        if got is None:
            failures.append('missing %s/%s' % (datasets[key[0]], key[1]))
        elif got != want:
            failures.append('differs %s/%s: %s != %s' % (datasets[key[0]], key[1], got, want))
    for key in actual:
        if key not in expected:
            failures.append('unexpected %s/%s' % (datasets[key[0]], key[1]))
    for index, want in counts.items():
        if seen_counts.get(index) != want:
            failures.append('count %s: files/dirs %s != %s' % (datasets[index], seen_counts.get(index), want))
    if 'No leaks' not in out or 'with 0 errors' not in out:
        failures.append('scrub or zdb -bcc reported problems')
    if failures:
        log('FAIL %s (%d problems):\n  %s' % (local, len(failures), '\n  '.join(failures[:30])))
        return False
    log('PASS %s: %d files in %d datasets (size, SHA-256, mtime; file and '
        'directory counts) verified by Linux OpenZFS; scrub clean; no leaks'
        % (local, len(expected), len(datasets)))
    return True


def main():
    dirs = [a for a in sys.argv[1:] if a != HOST]
    if not dirs:
        raise SystemExit(__doc__)
    results = [validate(d) for d in dirs]
    if not all(results):
        raise SystemExit(1)


if __name__ == '__main__':
    main()
