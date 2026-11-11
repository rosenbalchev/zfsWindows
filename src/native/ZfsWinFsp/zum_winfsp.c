// SPDX-License-Identifier: CDDL-1.0
/*
 * zfswin-um.exe: OpenZFS datasets as Windows volumes in user mode, through
 * WinFsp (https://github.com/winfsp/winfsp).
 *
 *   WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos
 *
 * This file is the front end only: it turns WinFsp's file-system requests
 * into calls on the portable ZPL layer (zum.h) and holds the per-handle
 * state.  The ZFS engine underneath is upstream libzpool, the same DMU/SPA
 * code the kernel driver runs.  No Microsoft-signed component of our own is
 * needed: WinFsp's driver is already signed, and this is a normal service.
 *
 * It also serves the ZFS ioctl bridge (sys/zfs_bridge.h) on a local pipe,
 * so zpool.exe, zfs.exe and everything on top of them (ZfsWinBroker,
 * zfswin.exe, the PowerShell module, ZfsWinGui) administer this engine as
 * they administer the kernel driver.
 *
 * Usage:
 *   zfswin-um mount <source> <pool> <dataset> <mountpoint> [--readonly]
 *       Console mode; Ctrl+C unmounts and exports.  <source> is an image
 *       directory, an image file or a device path (';'-separated list);
 *       <mountpoint> is X: or an empty folder.
 *   zfswin-um            (as the ZfsWinUserMode service)
 *       Imports the pools recorded under HKLM\SOFTWARE\OpenZFS for Windows\
 *       UserMode\Pools, then mounts every entry under ...\Mounts\<name>
 *       (Source, Pool, Dataset, MountPoint, ReadOnly).
 */

/* First, as in WinFsp's samples: it brings <windows.h> and the NT types. */
#include <winfsp/winfsp.h>
#include <sddl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "zum.h"
/* The wire format, shared with libspl's client (patch 0024). */
#include "../../../third_party/openzfs-windows/lib/libspl/include/os/windows/sys/zfs_bridge.h"

#define PROGNAME L"ZfsWinUserMode"
#define BASE_KEY L"SOFTWARE\\OpenZFS for Windows\\UserMode"
#define MOUNTS_KEY BASE_KEY L"\\Mounts"
#define POOLS_KEY BASE_KEY L"\\Pools"
#define MAX_MOUNTS 64
#define MAX_SNAPSHOTS 64
#define OBJECT_LOCK_COUNT 64
#define PATH_BYTES (32768 * 3)

/*
 * Block cloning.  WinFsp hands a file system only control codes with a
 * custom device type (bit 0x8000), so the standard
 * FSCTL_DUPLICATE_EXTENTS_TO_FILE never arrives here; this code takes the
 * same DUPLICATE_EXTENTS_DATA input on a handle to the target file.
 * zfswin.exe clone uses it when the standard one is not supported.
 */
#define ZUM_IOCTL_DUPLICATE_EXTENTS \
    CTL_CODE(0x8F5A, 0x801, METHOD_BUFFERED, FILE_ANY_ACCESS)

/* .zfs control directory object numbers, as the kernel driver's. */
#define ZUM_CTL_ROOT_INDEX 0x0000FFFFFFFFFFFFULL
#define ZUM_CTL_SNAPDIR_INDEX 0x0000FFFFFFFFFFFDULL

/* Error statuses not in every SDK's ntstatus subset. */
#define ZUM_STATUS_CANNOT_DELETE ((NTSTATUS)0xC0000121L)

typedef struct snapshot {
    char name[256];
    uint64_t id;
    zum_fs_t *fs;
    LONG refs;			/* open handles */
} snapshot_t;

typedef struct mount {
    BOOLEAN in_use;
    BOOLEAN readonly;
    BOOLEAN suspended;		/* released for a rollback */
    zum_fs_t *fs;
    FSP_FILE_SYSTEM *fsp;
    SRWLOCK lock;		/* mount lifetime/barrier for exclusive mutations */
    SRWLOCK object_locks[OBJECT_LOCK_COUNT]; /* serialize I/O per file */
    uint64_t root;
    int case_sensitive;
    char dataset[256];
    char pool[256];
    WCHAR mountpoint[MAX_PATH];
    CRITICAL_SECTION snap_lock;	/* the snapshots array */
    snapshot_t snapshots[MAX_SNAPSHOTS];
} mount_t;

typedef enum kind { K_FILE, K_CTL_ROOT, K_CTL_SNAPDIR } kind_t;

typedef struct file_context {
    kind_t kind;
    zum_fs_t *fs;		/* the live dataset or a snapshot */
    snapshot_t *snapshot;	/* referenced while the handle is open */
    uint64_t object;		/* data object: the stream's, for a stream */
    uint64_t base;		/* the file itself */
    char *stream;		/* stream name, or NULL */
    BOOLEAN is_dir;
    BOOLEAN write_through;
    UINT32 pid;			/* the opening process (Create/Open only) */
    PVOID dir_buffer;
} file_context_t;

static mount_t mounts[MAX_MOUNTS];
static SRWLOCK table_lock = SRWLOCK_INIT;	/* slots in mounts[] */
static HANDLE service_token;

#define MOUNT(fsp) ((mount_t *)(fsp)->UserContext)
#define EXCLUSIVE(m) AcquireSRWLockExclusive(&(m)->lock)
#define SHARED(m) AcquireSRWLockShared(&(m)->lock)
#define UNLOCK_EXCLUSIVE(m) ReleaseSRWLockExclusive(&(m)->lock)
#define UNLOCK_SHARED(m) ReleaseSRWLockShared(&(m)->lock)

static SRWLOCK *
object_lock(mount_t *m, uint64_t object)
{
    return (&m->object_locks[object % OBJECT_LOCK_COUNT]);
}

static NTSTATUS
status_of(int error)
{
    return ((NTSTATUS)zum_fs_ntstatus(error));
}

static char *
utf8_of(PCWSTR s)
{
    int n = WideCharToMultiByte(CP_UTF8, 0, s, -1, NULL, 0, NULL, NULL);
    char *out = n > 0 ? malloc(n) : NULL;

    if (out != NULL)
        WideCharToMultiByte(CP_UTF8, 0, s, -1, out, n, NULL, NULL);
    return (out);
}

static void
wide_of(const char *s, PWSTR out, int chars)
{
    if (MultiByteToWideChar(CP_UTF8, 0, s, -1, out, chars) <= 0)
        out[0] = L'\0';
}

/*
 * "\dir\file[:stream[:$DATA]]" (UTF-16) to "/dir/file" (UTF-8) and the
 * stream name (NULL for the file's data).  NULL: not representable.
 */
static char *
to_path(PCWSTR name, char **stream)
{
    char *path, *p, *colon, *type;
    int n;

    *stream = NULL;
    n = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, name, -1, NULL, 0,
        NULL, NULL);
    if (n <= 0 || n > PATH_BYTES || (path = malloc(n)) == NULL)
        return (NULL);
    WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, name, -1, path, n,
        NULL, NULL);
    for (p = path; *p != '\0'; p++)
        if (*p == '\\')
            *p = '/';
    p = strrchr(path, '/');
    if ((colon = strchr(p != NULL ? p : path, ':')) != NULL) {
        *colon++ = '\0';
        if ((type = strchr(colon, ':')) != NULL) {
            *type++ = '\0';
            if (_stricmp(type, "$DATA") != 0) {
                free(path);
                return (NULL);
            }
        }
        if (*colon != '\0' && (*stream = _strdup(colon)) == NULL) {
            free(path);
            return (NULL);
        }
    }
    return (path);
}

/* ---- security descriptors ----------------------------------------------- */

/*
 * The kernel driver's descriptor for a dataset root with no stored one
 * (zfs_attach_security_root(): owner and group from the POSIX ids, its
 * def_dacls table).
 */
static PSECURITY_DESCRIPTOR
default_root_sd(uint64_t uid, uint64_t gid, ULONG *size)
{
    WCHAR sddl[512], owner[64];
    PSECURITY_DESCRIPTOR sd = NULL;

    if (uid == 0)
        wcscpy_s(owner, ARRAYSIZE(owner), L"SY");
    else
        swprintf_s(owner, ARRAYSIZE(owner), L"S-1-22-1-%llu", uid);
    swprintf_s(sddl, ARRAYSIZE(sddl), L"O:%sG:S-1-22-2-%lluD:"
        L"(A;;FA;;;BA)(A;OICIIO;FA;;;BA)(A;;FA;;;SY)(A;OICIIO;FA;;;SY)"
        L"(A;OICI;0x1200a9;;;BU)(A;OICIIO;0x1301bf;;;AU)(A;;0x1301bf;;;AU)",
        owner, gid);
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl,
        SDDL_REVISION_1, &sd, size))
        return (NULL);
    return (sd);
}

/*
 * An object's descriptor (LocalFree it): the stored one, else - for files
 * written elsewhere - the one the kernel driver derives, inheriting from
 * the parent's with the owner and group from the POSIX ids.
 */
static PSECURITY_DESCRIPTOR
object_sd(zum_fs_t *fs, uint64_t object, BOOLEAN is_dir, int depth,
    ULONG *size)
{
    PSECURITY_DESCRIPTOR sd = NULL, parent_sd, creator = NULL, out = NULL;
    uint64_t uid = 0, gid = 0, parent = 0;
    uint32_t len = 0;
    WCHAR sddl[128];
    ULONG n;
    int error;

    error = zum_fs_get_security(fs, object, NULL, 0, &len);
    if ((error == 0 || error == ERANGE) && len > 0 &&
        (sd = LocalAlloc(LMEM_FIXED, len)) != NULL) {
        if (zum_fs_get_security(fs, object, sd, len, &len) == 0 &&
            IsValidSecurityDescriptor(sd)) {
            *size = len;
            return (sd);
        }
        LocalFree(sd);
    }
    if (zum_fs_owner(fs, object, &uid, &gid, &parent) != 0)
        return (NULL);
    if (parent == object || depth > 256)
        return (default_root_sd(uid, gid, size));
    if ((parent_sd = object_sd(fs, parent, TRUE, depth + 1, &n)) == NULL)
        return (NULL);
    swprintf_s(sddl, ARRAYSIZE(sddl), uid == 0 ? L"O:SYG:S-1-22-2-%llu" :
        L"O:S-1-22-1-%lluG:S-1-22-2-%llu", uid == 0 ? gid : uid, gid);
    if (ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl,
        SDDL_REVISION_1, &creator, NULL) &&
        CreatePrivateObjectSecurityEx(parent_sd, creator, &out, NULL, is_dir,
        SEF_DACL_AUTO_INHERIT | SEF_SACL_AUTO_INHERIT |
        SEF_AVOID_OWNER_CHECK | SEF_AVOID_PRIVILEGE_CHECK, service_token,
        FspGetFileGenericMapping())) {
        n = GetSecurityDescriptorLength(out);
        if ((sd = LocalAlloc(LMEM_FIXED, n)) != NULL) {
            memcpy(sd, out, n);
            *size = n;
        }
        DestroyPrivateObjectSecurity(&out);
    }
    if (creator != NULL)
        LocalFree(creator);
    LocalFree(parent_sd);
    return (sd);
}

