// SPDX-License-Identifier: CDDL-1.0
/*
 * User-mode mutation tool for disposable ZFS pool images (plan steps 10-11).
 *
 * The ZPL operations live in the portable layer (src/native/ZfsUserMode,
 * shared with the WinFsp filesystem); this file is the command line, the
 * power-loss I/O journal and the run-script driver around it.  Every
 * mutation is synced before it is acknowledged.  It must never be pointed
 * at a production pool or a pool visible to another ZFS host.
 */

#include <errno.h>
#include <limits.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <libnvpair.h>
#include <libzpool.h>
#include <libzutil.h>
#include <sys/zfs_file.h>
#include <sys/zfs_ioctl.h>
#include <sys/zio.h>

#include "zum_impl.h"

typedef zum_context_t write_context_t;

static void
print_error(const char *operation, int error)
{
    (void) fprintf(stderr, "%s: %s (%d)\n", operation,
        strerror(error), error);
}

static int
parse_u64(const char *text, uint64_t *value)
{
    char *end = NULL;
    unsigned long long parsed;

    errno = 0;
    parsed = strtoull(text, &end, 0);
    if (errno != 0 || end == text || *end != '\0')
        return (EINVAL);
    *value = (uint64_t)parsed;
    return (0);
}

#define PAYLOAD_LIMIT (256ULL * 1024 * 1024)

/*
 * libspl maps fopen()/fread() to its wosix stream wrappers, on which
 * fseek()/ftell() are not reliable, so read sequentially until EOF.
 */
static int
read_payload(const char *path, void **data, uint64_t *size)
{
    FILE *file = NULL;
    char *buffer = NULL, *grown;
    size_t length = 0, capacity = 0, got;

    file = fopen(path, "rb");
    if (file == NULL)
        return (errno != 0 ? errno : ENOENT);
    for (;;) {
        if (length == capacity) {
            capacity = capacity == 0 ? 64 * 1024 : capacity * 2;
            if (capacity > PAYLOAD_LIMIT) {
                free(buffer);
                fclose(file);
                return (EFBIG);
            }
            grown = realloc(buffer, capacity);
            if (grown == NULL) {
                free(buffer);
                fclose(file);
                return (ENOMEM);
            }
            buffer = grown;
        }
        got = fread(buffer + length, 1, capacity - length, file);
        length += got;
        if (got == 0)
            break;
    }
    if (ferror(file)) {
        free(buffer);
        fclose(file);
        return (EIO);
    }
    fclose(file);
    *data = buffer;
    *size = (uint64_t)length;
    return (0);
}

/* Print the persisted ZPL state of a path (used to check reopen cycles). */
static int
stat_path(write_context_t *context, const char *path)
{
    zum_znode_t *zp = NULL;
    uint64_t object;
    int error;

    if ((error = zum_lookup_path(context, path, &object)) != 0)
        return (error);
    if ((error = zum_zget(context, object, &zp)) != 0)
        return (error);
    (void) printf("path=%s object=%llu mode=%llo size=%llu links=%llu "
        "blksz=%u\n", path, (unsigned long long)zp->z_id,
        (unsigned long long)zp->z_mode, (unsigned long long)zp->z_size,
        (unsigned long long)zp->z_links, zp->z_blksz);
    zum_zrele(zp);
    return (0);
}

static int
write_file(write_context_t *context, const char *path, boolean_t append,
    uint64_t offset, const char *payload_path, uint64_t *written_at)
{
    void *payload = NULL;
    uint64_t payload_size = 0, object;
    int error;

    if ((error = read_payload(payload_path, &payload, &payload_size)) != 0)
        return (error);
    error = zum_lookup_path(context, path, &object);
    if (error == 0) {
        *written_at = append ? ZUM_APPEND : offset;
        error = zum_write_object(context, object, written_at, payload,
            payload_size);
    }
    free(payload);
    return (error);
}

/*
 * I/O journal for power-loss simulation (ZFS_WRITE_CORE_JOURNAL=<file>).
 *
 * Every vdev file write is recorded with the bytes it replaces, and every
 * flush and acknowledged script step is recorded in the same total order,
 * through libzpool's zfs_file observer.  scripts/zfs_crash_image.py replays
 * the journal to reconstruct the device as it would be after a power cut at
 * any recorded moment: writes completed before a completed flush survive,
 * later ones survive or vanish sector by sector.
 */
