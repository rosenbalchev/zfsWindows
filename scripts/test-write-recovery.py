#!/usr/bin/env python3
"""Failure injection and recovery for zfs-write-core, judged by Linux OpenZFS.

Each scenario starts from a fresh pool created by Linux OpenZFS, drives
zfs-write-core.exe with a run-script workload whose steps are acknowledged
("ack <n>") only once their transaction groups are synced, injects a
failure, and then lets upstream Linux OpenZFS import and judge the result.

An image must import without rewind (`-F`), and its namespace and every file
byte must equal the model after the acknowledged steps.  The one in-flight
step may additionally be complete; a crash inside a shrinking truncate may
leave the old size with a partly zeroed tail, as upstream zfs_freesp() does.
Then the unlinked set must drain, scrub must report no errors, and
`zdb -bcc` must report no leaks.

Scenarios:
  power-loss    the I/O journal is replayed to power cuts at chosen points;
                unflushed writes are lost sector by sector
  win-recovery  Windows reopens power-cut images and keeps writing
  kill          the adapter process is terminated at random times
  io-errors     zinject write faults on the only device mid-workload
  enospc        the pool fills; the failing step must not be acknowledged
  mirror        write faults on one member, a missing member, and a member
                with 96 MiB of overwritten sectors
  labels        the front or back pair of vdev labels is destroyed

Usage: python scripts/test-write-recovery.py [vm-host] [scenario ...]
"""
import base64
import errno
import json
import os
import random
import shutil
import subprocess
import sys
import threading
import time

from zfstest import EXE, HOST, MANIFEST_PY, ROOT, Model, log, scp, \
    shell_quote, vm

POOL = 'zfswin_rec'
DATASET = POOL + '/data'
REMOTE = '/root/zfswin-recovery'
WORK = os.path.join(ROOT, 'TestVolumes', 'recovery')
MiB = 1024 * 1024
SCENARIOS = ['power-loss', 'win-recovery', 'kill', 'io-errors', 'enospc',
             'mirror', 'labels']


def win_path(path):
    return os.path.abspath(path)


# ---------------------------------------------------------------- payloads

def make_payloads():
    rnd = random.Random(20261023)
    data = {
        'old': rnd.randbytes(1 * MiB),
        'm1': rnd.randbytes(1 * MiB),
        'm2': rnd.randbytes(2 * MiB),
        'm3': rnd.randbytes(3 * MiB + 777),
        'm4': rnd.randbytes(4 * MiB),
        'k50': rnd.randbytes(50 * 1024),
        'k100': rnd.randbytes(100 * 1024),
        'small': b'small payload\n',
        'note': 'recovery ünïcode note\n'.encode('utf-8'),
    }
    directory = os.path.join(WORK, 'payload')
    os.makedirs(directory, exist_ok=True)
    out = {}
    for name, content in data.items():
        path = os.path.join(directory, name)
        with open(path, 'wb') as handle:
            handle.write(content)
        out[name] = (win_path(path), content)
    return out


PAYLOADS = None


# ----------------------------------------------------------- steps, model

def apply(model, step):
    op = step[0]
    if op == 'mkdir':
        model.mkdir(step[1])
    elif op == 'create-file':
        model.create(step[1])
    elif op == 'write-file':
        offset = step[2] if step[2] == 'append' else int(step[2])
        model.write(step[1], offset, PAYLOADS[step[3]][1])
    elif op == 'truncate':
        model.truncate(step[1], int(step[2]))
    elif op == 'remove':
        model.remove(step[1])
    elif op == 'rmdir':
        model.rmdir(step[1])
    elif op == 'rename':
        model.rename(step[1], step[2])
    elif op in ('inject-write-errors', 'clear-injections', 'sync', 'probe'):
        pass
    else:
        raise ValueError(op)


def models_after(model, steps):
    """models[k] is the expected state once k steps are acknowledged."""
    models = [model.copy()]
    for step in steps:
        model = model.copy()
        apply(model, step)
        models.append(model)
    return models