static NTSTATUS
copy_sd(PSECURITY_DESCRIPTOR sd, ULONG size, PSECURITY_DESCRIPTOR out,
    SIZE_T *psize)
{
    if (sd == NULL)
        return (STATUS_INSUFFICIENT_RESOURCES);
    if (*psize < size) {
        *psize = size;
        return (STATUS_BUFFER_OVERFLOW);
    }
    *psize = size;
    if (out != NULL)
        memcpy(out, sd, size);
    return (STATUS_SUCCESS);
}

/* ---- snapshots (.zfs\snapshot) --------------------------------------------- */

/* The open view of a snapshot of m's dataset, opening it on first use. */
static snapshot_t *
snapshot_get(mount_t *m, const char *name, int *error)
{
    snapshot_t *s = NULL, *free_slot = NULL;
    int i;

    EnterCriticalSection(&m->snap_lock);
    for (i = 0; i < MAX_SNAPSHOTS; i++) {
        if (m->snapshots[i].fs == NULL) {
            if (free_slot == NULL)
                free_slot = &m->snapshots[i];
        } else if ((m->case_sensitive ? strcmp(m->snapshots[i].name, name) :
            _stricmp(m->snapshots[i].name, name)) == 0) {
            s = &m->snapshots[i];
            break;
        }
    }
    if (s == NULL && free_slot == NULL)
        *error = EMFILE;
    else if (s == NULL &&
        (*error = zum_fs_snapshot_open(m->fs, name, &free_slot->fs)) == 0) {
        s = free_slot;
        strncpy_s(s->name, sizeof (s->name), name, _TRUNCATE);
        s->refs = 0;
    }
    LeaveCriticalSection(&m->snap_lock);
    return (s);
}

/*
 * Close unused snapshot views (all, or the one named).  force: also those
 * with open handles, once the volume is gone and no callback can reach
 * them (the cache manager closes cached files lazily, step-18 VM run).
 */
static void
snapshots_close(mount_t *m, const char *name, BOOLEAN force)
{
    int i;

    EnterCriticalSection(&m->snap_lock);
    for (i = 0; i < MAX_SNAPSHOTS; i++) {
        snapshot_t *s = &m->snapshots[i];

        if (s->fs == NULL || (s->refs > 0 && !force) ||
            (name != NULL && strcmp(s->name, name) != 0))
            continue;
        zum_fs_snapshot_close(s->fs);
        s->fs = NULL;
    }
    LeaveCriticalSection(&m->snap_lock);
}

/* ---- name resolution ------------------------------------------------------- */

typedef struct target {
    kind_t kind;
    zum_fs_t *fs;
    snapshot_t *snapshot;
    const char *rel;		/* path inside fs */
} target_t;

static int
name_eq(mount_t *m, const char *a, const char *b, size_t n)
{
    return ((m->case_sensitive ? strncmp(a, b, n) : _strnicmp(a, b, n)) == 0);
}

/* Route "/.zfs/snapshot/<name>/..." to the snapshot; everything else live. */
static int
resolve(mount_t *m, char *path, target_t *t)
{
    char *p = path, *slash;
    int error = 0;

    memset(t, 0, sizeof (*t));
    t->kind = K_FILE;
    t->fs = m->fs;
    t->rel = path;
    while (*p == '/')
        p++;
    if (!name_eq(m, p, ".zfs", 4) || (p[4] != '\0' && p[4] != '/'))
        return (0);
    p += 4;
    while (*p == '/')
        p++;
    if (*p == '\0') {
        t->kind = K_CTL_ROOT;
        return (0);
    }
    if (!name_eq(m, p, "snapshot", 8) || (p[8] != '\0' && p[8] != '/'))
        return (ENOENT);
    p += 8;
    while (*p == '/')
        p++;
    if (*p == '\0') {
        t->kind = K_CTL_SNAPDIR;
        return (0);
    }
    if ((slash = strchr(p, '/')) != NULL)
        *slash = '\0';
    t->snapshot = snapshot_get(m, p, &error);
    if (t->snapshot == NULL)
        return (error);
    t->fs = t->snapshot->fs;
    if (slash != NULL) {
        *slash = '/';
        t->rel = slash;
    } else {
        t->rel = "";
    }
    return (0);
}

/* ---- file information ------------------------------------------------------ */

static uint64_t
index_of(const file_context_t *c, uint64_t object)
{
    return (c != NULL && c->snapshot != NULL ?
        (object | (c->snapshot->id << 48)) : object);
}

static void
to_file_info(const zum_attr_t *a, FSP_FSCTL_FILE_INFO *info)
{
    memset(info, 0, sizeof (*info));
    info->FileAttributes = a->attributes == ZUM_FA_NORMAL && a->is_dir ?
        FILE_ATTRIBUTE_DIRECTORY : a->attributes;
    info->AllocationSize = a->allocation;
    info->FileSize = a->size;
    info->CreationTime = a->creation_time;
    info->LastAccessTime = a->access_time;
    info->LastWriteTime = a->write_time;
    info->ChangeTime = a->change_time;
    info->IndexNumber = a->object;
    info->HardLinks = 0;	/* WinFsp: "unimplemented; set to 0" */
}

/* A .zfs directory: read-only, with the dataset root's times. */
static NTSTATUS
ctl_info(mount_t *m, uint64_t index, uint64_t creation,
    FSP_FSCTL_FILE_INFO *info)
{
    zum_attr_t attr;
    int error = zum_fs_getattr(m->fs, m->root, &attr);

    if (error != 0)
        return (status_of(error));
    to_file_info(&attr, info);
    info->FileAttributes = FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_READONLY;
    info->IndexNumber = index;
    if (creation != 0)
        info->CreationTime = info->LastWriteTime = info->ChangeTime = creation;
    return (STATUS_SUCCESS);
}

static NTSTATUS
context_info_unlocked(mount_t *m, file_context_t *c,
    FSP_FSCTL_FILE_INFO *info)
{
    zum_attr_t attr, base;
    int error;

    if (c->kind == K_CTL_ROOT)
        return (ctl_info(m, ZUM_CTL_ROOT_INDEX, 0, info));
    if (c->kind == K_CTL_SNAPDIR)
        return (ctl_info(m, ZUM_CTL_SNAPDIR_INDEX, 0, info));
    if ((error = zum_fs_getattr(c->fs, c->object, &attr)) != 0)
        return (status_of(error));
    /* A stream has its own size, and its file's attributes and times. */
    if (c->stream != NULL && zum_fs_getattr(c->fs, c->base, &base) == 0) {
        base.size = attr.size;
        base.allocation = attr.allocation;
        attr = base;
    }
    to_file_info(&attr, info);
    info->IndexNumber = index_of(c, c->base);
    if (c->snapshot != NULL)
        info->FileAttributes |= FILE_ATTRIBUTE_READONLY;
    return (STATUS_SUCCESS);
}

static NTSTATUS
context_info(mount_t *m, file_context_t *c, FSP_FSCTL_FILE_INFO *info)
{
    SRWLOCK *lock;
    NTSTATUS status;

    if (c->kind != K_FILE)
        return (context_info_unlocked(m, c, info));
    lock = object_lock(m, c->base);
    AcquireSRWLockShared(lock);
    status = context_info_unlocked(m, c, info);
    ReleaseSRWLockShared(lock);
    return (status);
}

static NTSTATUS
new_context(const target_t *t, uint64_t object, uint64_t base, char *stream,
    BOOLEAN is_dir, UINT32 create_options, PVOID *pcontext)
{
    file_context_t *c = calloc(1, sizeof (*c));

    if (c == NULL) {
        free(stream);
        return (STATUS_INSUFFICIENT_RESOURCES);
    }
    c->kind = t->kind;
    c->fs = t->fs;
    c->snapshot = t->snapshot;
    if (c->snapshot != NULL)
        InterlockedIncrement(&c->snapshot->refs);
    c->object = object;
    c->base = base;
    c->stream = stream;
    c->is_dir = is_dir;
    c->write_through = (create_options & FILE_WRITE_THROUGH) != 0;
    /* WinFsp knows the caller only during Create and Open. */
    c->pid = FspFileSystemOperationProcessId();
    *pcontext = c;
    return (STATUS_SUCCESS);
}

static BOOLEAN
writable(mount_t *m, file_context_t *c)
{
    return (!m->readonly && c->kind == K_FILE && c->snapshot == NULL);
}

/* ---- WinFsp operations ----------------------------------------------------- */

static NTSTATUS
GetVolumeInfo(FSP_FILE_SYSTEM *fsp, FSP_FSCTL_VOLUME_INFO *info)
{
    mount_t *m = MOUNT(fsp);
    zum_statfs_t st;
    int error;

    SHARED(m);
    error = zum_fs_statfs(m->fs, &st);
    UNLOCK_SHARED(m);
    if (error != 0)
        return (status_of(error));
    info->TotalSize = st.total_bytes;
    info->FreeSize = st.free_bytes;
    info->VolumeLabelLength = (UINT16)(wcslen(L"ZFS") * sizeof (WCHAR));
    memcpy(info->VolumeLabel, L"ZFS", info->VolumeLabelLength);
    return (STATUS_SUCCESS);
}

static NTSTATUS
GetSecurityByName(FSP_FILE_SYSTEM *fsp, PWSTR name, PUINT32 pattributes,
    PSECURITY_DESCRIPTOR out, SIZE_T *psize)
{
    mount_t *m = MOUNT(fsp);
    char *stream, *path = to_path(name, &stream);
    PSECURITY_DESCRIPTOR sd = NULL;
    FSP_FSCTL_FILE_INFO info;
    zum_attr_t attr;
    target_t t;
    uint64_t object;
    ULONG size = 0;
    NTSTATUS status;
    int error;

    if (path == NULL)
        return (STATUS_OBJECT_NAME_INVALID);
    SHARED(m);
    error = resolve(m, path, &t);
    if (error == 0 && t.kind != K_FILE) {
        status = ctl_info(m, ZUM_CTL_ROOT_INDEX, 0, &info);
        if (NT_SUCCESS(status) && pattributes != NULL)
            *pattributes = info.FileAttributes;
        if (NT_SUCCESS(status) && psize != NULL &&
            (sd = object_sd(m->fs, m->root, TRUE, 0, &size)) != NULL)
            status = copy_sd(sd, size, out, psize);
        goto done;
    }
    if (error == 0)
        error = zum_fs_lookup(t.fs, t.rel, &object);
    if (error == 0)
        error = zum_fs_getattr(t.fs, object, &attr);
    if (error == 0 && stream != NULL) {
        uint64_t s;

        error = zum_fs_stream_open(t.fs, object, stream, 0, &s);
    }
    status = status_of(error);
    if (NT_SUCCESS(status) && pattributes != NULL) {
        *pattributes = attr.is_dir ? (attr.attributes |
            FILE_ATTRIBUTE_DIRECTORY) & ~FILE_ATTRIBUTE_NORMAL :
            attr.attributes;
        if (t.snapshot != NULL)
            *pattributes |= FILE_ATTRIBUTE_READONLY;
    }
    if (NT_SUCCESS(status) && psize != NULL) {
        sd = object_sd(t.fs, object, (BOOLEAN)attr.is_dir, 0, &size);
        status = copy_sd(sd, size, out, psize);
    }
done:
    UNLOCK_SHARED(m);
    if (sd != NULL)
        LocalFree(sd);
    free(path);
    free(stream);
    return (status);
}

