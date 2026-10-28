#!/usr/bin/env python3
"""Upstream validation of the pools written by Test-DriverInVm.ps1 -Phase extended.

TestVolumes/vm/extended/ holds lz4/lz4.raw (the Linux-created lz4 fixture
after the extended workload), mirror/a.raw and mirror/b.raw (a mirror the
driver created, whose member b was corrupted and repaired by a Windows
scrub), and the verifier's expectation JSON files.  Linux OpenZFS must:

- import both pools without rewind;
- match windows-extended/ to the post-mutation expectation, and its @win1
  snapshot to the pre-mutation one (through .zfs/snapshot);
- match the dedup, zstd and gzip child datasets, with dedup and compression
  actually in effect (dedupratio, compressratio, zdb -DD);
- find every alternate data stream as a user.<name>:$DATA xattr with the
  same bytes (a value over Linux's 64 KiB xattr limit is matched by name),
  and the BUILTIN\\Users grant in each edited file's NT security descriptor
  (user.windows:security.NTACL);
- scrub both pools clean and report no leaks with zdb -bcc.

Usage: python scripts/vm/validate-extended.py [vm-host]
"""
import json
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                '..'))
from zfstest import HOST, MANIFEST_PY, ROOT, log, scp, shell_quote, vm

LOCAL = os.path.join(ROOT, 'TestVolumes', 'vm', 'extended')
REMOTE = '/root/zfswin-driver/extended'
# BUILTIN\Users (S-1-5-32-545) as a binary SID.
USERS_SID = '01020000000000052000000021020000'

# Run on the VM: every xattr of the given paths, with its size and SHA-256.
XATTR_PY = r'''
import errno, hashlib, json, os, sys
out = {}
for path in sys.argv[1:]:
    out[path] = {}
    for name in os.listxattr(path):
        try:
            value = os.getxattr(path, name)
        except OSError as e:
            # Linux caps an xattr value at 64 KiB (XATTR_SIZE_MAX).
            if e.errno != errno.E2BIG:
                raise
            out[path][name] = {'size': None, 'sha256': None, 'hex': ''}
            continue
        out[path][name] = {'size': len(value),
                           'sha256': hashlib.sha256(value).hexdigest().upper(),
                           'hex': value.hex() if len(value) < 4096 else ''}
print(json.dumps(out))
'''


def expectation(name):
    with open(os.path.join(LOCAL, name + '.json'), encoding='utf-8-sig') as f:
        return json.load(f)['result']


def compare(label, actual, expect, failures):
    """actual: MANIFEST_PY output; expect: verifier files/dirs."""
    files = {k: v['sha256'].upper() for k, v in actual.items()
             if v['type'] == 'file'}
    dirs = {k for k, v in actual.items() if v['type'] == 'dir' and k}
    want = {k: v.upper() for k, v in expect['files'].items()}
    for rel in sorted(set(want) - set(files))[:10]:
        failures.append('%s: missing file %s' % (label, rel))
    for rel in sorted(set(files) - set(want))[:10]:
        failures.append('%s: unexpected file %s' % (label, rel))
    bad = [rel for rel in want if rel in files and files[rel] != want[rel]]
    for rel in bad[:10]:
        failures.append('%s: %s differs' % (label, rel))
    if dirs != set(expect['dirs']):
        failures.append('%s: directories differ (%d vs %d)' % (
            label, len(dirs), len(expect['dirs'])))
    log('  %s: %d files, %d directories, %d mismatches' % (
        label, len(files), len(dirs), len(bad)))


def manifest(path):
    return json.loads(vm('python3 -c %s %s' % (shell_quote(MANIFEST_PY),
                                               shell_quote(path)),
                         capture=True))