def write_script(path, steps):
    with open(path, 'w', newline='\n') as handle:
        for step in steps:
            words = list(step)
            if words[0] == 'write-file':
                words[3] = PAYLOADS[words[3]][0]
            handle.write(' '.join(map(str, words)) + '\n')


def base_model():
    """What populate() puts in a fresh dataset."""
    model = Model()
    model.create('existing.txt')
    model.write('existing.txt', 0, b'hello\n')
    model.mkdir('dir')
    model.mkdir('linux')
    model.create('linux/old.bin')
    model.write('linux/old.bin', 0, PAYLOADS['old'][1])
    return model


WORKLOAD = [
    ('mkdir', 'a'),
    ('create-file', 'a/one.bin'),
    ('write-file', 'a/one.bin', 0, 'm1'),
    ('create-file', 'two.bin'),
    ('write-file', 'two.bin', 0, 'm3'),
    ('write-file', 'two.bin', 'append', 'k100'),
    ('rename', 'two.bin', 'a/two.bin'),
    ('truncate', 'a/one.bin', 4096),
    ('create-file', 'three.bin'),
    ('write-file', 'three.bin', 0, 'm2'),
    ('remove', 'three.bin'),
    ('mkdir', 'b'),
    ('rename', 'a', 'b/a'),
    ('mkdir', 'c'),
    ('rmdir', 'c'),
    ('remove', 'linux/old.bin'),
    ('write-file', 'existing.txt', 'append', 'small'),
    ('create-file', 'b/four.bin'),
    ('write-file', 'b/four.bin', 0, 'm4'),
    ('write-file', 'b/four.bin', 1000000, 'k50'),
    ('truncate', 'b/four.bin', 10),
    ('rename', 'b/four.bin', 'b/a/four.bin'),
    ('truncate', 'b/a/one.bin', 200000),
    ('create-file', 'b/note.txt'),
    ('write-file', 'b/note.txt', 0, 'note'),
]


# ------------------------------------------------------------ Linux side

def create_base(name, members, size='128M', mirror=False):
    """Fresh pool on the VM, copied to WORK/<name>/base."""
    remote = '%s/%s/base' % (REMOTE, name)
    files = ' '.join('%s/%s' % (remote, m) for m in members)
    vm('''set -e
zpool list -H -o name %(p)s >/dev/null 2>&1 && zpool export %(p)s
rm -rf %(r)s && mkdir -p %(r)s
for f in %(files)s; do truncate -s %(size)s $f; done
zpool create -o cachefile=none -O compression=lz4 %(p)s %(mirror)s %(files)s
zfs create %(ds)s
printf 'hello\\n' > /%(ds)s/existing.txt
mkdir /%(ds)s/dir /%(ds)s/linux
cp %(payload)s/old /%(ds)s/linux/old.bin
zpool export %(p)s
''' % {'p': POOL, 'r': remote, 'files': files, 'size': size,
       'mirror': 'mirror' if mirror else '', 'ds': DATASET,
       'payload': REMOTE + '/payload'})
    local = os.path.join(WORK, name, 'base')
    shutil.rmtree(os.path.join(WORK, name), ignore_errors=True)
    os.makedirs(local)
    for member in members:
        scp('root@%s:%s/%s' % (HOST, remote, member),
            os.path.join(local, member))
    return local


def fresh_run_dir(name, label='run', members=None):
    base = os.path.join(WORK, name, 'base')
    run = os.path.join(WORK, name, label)
    shutil.rmtree(run, ignore_errors=True)
    os.makedirs(run)
    for member in members or os.listdir(base):
        shutil.copyfile(os.path.join(base, member),
                        os.path.join(run, member))
    return run


