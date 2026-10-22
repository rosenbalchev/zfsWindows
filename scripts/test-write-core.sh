#!/usr/bin/env bash
# End-to-end check of zfs-write-core against upstream Linux OpenZFS.
#
# Creates a fresh disposable pool on the Linux VM, mutates a copy of it on
# Windows with zfs-write-core.exe, then imports the result on Linux and
# verifies namespace, sizes, SHA-256 content, scrub, and space-map leaks.
#
# Usage: scripts/test-write-core.sh [vm-host]
# Environment: ZFS_VM_HOST, ZFS_VM_KEY, ZFS_WRITE_CORE (exe path).
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
host=${1:-${ZFS_VM_HOST:-192.168.195.63}}
key=${ZFS_VM_KEY:-$HOME/.ssh/zfswindows-agent_ed25519}
exe=${ZFS_WRITE_CORE:-$root/out/openzfs-write-debug/zfs-write-core/zfs-write-core.exe}
pool=zfswin_write
dataset=$pool/data
remote=/root/zfswin-write
work=$root/TestVolumes/write
ssh_opts=(-i "$key" -o BatchMode=yes -o ConnectTimeout=10)

vm() { ssh "${ssh_opts[@]}" "root@$host" "$@"; }
winpath() { cygpath -w "$1"; }

[ -x "$exe" ] || { echo "missing $exe; run scripts/build-write-core.ps1" >&2; exit 1; }

echo "== creating base pool on $host"
vm "set -e
    zpool list -H -o name $pool >/dev/null 2>&1 && zpool export $pool
    rm -rf $remote && mkdir -p $remote/base $remote/out
    truncate -s 256M $remote/base/disk01.raw
    zpool create -o cachefile=none -O compression=lz4 $pool $remote/base/disk01.raw
    zfs create $dataset
    printf 'hello\n' > /$dataset/existing.txt
    mkdir /$dataset/dir
    zpool export $pool"

rm -rf "$work" && mkdir -p "$work/run" "$work/payload" "$work/expected"
scp -q "${ssh_opts[@]}" "root@$host:$remote/base/disk01.raw" "$work/run/disk01.raw"

echo "== generating payloads"
python - "$work" <<'PY'
import os, random, sys
work = sys.argv[1]
rnd = random.Random(20261022)
def put(name, data):
    open(os.path.join(work, 'payload', name), 'wb').write(data)
small = b'written by zfs-write-core\n'
big = bytes(rnd.getrandbits(8) for _ in range(3 * 1024 * 1024 + 4321))
tail = b'appended tail\n' * 71
patch = bytes(rnd.getrandbits(8) for _ in range(5000))
sparse = b'after the hole\n'
for n, d in [('small', small), ('big', big), ('tail', tail),
             ('patch', patch), ('sparse', sparse)]:
    put(n, d)
expected = {
    'small.txt': small,
    'big.bin': bytearray(big + tail),
    'sparse.bin': b'\0' * (1024 * 1024) + sparse,
    'empty.txt': b'',
}
expected['big.bin'][100000:105000] = patch
for n, d in expected.items():
    open(os.path.join(work, 'expected', n), 'wb').write(bytes(d))
PY

run() { echo "+ $*"; "$exe" "$1" "$(winpath "$work/run")" "$pool" "$dataset" "${@:2}"; }
p() { winpath "$work/payload/$1"; }

echo "== mutating image on Windows"
run create-file small.txt
run write-file small.txt 0 "$(p small)"
run create-file big.bin
run write-file big.bin 0 "$(p big)"
run write-file big.bin append "$(p tail)"
run write-file big.bin 100000 "$(p patch)"
run create-file sparse.bin
run write-file sparse.bin 1048576 "$(p sparse)"
run create-file empty.txt
if run create-file small.txt 2>/dev/null; then
    echo "FAIL: duplicate create succeeded" >&2; exit 1
fi

(cd "$work/expected" && sha256sum -- * | sed 's/ \*/  /' | sort -k2) > "$work/expected.sha256"
scp -q "${ssh_opts[@]}" "$work/run/disk01.raw" "root@$host:$remote/out/disk01.raw"
scp -q "${ssh_opts[@]}" "$work/expected.sha256" "root@$host:$remote/expected.sha256"

echo "== verifying on Linux OpenZFS"
vm "set -e
    zpool import -d $remote/out $pool
    trap 'zpool export $pool 2>/dev/null || true' EXIT
    cd /$dataset
    ls -la
    test \"\$(ls -A | sort | tr '\n' ' ')\" = 'big.bin dir empty.txt existing.txt small.txt sparse.bin '
    sha256sum -c $remote/expected.sha256
    stat -c '%n %s %a %h' big.bin small.txt sparse.bin empty.txt
    test \"\$(stat -c %s /$dataset)\" = 8  # ZPL dir size: 2 + six entries
    printf 'linux append\n' >> small.txt
    zpool scrub -w $pool
    zpool status -x $pool | grep -q 'is healthy'
    zpool status $pool | grep -E 'scan|errors'
    cd /
    zpool export $pool
    trap - EXIT
    zdb -e -p $remote/out -bcc $pool | grep -E 'leaked|No leaks'
    zdb -e -p $remote/out -bcc $pool | grep -q 'No leaks'"
echo "PASS"