static NTSTATUS
Create(FSP_FILE_SYSTEM *fsp, PWSTR name, UINT32 create_options,
    UINT32 granted_access, UINT32 attributes, PSECURITY_DESCRIPTOR sd,
    UINT64 allocation_size, PVOID *pcontext, FSP_FSCTL_FILE_INFO *info)
{
    mount_t *m = MOUNT(fsp);
    BOOLEAN is_dir = (create_options & FILE_DIRECTORY_FILE) != 0;
    char *stream, *path = to_path(name, &stream);
    uint64_t object = 0, base = 0;
    target_t t;
    NTSTATUS status;
    int error;

    (void) granted_access; (void) allocation_size;
    if (path == NULL)
        return (STATUS_OBJECT_NAME_INVALID);
    if (m->readonly) {
        free(path);
        free(stream);
        return (STATUS_MEDIA_WRITE_PROTECTED);
    }
    EXCLUSIVE(m);
    error = resolve(m, path, &t);
    if (error == 0 && (t.kind != K_FILE || t.snapshot != NULL))
        error = EROFS;
    if (error == 0 && stream != NULL) {
        /* A new stream of an existing file. */
        error = zum_fs_lookup(t.fs, t.rel, &base);
        if (error == 0)
            error = zum_fs_stream_open(t.fs, base, stream, 0, &object);
        if (error == 0)
            error = EEXIST;
        else if (error == ENOENT && base != 0)
            error = zum_fs_stream_open(t.fs, base, stream, 1, &object);
        is_dir = FALSE;
    } else if (error == 0) {
        error = zum_fs_create(t.fs, t.rel, is_dir, &object);
        base = object;
        if (error == 0 && (attributes & ZUM_FA_SETTABLE) != 0)
            error = zum_fs_setattr(t.fs, object,
                attributes & ZUM_FA_SETTABLE, 0, 0, 0, 0);
        /* WinFsp computed it from the parent's and the caller's. */
        if (error == 0 && sd != NULL)
            error = zum_fs_set_security(t.fs, object, sd,
                GetSecurityDescriptorLength(sd));
    }
    status = error == ENOENT ? STATUS_OBJECT_PATH_NOT_FOUND :
        error == EROFS ? STATUS_MEDIA_WRITE_PROTECTED : status_of(error);
    if (NT_SUCCESS(status))
        status = new_context(&t, object, base, stream, is_dir,
            create_options, pcontext);
    else
        free(stream);
    if (NT_SUCCESS(status))
        status = context_info(m, *pcontext, info);
    UNLOCK_EXCLUSIVE(m);
    free(path);
    return (status);
}

static NTSTATUS
Open(FSP_FILE_SYSTEM *fsp, PWSTR name, UINT32 create_options,
    UINT32 granted_access, PVOID *pcontext, FSP_FSCTL_FILE_INFO *info)
{
    mount_t *m = MOUNT(fsp);
    char *stream, *path = to_path(name, &stream);
    uint64_t object = 0, base = 0;
    zum_attr_t attr;
    target_t t;
    NTSTATUS status;
    int error;

    (void) granted_access;
    if (path == NULL)
        return (STATUS_OBJECT_NAME_INVALID);
    SHARED(m);
    error = resolve(m, path, &t);
    if (error == 0 && t.kind != K_FILE) {
        if (stream != NULL)
            error = ENOENT;
    } else if (error == 0) {
        error = zum_fs_lookup(t.fs, t.rel, &base);
        object = base;
        if (error == 0)
            error = zum_fs_getattr(t.fs, base, &attr);
        if (error == 0 && stream != NULL)
            error = zum_fs_stream_open(t.fs, base, stream, 0, &object);
    }
    status = status_of(error);
    if (NT_SUCCESS(status))
        status = new_context(&t, object, base, stream,
            t.kind != K_FILE || (stream == NULL && attr.is_dir),
            create_options, pcontext);
    else
        free(stream);
    if (NT_SUCCESS(status))
        status = context_info(m, *pcontext, info);
    UNLOCK_SHARED(m);
    free(path);
    return (status);
}

static NTSTATUS
Overwrite(FSP_FILE_SYSTEM *fsp, PVOID context, UINT32 attributes,
    BOOLEAN replace_attributes, UINT64 allocation_size,
    FSP_FSCTL_FILE_INFO *info)
{
    mount_t *m = MOUNT(fsp);
    file_context_t *c = context;
    zum_attr_t attr;
    NTSTATUS status;
    int error;

    (void) allocation_size;
    if (!writable(m, c))
        return (STATUS_MEDIA_WRITE_PROTECTED);
    EXCLUSIVE(m);
    error = zum_fs_truncate(c->fs, c->object, 0);
    if (error == 0 && c->stream == NULL)
        error = zum_fs_getattr(c->fs, c->object, &attr);
    if (error == 0 && c->stream == NULL) {
        uint32_t a = replace_attributes ? attributes :
            (attr.attributes | attributes);
        error = zum_fs_setattr(c->fs, c->object, a & ZUM_FA_SETTABLE,
            0, 0, 0, 0);
    }
    status = status_of(error);
    if (NT_SUCCESS(status))
        status = context_info(m, c, info);
    UNLOCK_EXCLUSIVE(m);
    return (status);
}

static VOID
Cleanup(FSP_FILE_SYSTEM *fsp, PVOID context, PWSTR name, ULONG flags)
{
    mount_t *m = MOUNT(fsp);
    file_context_t *c = context;
    char *stream, *path;
    target_t t;
    int error;

    if (!(flags & FspCleanupDelete) || name == NULL || !writable(m, c))
        return;
    if ((path = to_path(name, &stream)) == NULL)
        return;
    EXCLUSIVE(m);
    error = resolve(m, path, &t);
    if (error == 0 && c->stream != NULL)
        error = zum_fs_stream_remove(c->fs, c->base, c->stream);
    else if (error == 0)
        error = zum_fs_remove(c->fs, t.rel, c->is_dir);
    UNLOCK_EXCLUSIVE(m);
    if (error != 0)
        FspServiceLog(EVENTLOG_WARNING_TYPE,
            L"delete of %s failed: error %d", name, error);
    free(path);
    free(stream);
}

static VOID
Close(FSP_FILE_SYSTEM *fsp, PVOID context)
{
    file_context_t *c = context;

    (void) fsp;
    if (c->snapshot != NULL)
        InterlockedDecrement(&c->snapshot->refs);
    FspFileSystemDeleteDirectoryBuffer(&c->dir_buffer);
    free(c->stream);
    free(c);
}

static NTSTATUS
Read(FSP_FILE_SYSTEM *fsp, PVOID context, PVOID buffer, UINT64 offset,
    ULONG length, PULONG pdone)
{
    mount_t *m = MOUNT(fsp);
    file_context_t *c = context;
    SRWLOCK *lock;
    uint32_t done = 0;
    int error;

    if (c->kind != K_FILE)
        return (STATUS_INVALID_DEVICE_REQUEST);
    SHARED(m);
    lock = object_lock(m, c->base);
    AcquireSRWLockShared(lock);
    error = zum_fs_read(c->fs, c->object, offset, length, buffer, &done);
    ReleaseSRWLockShared(lock);
    UNLOCK_SHARED(m);
    *pdone = done;
    if (error != 0)
        return (status_of(error));
    return (done == 0 && length != 0 ? STATUS_END_OF_FILE : STATUS_SUCCESS);
}

static NTSTATUS
Write(FSP_FILE_SYSTEM *fsp, PVOID context, PVOID buffer, UINT64 offset,
    ULONG length, BOOLEAN to_end, BOOLEAN constrained,
    PULONG pdone, FSP_FSCTL_FILE_INFO *info)
{
    mount_t *m = MOUNT(fsp);
    file_context_t *c = context;
    SRWLOCK *lock;
    zum_attr_t attr;
    uint64_t at = to_end ? ZUM_FS_APPEND : offset;
    NTSTATUS status;
    int error = 0;

    *pdone = 0;
    if (!writable(m, c))
        return (STATUS_MEDIA_WRITE_PROTECTED);
    SHARED(m);
    lock = object_lock(m, c->base);
    AcquireSRWLockExclusive(lock);
    /* Paging I/O never extends the file. */
    if (constrained) {
        error = zum_fs_getattr(c->fs, c->object, &attr);
        if (error == 0 && offset >= attr.size)
            length = 0;
        else if (error == 0 && offset + length > attr.size)
            length = (ULONG)(attr.size - offset);
    }
    if (error == 0 && length > 0)
        error = zum_fs_write(c->fs, c->object, &at, buffer, length);
    if (error == 0 && c->write_through)
        error = zum_fs_sync(c->fs);
    status = status_of(error);
    if (NT_SUCCESS(status)) {
        *pdone = length;
        status = context_info_unlocked(m, c, info);
    }
    ReleaseSRWLockExclusive(lock);
    UNLOCK_SHARED(m);
    return (status);
}

static NTSTATUS
Flush(FSP_FILE_SYSTEM *fsp, PVOID context, FSP_FSCTL_FILE_INFO *info)
{
    mount_t *m = MOUNT(fsp);
    file_context_t *c = context;
    NTSTATUS status = STATUS_SUCCESS;

    EXCLUSIVE(m);
    (void) zum_fs_sync(m->fs);
    if (c != NULL)
        status = context_info(m, c, info);
    UNLOCK_EXCLUSIVE(m);
    return (status);
}

static NTSTATUS
GetFileInfo(FSP_FILE_SYSTEM *fsp, PVOID context, FSP_FSCTL_FILE_INFO *info)
{
    mount_t *m = MOUNT(fsp);
    NTSTATUS status;

    SHARED(m);
    status = context_info(m, context, info);
    UNLOCK_SHARED(m);
    return (status);
}