def upload(local_dir, remote_dir, members=None):
    vm('rm -rf %s && mkdir -p %s' % (remote_dir, remote_dir))
    for member in members or sorted(os.listdir(local_dir)):
        scp(os.path.join(local_dir, member),
            'root@%s:%s/%s' % (HOST, remote_dir, member))


LINUX_CHECK = r'''set -e
cd /
d=%(dir)s
zpool import %(force)s -d $d %(p)s
trap 'cd /; zpool export %(p)s 2>/dev/null || true' EXIT
zpool wait -t resilver %(p)s
zpool status %(p)s | grep 'scan:' > /tmp/zwr-import-scan.txt || true
python3 -c %(manifest)s /%(ds)s %(dump)s > /tmp/zwr-manifest.json
q=$(zdb -dddd %(ds)s 1 | awk '/DELETE_QUEUE/{print $3}')
for i in $(seq 60); do
    zpool sync %(p)s
    zdb -dddd %(ds)s $q | grep -q ' 0 entries' && break
    sleep 1
done
zpool scrub -w %(p)s
zpool status -v %(p)s > /tmp/zwr-status.txt
zpool clear %(p)s
zpool export %(p)s
trap - EXIT
zdb -e -p $d -bcc %(p)s > /tmp/zwr-zdb.txt 2>&1 || true
dq=$(zdb -e -p $d -dddd %(ds)s $q | grep -o '[0-9]* entries' | cut -d' ' -f1)
python3 - "$dq" <<'EOF'
import json, re, sys
status = open('/tmp/zwr-status.txt').read()
zdb = open('/tmp/zwr-zdb.txt').read()
scan = re.search(r'scan: (.*)', status)
imported = open('/tmp/zwr-import-scan.txt').read().strip()
print(json.dumps({
    'manifest': json.load(open('/tmp/zwr-manifest.json')),
    'scan': scan.group(1) if scan else '',
    'import_scan': imported,
    'scrub_errors': 'with 0 errors' not in status,
    'data_errors': 'No known data errors' not in status,
    'no_leaks': 'No leaks' in zdb,
    'zdb': [l for l in zdb.splitlines() if 'leak' in l.lower()][:5],
    'delete_queue': int(sys.argv[1]),
}))
EOF
'''


def linux_check(remote_dir, force=False, dump=()):
    """Import on Linux and return the manifest plus health facts."""
    out = vm(LINUX_CHECK % {
        'dir': remote_dir, 'p': POOL, 'ds': DATASET,
        'force': '-f' if force else '', 'manifest': shell_quote(MANIFEST_PY),
        'dump': ' '.join(shell_quote(p) for p in dump)}, capture=True)
    return json.loads(out.strip().splitlines()[-1])


def require_healthy(result, label):
    problems = []
    if result['scrub_errors']:
        problems.append('scrub reported errors: ' + result['scan'])
    if result['data_errors']:
        problems.append('zpool status lists data errors')
    if not result['no_leaks']:
        problems.append('zdb -bcc: %s' % result['zdb'])
    if result['delete_queue'] != 0:
        problems.append('unlinked set holds %d' % result['delete_queue'])
    if problems:
        raise SystemExit('FAIL (%s): %s' % (label, '; '.join(problems)))


def manifest_mismatch(actual, expected, skip=()):
    lines = []
    for path in sorted(set(actual) | set(expected)):
        if path in skip:
            continue
        a = dict(actual.get(path) or {})
        a.pop('content', None)
        if a != expected.get(path):
            lines.append('%r: linux=%s expected=%s' % (
                path, actual.get(path), expected.get(path)))
    return lines