#define JOURNAL_MAGIC 0x314a575aU	/* "ZWJ1" little-endian */

typedef enum journal_type {
    JOURNAL_FILE = 1,		/* file id -> path (payload: path) */
    JOURNAL_WRITE_BEGIN,	/* payload: previous bytes, then new bytes */
    JOURNAL_WRITE_END,
    JOURNAL_FLUSH_BEGIN,
    JOURNAL_FLUSH_END,
    JOURNAL_ACK			/* id: acknowledged script step */
} journal_type_t;

typedef struct journal_record {
    uint32_t magic;
    uint32_t type;
    uint64_t id;		/* write/flush sequence, or script step */
    uint64_t file;
    uint64_t offset;
    uint64_t length;		/* bytes of new data, or path length */
    int64_t result;		/* errno of END records */
} journal_record_t;

#define JOURNAL_MAX_FILES 16

static struct {
    HANDLE handle;
    CRITICAL_SECTION lock;
    uint64_t next_id;
    int nfiles;
    zfs_file_t *files[JOURNAL_MAX_FILES];
} journal = { INVALID_HANDLE_VALUE };

static void
journal_append(const void *data, size_t length)
{
    const char *p = data;
    DWORD wrote;

    while (length > 0) {
        VERIFY(WriteFile(journal.handle, p, (DWORD)MIN(length, 1 << 30),
            &wrote, NULL));
        p += wrote;
        length -= wrote;
    }
}

static void
journal_record(journal_type_t type, uint64_t id, uint64_t file,
    uint64_t offset, uint64_t length, int64_t result)
{
    journal_record_t record = {
        JOURNAL_MAGIC, type, id, file, offset, length, result
    };

    journal_append(&record, sizeof (record));
}

/* Called with the journal lock held. */
static uint64_t
journal_file_id(zfs_file_t *fp)
{
    char path[MAX_PATH * 2];
    DWORD length;
    int i;

    for (i = 0; i < journal.nfiles; i++)
        if (journal.files[i] == fp)
            return ((uint64_t)i);
    VERIFY3S(journal.nfiles, <, JOURNAL_MAX_FILES);
    journal.files[journal.nfiles] = fp;
    length = GetFinalPathNameByHandleA(ITOH(fp->f_fd), path, sizeof (path),
        FILE_NAME_NORMALIZED);
    if (length == 0 || length >= sizeof (path))
        length = (DWORD)snprintf(path, sizeof (path), "fd-%d", fp->f_fd);
    journal_record(JOURNAL_FILE, 0, (uint64_t)journal.nfiles, 0, length, 0);
    journal_append(path, length);
    return ((uint64_t)journal.nfiles++);
}

static void
journal_observer(zfs_file_event_t event, zfs_file_t *fp, const void *buf,
    size_t count, loff_t offset, int result)
{
    OVERLAPPED ov = { 0 };
    DWORD got = 0;
    void *before;
    uint64_t file;

    EnterCriticalSection(&journal.lock);
    file = journal_file_id(fp);
    switch (event) {
    case ZFS_FILE_WRITE_BEGIN:
        /* The bytes this write replaces; zero beyond the end of file. */
        VERIFY3P(before = calloc(1, MAX(count, 1)), !=, NULL);
        ov.Offset = (DWORD)((uint64_t)offset & 0xffffffffULL);
        ov.OffsetHigh = (DWORD)((uint64_t)offset >> 32);
        (void) ReadFile(ITOH(fp->f_fd), before, (DWORD)count, &got, &ov);
        journal_record(JOURNAL_WRITE_BEGIN, journal.next_id++, file,
            (uint64_t)offset, count, 0);
        journal_append(before, count);
        journal_append(buf, count);
        free(before);
        break;
    case ZFS_FILE_WRITE_END:
        journal_record(JOURNAL_WRITE_END, journal.next_id++, file,
            (uint64_t)offset, count, result);
        break;
    case ZFS_FILE_FLUSH_BEGIN:
        journal_record(JOURNAL_FLUSH_BEGIN, journal.next_id++, file, 0, 0, 0);
        break;
    case ZFS_FILE_FLUSH_END:
        journal_record(JOURNAL_FLUSH_END, journal.next_id++, file, 0, 0,
            result);
        break;
    }
    LeaveCriticalSection(&journal.lock);
}