def main():
    failures = []
    vm('for p in zc_lz4 zw_mirror; do zpool list -H -o name $p >/dev/null '
       '2>&1 && zpool export $p; done; rm -rf %s && mkdir -p %s/lz4 %s/mirror'
       % (REMOTE, REMOTE, REMOTE))
    for rel in ('lz4/lz4.raw', 'mirror/a.raw', 'mirror/b.raw'):
        scp(os.path.join(LOCAL, *rel.split('/')),
            'root@%s:%s/%s' % (HOST, REMOTE, rel))

    # --- zc_lz4 ---------------------------------------------------------
    vm('zpool import -d %s/lz4 zc_lz4' % REMOTE)
    try:
        mnt = vm('zfs get -H -o value mountpoint zc_lz4/data',
                 capture=True).strip()
        ext = mnt + '/windows-extended'
        compare('live windows-extended', manifest(ext),
                expectation('snapshot-mutate'), failures)
        compare('@win1 windows-extended',
                manifest(mnt + '/.zfs/snapshot/win1/windows-extended'),
                expectation('extended-write'), failures)
        for child in ('dedup', 'zstd', 'gzip'):
            path = vm('zfs get -H -o value mountpoint zc_lz4/data/' + child,
                      capture=True).strip()
            compare(child, manifest(path), expectation('fill-' + child),
                    failures)

        # Alternate data streams and DACLs, as Linux sees them.
        before = expectation('extended-write')
        paths = sorted({ext + '/' + k.split(':')[0] for k in before['streams']}
                       | {ext + '/' + a for a in before['acls']})
        xattrs = json.loads(vm('python3 -c %s %s' % (
            shell_quote(XATTR_PY), ' '.join(shell_quote(p) for p in paths)),
            capture=True))
        for key, sha in before['streams'].items():
            rel, stream = key.split(':')
            found = [n for n, v in xattrs[ext + '/' + rel].items()
                     if v['sha256'] == sha.upper()]
            # A value over 64 KiB is listed but unreadable through the
            # Linux xattr API; match it by name only.
            unreadable = [n for n, v in xattrs[ext + '/' + rel].items()
                          if v['size'] is None and stream in n]
            log('  stream %s: xattr %s' % (key, found or (
                '%s listed, value over the Linux 64 KiB xattr limit'
                % unreadable if unreadable else 'NOT FOUND')))
            if not found and not unreadable:
                failures.append('stream %s has no matching xattr' % key)
        for rel in before['acls']:
            # The driver stores the descriptor as "windows:security.NTACL",
            # which Linux presents in the user namespace.
            ntacl = xattrs[ext + '/' + rel].get('user.windows:security.NTACL')
            granted = ntacl is not None and USERS_SID in ntacl['hex']
            log('  acl %s: user.windows:security.NTACL %s, BUILTIN\\Users %s' % (
                rel, '%d bytes' % ntacl['size'] if ntacl else 'missing',
                'present' if granted else 'ABSENT'))
            if not granted:
                failures.append('acl %s: Users grant not in the NT ACL xattr'
                                % rel)

        props = vm('zfs get -H -o name,property,value compression,'
                   'compressratio,dedup -r zc_lz4/data | grep -v @',
                   capture=True)
        for line in props.strip().splitlines():
            log('  ' + line)
        ratio = {l.split('\t')[0]: l.split('\t')[2] for l in props.splitlines()
                 if '\tcompressratio\t' in l}
        for child in ('zstd', 'gzip'):
            if float(ratio['zc_lz4/data/' + child].rstrip('x')) < 2:
                failures.append('%s compressratio %s' % (
                    child, ratio['zc_lz4/data/' + child]))
        dedup = vm('zpool get -H -o value dedupratio zc_lz4; '
                   "zdb -DD zc_lz4 | grep -E 'DDT-|dedup =' | head -5",
                   capture=True).strip().splitlines()
        for line in dedup:
            log('  ' + line.strip())
        if float(dedup[0].rstrip('x')) < 1.2:
            failures.append('pool dedupratio %s' % dedup[0])
        out = vm('''set -e
zpool scrub -w zc_lz4
zpool status -v zc_lz4 | grep -E 'state:|scan:|errors:'
''', capture=True)
        for line in out.strip().splitlines():
            log('  ' + line.strip())
        if 'with 0 errors' not in out:
            failures.append('zc_lz4 scrub not clean')
    finally:
        vm('cd /; zpool export zc_lz4')
    out = vm('zdb -e -p %s/lz4 -bcc zc_lz4 2>&1 | grep -E "leaked|No leaks"'
             % REMOTE, capture=True)
    log('  zc_lz4 ' + out.strip())
    if 'No leaks' not in out:
        failures.append('zc_lz4: zdb -bcc reported leaks')

    # --- zw_mirror ------------------------------------------------------
    vm('zpool import -d %s/mirror zw_mirror' % REMOTE)
    try:
        mnt = vm('zfs get -H -o value mountpoint zw_mirror',
                 capture=True).strip()
        compare('zw_mirror data', manifest(mnt + '/data'),
                expectation('mirror-fill'), failures)
        out = vm('''set -e
zpool scrub -w zw_mirror
zpool status -v zw_mirror | grep -E 'state:|scan:|errors:|raw'
''', capture=True)
        for line in out.strip().splitlines():
            log('  ' + line.strip())
        if 'repaired 0B' not in out or 'with 0 errors' not in out:
            failures.append('zw_mirror scrub not clean on Linux')
    finally:
        vm('cd /; zpool export zw_mirror')
    out = vm('zdb -e -p %s/mirror -bcc zw_mirror 2>&1 | grep -E '
             '"leaked|No leaks"' % REMOTE, capture=True)
    log('  zw_mirror ' + out.strip())
    if 'No leaks' not in out:
        failures.append('zw_mirror: zdb -bcc reported leaks')

    if failures:
        raise SystemExit('FAIL:\n  ' + '\n  '.join(failures))
    log('PASS: extended driver workload, snapshots, dedup, compression, '
        'streams, DACLs and the scrub-repaired mirror verified by Linux '
        'OpenZFS; scrubs clean; no leaks')


if __name__ == '__main__':
    main()