def judge(actual, models, steps, acked):
    """Which allowed state the image is in, or raise.

    acked is the number of acknowledged steps; step 'acked' was in flight.
    """
    candidates = [(acked, models[acked])]
    if acked < len(steps):
        candidates.append((acked + 1, models[acked + 1]))
    for k, model in candidates:
        if not manifest_mismatch(actual, model.expected()):
            return 'state %d' % k
    if acked < len(steps) and steps[acked][0] == 'truncate':
        path = steps[acked][1]
        old = models[acked].files[path]
        new_size = int(steps[acked][2])
        entry = actual.get(path) or {}
        if new_size < len(old) and 'content' in entry and \
                not manifest_mismatch(actual, models[acked].expected(),
                                      skip=(path,)):
            data = base64.b64decode(entry['content'])
            if len(data) == len(old) and data[:new_size] == old[:new_size] \
                    and all(b in (0, o) for b, o in
                            zip(data[new_size:], old[new_size:])):
                return 'state %d with truncate %s partly freed' % (acked,
                                                                  path)
    detail = manifest_mismatch(actual, models[acked].expected())
    raise SystemExit('FAIL: image matches neither state %d nor %d:\n  %s' % (
        acked, acked + 1, '\n  '.join(detail[:12])))


def dump_paths(steps, acked):
    if acked < len(steps) and steps[acked][0] == 'truncate':
        return (steps[acked][1],)
    return ()


# ---------------------------------------------------------- Windows side

class ScriptRun:
    """zfs-write-core run-script with acknowledgement tracking."""

    def __init__(self, run_dir, steps, journal=None, label='script'):
        self.script = os.path.join(os.path.dirname(run_dir),
                                   label + '.steps')
        write_script(self.script, steps)
        env = dict(os.environ)
        if journal:
            env['ZFS_WRITE_CORE_JOURNAL'] = win_path(journal)
        self.acks = []
        self.lines = []
        self.last_output = time.monotonic()
        self.process = subprocess.Popen(
            [EXE, 'run-script', win_path(run_dir), POOL, DATASET,
             win_path(self.script)],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=env)
        self.stderr = []
        self.readers = [
            threading.Thread(target=self._read, args=(self.process.stdout,
                                                      self.lines)),
            threading.Thread(target=self._read, args=(self.process.stderr,
                                                      self.stderr))]
        for reader in self.readers:
            reader.start()

    def _read(self, stream, sink):
        for raw in stream:
            line = raw.decode('utf-8', 'replace').rstrip()
            sink.append(line)
            self.last_output = time.monotonic()
            if line.startswith('ack '):
                self.acks.append(int(line.split()[1]))

    def wait(self, timeout=600):
        try:
            self.process.wait(timeout)
        finally:
            for reader in self.readers:
                reader.join()
        return self.process.returncode

    def kill(self):
        self.process.kill()	# TerminateProcess: no cleanup runs
        return self.wait()

    def errors(self):
        return [l for l in self.stderr if 'Assuming 512' not in l]


def run_steps(run_dir, steps, label='script', journal=None):
    run = ScriptRun(run_dir, steps, journal, label)
    code = run.wait()
    if code != 0 or len(run.acks) != len(steps):
        raise SystemExit('FAIL: %s exited %d after %d/%d acks\n%s' % (
            label, code, len(run.acks), len(steps), '\n'.join(run.errors())))
    return run


def expect_state(result, model, label):
    detail = manifest_mismatch(result['manifest'], model.expected())
    if detail:
        raise SystemExit('FAIL (%s):\n  %s' % (label, '\n  '.join(detail)))
    require_healthy(result, label)
    log('  %s: %d entries match; %s' % (label, len(model.expected()),
                                       result['scan']))


# -------------------------------------------------------------- scenarios