static NTSTATUS
SetBasicInfo(FSP_FILE_SYSTEM *fsp, PVOID context, UINT32 attributes,
    UINT64 creation, UINT64 access, UINT64 write, UINT64 change,
    FSP_FSCTL_FILE_INFO *info)
{
    mount_t *m = MOUNT(fsp);
    file_context_t *c = context;
    NTSTATUS status;
    int error;

    if (!writable(m, c))
        return (STATUS_MEDIA_WRITE_PROTECTED);
    EXCLUSIVE(m);
    error = zum_fs_setattr(c->fs, c->base,
        attributes == INVALID_FILE_ATTRIBUTES ? UINT32_MAX :
        attributes & ZUM_FA_SETTABLE, creation, access, write, change);
    status = status_of(error);
    if (NT_SUCCESS(status))
        status = context_info(m, c, info);
    UNLOCK_EXCLUSIVE(m);
    return (status);
}

static NTSTATUS
SetFileSize(FSP_FILE_SYSTEM *fsp, PVOID context, UINT64 size,
    BOOLEAN allocation, FSP_FSCTL_FILE_INFO *info)
{
    mount_t *m = MOUNT(fsp);
    file_context_t *c = context;
    zum_attr_t attr;
    NTSTATUS status;
    int error;

    if (!writable(m, c))
        return (STATUS_MEDIA_WRITE_PROTECTED);
    EXCLUSIVE(m);
    error = zum_fs_getattr(c->fs, c->object, &attr);
    /* Allocation size only shrinks a file; ZFS allocates on write. */
    if (error == 0 && (!allocation || size < attr.size))
        error = zum_fs_truncate(c->fs, c->object, size);
    status = status_of(error);
    if (NT_SUCCESS(status))
        status = context_info(m, c, info);
    UNLOCK_EXCLUSIVE(m);
    return (status);
}

static NTSTATUS
CanDelete(FSP_FILE_SYSTEM *fsp, PVOID context, PWSTR name)
{
    mount_t *m = MOUNT(fsp);
    file_context_t *c = context;
    zum_attr_t attr;
    NTSTATUS status = STATUS_SUCCESS;

    (void) name;
    if (!writable(m, c))
        return (STATUS_MEDIA_WRITE_PROTECTED);
    if (c->object == m->root)
        return (STATUS_ACCESS_DENIED);
    SHARED(m);
    if (zum_fs_getattr(c->fs, c->base, &attr) == 0 &&
        (attr.attributes & ZUM_FA_READONLY))
        status = ZUM_STATUS_CANNOT_DELETE;
    else if (c->stream == NULL && c->is_dir &&
        !zum_fs_dir_empty(c->fs, c->object))
        status = STATUS_DIRECTORY_NOT_EMPTY;
    UNLOCK_SHARED(m);
    return (status);
}

static NTSTATUS
Rename(FSP_FILE_SYSTEM *fsp, PVOID context, PWSTR name, PWSTR new_name,
    BOOLEAN replace)
{
    mount_t *m = MOUNT(fsp);
    file_context_t *c = context;
    char *s1, *s2, *from = to_path(name, &s1), *to = to_path(new_name, &s2);
    target_t tf, tt;
    zum_attr_t attr;
    uint64_t target;
    NTSTATUS status = STATUS_SUCCESS;
    int error;

    if (from == NULL || to == NULL)
        status = STATUS_OBJECT_NAME_INVALID;
    else if (s1 != NULL || s2 != NULL || c->stream != NULL)
        status = STATUS_NOT_SUPPORTED;	/* stream renames */
    else if (!writable(m, c))
        status = STATUS_MEDIA_WRITE_PROTECTED;
    if (!NT_SUCCESS(status))
        goto out;
    EXCLUSIVE(m);
    error = resolve(m, from, &tf);
    if (error == 0)
        error = resolve(m, to, &tt);
    if (error == 0 && (tt.kind != K_FILE || tt.snapshot != NULL))
        error = EROFS;
    if (error == 0)
        error = zum_fs_lookup(m->fs, tt.rel, &target);
    if (error == 0 && target != c->object) {
        /* The same object is a case-only rename on a case-insensitive name. */
        if (!replace)
            status = STATUS_OBJECT_NAME_COLLISION;
        else if (zum_fs_getattr(m->fs, target, &attr) == 0 && attr.is_dir)
            status = STATUS_ACCESS_DENIED;
    } else if (error != 0 && error != ENOENT) {
        status = status_of(error);
    }
    if (NT_SUCCESS(status))
        status = status_of(zum_fs_rename(m->fs, tf.rel, tt.rel));
    UNLOCK_EXCLUSIVE(m);
out:
    free(from);
    free(to);
    free(s1);
    free(s2);
    return (status);
}

static NTSTATUS
GetSecurity(FSP_FILE_SYSTEM *fsp, PVOID context, PSECURITY_DESCRIPTOR out,
    SIZE_T *psize)
{
    mount_t *m = MOUNT(fsp);
    file_context_t *c = context;
    PSECURITY_DESCRIPTOR sd;
    zum_attr_t attr = { 0 };
    ULONG size = 0;
    NTSTATUS status;

    SHARED(m);
    if (c->kind != K_FILE)
        sd = object_sd(m->fs, m->root, TRUE, 0, &size);
    else {
        (void) zum_fs_getattr(c->fs, c->base, &attr);
        sd = object_sd(c->fs, c->base, (BOOLEAN)attr.is_dir, 0, &size);
    }
    UNLOCK_SHARED(m);
    status = copy_sd(sd, size, out, psize);
    if (sd != NULL)
        LocalFree(sd);
    return (status);
}

static NTSTATUS
SetSecurity(FSP_FILE_SYSTEM *fsp, PVOID context, SECURITY_INFORMATION what,
    PSECURITY_DESCRIPTOR change)
{
    mount_t *m = MOUNT(fsp);
    file_context_t *c = context;
    PSECURITY_DESCRIPTOR sd, updated = NULL;
    zum_attr_t attr = { 0 };
    ULONG size = 0;
    NTSTATUS status;

    if (!writable(m, c))
        return (STATUS_MEDIA_WRITE_PROTECTED);
    EXCLUSIVE(m);
    (void) zum_fs_getattr(c->fs, c->base, &attr);
    sd = object_sd(c->fs, c->base, (BOOLEAN)attr.is_dir, 0, &size);
    status = sd == NULL ? STATUS_INSUFFICIENT_RESOURCES :
        FspSetSecurityDescriptor(sd, what, change, &updated);
    if (NT_SUCCESS(status)) {
        status = status_of(zum_fs_set_security(c->fs, c->base, updated,
            GetSecurityDescriptorLength(updated)));
        FspDeleteSecurityDescriptor(updated,
            (NTSTATUS (*)())FspSetSecurityDescriptor);
    }
    UNLOCK_EXCLUSIVE(m);
    if (sd != NULL)
        LocalFree(sd);
    return (status);
}

typedef struct dir_fill {
    PVOID *buffer;
    NTSTATUS result;
    const file_context_t *context;
} dir_fill_t;

static BOOLEAN
add_dir_entry(dir_fill_t *f, PCWSTR name, size_t name_chars,
    const FSP_FSCTL_FILE_INFO *info)
{
    union {
        UINT8 bytes[sizeof (FSP_FSCTL_DIR_INFO) + 256 * sizeof (WCHAR)];
        FSP_FSCTL_DIR_INFO d;
    } entry;

    if (name_chars > 255)
        return (TRUE);		/* not representable; skipped */
    memset(&entry.d, 0, sizeof (entry.d));
    entry.d.Size = (UINT16)(FIELD_OFFSET(FSP_FSCTL_DIR_INFO, FileNameBuf) +
        name_chars * sizeof (WCHAR));
    entry.d.FileInfo = *info;
    memcpy(entry.d.FileNameBuf, name, name_chars * sizeof (WCHAR));
    return (FspFileSystemFillDirectoryBuffer(f->buffer, &entry.d,
        &f->result));
}

static int
dirent_cb(void *arg, const char *name, const zum_attr_t *attr)
{
    dir_fill_t *f = arg;
    FSP_FSCTL_FILE_INFO info;
    WCHAR wname[256];
    int n = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, name, -1,
        wname, 256);

    if (n <= 1)
        return (0);		/* not valid UTF-8 or too long: skipped */
    to_file_info(attr, &info);
    info.IndexNumber = index_of(f->context, attr->object);
    if (f->context->snapshot != NULL)
        info.FileAttributes |= FILE_ATTRIBUTE_READONLY;
    return (add_dir_entry(f, wname, (size_t)n - 1, &info) ? 0 : -1);
}

typedef struct snap_fill {
    dir_fill_t *fill;
    mount_t *m;
} snap_fill_t;

static int
snapshot_cb(void *arg, const char *name, uint64_t id, uint64_t creation)
{
    snap_fill_t *s = arg;
    FSP_FSCTL_FILE_INFO info;
    WCHAR wname[256];
    int n = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, name, -1,
        wname, 256);

    if (n <= 1 || !NT_SUCCESS(ctl_info(s->m, id, creation, &info)))
        return (0);
    return (add_dir_entry(s->fill, wname, (size_t)n - 1, &info) ? 0 : -1);
}

static NTSTATUS
ReadDirectory(FSP_FILE_SYSTEM *fsp, PVOID context, PWSTR pattern,
    PWSTR marker, PVOID buffer, ULONG length, PULONG pdone)
{
    mount_t *m = MOUNT(fsp);
    file_context_t *c = context;
    dir_fill_t fill = { &c->dir_buffer, STATUS_SUCCESS, c };
    FSP_FSCTL_FILE_INFO self, info;
    snap_fill_t sf = { &fill, m };
    int error = 0;

    (void) pattern;
    if (FspFileSystemAcquireDirectoryBuffer(&c->dir_buffer, marker == NULL,
        &fill.result)) {
        SHARED(m);
        fill.result = context_info(m, c, &self);
        if (NT_SUCCESS(fill.result) &&
            (c->kind != K_FILE || c->object != m->root)) {
            /* ".." carries this directory's times, as memfs does. */
            if (add_dir_entry(&fill, L".", 1, &self))
                (void) add_dir_entry(&fill, L"..", 2, &self);
        }
        if (!NT_SUCCESS(fill.result)) {
            ;
        } else if (c->kind == K_CTL_ROOT) {
            if (NT_SUCCESS(ctl_info(m, ZUM_CTL_SNAPDIR_INDEX, 0, &info)))
                (void) add_dir_entry(&fill, L"snapshot", 8, &info);
        } else if (c->kind == K_CTL_SNAPDIR) {
            error = zum_fs_snapshot_list(m->fs, snapshot_cb, &sf);
        } else {
            /* .zfs is listed in the root when snapdir=visible. */
            if (c->object == m->root && c->snapshot == NULL &&
                zum_fs_snapdir(m->fs) == 1 &&
                NT_SUCCESS(ctl_info(m, ZUM_CTL_ROOT_INDEX, 0, &info)))
                (void) add_dir_entry(&fill, L".zfs", 4, &info);
            error = zum_fs_readdir(c->fs, c->object, dirent_cb, &fill);
        }
        UNLOCK_SHARED(m);
        FspFileSystemReleaseDirectoryBuffer(&c->dir_buffer);
        /* -1: filling failed, and fill.result says why. */
        if (error > 0)
            fill.result = status_of(error);
    }
    if (!NT_SUCCESS(fill.result))
        return (fill.result);
    FspFileSystemReadDirectoryBuffer(&c->dir_buffer, marker, buffer, length,
        pdone);
    return (STATUS_SUCCESS);
}

