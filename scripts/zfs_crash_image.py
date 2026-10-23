#!/usr/bin/env python3
"""Reconstruct power-cut device images from a zfs-write-core I/O journal.

zfs-write-core (ZFS_WRITE_CORE_JOURNAL=<file>) records, in one total order,
every vdev file write together with the bytes it replaced, every flush, and
every acknowledged script step.  Given the final device files and that
journal, this tool rebuilds the devices as they would be after losing power
at journal record CUT:

* writes that began at or after CUT never happened (reverted);
* a write is durable when it completed before a flush of the same file
  began and that flush completed before CUT;
* every other write that began before CUT sat in a volatile write cache:
  each of its 512-byte sectors independently survives or is lost
  (--mode random), all are lost (prefix), or all survive (all).

Only the standard library is used; the script runs on the verification VM.

  zfs_crash_image.py info <journal>
  zfs_crash_image.py selfcheck <journal> <initial-dir> <final-dir>
  zfs_crash_image.py build <journal> <final-dir> <out-dir> <cut>
      [--mode random|prefix|all] [--seed N]
  zfs_crash_image.py drop-writes <journal> <dir> <device-name>
      <first-ack> <last-ack>
"""
import hashlib
import json
import mmap
import os
import random
import shutil
import struct
import sys

MAGIC = 0x314a575a
HEADER = struct.Struct('<IIQQQQq')
FILE, WRITE_BEGIN, WRITE_END, FLUSH_BEGIN, FLUSH_END, ACK = range(1, 7)
SECTOR = 512


class Journal:
    def __init__(self, path):
        self.handle = open(path, 'rb')
        self.data = mmap.mmap(self.handle.fileno(), 0, access=mmap.ACCESS_READ)
        self.records = []	# (type, id, file, offset, length, payload_at)
        self.results = []	# errno of END records
        self.files = {}		# file id -> basename
        position = 0
        while position < len(self.data):
            magic, kind, ident, file_id, offset, length, result = \
                HEADER.unpack_from(self.data, position)
            if magic != MAGIC:
                raise SystemExit('bad journal record at %d' % position)
            position += HEADER.size
            payload = position
            if kind == FILE:
                path = bytes(self.data[position:position + length]).decode()
                self.files[file_id] = path.replace('\\', '/').rsplit('/')[-1]
                position += length
            elif kind == WRITE_BEGIN:
                position += 2 * length
            self.records.append((kind, ident, file_id, offset, length,
                                 payload))
            self.results.append(result)

    def before(self, index):
        _, _, _, _, length, at = self.records[index]
        return self.data[at:at + length]

    def after(self, index):
        _, _, _, _, length, at = self.records[index]
        return self.data[at + length:at + 2 * length]

    def writes(self):
        """WRITE_BEGIN index -> successful WRITE_END index, else None."""
        open_writes, out = {}, {}
        for index, (kind, _, file_id, offset, length, _) in \
                enumerate(self.records):
            key = (file_id, offset, length)
            if kind == WRITE_BEGIN:
                open_writes.setdefault(key, []).append(index)
                out[index] = None
            elif kind == WRITE_END:
                begin = open_writes[key].pop(0)
                out[begin] = index if self.results[index] == 0 else None
        return out

    def acks(self):
        return [(index, rec[1]) for index, rec in enumerate(self.records)
                if rec[0] == ACK]


def info(journal):
    kinds = {}
    for rec in journal.records:
        kinds[rec[0]] = kinds.get(rec[0], 0) + 1
    flush_ends = [i for i, rec in enumerate(journal.records)
                  if rec[0] == FLUSH_END]
    return {'records': len(journal.records), 'files': journal.files,
            'writes': kinds.get(WRITE_BEGIN, 0),
            'flushes': kinds.get(FLUSH_END, 0), 'acks': journal.acks(),
            'flush_ends': flush_ends}


def open_devices(journal, directory):
    return {file_id: open(os.path.join(directory, name), 'r+b')
            for file_id, name in journal.files.items()}


def revert_from(journal, devices, cut):
    """Undo every write that began at or after record 'cut'."""
    for index in range(len(journal.records) - 1, cut - 1, -1):
        kind, _, file_id, offset, _, _ = journal.records[index]
        if kind == WRITE_BEGIN:
            devices[file_id].seek(offset)
            devices[file_id].write(journal.before(index))