def scenario_power_loss(state):
    log('== power-loss: journaled run, then reconstructed power cuts')
    create_base('power', ['disk01.raw'])
    run_dir = fresh_run_dir('power')
    journal = os.path.join(WORK, 'power', 'run.journal')
    run_steps(run_dir, WORKLOAD, 'workload', journal)
    models = models_after(base_model(), WORKLOAD)
    remote = REMOTE + '/power'
    upload(run_dir, remote + '/final')
    scp(journal, 'root@%s:%s/run.journal' % (HOST, remote))
    scp(os.path.join(ROOT, 'scripts', 'zfs_crash_image.py'),
        'root@%s:%s/zfs_crash_image.py' % (HOST, REMOTE))
    tool = 'python3 %s/zfs_crash_image.py' % REMOTE
    check = json.loads(vm('%s selfcheck %s/run.journal %s/base %s/final' % (
        tool, remote, remote, remote), capture=True))
    if not check['reverts_to_initial']:
        raise SystemExit('FAIL: journal does not revert to the base image')
    info = json.loads(vm('%s info %s/run.journal' % (tool, remote),
                         capture=True))
    log('  journal: %d records, %d writes, %d flushes, %d acks; reverting '
        'all writes reproduces the base image' % (
            info['records'], info['writes'], info['flushes'],
            len(info['acks'])))
    # The completed run, exported normally.
    vm('rm -rf %s/final-check && cp -r %s/final %s/final-check' % (
        remote, remote, remote))
    expect_state(linux_check(remote + '/final-check'), models[-1],
                 'completed run')

    rnd = random.Random(1123)
    ack_index = {step: index for index, step in info['acks']}
    # Just before every third acknowledgement (the step is durable but not
    # yet reported), uniformly random records in each loss mode, and right
    # after flushes, where the most writes are volatile.
    cuts = [(ack_index[s], 'random') for s in sorted(ack_index)[::3]]
    cuts += [(rnd.randrange(info['records']), mode)
             for mode, count in (('random', 12), ('prefix', 10), ('all', 4))
             for _ in range(count)]
    cuts += [(index + 1, 'random')
             for index in rnd.sample(info['flush_ends'], 10)]
    cuts += [(0, 'all'), (info['records'], 'prefix')]
    for trial, (cut, mode) in enumerate(cuts):
        acked = sum(1 for index, _ in info['acks'] if index < cut)
        built = json.loads(vm('%s build %s/run.journal %s/final %s/cut %d '
                              '--mode %s --seed %d' % (
                                  tool, remote, remote, remote, cut, mode,
                                  trial), capture=True))
        result = linux_check(remote + '/cut', force=True,
                             dump=dump_paths(WORKLOAD, acked))
        verdict = judge(result['manifest'], models, WORKLOAD, acked)
        require_healthy(result, 'cut %d' % cut)
        state.setdefault('cuts', []).append((cut, mode, trial, acked,
                                             verdict))
        log('  cut %5d/%d %-6s acked %2d, volatile %3d (lost %4d sectors): '
            '%s' % (cut, info['records'], mode, acked,
                    built['volatile_writes'], built['sectors_lost'], verdict))
    log('  %d power cuts: every image imported without rewind, matched an '
        'allowed state, scrubbed clean, and had no leaks' % len(cuts))
    state['power_info'] = info


def scenario_win_recovery(state):
    log('== win-recovery: Windows reopens power-cut images and continues')
    if 'cuts' not in state:
        scenario_power_loss(state)
    tool = 'python3 %s/zfs_crash_image.py' % REMOTE
    remote = REMOTE + '/power'
    models = models_after(base_model(), WORKLOAD)
    picks = [c for c in state['cuts']
             if c[1] == 'random' and 3 <= c[3] < len(WORKLOAD) - 2 and
             ' with ' not in c[4]][:2]
    for cut, mode, trial, acked, verdict in picks:
        vm('%s build %s/run.journal %s/final %s/reopen %d --mode %s '
           '--seed %d >/dev/null' % (tool, remote, remote, remote, cut, mode,
                                     trial))
        run_dir = os.path.join(WORK, 'power', 'reopen')
        shutil.rmtree(run_dir, ignore_errors=True)
        os.makedirs(run_dir)
        scp('root@%s:%s/reopen/disk01.raw' % (HOST, remote),
            os.path.join(run_dir, 'disk01.raw'))
        k = int(verdict.split()[1])
        more = [('mkdir', 'recovered'),
                ('create-file', 'recovered/after-crash.bin'),
                ('write-file', 'recovered/after-crash.bin', 0, 'm2'),
                ('rename', 'existing.txt', 'recovered/existing.txt'),
                ('remove', 'recovered/existing.txt')]
        run_steps(run_dir, more, 'reopen')
        upload(run_dir, remote + '/reopen-out')
        expect_state(linux_check(remote + '/reopen-out'),
                     models_after(models[k], more)[-1],
                     'cut %d (%s) reopened on Windows' % (cut, verdict))