typedef struct stream_fill {
    PVOID buffer;
    ULONG length;
    PULONG done;
    BOOLEAN full;
} stream_fill_t;

static BOOLEAN
add_stream(stream_fill_t *f, PCWSTR name, size_t chars, uint64_t size,
    uint64_t allocation)
{
    union {
        UINT8 bytes[sizeof (FSP_FSCTL_STREAM_INFO) + 256 * sizeof (WCHAR)];
        FSP_FSCTL_STREAM_INFO s;
    } entry;

    if (chars > 255)
        return (TRUE);
    entry.s.Size = (UINT16)(sizeof (FSP_FSCTL_STREAM_INFO) +
        chars * sizeof (WCHAR));
    entry.s.StreamSize = size;
    entry.s.StreamAllocationSize = allocation;
    memcpy(entry.s.StreamNameBuf, name, chars * sizeof (WCHAR));
    if (!FspFileSystemAddStreamInfo(&entry.s, f->buffer, f->length, f->done))
        f->full = TRUE;
    return (!f->full);
}

static int
stream_cb(void *arg, const char *name, const zum_attr_t *attr)
{
    WCHAR wname[256];
    int n = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, name, -1,
        wname, 256);

    if (n <= 1)
        return (0);
    return (add_stream(arg, wname, (size_t)n - 1, attr->size,
        attr->allocation) ? 0 : -1);
}

static NTSTATUS
GetStreamInfo(FSP_FILE_SYSTEM *fsp, PVOID context, PVOID buffer,
    ULONG length, PULONG pdone)
{
    mount_t *m = MOUNT(fsp);
    file_context_t *c = context;
    stream_fill_t f = { buffer, length, pdone, FALSE };
    zum_attr_t attr;
    int error = 0;

    *pdone = 0;
    if (c->kind != K_FILE)
        return (FspFileSystemAddStreamInfo(NULL, buffer, length, pdone),
            STATUS_SUCCESS);
    SHARED(m);
    /* The unnamed data stream of a file, then the named ones. */
    if (zum_fs_getattr(c->fs, c->base, &attr) == 0 && !attr.is_dir)
        (void) add_stream(&f, L"", 0, attr.size, attr.allocation);
    if (!f.full)
        error = zum_fs_stream_list(c->fs, c->base, stream_cb, &f);
    UNLOCK_SHARED(m);
    if (error > 0)
        return (status_of(error));
    if (!f.full)
        FspFileSystemAddStreamInfo(NULL, buffer, length, pdone);
    return (STATUS_SUCCESS);
}

/* The object behind a handle the calling process passed to us, if ours. */
static NTSTATUS
foreign_handle_object(mount_t *m, UINT32 pid, HANDLE remote, uint64_t *object)
{
    BY_HANDLE_FILE_INFORMATION bhfi;
    HANDLE process, local = NULL;
    NTSTATUS status = STATUS_INVALID_HANDLE;
    zum_statfs_t st;

    process = OpenProcess(PROCESS_DUP_HANDLE, FALSE, pid);
    if (process == NULL)
        return (STATUS_ACCESS_DENIED);
    /* The caller's access: file information needs FILE_READ_ATTRIBUTES. */
    if (DuplicateHandle(process, remote, GetCurrentProcess(), &local,
        0, FALSE, DUPLICATE_SAME_ACCESS)) {
        /* Answered by another dispatcher thread; we hold no lock here. */
        if (GetFileInformationByHandle(local, &bhfi) &&
            zum_fs_statfs(m->fs, &st) == 0 &&
            bhfi.dwVolumeSerialNumber == (DWORD)st.fsid_guid) {
            *object = ((uint64_t)bhfi.nFileIndexHigh << 32) |
                bhfi.nFileIndexLow;
            status = STATUS_SUCCESS;
        } else {
            status = STATUS_NOT_SAME_DEVICE;
        }
        CloseHandle(local);
    }
    CloseHandle(process);
    return (status);
}

static NTSTATUS
Control(FSP_FILE_SYSTEM *fsp, PVOID context, UINT32 code, PVOID in,
    ULONG in_length, PVOID out, ULONG out_length, PULONG pdone)
{
    mount_t *m = MOUNT(fsp);
    file_context_t *c = context;
    DUPLICATE_EXTENTS_DATA d;
    uint64_t source, length;
    NTSTATUS status;
    int error;

    (void) out; (void) out_length;
    *pdone = 0;
    if (code != ZUM_IOCTL_DUPLICATE_EXTENTS)
        return (STATUS_INVALID_DEVICE_REQUEST);
    if (in_length < sizeof (d))
        return (STATUS_INVALID_PARAMETER);
    if (!writable(m, c) || c->stream != NULL || c->is_dir)
        return (STATUS_MEDIA_WRITE_PROTECTED);
    memcpy(&d, in, sizeof (d));
    if (d.SourceFileOffset.QuadPart < 0 || d.TargetFileOffset.QuadPart < 0 ||
        d.ByteCount.QuadPart <= 0)
        return (STATUS_INVALID_PARAMETER);
    /* The process that opened the target holds the source handle. */
    if (!NT_SUCCESS(status = foreign_handle_object(m, c->pid, d.FileHandle,
        &source)))
        return (status);
    length = (uint64_t)d.ByteCount.QuadPart;
    EXCLUSIVE(m);
    error = zum_fs_clone(m->fs, source, (uint64_t)d.SourceFileOffset.QuadPart,
        c->object, (uint64_t)d.TargetFileOffset.QuadPart, &length);
    UNLOCK_EXCLUSIVE(m);
    if (error == EOPNOTSUPP)
        return (STATUS_NOT_SUPPORTED);
    return (status_of(error));
}

static FSP_FILE_SYSTEM_INTERFACE zum_interface = {
    .GetVolumeInfo = GetVolumeInfo,
    .GetSecurityByName = GetSecurityByName,
    .Create = Create,
    .Open = Open,
    .Overwrite = Overwrite,
    .Cleanup = Cleanup,
    .Close = Close,
    .Read = Read,
    .Write = Write,
    .Flush = Flush,
    .GetFileInfo = GetFileInfo,
    .SetBasicInfo = SetBasicInfo,
    .SetFileSize = SetFileSize,
    .CanDelete = CanDelete,
    .Rename = Rename,
    .GetSecurity = GetSecurity,
    .SetSecurity = SetSecurity,
    .ReadDirectory = ReadDirectory,
    .GetStreamInfo = GetStreamInfo,
    .Control = Control,
};

/* ---- registry: pools and mounts to restore at start ----------------------- */

static void
mount_key_name(const char *dataset, PWSTR out, int chars)
{
    WCHAR w[256];
    PWSTR p;

    wide_of(dataset, w, 256);
    for (p = w; *p != L'\0'; p++)
        if (*p == L'/')
            *p = L'_';
    swprintf_s(out, chars, L"auto-%s", w);
}

static void
record_mount(const char *pool, const char *dataset, PCWSTR mountpoint,
    BOOLEAN readonly)
{
    WCHAR name[300], key[400], wpool[256], wds[256];
    DWORD ro = readonly, automatic = 1;
    HKEY h;

    mount_key_name(dataset, name, 300);
    swprintf_s(key, 400, L"%s\\%s", MOUNTS_KEY, name);
    if (RegCreateKeyExW(HKEY_LOCAL_MACHINE, key, 0, NULL, 0, KEY_WRITE, NULL,
        &h, NULL) != ERROR_SUCCESS)
        return;
    wide_of(pool, wpool, 256);
    wide_of(dataset, wds, 256);
    RegSetValueExW(h, L"Pool", 0, REG_SZ, (BYTE *)wpool,
        (DWORD)((wcslen(wpool) + 1) * sizeof (WCHAR)));
    RegSetValueExW(h, L"Dataset", 0, REG_SZ, (BYTE *)wds,
        (DWORD)((wcslen(wds) + 1) * sizeof (WCHAR)));
    RegSetValueExW(h, L"MountPoint", 0, REG_SZ, (BYTE *)mountpoint,
        (DWORD)((wcslen(mountpoint) + 1) * sizeof (WCHAR)));
    RegSetValueExW(h, L"ReadOnly", 0, REG_DWORD, (BYTE *)&ro, sizeof (ro));
    /* Made by `zfs mount`, so `zfs unmount`/export remove it again. */
    RegSetValueExW(h, L"Auto", 0, REG_DWORD, (BYTE *)&automatic,
        sizeof (automatic));
    RegCloseKey(h);
}

static void
forget_mount(const char *dataset)
{
    WCHAR name[300], key[400];

    mount_key_name(dataset, name, 300);
    swprintf_s(key, 400, L"%s\\%s", MOUNTS_KEY, name);
    RegDeleteKeyW(HKEY_LOCAL_MACHINE, key);
}

static BOOLEAN
read_value(HKEY key, PCWSTR name, PWSTR out, DWORD chars)
{
    DWORD bytes = chars * sizeof (WCHAR);

    out[0] = L'\0';
    return (RegGetValueW(key, NULL, name, RRF_RT_REG_SZ, NULL, out,
        &bytes) == ERROR_SUCCESS);
}

/* A value of a pool's record: Sources, or CacheFile (if it has one). */
static void
pool_value(PCWSTR pool, PCWSTR name, PWSTR out, DWORD chars)
{
    WCHAR key[400];
    HKEY h;

    out[0] = L'\0';
    swprintf_s(key, 400, L"%s\\%s", POOLS_KEY, pool);
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, key, 0, KEY_READ, &h) ==
        ERROR_SUCCESS) {
        (void) read_value(h, name, out, chars);
        RegCloseKey(h);
    }
}

/* ---- mounting -------------------------------------------------------------- */

static mount_t *
find_mount(const char *dataset)
{
    int i;

    for (i = 0; i < MAX_MOUNTS; i++)
        if (mounts[i].in_use && strcmp(mounts[i].dataset, dataset) == 0)
            return (&mounts[i]);
    return (NULL);
}

