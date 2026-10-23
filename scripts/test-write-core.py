#!/usr/bin/env python3
"""End-to-end check of zfs-write-core against upstream Linux OpenZFS.

A fresh disposable pool is created on the Linux VM and copied to Windows.
Windows mutates it with zfs-write-core.exe (one import/export cycle per
command), Linux imports and verifies it, mutates it in turn, and Windows then
reopens and mutates the Linux-written image once more before a final Linux
verification.  Every expected file byte, directory size and link count comes
from an in-memory model that the same operation list drives, and each
verification includes scrub, `zdb -bcc` leak checking, and the unlinked set.

Usage: python scripts/test-write-core.py [vm-host]
Environment: ZFS_VM_HOST, ZFS_VM_KEY, ZFS_WRITE_CORE (exe path).
"""
import errno
import hashlib
import json
import os
import random
import re
import shutil
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
HOST = (sys.argv[1] if len(sys.argv) > 1
        else os.environ.get('ZFS_VM_HOST', '192.168.195.63'))
KEY = os.environ.get('ZFS_VM_KEY',
                     os.path.expanduser('~/.ssh/zfswindows-agent_ed25519'))
EXE = os.environ.get('ZFS_WRITE_CORE', os.path.join(
    ROOT, 'out', 'openzfs-write-debug', 'zfs-write-core',
    'zfs-write-core.exe'))
POOL = 'zfswin_write'
DATASET = POOL + '/data'
REMOTE = '/root/zfswin-write'
WORK = os.path.join(ROOT, 'TestVolumes', 'write')
SSH = ['-i', KEY, '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=10']

# Run on the VM: list the dataset as JSON (type, size, links, SHA-256).
MANIFEST_PY = r'''
import hashlib, json, os, sys
top = sys.argv[1]
out = {}
for base, dirs, files in os.walk(top):
    for name in dirs + files:
        path = os.path.join(base, name)
        st = os.lstat(path)
        rel = os.path.relpath(path, top).replace(os.sep, '/')
        entry = {'size': st.st_size, 'links': st.st_nlink}
        if os.path.isdir(path):
            entry['type'] = 'dir'
        else:
            entry['type'] = 'file'
            entry['sha256'] = hashlib.sha256(open(path, 'rb').read()).hexdigest()
        out[rel] = entry
st = os.stat(top)
out[''] = {'type': 'dir', 'size': st.st_size, 'links': st.st_nlink}
print(json.dumps(out))
'''


def log(text):
    print(text, flush=True)


def vm(script, capture=False):
    # Bytes, not text: Windows text mode would send CRLF line endings.
    result = subprocess.run(['ssh', *SSH, 'root@' + HOST, 'bash -s'],
                            input=script.encode('utf-8'), check=True,
                            stdout=subprocess.PIPE if capture else None)
    return result.stdout.decode('utf-8') if capture else None


def scp(source, target):
    subprocess.run(['scp', '-q', *SSH, source, target], check=True)


class Model:
    """Expected namespace: '' is the dataset root."""

    def __init__(self):
        self.dirs = {''}
        self.files = {}

    @staticmethod
    def parent(path):
        return path.rpartition('/')[0]

    def create(self, path):
        self.files[path] = bytearray()

    def mkdir(self, path):
        self.dirs.add(path)

    def write(self, path, offset, data):
        content = self.files[path]
        if offset == 'append':
            offset = len(content)
        if len(content) < offset:
            content.extend(b'\0' * (offset - len(content)))
        content[offset:offset + len(data)] = data

    def truncate(self, path, size):
        content = self.files[path]
        if size < len(content):
            del content[size:]
        else:
            content.extend(b'\0' * (size - len(content)))

    def remove(self, path):
        del self.files[path]

    def rmdir(self, path):
        self.dirs.remove(path)

    def rename(self, source, target):
        if source in self.files:
            self.files[target] = self.files.pop(source)
            return
        self.dirs.discard(target)
        prefix = source + '/'
        self.dirs = {target + d[len(source):] if d == source or
                     d.startswith(prefix) else d for d in self.dirs}
        self.files = {target + f[len(source):] if f.startswith(prefix)
                      else f: c for f, c in self.files.items()}

    def expected(self):
        """ZPL directory size is 2 + entries; links are 2 + subdirs."""
        out = {}
        for d in self.dirs:
            entries = [p for p in list(self.dirs) + list(self.files)
                       if p and self.parent(p) == d]
            subdirs = [p for p in entries if p in self.dirs]
            out[d] = {'type': 'dir', 'size': 2 + len(entries),
                      'links': 2 + len(subdirs)}
        for f, content in self.files.items():
            out[f] = {'type': 'file', 'size': len(content), 'links': 1,
                      'sha256': hashlib.sha256(content).hexdigest()}
        return out