def scenario_kill(state):
    log('== kill: the adapter is terminated at random moments')
    create_base('kill', ['disk01.raw'])
    models = models_after(base_model(), WORKLOAD)
    rnd = random.Random(77)
    # The workload takes about two seconds, so kills are placed by progress:
    # after a random number of acknowledgements plus 0-120 ms, or during
    # the import (trial 0).
    targets = [None] + sorted(rnd.sample(range(len(WORKLOAD) - 1), 5))
    for trial, target in enumerate(targets):
        run_dir = fresh_run_dir('kill', 'run%d' % trial)
        run = ScriptRun(run_dir, WORKLOAD, label='kill%d' % trial)
        started = time.monotonic()
        if target is None:
            time.sleep(rnd.uniform(0.05, 0.4))
        else:
            while len(run.acks) < target and run.process.poll() is None:
                time.sleep(0.002)
            time.sleep(rnd.uniform(0, 0.12))
        delay = time.monotonic() - started
        if run.process.poll() is not None:
            raise SystemExit('FAIL: workload finished before the kill')
        run.kill()
        acked = len(run.acks)
        remote = '%s/kill/run%d' % (REMOTE, trial)
        upload(run_dir, remote)
        result = linux_check(remote, force=True,
                             dump=dump_paths(WORKLOAD, acked))
        verdict = judge(result['manifest'], models, WORKLOAD, acked)
        require_healthy(result, 'kill %d' % trial)
        log('  killed after %.2fs, %2d steps acknowledged: %s; %s' % (
            delay, acked, verdict, result['scan']))


def scenario_io_errors(state):
    log('== io-errors: zinject write faults on the only device')
    create_base('ioerr', ['disk01.raw'])
    steps = WORKLOAD[:6] + [('inject-write-errors', '*')] + WORKLOAD[6:10]
    models = models_after(base_model(), steps)
    run_dir = fresh_run_dir('ioerr')
    run = ScriptRun(run_dir, steps, label='ioerr')
    deadline = time.monotonic() + 300
    while run.process.poll() is None and time.monotonic() < deadline:
        if len(run.acks) > 6 and time.monotonic() - run.last_output > 15:
            break
        time.sleep(0.5)
    hung = run.process.poll() is None
    code = run.kill() if hung else run.wait()
    acked = len(run.acks)
    if acked != 7:
        raise SystemExit('FAIL: expected 7 acknowledged steps (through the '
                         'injection), got %d' % acked)
    log('  adapter %s after the fault; steps after it were not '
        'acknowledged' % ('hung (pool suspended) and was killed' if hung
                          else 'exited with %d' % code))
    remote = REMOTE + '/ioerr/run'
    upload(run_dir, remote)
    result = linux_check(remote, force=True, dump=dump_paths(steps, acked))
    verdict = judge(result['manifest'], models, steps, acked)
    require_healthy(result, 'io-errors')
    log('  Linux: %s; %s' % (verdict, result['scan']))