def build(journal, final_dir, out_dir, cut, mode, seed):
    os.makedirs(out_dir, exist_ok=True)
    for name in journal.files.values():
        shutil.copyfile(os.path.join(final_dir, name),
                        os.path.join(out_dir, name))
    devices = open_devices(journal, out_dir)
    revert_from(journal, devices, cut)

    # Latest completed flush (by FLUSH_BEGIN index) per file before the cut.
    # Concurrent flushes are paired first-in first-out, which can only
    # credit a flush with an earlier start, i.e. fewer durable writes.
    open_flushes, durable_before = {}, {}
    for index in range(cut):
        kind, _, file_id = journal.records[index][:3]
        if kind == FLUSH_BEGIN:
            open_flushes.setdefault(file_id, []).append(index)
        elif kind == FLUSH_END:
            begin = open_flushes[file_id].pop(0)
            if journal.results[index] != 0:
                continue	# a failed flush makes nothing durable
            durable_before[file_id] = max(durable_before.get(file_id, -1),
                                          begin)

    # Volatile writes, per sector, in issue order.
    rnd = random.Random(seed)
    sectors = {}
    volatile = 0
    for begin, end in sorted(journal.writes().items()):
        if begin >= cut:
            continue
        file_id, offset, length = journal.records[begin][2:5]
        if end is not None and end < cut and \
                end < durable_before.get(file_id, -1):
            continue
        if offset % SECTOR or length % SECTOR:
            raise SystemExit('unaligned write at record %d' % begin)
        volatile += 1
        for s in range(length // SECTOR):
            sectors.setdefault((file_id, offset + s * SECTOR), []).append(
                (begin, s))

    lost = kept = 0
    for (file_id, position), history in sectors.items():
        content = None
        for begin, s in history:
            keep = (mode == 'all' or
                    (mode == 'random' and rnd.random() < 0.5))
            if keep:
                content = journal.after(begin)[s * SECTOR:(s + 1) * SECTOR]
                kept += 1
            else:
                lost += 1
        if content is None:	# the flushed contents before the first write
            begin, s = history[0]
            content = journal.before(begin)[s * SECTOR:(s + 1) * SECTOR]
        devices[file_id].seek(position)
        devices[file_id].write(content)
    for handle in devices.values():
        handle.close()

    acked = [step for index, step in journal.acks() if index < cut]
    return {'cut': cut, 'mode': mode, 'seed': seed,
            'last_ack': max(acked) if acked else -1,
            'volatile_writes': volatile, 'sectors_kept': kept,
            'sectors_lost': lost}


def drop_writes(journal, directory, name, first_ack, last_ack):
    """Make one device lose the writes issued between two acknowledgements.

    zinject device faults complete a write and then report EIO, so the
    bytes still reach the file; this reverts them, as on a disk that really
    failed to store them.  Sectors rewritten after the window keep their
    later contents.
    """
    acks = dict((step, index) for index, step in journal.acks())
    start, end = acks[first_ack], acks[last_ack]
    file_id = [f for f, n in journal.files.items() if n == name][0]
    later = set()
    for index in range(end, len(journal.records)):
        kind, _, fid, offset, length, _ = journal.records[index]
        if kind == WRITE_BEGIN and fid == file_id:
            later.update(range(offset // SECTOR,
                               (offset + length) // SECTOR))
    dropped = 0
    with open(os.path.join(directory, name), 'r+b') as device:
        for index in range(end - 1, start, -1):
            kind, _, fid, offset, length, _ = journal.records[index]
            if kind != WRITE_BEGIN or fid != file_id:
                continue
            before = journal.before(index)
            for s in range(length // SECTOR):
                if offset // SECTOR + s in later:
                    continue
                device.seek(offset + s * SECTOR)
                device.write(before[s * SECTOR:(s + 1) * SECTOR])
                dropped += 1
    return {'device': name, 'sectors_dropped': dropped}


def digest(directory, names):
    return {name: hashlib.sha256(open(os.path.join(directory, name),
                                      'rb').read()).hexdigest()
            for name in names}


def selfcheck(journal, initial_dir, final_dir, scratch):
    """Reverting every write must give back the initial devices exactly."""
    names = list(journal.files.values())
    for name in names:
        shutil.copyfile(os.path.join(final_dir, name),
                        os.path.join(scratch, name))
    devices = open_devices(journal, scratch)
    revert_from(journal, devices, 0)
    for handle in devices.values():
        handle.close()
    ok = digest(scratch, names) == digest(initial_dir, names)
    return {'reverts_to_initial': ok}


def main(argv):
    if len(argv) >= 2 and argv[0] == 'info':
        print(json.dumps(info(Journal(argv[1]))))
    elif len(argv) >= 4 and argv[0] == 'selfcheck':
        scratch = argv[3].rstrip('/') + '.selfcheck'
        os.makedirs(scratch, exist_ok=True)
        print(json.dumps(selfcheck(Journal(argv[1]), argv[2], argv[3],
                                   scratch)))
        shutil.rmtree(scratch)
    elif len(argv) == 6 and argv[0] == 'drop-writes':
        print(json.dumps(drop_writes(Journal(argv[1]), argv[2], argv[3],
                                     int(argv[4]), int(argv[5]))))
    elif len(argv) >= 5 and argv[0] == 'build':
        mode, seed = 'random', 0
        rest = argv[5:]
        while rest:
            if rest[0] == '--mode':
                mode = rest[1]
            elif rest[0] == '--seed':
                seed = int(rest[1])
            else:
                raise SystemExit('unknown option ' + rest[0])
            rest = rest[2:]
        print(json.dumps(build(Journal(argv[1]), argv[2], argv[3],
                               int(argv[4]), mode, seed)))
    else:
        raise SystemExit(__doc__)


if __name__ == '__main__':
    main(sys.argv[1:])