/*
 * Mount a dataset.  source: where to find its pool if not imported yet;
 * mountpoint: "X:", an empty or missing folder, or NULL for any free drive
 * letter.  The table lock is held exclusive by the caller.
 */
static int
mount_one(PCWSTR source, const char *pool, const char *dataset,
    PCWSTR mountpoint, BOOLEAN readonly, BOOLEAN quiet)
{
    FSP_FSCTL_VOLUME_PARAMS params;
    zum_fs_args_t args;
    zum_statfs_t st;
    mount_t *m = NULL;
    FILETIME now;
    NTSTATUS status;
    WCHAR wds[256];
    int i, error;

    if (find_mount(dataset) != NULL)
        return (EBUSY);
    for (i = 0; i < MAX_MOUNTS && m == NULL; i++)
        if (!mounts[i].in_use)
            m = &mounts[i];
    if (m == NULL)
        return (EMFILE);
    memset(m, 0, sizeof (*m));
    InitializeSRWLock(&m->lock);
    for (i = 0; i < OBJECT_LOCK_COUNT; i++)
        InitializeSRWLock(&m->object_locks[i]);
    InitializeCriticalSection(&m->snap_lock);
    m->readonly = readonly;
    strncpy_s(m->dataset, sizeof (m->dataset), dataset, _TRUNCATE);
    strncpy_s(m->pool, sizeof (m->pool), pool, _TRUNCATE);
    wide_of(dataset, wds, 256);

    args.search_path = source != NULL ? utf8_of(source) : _strdup("");
    args.pool = pool;
    args.dataset = dataset;
    args.readonly = readonly;
    error = args.search_path != NULL ? zum_fs_open(&args, &m->fs) : ENOMEM;
    free((void *)args.search_path);
    if (error != 0) {
        if (!quiet)
            FspServiceLog(EVENTLOG_ERROR_TYPE,
                L"cannot open %s from %s: error %d", wds,
                source != NULL ? source : L"(imported pools)", error);
        DeleteCriticalSection(&m->snap_lock);
        return (error);
    }
    (void) zum_fs_lookup(m->fs, "", &m->root);
    (void) zum_fs_statfs(m->fs, &st);
    m->case_sensitive = st.case_sensitive;

    memset(&params, 0, sizeof (params));
    GetSystemTimeAsFileTime(&now);
    params.Version = sizeof (FSP_FSCTL_VOLUME_PARAMS);
    params.SectorSize = 4096;
    params.SectorsPerAllocationUnit = 1;
    params.MaxComponentLength = 255;
    params.VolumeCreationTime = ((UINT64)now.dwHighDateTime << 32) |
        now.dwLowDateTime;
    params.VolumeSerialNumber = (UINT32)st.fsid_guid;
    params.FileInfoTimeout = 1000;
    params.CaseSensitiveSearch = st.case_sensitive != 0;
    params.CasePreservedNames = 1;
    params.UnicodeOnDisk = 1;
    params.PersistentAcls = 1;
    params.NamedStreams = 1;
    params.DeviceControl = 1;
    params.PostCleanupWhenModifiedOnly = 1;
    params.UmFileContextIsUserContext2 = 1;
    params.ReadOnlyVolume = readonly;
    wcscpy_s(params.FileSystemName, ARRAYSIZE(params.FileSystemName),
        L"ZFS");

    status = FspFileSystemCreate(L"" FSP_FSCTL_DISK_DEVICE_NAME, &params,
        &zum_interface, &m->fsp);
    if (NT_SUCCESS(status)) {
        m->fsp->UserContext = m;
        status = FspFileSystemSetMountPoint(m->fsp, (PWSTR)mountpoint);
    }
    if (NT_SUCCESS(status))
        status = FspFileSystemStartDispatcher(m->fsp, 0);
    if (!NT_SUCCESS(status)) {
        FspServiceLog(EVENTLOG_ERROR_TYPE,
            L"cannot mount %s on %s: 0x%08lx", wds,
            mountpoint != NULL ? mountpoint : L"(any drive letter)", status);
        if (m->fsp != NULL)
            FspFileSystemDelete(m->fsp);
        (void) zum_fs_close(m->fs);
        DeleteCriticalSection(&m->snap_lock);
        return (status == STATUS_OBJECT_NAME_COLLISION ? EEXIST : EINVAL);
    }
    wcsncpy_s(m->mountpoint, MAX_PATH, FspFileSystemMountPoint(m->fsp),
        _TRUNCATE);
    m->in_use = TRUE;
    FspServiceLog(EVENTLOG_INFORMATION_TYPE, L"zfswin-um %hs: mounted %s on "
        L"%s%s", ZFSWIN_VERSION, wds, m->mountpoint,
        readonly ? L" (read-only)" : L"");
    return (0);
}

/* Unmount; the pool stays imported.  Table lock held exclusive. */
static void
unmount_one(mount_t *m)
{
    WCHAR wds[256];
    int error;

    FspFileSystemStopDispatcher(m->fsp);
    FspFileSystemDelete(m->fsp);
    snapshots_close(m, NULL, TRUE);
    error = zum_fs_close(m->fs);	/* sync and release the dataset */
    wide_of(m->dataset, wds, 256);
    FspServiceLog(error == 0 ? EVENTLOG_INFORMATION_TYPE :
        EVENTLOG_ERROR_TYPE, L"unmounted %s from %s (%d)", wds,
        m->mountpoint, error);
    DeleteCriticalSection(&m->snap_lock);
    m->in_use = FALSE;
}

static void
unmount_all(void)
{
    int i;

    AcquireSRWLockExclusive(&table_lock);
    for (i = 0; i < MAX_MOUNTS; i++)
        if (mounts[i].in_use)
            unmount_one(&mounts[i]);
    ReleaseSRWLockExclusive(&table_lock);
}

static void bridge_pool_imported(const char *pool, const char *sources);

/*
 * Service start: the recorded pools, then every configured mount.  Returns
 * how many could not be imported or mounted; quiet: no event log entries.
 */
static int
restore_configured(BOOLEAN quiet)
{
    WCHAR name[256], source[2048], pool[256], dataset[256], mp[MAX_PATH];
    WCHAR cache[MAX_PATH];
    HKEY root, key;
    DWORD i, chars, readonly, size;
    char *upool, *uds, *usrc, *ucache;
    int failed = 0;

    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, POOLS_KEY, 0, KEY_READ, &root) ==
        ERROR_SUCCESS) {
        for (i = 0; chars = ARRAYSIZE(name), RegEnumKeyExW(root, i, name,
            &chars, NULL, NULL, NULL, NULL) == ERROR_SUCCESS; i++) {
            int error;

            pool_value(name, L"Sources", source, ARRAYSIZE(source));
            pool_value(name, L"CacheFile", cache, ARRAYSIZE(cache));
            upool = utf8_of(name);
            usrc = utf8_of(source);
            ucache = utf8_of(cache);
            error = upool && usrc && ucache ?
                zum_pool_import(upool, usrc, ucache) : ENOMEM;
            if (error != 0) {
                failed++;
                if (!quiet)
                    FspServiceLog(EVENTLOG_ERROR_TYPE,
                        L"cannot import pool %s from %s: error %d", name,
                        source, error);
            } else {
                /* Found elsewhere (disks renumbered): record where. */
                char now[4096];

                zum_pool_sources(upool, now, sizeof (now));
                if (now[0] != '\0' && strcmp(now, usrc) != 0)
                    bridge_pool_imported(upool, now);
            }
            free(upool);
            free(usrc);
            free(ucache);
        }
        RegCloseKey(root);
    }

    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, MOUNTS_KEY, 0, KEY_READ, &root) !=
        ERROR_SUCCESS)
        return (failed);	/* nothing configured */
    AcquireSRWLockExclusive(&table_lock);
    for (i = 0; chars = ARRAYSIZE(name), RegEnumKeyExW(root, i, name,
        &chars, NULL, NULL, NULL, NULL) == ERROR_SUCCESS; i++) {
        if (RegOpenKeyExW(root, name, 0, KEY_READ, &key) != ERROR_SUCCESS)
            continue;
        readonly = 0;
        size = sizeof (readonly);
        (void) RegGetValueW(key, NULL, L"ReadOnly", RRF_RT_REG_DWORD, NULL,
            &readonly, &size);
        if (read_value(key, L"Pool", pool, 256) &&
            read_value(key, L"Dataset", dataset, 256) &&
            read_value(key, L"MountPoint", mp, MAX_PATH)) {
            if (!read_value(key, L"Source", source, ARRAYSIZE(source)) ||
                source[0] == L'\0')
                pool_value(pool, L"Sources", source, ARRAYSIZE(source));
            int error = ENOMEM;

            upool = utf8_of(pool);
            uds = utf8_of(dataset);
            if (upool != NULL && uds != NULL)
                error = mount_one(source, upool, uds,
                    wcscmp(mp, L"?:") == 0 ? NULL : mp, readonly != 0,
                    quiet);
            if (error != 0 && error != EBUSY)	/* EBUSY: mounted */
                failed++;
            free(upool);
            free(uds);
        } else if (!quiet) {
            FspServiceLog(EVENTLOG_ERROR_TYPE,
                L"mount %s: Pool, Dataset and MountPoint are required",
                name);
        }
        RegCloseKey(key);
    }
    ReleaseSRWLockExclusive(&table_lock);
    RegCloseKey(root);
    return (failed);
}

/*
 * At boot the service can start before the disks of a recorded pool are
 * there (and their drivers ready): what failed at start is tried again
 * every 5 s for 3 minutes, then once more with errors in the event log.
 */
#define RESTORE_RETRY_MS	5000
#define RESTORE_RETRY_TRIES	36
static HANDLE restore_stop, restore_thread;

static DWORD WINAPI
restore_retry(LPVOID unused)
{
    int tries;

    (void) unused;
    for (tries = 1; WaitForSingleObject(restore_stop, RESTORE_RETRY_MS) ==
        WAIT_TIMEOUT; tries++) {
        if (restore_configured(tries < RESTORE_RETRY_TRIES) == 0) {
            FspServiceLog(EVENTLOG_INFORMATION_TYPE,
                L"recorded pools and mounts restored after %d s",
                tries * RESTORE_RETRY_MS / 1000);
            break;
        }
        if (tries >= RESTORE_RETRY_TRIES)
            break;
    }
    return (0);
}

/* ---- bridge callbacks (zum_bridge_ops_t) ----------------------------------- */

/* Whether path lies on one of our volumes ("E:" or a folder mount). */
static BOOLEAN
inside_our_volume(PCWSTR path)
{
    size_t n;
    int i;

    for (i = 0; i < MAX_MOUNTS; i++) {
        if (!mounts[i].in_use)
            continue;
        n = wcslen(mounts[i].mountpoint);
        if (n > 0 && _wcsnicmp(path, mounts[i].mountpoint, n) == 0 &&
            (path[n] == L'\0' || path[n] == L'\\'))
            return (TRUE);
    }
    return (FALSE);
}