def scenario_enospc(state):
    log('== enospc: fill a 64 MiB pool')
    create_base('enospc', ['disk01.raw'], size='64M')
    fill = []
    for i in range(24):
        fill += [('create-file', 'fill%02d.bin' % i),
                 ('write-file', 'fill%02d.bin' % i, 0, 'm4')]
    run_dir = fresh_run_dir('enospc')
    run = ScriptRun(run_dir, fill, label='fill')
    code = run.wait()
    acked = len(run.acks)
    failure = [l for l in run.errors() if l.startswith('step ')]
    if code == 0 or not failure or \
            not failure[0].endswith('(%d)' % errno.ENOSPC):
        raise SystemExit('FAIL: expected an ENOSPC step failure, got exit '
                         '%d: %s' % (code, run.errors()))
    log('  %s (after %d acknowledged steps, %d MiB written)' % (
        failure[0], acked, sum(4 for s in fill[:acked]
                               if s[0] == 'write-file')))
    model = models_after(base_model(), fill[:acked])[-1]
    written = [s[1] for s in fill[:acked] if s[0] == 'write-file']
    free = [('remove', p) for p in written[:3]] + [('sync',), ('sync',),
                                                   ('sync',)] + [
        ('create-file', 'after-free.bin'),
        ('write-file', 'after-free.bin', 0, 'm1')]
    run_steps(run_dir, free, 'free')
    model = models_after(model, free)[-1]
    remote = REMOTE + '/enospc/run'
    upload(run_dir, remote)
    expect_state(linux_check(remote), model,
                 'pool after ENOSPC and reclaim')


def member_alone_checks(remote, members, model, label):
    """Import each mirror member by itself and compare everything."""
    for member in members:
        alone = '%s-%s' % (remote, member.split('.')[0])
        vm('rm -rf %s && mkdir -p %s && cp %s/%s %s/' % (
            alone, alone, remote, member, alone))
        expect_state(linux_check(alone), model,
                     '%s, %s alone' % (label, member))


def require_stale(remote, member, model, label):
    """Before Linux repairs the pair, the member alone must be unusable or
    out of date; otherwise the scenario would not exercise any repair."""
    alone = '%s-%s-before' % (remote, member.split('.')[0])
    out = vm('''rm -rf %(a)s && mkdir -p %(a)s && cp %(r)s/%(m)s %(a)s/
if zpool import -d %(a)s %(p)s 2>/tmp/zwr-import.err; then
    python3 -c %(manifest)s /%(ds)s
    zpool export %(p)s
else
    echo "{\\"import_error\\": \\"$(head -1 /tmp/zwr-import.err)\\"}"
fi
rm -rf %(a)s
''' % {'a': alone, 'r': remote, 'm': member, 'p': POOL, 'ds': DATASET,
       'manifest': shell_quote(MANIFEST_PY)}, capture=True)
    result = json.loads(out.strip().splitlines()[-1])
    if 'import_error' in result:
        log('  %s: before repair, %s alone does not import (%s)' % (
            label, member, result['import_error']))
        return
    detail = manifest_mismatch(result, model.expected())
    if not detail:
        raise SystemExit('FAIL (%s): %s alone was already complete before '
                         'repair; nothing was tested' % (label, member))
    log('  %s: before repair, %s alone is stale (%d entries differ)' % (
        label, member, len(detail)))


