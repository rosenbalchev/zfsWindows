#!/usr/bin/env python3
"""Linux OpenZFS reference for the step-13 L2ARC scenario.

Runs on the Linux verification VM the same workload that guest-cache.ps1
-Stage l2arc runs through the Windows driver, so the Windows counters can
be judged against upstream behaviour instead of expectations:

- zfs_arc_min 64 MiB, then zfs_arc_max 192 MiB (Linux ignores an arc_max
  below arc_min, which defaults to 1/32 of RAM), l2arc_noprefetch 0,
  l2arc_write_max/boost 64 MiB;
- a 1536 MiB data vdev and a 512 MiB cache vdev (file-backed);
- l2arc_rebuild_blocks_min_l2size 0 (the default, 1 GiB, would skip
  persistent L2ARC on this cache device);
- a 512 MiB random file, then 4 passes of 4,096 random 128 KiB reads from a
  fixed seed (a working set larger than the ARC; sequential rereads would
  be served by prefetch and rarely reach the L2ARC), 10 s apart;
- export, overwrite 256 MiB of the cache device past its labels, import
  (persistent L2ARC rebuild), and 2 more passes, which must return the same
  bytes (checksummed L2ARC reads fall back to the pool).

Every tunable is restored afterwards.  Prints the arcstats deltas as JSON.

Usage: python scripts/vm/l2arc-reference.py [vm-host]
"""
import json
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                '..'))
from zfstest import log, vm

SCRIPT = r'''
set -e
D=/root/zfswin-l2ref
P=/sys/module/zfs/parameters
zpool list -H -o name zr_l2 >/dev/null 2>&1 && zpool export zr_l2
rm -rf $D && mkdir -p $D
saved="$(cat $P/zfs_arc_min) $(cat $P/zfs_arc_max) $(cat $P/l2arc_noprefetch) $(cat $P/l2arc_write_max) $(cat $P/l2arc_write_boost) $(cat $P/l2arc_rebuild_blocks_min_l2size)"
restore() {
    set -- $saved
    echo $2 > $P/zfs_arc_max; echo $1 > $P/zfs_arc_min
    echo $3 > $P/l2arc_noprefetch
    echo $4 > $P/l2arc_write_max; echo $5 > $P/l2arc_write_boost
    echo $6 > $P/l2arc_rebuild_blocks_min_l2size
    zpool list -H -o name zr_l2 >/dev/null 2>&1 && zpool export zr_l2 || true
    rm -rf $D
}
trap restore EXIT
echo 67108864 > $P/zfs_arc_min
echo 201326592 > $P/zfs_arc_max
grep -q "^c_max .* 201326592$" /proc/spl/kstat/zfs/arcstats || { echo "zfs_arc_max not applied"; exit 1; }
echo 0 > $P/l2arc_noprefetch
echo 67108864 > $P/l2arc_write_max
echo 67108864 > $P/l2arc_write_boost
echo 0 > $P/l2arc_rebuild_blocks_min_l2size
truncate -s 1536M $D/data.raw
truncate -s 512M $D/cache.raw
zpool create -f -o ashift=12 -o cachefile=none -O compression=off \
    -O mountpoint=$D/mnt zr_l2 $D/data.raw cache $D/cache.raw
mkdir -p $D/mnt/data
head -c 536870912 /dev/urandom > $D/mnt/data/random-0.bin
# Random 128 KiB reads from a fixed seed; prints the SHA-256 of all bytes read.
rand() {
    python3 - "$D/mnt/data/random-0.bin" <<'EOF'
import hashlib, os, random, sys
fd = os.open(sys.argv[1], os.O_RDONLY)
blocks = os.fstat(fd).st_size // 131072
rng, h = random.Random(1307), hashlib.sha256()
for _ in range(4096):
    h.update(os.pread(fd, 131072, rng.randrange(blocks) * 131072))
print(h.hexdigest())
EOF
}
zpool export zr_l2; zpool import -d $D zr_l2
want=$(rand)

stats() {
    awk 'NR>2 {print $1, $3}' /proc/spl/kstat/zfs/arcstats |
    grep -E '^(l2_hits|l2_misses|l2_size|l2_cksum_bad|l2_io_error|l2_writes_done|l2_rebuild_success|l2_rebuild_cksum_lb_errors|l2_rebuild_io_errors|l2_rebuild_bufs) ' |
    tr ' ' '=' | paste -sd' '
}
s0=$(stats)
for i in 1 2 3 4; do
    sleep 10
    got=$(rand)
    [ "$got" = "$want" ] || echo "WRONG read $i"
done
s1=$(stats)
zpool export zr_l2
# Past the front labels, as on Windows.
dd if=/dev/urandom of=$D/cache.raw bs=1M seek=32 count=256 conv=notrunc status=none
zpool import -d $D zr_l2
sleep 5
s2=$(stats)
for i in 1 2; do
    got=$(rand)
    [ "$got" = "$want" ] || echo "WRONG read after corruption $i"
done
s3=$(stats)
echo "S0 $s0"; echo "S1 $s1"; echo "S2 $s2"; echo "S3 $s3"
zpool status zr_l2 | grep -E 'state|errors|cache' | head -5
'''


def parse(line):
    return {k: int(v) for k, v in (kv.split('=') for kv in line.split()[1:])}


def main():
    out = vm(SCRIPT, capture=True)
    lines = out.strip().splitlines()
    snaps = {l.split()[0]: parse(l) for l in lines if l[:2] in ('S0', 'S1', 'S2', 'S3')}
    delta = lambda a, b: {k: snaps[b][k] - snaps[a][k] for k in snaps[a]}
    result = {
        'feed': delta('S0', 'S1'),
        'l2SizeAfterFeed': snaps['S1']['l2_size'],
        'rebuildOnImport': delta('S1', 'S2'),
        'afterCorrupt': delta('S2', 'S3'),
        'wrongReads': [l for l in lines if l.startswith('WRONG')],
        'status': [l.strip() for l in lines if not l[:2] in ('S0', 'S1', 'S2', 'S3')
                   and not l.startswith('WRONG')],
    }
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
