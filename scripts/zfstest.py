"""Shared helpers for the zfs-write-core tests against Linux OpenZFS.

The VM host comes from the first command-line argument or ZFS_VM_HOST; the
SSH key from ZFS_VM_KEY; the adapter from ZFS_WRITE_CORE.
"""
import hashlib
import os
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
# A first argument containing '.' or ':' is the VM host (later arguments
# belong to the individual test).
HOST = (sys.argv[1] if len(sys.argv) > 1 and any(c in sys.argv[1]
                                                 for c in '.:')
        else os.environ.get('ZFS_VM_HOST', '192.168.195.60'))
KEY = os.environ.get('ZFS_VM_KEY',
                     os.path.expanduser('~/.ssh/zfswindows-agent_ed25519'))
EXE = os.environ.get('ZFS_WRITE_CORE', os.path.join(
    ROOT, 'out', 'openzfs-write-debug', 'zfs-write-core',
    'zfs-write-core.exe'))
SSH = ['-i', KEY, '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=10']

# Run on the VM: list the dataset as JSON (type, size, links, SHA-256).
# Further arguments name files whose content is included (base64).
MANIFEST_PY = r'''
import base64, hashlib, json, os, sys
top = sys.argv[1]
dump = set(sys.argv[2:])
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
            data = open(path, 'rb').read()
            entry['sha256'] = hashlib.sha256(data).hexdigest()
            if rel in dump:
                entry['content'] = base64.b64encode(data).decode()
        out[rel] = entry
st = os.stat(top)
out[''] = {'type': 'dir', 'size': st.st_size, 'links': st.st_nlink}
print(json.dumps(out))
'''


def shell_quote(text):
    return "'" + text.replace("'", "'\\''") + "'"


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

    def copy(self):
        other = Model()
        other.dirs = set(self.dirs)
        other.files = {p: bytearray(c) for p, c in self.files.items()}
        return other

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


def delete_queue_entries(image_dir, dataset):
    """Entries in a dataset's unlinked set, read from an exported image."""
    out = vm('''set -e
q=$(zdb -e -p %(d)s -dddd %(ds)s 1 | awk '/DELETE_QUEUE/{print $3}')
zdb -e -p %(d)s -dddd %(ds)s $q | grep -o '[0-9]* entries'
''' % {'d': image_dir, 'ds': dataset}, capture=True)
    return int(out.split()[0])