class Windows:
    """Runs zfs-write-core commands and mirrors their effect in the model."""

    def __init__(self, model, image_dir, payloads):
        self.model = model
        self.image_dir = image_dir
        self.payloads = payloads
        self.cycles = 0

    def run(self, command, *args, expect_errno=None):
        argv = [EXE, command, self.image_dir, POOL, DATASET, *map(str, args)]
        result = subprocess.run(argv, capture_output=True, text=True)
        self.cycles += 1
        stderr = '\n'.join(line for line in result.stderr.splitlines()
                           if 'Assuming 512' not in line)
        if expect_errno is None:
            if result.returncode != 0:
                raise SystemExit('FAIL: %s %s\n%s%s' % (
                    command, ' '.join(map(str, args)), result.stdout, stderr))
            log('+ %s %s -> %s' % (command, ' '.join(map(str, args)),
                                    result.stdout.strip()))
            return result.stdout
        match = re.search(r'^%s: .*\((\d+)\)$' % re.escape(command), stderr,
                          re.M)
        if result.returncode == 0 or not match or \
                int(match.group(1)) != expect_errno:
            raise SystemExit('FAIL: %s %s should fail with %s\n%s%s' % (
                command, ' '.join(map(str, args)),
                errno.errorcode[expect_errno], result.stdout, stderr))
        log('+ %s %s -> %s (expected)' % (
            command, ' '.join(map(str, args)), errno.errorcode[expect_errno]))
        return None

    def create(self, path):
        self.run('create-file', path)
        self.model.create(path)

    def mkdir(self, path):
        self.run('mkdir', path)
        self.model.mkdir(path)

    def write(self, path, offset, payload):
        self.run('write-file', path, offset, self.payloads[payload][0])
        self.model.write(path, offset, self.payloads[payload][1])

    def truncate(self, path, size):
        self.run('truncate', path, size)
        self.model.truncate(path, size)

    def remove(self, path, *flags):
        self.run('remove', path, *flags)
        self.model.remove(path)

    def rmdir(self, path, *flags):
        self.run('rmdir', path, *flags)
        self.model.rmdir(path)

    def rename(self, source, target):
        self.run('rename', source, target)
        self.model.rename(source, target)

    def stat_size(self, path):
        out = self.run('stat', path)
        return int(re.search(r' size=(\d+)', out).group(1))


def make_payloads(directory):
    rnd = random.Random(20261022)
    data = {
        'small': b'written by zfs-write-core\n',
        'big': rnd.randbytes(3 * 1024 * 1024 + 4321),
        'tail': b'appended tail\n' * 71,
        'patch': rnd.randbytes(5000),
        'sparse': b'after the hole\n',
        'note': 'nested ünïcode note\n'.encode('utf-8'),
        'grow': rnd.randbytes(100),
    }
    payloads = {}
    for name, content in data.items():
        path = os.path.join(directory, name)
        with open(path, 'wb') as handle:
            handle.write(content)
        payloads[name] = (path, content)
    return payloads