static int
bridge_mount(const char *dataset, const char *where, int readonly)
{
    WCHAR wwhere[MAX_PATH], wds[256];
    PCWSTR mp = wwhere;
    char pool[256];
    const char *slash = strchr(dataset, '/');
    HANDLE event;
    mount_t *m;
    WIN32_FILE_ATTRIBUTE_DATA fa;
    int error;

    wide_of(where, wwhere, MAX_PATH);
    strncpy_s(pool, sizeof (pool), dataset, slash != NULL ?
        (size_t)(slash - dataset) : _TRUNCATE);
    AcquireSRWLockExclusive(&table_lock);
    if (wcscmp(wwhere, L"?:") == 0) {
        mp = NULL;
    } else if (wcslen(wwhere) == 2 && wwhere[1] == L':') {
        wwhere[0] = towupper(wwhere[0]);
    } else if (inside_our_volume(wwhere)) {
        /*
         * A child dataset's folder inside its parent's volume: WinFsp
         * cannot mount on a folder of a WinFsp volume (the kernel driver
         * makes its own reparse point there), so it gets a drive letter.
         */
        mp = NULL;
    } else if (GetFileAttributesExW(wwhere, GetFileExInfoStandard, &fa) &&
        (fa.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) &&
        !RemoveDirectoryW(wwhere)) {
        /* WinFsp creates the folder mount point itself; it must be absent. */
        ReleaseSRWLockExclusive(&table_lock);
        return (EBUSY);
    }
    error = mount_one(NULL, pool, dataset, mp, readonly != 0, FALSE);
    /*
     * A temporary snapshot mount (the broker's read-only clone under
     * <pool>/.zfswin-tmp) is not recorded: it must not come back at boot.
     */
    if (error == 0 && (m = find_mount(dataset)) != NULL &&
        strstr(dataset, "/.zfswin-tmp/") == NULL)
        record_mount(pool, dataset, m->mountpoint, readonly != 0);
    ReleaseSRWLockExclusive(&table_lock);
    if (error == 0) {
        char ev[400];

        /* do_mount() waits for this, as for the kernel driver's mount. */
        snprintf(ev, sizeof (ev), "Global\\MountComplete_{%s}", dataset);
        if ((event = OpenEventA(EVENT_MODIFY_STATE, FALSE, ev)) != NULL) {
            SetEvent(event);
            CloseHandle(event);
        }
    } else {
        wide_of(dataset, wds, 256);
        FspServiceLog(EVENTLOG_WARNING_TYPE, L"zfs mount %s on %hs: %d",
            wds, where, error);
    }
    return (error);
}

static int
bridge_unmount(const char *dataset)
{
    mount_t *m;
    int error = ENOENT;

    AcquireSRWLockExclusive(&table_lock);
    if ((m = find_mount(dataset)) != NULL) {
        unmount_one(m);
        error = 0;
    }
    ReleaseSRWLockExclusive(&table_lock);
    if (error == 0)
        forget_mount(dataset);
    return (error);
}

static void
bridge_unmount_pool(const char *pool)
{
    size_t n = strlen(pool);
    char dataset[256];
    int i;

    AcquireSRWLockExclusive(&table_lock);
    for (i = 0; i < MAX_MOUNTS; i++) {
        mount_t *m = &mounts[i];

        if (!m->in_use || strncmp(m->dataset, pool, n) != 0 ||
            (m->dataset[n] != '\0' && m->dataset[n] != '/'))
            continue;
        strncpy_s(dataset, sizeof (dataset), m->dataset, _TRUNCATE);
        unmount_one(m);
        forget_mount(dataset);
    }
    ReleaseSRWLockExclusive(&table_lock);
}

/* Rollback: hold the mount exclusively with the dataset released. */
static int
bridge_suspend(const char *dataset)
{
    mount_t *m;

    AcquireSRWLockShared(&table_lock);
    m = find_mount(dataset);
    if (m != NULL) {
        EXCLUSIVE(m);
        snapshots_close(m, NULL, FALSE);
        (void) zum_fs_suspend(m->fs);
        m->suspended = TRUE;
    }
    ReleaseSRWLockShared(&table_lock);
    return (m != NULL);
}

static void
bridge_resume(const char *dataset)
{
    mount_t *m;
    int error;

    AcquireSRWLockShared(&table_lock);
    if ((m = find_mount(dataset)) != NULL && m->suspended) {
        error = zum_fs_resume(m->fs);
        if (error == 0)
            (void) zum_fs_lookup(m->fs, "", &m->root);
        else
            FspServiceLog(EVENTLOG_ERROR_TYPE,
                L"remount after rollback failed: %d", error);
        m->suspended = FALSE;
        UNLOCK_EXCLUSIVE(m);
    }
    ReleaseSRWLockShared(&table_lock);
}

static void
bridge_snapshot_unmount(const char *snapshot)
{
    const char *at = strchr(snapshot, '@');
    char dataset[256];
    mount_t *m;

    if (at == NULL || (size_t)(at - snapshot) >= sizeof (dataset))
        return;
    memcpy(dataset, snapshot, at - snapshot);
    dataset[at - snapshot] = '\0';
    AcquireSRWLockShared(&table_lock);
    if ((m = find_mount(dataset)) != NULL) {
        EXCLUSIVE(m);
        snapshots_close(m, at + 1, FALSE);
        UNLOCK_EXCLUSIVE(m);
    }
    ReleaseSRWLockShared(&table_lock);
}

static void
bridge_pool_imported(const char *pool, const char *sources)
{
    WCHAR key[400], wpool[256], wsrc[4096];
    HKEY h;

    wide_of(pool, wpool, 256);
    wide_of(sources, wsrc, 4096);
    swprintf_s(key, 400, L"%s\\%s", POOLS_KEY, wpool);
    if (RegCreateKeyExW(HKEY_LOCAL_MACHINE, key, 0, NULL, 0, KEY_WRITE, NULL,
        &h, NULL) == ERROR_SUCCESS) {
        RegSetValueExW(h, L"Sources", 0, REG_SZ, (BYTE *)wsrc,
            (DWORD)((wcslen(wsrc) + 1) * sizeof (WCHAR)));
        RegCloseKey(h);
    }
}

/*
 * Only a recorded pool's: a pool a configured mount imported has no record
 * and is not imported by name at start.
 */
static void
bridge_pool_cachefile(const char *pool, const char *cachefile)
{
    WCHAR key[400], wpool[256], wcache[MAX_PATH];
    HKEY h;

    wide_of(pool, wpool, 256);
    wide_of(cachefile, wcache, MAX_PATH);
    swprintf_s(key, 400, L"%s\\%s", POOLS_KEY, wpool);
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, key, 0, KEY_WRITE, &h) !=
        ERROR_SUCCESS)
        return;
    if (wcache[0] != L'\0')
        RegSetValueExW(h, L"CacheFile", 0, REG_SZ, (BYTE *)wcache,
            (DWORD)((wcslen(wcache) + 1) * sizeof (WCHAR)));
    else
        RegDeleteValueW(h, L"CacheFile");
    RegCloseKey(h);
}

static void
bridge_pool_exported(const char *pool)
{
    WCHAR key[400], wpool[256];

    wide_of(pool, wpool, 256);
    swprintf_s(key, 400, L"%s\\%s", POOLS_KEY, wpool);
    RegDeleteKeyW(HKEY_LOCAL_MACHINE, key);
}

static const zum_bridge_ops_t bridge_ops = {
    .mount = bridge_mount,
    .unmount = bridge_unmount,
    .unmount_pool = bridge_unmount_pool,
    .suspend = bridge_suspend,
    .resume = bridge_resume,
    .snapshot_unmount = bridge_snapshot_unmount,
    .pool_imported = bridge_pool_imported,
    .pool_exported = bridge_pool_exported,
    .pool_cachefile = bridge_pool_cachefile,
};

/* ---- the bridge pipe server (sys/zfs_bridge.h) ----------------------------- */

static volatile LONG bridge_stopping;
static volatile LONG bridge_busy;	/* requests being executed */
static HANDLE bridge_thread;

static BOOL
pipe_io(HANDLE h, void *buf, uint64_t len, BOOL write)
{
    char *p = buf;
    DWORD n;

    while (len > 0) {
        DWORD chunk = (DWORD)(len > (1U << 30) ? (1U << 30) : len);
        BOOL ok = write ? WriteFile(h, p, chunk, &n, NULL) :
            ReadFile(h, p, chunk, &n, NULL);

        if (!ok || n == 0)
            return (FALSE);
        p += n;
        len -= n;
    }
    return (TRUE);
}

static size_t
mnttab_text(char *out, size_t size)
{
    size_t used = 0;
    int i, n;

    out[0] = '\0';
    AcquireSRWLockShared(&table_lock);
    for (i = 0; i < MAX_MOUNTS; i++) {
        if (!mounts[i].in_use || mounts[i].suspended)
            continue;
        n = snprintf(out + used, size - used, "%s\t%ls\t%d\n",
            mounts[i].dataset, mounts[i].mountpoint,
            zum_fs_mount_flags(mounts[i].fs) | (mounts[i].readonly ? 1 : 0));
        if (n < 0 || (size_t)n >= size - used)
            break;
        used += n;
    }
    ReleaseSRWLockShared(&table_lock);
    return (used);
}