static int
journal_open(const char *path)
{
    journal.handle = CreateFileA(path, GENERIC_WRITE, FILE_SHARE_READ, NULL,
        CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (journal.handle == INVALID_HANDLE_VALUE)
        return (EIO);
    InitializeCriticalSection(&journal.lock);
    zfs_file_observer = journal_observer;
    return (0);
}

static void
journal_ack(uint64_t step)
{
    if (journal.handle == INVALID_HANDLE_VALUE)
        return;
    EnterCriticalSection(&journal.lock);
    journal_record(JOURNAL_ACK, step, 0, 0, 0, 0);
    LeaveCriticalSection(&journal.lock);
}

static void
journal_close(void)
{
    if (journal.handle == INVALID_HANDLE_VALUE)
        return;
    zfs_file_observer = NULL;
    EnterCriticalSection(&journal.lock);
    CloseHandle(journal.handle);
    journal.handle = INVALID_HANDLE_VALUE;
    LeaveCriticalSection(&journal.lock);
}

typedef struct command {
    const char *name;
    int min_args, max_args;	/* after <image-directory> <pool> <dataset> */
    const char *usage;
} command_t;

static const command_t commands[] = {
    { "probe", 0, 0, "" },
    { "sync", 0, 0, "" },
    { "stat", 1, 1, "<path>" },
    { "create-file", 1, 1, "<path>" },
    { "mkdir", 1, 1, "<path>" },
    { "write-file", 3, 3, "<path> <offset|append> <payload-file>" },
    { "truncate", 2, 2, "<path> <size>" },
    { "remove", 1, 2, "<path> [--defer-reclaim]" },
    { "rmdir", 1, 2, "<path> [--defer-reclaim]" },
    { "rename", 2, 2, "<from-path> <to-path>" },
    { "run-script", 1, 1, "<script-file>" },
};

/*
 * run-script: one import, then one command per line (the commands above
 * without the image arguments, plus "inject-write-errors <leaf-path-part|*>"
 * and "clear-injections").  "ack <n>" is printed, and journaled, after step
 * n's transaction groups are synced, i.e. once the step is durable.
 */
#define SCRIPT_MAX_ARGS 8

static void
usage(const char *program)
{
    size_t i;

    for (i = 0; i < sizeof (commands) / sizeof (commands[0]); i++)
        (void) fprintf(stderr, "%s %s <image-directory> <pool> <dataset> %s\n",
            i == 0 ? "Usage:" : "      ", commands[i].name,
            commands[i].usage);
    (void) fprintf(stderr, "Paths are relative to the dataset root.\n");
    (void) program;
}

static int
run_command(write_context_t *context, const char *command, int argc,
    char **argv)
{
    boolean_t defer = argc == 2 && strcmp(argv[1], "--defer-reclaim") == 0;
    uint64_t value = 0;
    int error;

    if ((strcmp(command, "remove") == 0 || strcmp(command, "rmdir") == 0) &&
        argc == 2 && !defer)
        return (EINVAL);

    if (strcmp(command, "probe") == 0) {
        (void) printf("pool=%s dataset=%s root=%llu\n", context->pool_name,
            context->dataset_name, (unsigned long long)context->root_obj);
        return (0);
    }
    if (strcmp(command, "sync") == 0) {
        txg_wait_synced(dmu_objset_pool(context->os), 0);
        (void) printf("pool=%s dataset=%s synced\n", context->pool_name,
            context->dataset_name);
        return (0);
    }
    if (strcmp(command, "stat") == 0)
        return (stat_path(context, argv[0]));
    if (strcmp(command, "create-file") == 0 ||
        strcmp(command, "mkdir") == 0) {
        error = zum_create_path(context, argv[0],
            strcmp(command, "mkdir") == 0, &value);
        if (error == 0)
            (void) printf("%s %s object=%llu\n", command, argv[0],
                (unsigned long long)value);
        return (error);
    }
    if (strcmp(command, "write-file") == 0) {
        boolean_t append = strcmp(argv[1], "append") == 0;

        error = append ? 0 : parse_u64(argv[1], &value);
        if (error == 0)
            error = write_file(context, argv[0], append, value, argv[2],
                &value);
        if (error == 0)
            (void) printf("write-file %s offset=%llu\n", argv[0],
                (unsigned long long)value);
        return (error);
    }
    if (strcmp(command, "truncate") == 0) {
        error = parse_u64(argv[1], &value);
        if (error == 0)
            error = zum_truncate_path(context, argv[0], value);
        if (error == 0)
            (void) printf("truncate %s size=%llu\n", argv[0],
                (unsigned long long)value);
        return (error);
    }
    if (strcmp(command, "remove") == 0 || strcmp(command, "rmdir") == 0) {
        error = zum_remove_path(context, argv[0],
            strcmp(command, "rmdir") == 0, defer);
        if (error == 0)
            (void) printf("%s %s%s\n", command, argv[0],
                defer ? " (reclaim deferred)" : "");
        return (error);
    }
    if (strcmp(command, "rename") == 0) {
        error = zum_rename_path(context, argv[0], argv[1]);
        if (error == 0)
            (void) printf("rename %s -> %s\n", argv[0], argv[1]);
        return (error);
    }
    return (EINVAL);
}

static int
run_script(write_context_t *context, const char *path)
{
    char *text = NULL, *line, *line_next, *token, *next;
    char *argv[SCRIPT_MAX_ARGS];
    uint64_t size = 0, step = 0;
    int argc, error;
    size_t i;

    /* read_payload(): libspl's stdio streams are not fully stdio. */
    if ((error = read_payload(path, (void **)&text, &size)) != 0)
        return (error);
    if ((line = realloc(text, size + 1)) == NULL) {
        free(text);
        return (ENOMEM);
    }
    text = line;
    text[size] = '\0';
    for (line = strtok_s(text, "\r\n", &line_next); error == 0 &&
        line != NULL; line = strtok_s(NULL, "\r\n", &line_next)) {
        argc = 0;
        for (token = strtok_s(line, " \t", &next); token != NULL &&
            argc < SCRIPT_MAX_ARGS; token = strtok_s(NULL, " \t", &next))
            argv[argc++] = token;
        if (argc == 0 || argv[0][0] == '#')
            continue;
        if (strcmp(argv[0], "inject-write-errors") == 0 && argc == 2) {
            error = zum_inject_write_errors(context, argv[1]);
        } else if (strcmp(argv[0], "clear-injections") == 0 && argc == 1) {
            zum_clear_injections(context);
        } else {
            error = EINVAL;
            for (i = 0; i < sizeof (commands) / sizeof (commands[0]); i++) {
                if (strcmp(argv[0], commands[i].name) == 0 &&
                    strcmp(argv[0], "run-script") != 0 &&
                    argc - 1 >= commands[i].min_args &&
                    argc - 1 <= commands[i].max_args) {
                    error = run_command(context, argv[0], argc - 1,
                        argv + 1);
                    break;
                }
            }
        }
        if (error != 0) {
            (void) fprintf(stderr, "step %llu: %s: %s (%d)\n",
                (unsigned long long)step, argv[0], strerror(error), error);
            break;
        }
        journal_ack(step);
        (void) printf("ack %llu\n", (unsigned long long)step);
        (void) fflush(stdout);
        step++;
    }
    free(text);
    return (error);
}

int
main(int argc, char **argv)
{
    write_context_t context = { 0 };
    const command_t *command = NULL;
    int error, close_error;
    size_t i;

    for (i = 0; argc >= 2 && i < sizeof (commands) / sizeof (commands[0]);
        i++) {
        if (strcmp(argv[1], commands[i].name) == 0)
            command = &commands[i];
    }
    if (command == NULL || argc - 5 < command->min_args ||
        argc - 5 > command->max_args) {
        usage(argv[0]);
        return (2);
    }

    /*
     * VERIFY failures abort(); keep the debug CRT from blocking unattended
     * runs on a modal dialog so the failure surfaces as an exit code.
     */
    _set_abort_behavior(0, _WRITE_ABORT_MSG | _CALL_REPORTFAULT);

    context.sync_each_op = B_TRUE;
    context.search_path = argv[2];
    context.pool_name = argv[3];
    context.dataset_name = argv[4];

    if (getenv("ZFS_WRITE_CORE_JOURNAL") != NULL &&
        (error = journal_open(getenv("ZFS_WRITE_CORE_JOURNAL"))) != 0) {
        print_error("open journal", error);
        return (1);
    }

    error = zum_open(&context);
    if (error == 0) {
        if (strcmp(command->name, "run-script") == 0)
            error = run_script(&context, argv[5]);
        else
            error = run_command(&context, command->name, argc - 5,
                argv + 5);
        if (error != 0)
            print_error(command->name, error);
    }

    (void) fflush(stdout);
    close_error = zum_close(&context);
    journal_close();
    return (error == 0 && close_error == 0 ? 0 : 1);
}