def scenario_mirror(state):
    members = ['m1.raw', 'm2.raw']
    log('== mirror: write faults on one member')
    create_base('mirror', members, mirror=True)
    # window.bin is written only while m2 is faulted and never rewritten,
    # so its blocks stay live and must be repaired on m2.
    steps = WORKLOAD[:8] + [('inject-write-errors', 'm2.raw'),
                            ('create-file', 'window.bin'),
                            ('write-file', 'window.bin', 0, 'm3')] + \
        WORKLOAD[8:18] + [('clear-injections',)] + WORKLOAD[18:]
    model = models_after(base_model(), steps)[-1]
    run_dir = fresh_run_dir('mirror', 'faulted')
    journal = os.path.join(WORK, 'mirror', 'faulted.journal')
    run_steps(run_dir, steps, 'faulted', journal)
    # zinject reports EIO after the bytes reached the file; make m2 really
    # lack everything written while the fault was active.
    sys.path.insert(0, os.path.join(ROOT, 'scripts'))
    import zfs_crash_image
    dropped = zfs_crash_image.drop_writes(
        zfs_crash_image.Journal(journal), run_dir, 'm2.raw',
        steps.index(('inject-write-errors', 'm2.raw')),
        steps.index(('clear-injections',)))
    log('  m2.raw lost %d sectors written while faulted' %
        dropped['sectors_dropped'])
    remote = REMOTE + '/mirror/faulted'
    upload(run_dir, remote)
    require_stale(remote, 'm2.raw', model, 'faulted member')
    expect_state(linux_check(remote), model, 'faulted member repaired')
    member_alone_checks(remote, members, model, 'faulted member')

    log('== mirror: one member missing on Windows')
    run_dir = fresh_run_dir('mirror', 'missing', members=['m1.raw'])
    run_steps(run_dir, WORKLOAD, 'missing')
    remote = REMOTE + '/mirror/missing'
    upload(run_dir, remote)
    vm('cp %s/mirror/base/m2.raw %s/' % (REMOTE, remote))	# stale member
    model = models_after(base_model(), WORKLOAD)[-1]
    require_stale(remote, 'm2.raw', model, 'stale member')
    expect_state(linux_check(remote), model, 'stale member repaired')
    member_alone_checks(remote, members, model, 'stale member')

    log('== mirror: 96 MiB of one member overwritten before Windows writes')
    run_dir = fresh_run_dir('mirror', 'corrupt')
    rnd = random.Random(4242)
    with open(os.path.join(run_dir, 'm1.raw'), 'r+b') as handle:
        handle.seek(4 * MiB)
        for _ in range(96):
            handle.write(rnd.randbytes(MiB))
    run_steps(run_dir, WORKLOAD, 'corrupt')
    remote = REMOTE + '/mirror/corrupt'
    upload(run_dir, remote)
    result = linux_check(remote)
    expect_state(result, model, 'corrupted member repaired')
    member_alone_checks(remote, members, model, 'corrupted member')


def scenario_labels(state):
    for name, where in (('front', 0), ('back', -1)):
        log('== labels: %s label pair destroyed' % name)
        if name == 'front':
            create_base('labels', ['disk01.raw'])
        run_dir = fresh_run_dir('labels', name)
        path = os.path.join(run_dir, 'disk01.raw')
        size = os.path.getsize(path)
        with open(path, 'r+b') as handle:
            handle.seek(0 if where == 0 else size - 512 * 1024)
            handle.write(b'\0' * 512 * 1024)
        run_steps(run_dir, WORKLOAD[:10], 'labels-' + name)
        remote = '%s/labels/%s' % (REMOTE, name)
        upload(run_dir, remote)
        out = vm('zdb -l %s/disk01.raw' % remote, capture=True)
        if 'labels = 0 1 2 3' not in out:
            raise SystemExit('FAIL: labels not all valid and identical '
                             'after export:\n' + out[:2000])
        log('  all 4 labels valid after the Windows export')
        expect_state(linux_check(remote),
                     models_after(base_model(), WORKLOAD[:10])[-1],
                     '%s labels destroyed' % name)


def main():
    global PAYLOADS
    if not os.path.exists(EXE):
        raise SystemExit('missing %s; run scripts/build-write-core.ps1' % EXE)
    chosen = [a for a in sys.argv[1:] if a in SCENARIOS] or SCENARIOS
    os.makedirs(WORK, exist_ok=True)
    PAYLOADS = make_payloads()
    vm('mkdir -p %s/payload' % REMOTE)
    scp(PAYLOADS['old'][0], 'root@%s:%s/payload/old' % (HOST, REMOTE))
    state = {}
    started = time.monotonic()
    for name in chosen:
        globals()['scenario_' + name.replace('-', '_')](state)
    log('PASS (%s) in %.0f s' % (', '.join(chosen),
                                 time.monotonic() - started))


if __name__ == '__main__':
    main()