/* One request; FALSE ends the connection. */
static BOOL
serve_one(HANDLE pipe, ULONG pid)
{
    zfs_bridge_hdr_t hdr, resp;
    void *sect[ZFS_BRIDGE_SECTIONS] = { 0 };
    zum_ioctl_buffers_t io = { 0 };
    BOOL ok = FALSE;
    uint64_t need[ZFS_BRIDGE_SECTIONS];
    int i;

    if (!pipe_io(pipe, &hdr, sizeof (hdr), FALSE) ||
        hdr.zb_magic != ZFS_BRIDGE_MAGIC)
        return (FALSE);
    for (i = 0; i < ZFS_BRIDGE_SECTIONS; i++) {
        if (hdr.zb_len[i] > ZFS_BRIDGE_MAX_SECTION ||
            hdr.zb_cap[i] > ZFS_BRIDGE_MAX_SECTION)
            return (FALSE);
        need[i] = max(hdr.zb_len[i], hdr.zb_cap[i]);
        /* NUL after inputs: zc_history is read as a string. */
        if ((need[i] > 0 || i == ZFS_BRIDGE_HISTORY) &&
            (sect[i] = calloc(1, (size_t)need[i] + 1)) == NULL)
            goto out;
        if (hdr.zb_len[i] > 0 && !pipe_io(pipe, sect[i], hdr.zb_len[i],
            FALSE))
            goto out;
    }
    memset(&resp, 0, sizeof (resp));
    resp.zb_magic = ZFS_BRIDGE_MAGIC;
    resp.zb_op = hdr.zb_op;
    resp.zb_request = hdr.zb_request;
    if (hdr.zb_op == ZFS_BRIDGE_OP_MNTTAB) {
        resp.zb_len[ZFS_BRIDGE_DST] = sect[ZFS_BRIDGE_DST] == NULL ? 0 :
            mnttab_text(sect[ZFS_BRIDGE_DST],
            (size_t)hdr.zb_cap[ZFS_BRIDGE_DST]);
    } else if (hdr.zb_op == ZFS_BRIDGE_OP_KSTAT) {
        size_t used = 0;

        resp.zb_error = sect[ZFS_BRIDGE_SRC] == NULL ||
            sect[ZFS_BRIDGE_DST] == NULL ? EINVAL :
            zum_kstat_text(sect[ZFS_BRIDGE_SRC], sect[ZFS_BRIDGE_DST],
            (size_t)hdr.zb_cap[ZFS_BRIDGE_DST], &used);
        resp.zb_len[ZFS_BRIDGE_DST] = resp.zb_error == 0 ? used : 0;
    } else if (hdr.zb_op == ZFS_BRIDGE_OP_IOCTL &&
        hdr.zb_len[ZFS_BRIDGE_CMD] == zum_ioctl_cmd_size()) {
        io.cmd = sect[ZFS_BRIDGE_CMD];
        io.src = hdr.zb_len[ZFS_BRIDGE_SRC] ? sect[ZFS_BRIDGE_SRC] : NULL;
        io.src_size = hdr.zb_len[ZFS_BRIDGE_SRC];
        io.conf = hdr.zb_len[ZFS_BRIDGE_CONF] ? sect[ZFS_BRIDGE_CONF] : NULL;
        io.conf_size = hdr.zb_len[ZFS_BRIDGE_CONF];
        io.history = need[ZFS_BRIDGE_HISTORY] ? sect[ZFS_BRIDGE_HISTORY] :
            NULL;
        io.history_cap = hdr.zb_cap[ZFS_BRIDGE_HISTORY];
        io.dst = hdr.zb_cap[ZFS_BRIDGE_DST] ? sect[ZFS_BRIDGE_DST] : NULL;
        io.dst_cap = hdr.zb_cap[ZFS_BRIDGE_DST];
        InterlockedIncrement(&bridge_busy);
        resp.zb_error = zum_ioctl(hdr.zb_request, pid, &io);
        InterlockedDecrement(&bridge_busy);
        resp.zb_len[ZFS_BRIDGE_CMD] = zum_ioctl_cmd_size();
        resp.zb_len[ZFS_BRIDGE_HISTORY] = io.history_out;
        resp.zb_len[ZFS_BRIDGE_DST] = io.dst_out;
    } else {
        resp.zb_error = EINVAL;
    }
    ok = pipe_io(pipe, &resp, sizeof (resp), TRUE);
    for (i = 0; ok && i < ZFS_BRIDGE_SECTIONS; i++)
        if (resp.zb_len[i] > 0)
            ok = pipe_io(pipe, sect[i], resp.zb_len[i], TRUE);
out:
    for (i = 0; i < ZFS_BRIDGE_SECTIONS; i++)
        free(sect[i]);
    return (ok);
}

static DWORD WINAPI
bridge_client(LPVOID arg)
{
    HANDLE pipe = arg;
    ULONG pid = 0;

    (void) GetNamedPipeClientProcessId(pipe, &pid);
    while (!bridge_stopping && serve_one(pipe, pid))
        ;
    DisconnectNamedPipe(pipe);
    CloseHandle(pipe);
    return (0);
}

static DWORD WINAPI
bridge_listen(LPVOID arg)
{
    SECURITY_ATTRIBUTES sa = { sizeof (sa), NULL, FALSE };
    HANDLE pipe, worker;

    (void) arg;
    /* SYSTEM and (elevated) Administrators only: the kernel's secpolicy. */
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
        L"D:P(A;;GA;;;SY)(A;;GA;;;BA)", SDDL_REVISION_1,
        &sa.lpSecurityDescriptor, NULL))
        return (GetLastError());
    while (!bridge_stopping) {
        pipe = CreateNamedPipeW(ZFS_BRIDGE_PIPE_W, PIPE_ACCESS_DUPLEX,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT |
            PIPE_REJECT_REMOTE_CLIENTS, PIPE_UNLIMITED_INSTANCES, 65536,
            65536, 0, &sa);
        if (pipe == INVALID_HANDLE_VALUE) {
            FspServiceLog(EVENTLOG_ERROR_TYPE,
                L"cannot create the ZFS bridge pipe: %lu", GetLastError());
            break;
        }
        if (!ConnectNamedPipe(pipe, NULL) &&
            GetLastError() != ERROR_PIPE_CONNECTED) {
            CloseHandle(pipe);
            continue;
        }
        if (bridge_stopping) {
            CloseHandle(pipe);
            break;
        }
        worker = CreateThread(NULL, 0, bridge_client, pipe, 0, NULL);
        if (worker == NULL)
            CloseHandle(pipe);
        else
            CloseHandle(worker);
    }
    LocalFree(sa.lpSecurityDescriptor);
    return (0);
}

static void
bridge_start(void)
{
    bridge_stopping = 0;
    bridge_thread = CreateThread(NULL, 0, bridge_listen, NULL, 0, NULL);
}

/* Stop accepting, and let running requests finish (up to a minute). */
static void
bridge_stop(void)
{
    HANDLE h;
    int i;

    if (bridge_thread == NULL)
        return;
    InterlockedExchange(&bridge_stopping, 1);
    /* Wake the listener's ConnectNamedPipe(). */
    h = CreateFileW(ZFS_BRIDGE_PIPE_W, GENERIC_READ | GENERIC_WRITE, 0, NULL,
        OPEN_EXISTING, 0, NULL);
    if (h != INVALID_HANDLE_VALUE)
        CloseHandle(h);
    WaitForSingleObject(bridge_thread, 10000);
    CloseHandle(bridge_thread);
    bridge_thread = NULL;
    for (i = 0; i < 600 && bridge_busy > 0; i++)
        Sleep(100);
}

/* ---- service --------------------------------------------------------------- */

static NTSTATUS
SvcStart(FSP_SERVICE *service, ULONG argc, PWSTR *argv)
{
    char *pool, *dataset;
    int error = 0;

    (void) service;
    if (argc > 1 && !(argc >= 6 && wcscmp(argv[1], L"mount") == 0)) {
        FspServiceLog(EVENTLOG_ERROR_TYPE, L"usage: zfswin-um mount "
            L"<source> <pool> <dataset> <mountpoint> [--readonly]");
        return (STATUS_INVALID_PARAMETER);
    }
    zum_engine_start();
    {
        WCHAR dir[MAX_PATH];
        char *cache;

        ExpandEnvironmentStringsW(L"%ProgramData%\\OpenZFS for Windows",
            dir, MAX_PATH);
        CreateDirectoryW(dir, NULL);
        wcscat_s(dir, MAX_PATH, L"\\UserMode");
        CreateDirectoryW(dir, NULL);
        wcscat_s(dir, MAX_PATH, L"\\zpool.cache");
        if ((cache = utf8_of(dir)) != NULL) {
            zum_engine_set_cachefile(cache);
            free(cache);
        }
    }
    zum_bridge_init(&bridge_ops);
    {
        /* `zfs version` reads this as the kernel line (patch 0024). */
        const char *v = zum_engine_version();

        RegSetKeyValueA(HKEY_LOCAL_MACHINE,
            "SYSTEM\\CurrentControlSet\\Services\\ZfsWinUserMode", "zfs_version",
            REG_SZ, v, (DWORD)strlen(v) + 1);
    }
    bridge_start();
    if (argc >= 6) {
        pool = utf8_of(argv[3]);
        dataset = utf8_of(argv[4]);
        AcquireSRWLockExclusive(&table_lock);
        error = pool && dataset ? mount_one(argv[2], pool, dataset, argv[5],
            argc >= 7 && wcscmp(argv[6], L"--readonly") == 0, FALSE) :
            ENOMEM;
        ReleaseSRWLockExclusive(&table_lock);
        free(pool);
        free(dataset);
        return (error == 0 ? STATUS_SUCCESS : status_of(error));
    }
    if (restore_configured(TRUE) != 0) {
        restore_stop = CreateEventW(NULL, TRUE, FALSE, NULL);
        if (restore_stop != NULL)
            restore_thread = CreateThread(NULL, 0, restore_retry, NULL, 0,
                NULL);
    }
    return (STATUS_SUCCESS);
}

static NTSTATUS
SvcStop(FSP_SERVICE *service)
{
    ULONGLONG t0 = GetTickCount64(), t1, t2;
    int error;

    /*
     * Requests may still be finishing, and every pool is synced and
     * exported: tell the SCM to wait (its default 30 s was not enough in
     * the step-18 VM run, event 7011).
     */
    FspServiceRequestTime(service, 180000);
    if (restore_thread != NULL) {
        SetEvent(restore_stop);
        WaitForSingleObject(restore_thread, INFINITE);
        CloseHandle(restore_thread);
        CloseHandle(restore_stop);
        restore_thread = restore_stop = NULL;
    }
    bridge_stop();
    t1 = GetTickCount64();
    unmount_all();
    t2 = GetTickCount64();
    error = zum_engine_stop();	/* export every pool */
    FspServiceLog(error == 0 ? EVENTLOG_INFORMATION_TYPE :
        EVENTLOG_ERROR_TYPE, L"pools exported (%d); stop took %u ms "
        L"(bridge %u, unmount %u, export %u)", error,
        (unsigned)(GetTickCount64() - t0), (unsigned)(t1 - t0),
        (unsigned)(t2 - t1), (unsigned)(GetTickCount64() - t2));
    return (STATUS_SUCCESS);
}

int
wmain(int argc, wchar_t **argv)
{
    if (argc == 2 && wcscmp(argv[1], L"--version") == 0) {
        wprintf(L"zfswin-um %hs\n", ZFSWIN_VERSION);
        return (0);
    }
    /*
     * VERIFY failures abort(); keep the debug CRT from blocking on a modal
     * dialog so a failure ends the process (and the mount) visibly.
     */
    _set_abort_behavior(0, _WRITE_ABORT_MSG | _CALL_REPORTFAULT);
    if (!NT_SUCCESS(FspLoad(0))) {
        fwprintf(stderr, L"WinFsp is not installed "
            L"(https://github.com/winfsp/winfsp).\n");
        return (ERROR_DELAY_LOAD_FAILED);
    }
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &service_token))
        return (GetLastError());
    return ((int)FspServiceRun(PROGNAME, SvcStart, SvcStop, 0));
}
