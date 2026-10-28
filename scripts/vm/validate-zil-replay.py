#!/usr/bin/env python3
"""Let Linux OpenZFS replay intent logs written by the Windows driver.

Test-ZilReplay.ps1 leaves TestVolumes/vm/zil-unreplayed/ with the step-13
power-cut pools (never imported after the cut, so their ZIL is unreplayed)
and the writers' ack logs.  Linux imports them (-f: they were not
exported), which replays the logs, and reports per dataset whether every
acknowledged file exists with its full size and whether any entry has an
unexpected name.  Durable modes (flush, writethrough, syncalways, slog)
must have every acknowledged file; the others may lose recent ones.

Usage: python scripts/vm/validate-zil-replay.py [vm-host]
"""
import json
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                '..'))
from zfstest import HOST, ROOT, log, scp, shell_quote, vm

LOCAL = os.path.join(ROOT, 'TestVolumes', 'vm', 'zil-unreplayed')
REMOTE = '/root/zfswin-driver/zil'
DURABLE = {'flush', 'writethrough', 'syncalways', 'slog'}

LIST_PY = r'''
import json, os, sys
out = {}
for path in sys.argv[1:]:
    entries = {}
    if os.path.isdir(path):
        for n in os.listdir(path):
            entries[n] = os.lstat(os.path.join(path, n)).st_size
    out[path] = entries
print(json.dumps(out))
'''


def main():
    vm('for p in zw_main zw_slog; do zpool list -H -o name $p >/dev/null 2>&1 '
       '&& zpool export $p; done; rm -rf %s && mkdir -p %s/zw_main %s/zw_slog'
       % (REMOTE, REMOTE, REMOTE))
    scp(os.path.join(LOCAL, 'main.raw'), 'root@%s:%s/zw_main/' % (HOST, REMOTE))
    for f in ('slogdata.raw', 'slog.raw'):
        scp(os.path.join(LOCAL, f), 'root@%s:%s/zw_slog/' % (HOST, REMOTE))
    failures = []
    for pool, datasets in (('zw_main', ('buffered', 'flush', 'writethrough',
                                        'syncalways', 'syncdisabled')),
                           ('zw_slog', ('slog',))):
        vm('zpool import -f -d %s/%s %s' % (REMOTE, pool, pool))
        try:
            dirs = {d: vm('zfs get -H -o value mountpoint %s/%s' % (pool, d),
                          capture=True).strip() + '/w' for d in datasets}
            listing = json.loads(vm('python3 -c %s %s' % (
                shell_quote(LIST_PY),
                ' '.join(shell_quote(p) for p in dirs.values())), capture=True))
            for d, path in dirs.items():
                entries = listing[path]
                ack = os.path.join(LOCAL, 'ack-%s.log' % d)
                acked = [int(l) for l in open(ack).read().split()] if os.path.exists(ack) else []
                names = {'w-%06d.bin' % n for n in acked}
                missing = sorted(n for n in names if n not in entries)
                short = sorted(n for n in names if n in entries and entries[n] != 65536)
                odd = sorted(repr(n) for n in entries
                             if not (n.startswith('w-') and n.endswith('.bin')))
                log('  %s/%s: acked %d, present %d, acked missing %d, acked short %d, odd names %s'
                    % (pool, d, len(acked), len(entries), len(missing), len(short), odd))
                if d in DURABLE and (missing or short):
                    failures.append('%s/%s: acknowledged files missing %s short %s'
                                    % (pool, d, missing[:5], short[:5]))
                if odd:
                    failures.append('%s/%s: unexpected names %s' % (pool, d, odd))
            out = vm('zpool scrub -w %s; zpool status %s | grep -E "scan:|errors:"'
                     % (pool, pool), capture=True)
            log('  ' + out.strip().replace('\n', '\n  '))
        finally:
            vm('cd /; zpool export %s' % pool)
    if failures:
        raise SystemExit('FAIL:\n  ' + '\n  '.join(failures))
    log('PASS: Linux replayed the Windows-written intent logs; every '
        'acknowledged durable write is present')


if __name__ == '__main__':
    main()