def delete_queue_entries(image_dir):
    """Entries in the dataset's unlinked set of an exported image."""
    out = vm('''set -e
q=$(zdb -e -p %(d)s -dddd %(ds)s 1 | awk '/DELETE_QUEUE/{print $3}')
zdb -e -p %(d)s -dddd %(ds)s $q | grep -o '[0-9]* entries'
''' % {'d': image_dir, 'ds': DATASET}, capture=True)
    return int(out.split()[0])


def znode_signature(name):
    """Bonus size, mode and pflags of a root entry in the exported image."""
    out = vm('''set -e
root=$(zdb -e -p %(d)s -dddd %(ds)s 1 | awk '/ROOT =/{print $3}')
o=$(zdb -e -p %(d)s -dddd %(ds)s $root | awk -v n=%(n)s '$1==n{print $3}')
zdb -e -p %(d)s -dddd %(ds)s $o
''' % {'d': REMOTE + '/out', 'ds': DATASET, 'n': name}, capture=True)
    return tuple(re.search(pattern, out).group(1) for pattern in (
        r'(\d+)\s+bonus\s+System attributes', r'\smode\s+(\d+)',
        r'\spflags\s+(\w+)'))


def compare_znodes(pairs):
    """Windows-created znodes must match their Linux-created peers."""
    for windows, linux in pairs:
        ours, theirs = znode_signature(windows), znode_signature(linux)
        if ours != theirs:
            raise SystemExit('FAIL: %s %s differs from Linux %s %s' % (
                windows, ours, linux, theirs))
        log('  %s matches Linux-created %s (bonus %s, mode %s, pflags %s)' %
            ((windows, linux) + ours))


def verify_linux(model, label, linux_ops=''):
    """Import on Linux, compare with the model, scrub, export, zdb."""
    log('== verifying on Linux OpenZFS (%s)' % label)
    out = vm('''set -e
zpool import -d %(r)s/out %(p)s
trap 'cd /; zpool export %(p)s 2>/dev/null || true' EXIT
python3 -c %(py)s /%(ds)s
''' % {'r': REMOTE, 'p': POOL, 'ds': DATASET,
       'py': "'" + MANIFEST_PY.replace("'", "'\\''") + "'"}, capture=True)
    actual = json.loads(out.strip().splitlines()[-1])
    expected = model.expected()
    if actual != expected:
        for path in sorted(set(actual) | set(expected)):
            if actual.get(path) != expected.get(path):
                log('  %r: linux=%s expected=%s' % (
                    path, actual.get(path), expected.get(path)))
        raise SystemExit('FAIL: namespace/content mismatch (%s)' % label)
    log('  %d entries match the model (sizes, link counts, SHA-256)' %
        len(expected))
    vm('''set -e
zpool import -d %(r)s/out %(p)s
trap 'cd /; zpool export %(p)s 2>/dev/null || true' EXIT
cd /%(ds)s
%(ops)s
cd /
# Mounting drains the unlinked set asynchronously; wait for it.
q=$(zdb -dddd %(ds)s 1 | awk '/DELETE_QUEUE/{print $3}')
for i in $(seq 60); do
    zpool sync %(p)s
    zdb -dddd %(ds)s $q | grep -q ' 0 entries' && break
    sleep 1
done
zpool scrub -w %(p)s
zpool status -x %(p)s | grep -q 'is healthy'
zpool status %(p)s | grep -E 'scan:'
zpool export %(p)s
trap - EXIT
zdb -e -p %(r)s/out -bcc %(p)s 2>&1 | grep -E 'leaked|No leaks' | tee /tmp/zdb.out
grep -q 'No leaks' /tmp/zdb.out
''' % {'r': REMOTE, 'p': POOL, 'ds': DATASET, 'ops': linux_ops})
    entries = delete_queue_entries(REMOTE + '/out')
    if entries != 0:
        raise SystemExit('FAIL: unlinked set still holds %d entries' % entries)
    log('  unlinked set empty after Linux mount')


def main():
    if not os.path.exists(EXE):
        raise SystemExit('missing %s; run scripts/build-write-core.ps1' % EXE)

    log('== creating base pool on %s' % HOST)
    vm('''set -e
zpool list -H -o name %(p)s >/dev/null 2>&1 && zpool export %(p)s
rm -rf %(r)s && mkdir -p %(r)s/base %(r)s/out
truncate -s 256M %(r)s/base/disk01.raw
zpool create -o cachefile=none -O compression=lz4 %(p)s %(r)s/base/disk01.raw
zfs create %(ds)s
printf 'hello\\n' > /%(ds)s/existing.txt
mkdir /%(ds)s/dir /%(ds)s/linux
head -c 2097152 /dev/urandom > /%(ds)s/linux/victim.bin
zpool export %(p)s
''' % {'p': POOL, 'r': REMOTE, 'ds': DATASET})
    model = Model()
    model.mkdir('dir')
    model.mkdir('linux')
    model.create('existing.txt')
    model.write('existing.txt', 0, b'hello\n')
    model.create('linux/victim.bin')

    shutil.rmtree(WORK, ignore_errors=True)
    run_dir = os.path.join(WORK, 'run')
    os.makedirs(run_dir)
    os.makedirs(os.path.join(WORK, 'payload'))
    image = os.path.join(run_dir, 'disk01.raw')
    scp('root@%s:%s/base/disk01.raw' % (HOST, REMOTE), image)
    payloads = make_payloads(os.path.join(WORK, 'payload'))
    win = Windows(model, run_dir, payloads)

    log('== phase 1: mutating the Linux-created image on Windows')
    win.create('small.txt')
    win.write('small.txt', 0, 'small')
    win.create('big.bin')
    win.write('big.bin', 0, 'big')
    win.write('big.bin', 'append', 'tail')
    win.write('big.bin', 100000, 'patch')
    win.create('sparse.bin')
    win.write('sparse.bin', 1048576, 'sparse')
    win.create('empty.txt')
    win.run('create-file', 'small.txt', expect_errno=errno.EEXIST)

    # Directories and nested paths, including a directory Linux created.
    win.mkdir('docs')
    win.mkdir('docs/sub')
    win.create('docs/sub/note.txt')
    win.write('docs/sub/note.txt', 0, 'note')
    win.create('dir/in-linux-dir.txt')
    win.write('dir/in-linux-dir.txt', 0, 'small')
    win.run('mkdir', 'docs', expect_errno=errno.EEXIST)
    win.run('create-file', 'missing/x.txt', expect_errno=errno.ENOENT)
    win.run('create-file', 'small.txt/x', expect_errno=errno.ENOTDIR)
    win.run('create-file', 'docs/../x', expect_errno=errno.EINVAL)

    # Truncate: shrink across blocks, within a block, extend, and to zero.
    win.truncate('big.bin', 2000000)
    win.truncate('small.txt', 10)
    win.create('grow.bin')
    win.write('grow.bin', 0, 'grow')
    win.truncate('grow.bin', 300000)
    win.create('zero.bin')
    win.write('zero.bin', 0, 'big')
    win.truncate('zero.bin', 0)
    win.run('truncate', 'docs', 0, expect_errno=errno.EISDIR)

    # Delete, including Linux-created files and a deferred reclaim.
    win.remove('existing.txt')
    win.remove('linux/victim.bin')
    win.create('victim2.bin')
    win.write('victim2.bin', 0, 'big')
    win.remove('victim2.bin')
    win.create('deferred.bin')
    win.write('deferred.bin', 0, 'big')
    win.remove('deferred.bin', '--defer-reclaim')
    win.run('remove', 'docs', expect_errno=errno.EPERM)
    win.run('remove', 'missing.txt', expect_errno=errno.ENOENT)
    win.run('rmdir', 'sparse.bin', expect_errno=errno.ENOTDIR)
    win.run('rmdir', 'docs', expect_errno=errno.ENOTEMPTY)
    win.mkdir('gone')
    win.rmdir('gone')
    win.mkdir('gone2')
    win.rmdir('gone2', '--defer-reclaim')

    # Rename: same directory, across directories, directory moves, and
    # replacing an existing file or empty directory.
    win.rename('small.txt', 'renamed.txt')
    win.rename('docs/sub/note.txt', 'dir/note.txt')
    win.rename('docs/sub', 'dir/sub')
    win.create('target.txt')
    win.write('target.txt', 0, 'patch')
    win.rename('renamed.txt', 'target.txt')
    win.mkdir('emptydir')
    win.mkdir('docs/emptytarget')
    win.rename('emptydir', 'docs/emptytarget')
    win.run('rename', 'dir', 'dir/sub/inside', expect_errno=errno.EINVAL)
    win.run('rename', 'sparse.bin', 'dir', expect_errno=errno.EISDIR)
    win.run('rename', 'docs', 'sparse.bin', expect_errno=errno.ENOTDIR)
    win.run('rename', 'dir', 'docs', expect_errno=errno.ENOTEMPTY)
    win.run('rename', 'missing', 'x', expect_errno=errno.ENOENT)

    # Reopen persistence on Windows: every command above was its own
    # import/export cycle; read back sizes recorded by earlier cycles.
    for path in ('big.bin', 'target.txt', 'grow.bin', 'zero.bin'):
        if win.stat_size(path) != len(model.files[path]):
            raise SystemExit('FAIL: stat size of %s' % path)
    log('  %d Windows import/export cycles' % win.cycles)

    scp(image, 'root@%s:%s/out/disk01.raw' % (HOST, REMOTE))
    log('== crash state: deferred reclaims are on the unlinked set')
    entries = delete_queue_entries(REMOTE + '/out')
    if entries != 2:
        raise SystemExit('FAIL: expected 2 unlinked-set entries, got %d' %
                         entries)
    vm('zdb -e -p %s/out -bcc %s 2>&1 | grep -E "leaked|No leaks" | '
       'tee /tmp/zdb.out; grep -q "No leaks" /tmp/zdb.out' % (REMOTE, POOL))

    linux_append = b'linux append\n'
    linux_made = b'made on linux\n' * 1000
    verify_linux(model, 'after Windows phase 1', '''
printf 'linux append\\n' >> target.txt
python3 -c "import sys; sys.stdout.write('made on linux\\\\n' * 1000)" > linux-made.txt
mkdir -p linuxdir/deep
touch linux-empty.txt
rm dir/in-linux-dir.txt
mv docs/emptytarget docs/moved
''')
    model.write('target.txt', 'append', linux_append)
    model.create('linux-made.txt')
    model.write('linux-made.txt', 0, linux_made)
    model.mkdir('linuxdir')
    model.mkdir('linuxdir/deep')
    model.create('linux-empty.txt')
    model.remove('dir/in-linux-dir.txt')
    model.rename('docs/emptytarget', 'docs/moved')
    verify_linux(model, 'after Linux mutations')
    compare_znodes([('docs', 'linuxdir'), ('empty.txt', 'linux-empty.txt')])

    log('== phase 2: reopening the Linux-written image on Windows')
    scp('root@%s:%s/out/disk01.raw' % (HOST, REMOTE), image)
    if win.stat_size('linux-made.txt') != len(linux_made):
        raise SystemExit('FAIL: Linux file size not visible on Windows')
    win.write('linux-made.txt', 'append', 'tail')
    win.rmdir('linuxdir/deep')
    win.rmdir('linuxdir')
    win.rename('linux-made.txt', 'dir/sub/linux-made.txt')
    win.truncate('target.txt', 3000)
    win.remove('big.bin')
    win.mkdir('again')
    win.create('again/new.txt')
    win.write('again/new.txt', 0, 'note')
    scp(image, 'root@%s:%s/out/disk01.raw' % (HOST, REMOTE))
    verify_linux(model, 'after Windows phase 2')
    log('PASS (%d Windows import/export cycles)' % win.cycles)


if __name__ == '__main__':
    main()
