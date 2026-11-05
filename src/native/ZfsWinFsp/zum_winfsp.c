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
 * Usage:
 *   zfswin-um mount <source> <pool> <dataset> <mountpoint> [--readonly]
 *       Console mode; Ctrl+C unmounts and exports.  <source> is an image
 *       directory or a device path; <mountpoint> is X: or an empty folder.
 *   zfswin-um            (as the ZfsWinUserMode service)
 *       Mounts every entry under HKLM\SOFTWARE\OpenZFS for Windows\
 *       UserMode\Mounts\<name> (Source, Pool, Dataset, MountPoint, ReadOnly).
 */

/* First, as in WinFsp's samples: it brings <windows.h> and the NT types. */
#include <winfsp/winfsp.h>
#include <sddl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "zum.h"

#define PROGNAME L"ZfsWinUserMode"
#define MOUNTS_KEY L"SOFTWARE\\OpenZFS for Windows\\UserMode\\Mounts"
#define MAX_MOUNTS 16
#define PATH_BYTES (32768 * 3)

/* Error statuses not in every SDK's ntstatus subset. */
#define ZUM_STATUS_CANNOT_DELETE ((NTSTATUS)0xC0000121L)

typedef struct mount {
    zum_fs_t *fs;
    FSP_FILE_SYSTEM *fsp;
    SRWLOCK lock;		/* exclusive for mutations, shared for reads */
    uint64_t root;
    BOOLEAN readonly;
    WCHAR mountpoint[MAX_PATH];
} mount_t;

typedef struct file_context {
    uint64_t object;
    BOOLEAN is_dir;
    BOOLEAN write_through;
    PVOID dir_buffer;
} file_context_t;

static mount_t mounts[MAX_MOUNTS];
static int nmounts;
static PSECURITY_DESCRIPTOR default_sd;
static ULONG default_sd_size;

#define MOUNT(fsp) ((mount_t *)(fsp)->UserContext)
#define EXCLUSIVE(m) AcquireSRWLockExclusive(&(m)->lock)
#define SHARED(m) AcquireSRWLockShared(&(m)->lock)
#define UNLOCK_EXCLUSIVE(m) ReleaseSRWLockExclusive(&(m)->lock)
#define UNLOCK_SHARED(m) ReleaseSRWLockShared(&(m)->lock)

static NTSTATUS
status_of(int error)
{
    return ((NTSTATUS)zum_fs_ntstatus(error));
}

/* "\dir\file" (UTF-16) to "/dir/file" (UTF-8); NULL when not convertible. */
static char *
to_path(PCWSTR name)
{
    int n = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, name, -1,
        NULL, 0, NULL, NULL);
    char *path, *p;

    if (n <= 0 || n > PATH_BYTES)
        return (NULL);
    if ((path = malloc(n)) == NULL)
        return (NULL);
    WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, name, -1, path, n,
        NULL, NULL);
    for (p = path; *p != '\0'; p++)
        if (*p == '\\')
            *p = '/';
    return (path);
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

static NTSTATUS
file_info(mount_t *m, uint64_t object, FSP_FSCTL_FILE_INFO *info)
{
    zum_attr_t attr;
    int error = zum_fs_getattr(m->fs, object, &attr);

    if (error == 0)
        to_file_info(&attr, info);
    return (status_of(error));
}

static NTSTATUS
new_context(uint64_t object, BOOLEAN is_dir, UINT32 create_options,
    PVOID *pcontext)
{
    file_context_t *c = calloc(1, sizeof (*c));

    if (c == NULL)
        return (STATUS_INSUFFICIENT_RESOURCES);
    c->object = object;
    c->is_dir = is_dir;
    c->write_through = (create_options & FILE_WRITE_THROUGH) != 0;
    *pcontext = c;
    return (STATUS_SUCCESS);
}

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
    PSECURITY_DESCRIPTOR sd, SIZE_T *psd_size)
{
    mount_t *m = MOUNT(fsp);
    char *path = to_path(name);
    zum_attr_t attr;
    uint64_t object;
    int error;

    if (path == NULL)
        return (STATUS_OBJECT_NAME_INVALID);
    SHARED(m);
    error = zum_fs_lookup(m->fs, path, &object);
    if (error == 0)
        error = zum_fs_getattr(m->fs, object, &attr);
    UNLOCK_SHARED(m);
    free(path);
    if (error != 0)
        return (status_of(error));
    if (pattributes != NULL)
        *pattributes = attr.is_dir ? (attr.attributes |
            FILE_ATTRIBUTE_DIRECTORY) & ~FILE_ATTRIBUTE_NORMAL :
            attr.attributes;
    if (psd_size != NULL) {
        if (*psd_size < default_sd_size) {
            *psd_size = default_sd_size;
            return (STATUS_BUFFER_OVERFLOW);
        }
        *psd_size = default_sd_size;
        if (sd != NULL)
            memcpy(sd, default_sd, default_sd_size);
    }
    return (STATUS_SUCCESS);
}

static NTSTATUS
Create(FSP_FILE_SYSTEM *fsp, PWSTR name, UINT32 create_options,
    UINT32 granted_access, UINT32 attributes, PSECURITY_DESCRIPTOR sd,
    UINT64 allocation_size, PVOID *pcontext, FSP_FSCTL_FILE_INFO *info)
{
    mount_t *m = MOUNT(fsp);
    BOOLEAN is_dir = (create_options & FILE_DIRECTORY_FILE) != 0;
    char *path = to_path(name);
    uint64_t object;
    NTSTATUS status;
    int error;

    (void) granted_access; (void) sd; (void) allocation_size;
    if (path == NULL)
        return (STATUS_OBJECT_NAME_INVALID);
    if (m->readonly) {
        free(path);
        return (STATUS_MEDIA_WRITE_PROTECTED);
    }
    EXCLUSIVE(m);
    error = zum_fs_create(m->fs, path, is_dir, &object);
    if (error == 0 && (attributes & ZUM_FA_SETTABLE) != 0)
        error = zum_fs_setattr(m->fs, object,
            attributes & ZUM_FA_SETTABLE, 0, 0, 0, 0);
    status = error == ENOENT ? STATUS_OBJECT_PATH_NOT_FOUND :
        status_of(error);
    if (NT_SUCCESS(status))
        status = file_info(m, object, info);
    UNLOCK_EXCLUSIVE(m);
    free(path);
    if (!NT_SUCCESS(status))
        return (status);
    return (new_context(object, is_dir, create_options, pcontext));
}

static NTSTATUS
Open(FSP_FILE_SYSTEM *fsp, PWSTR name, UINT32 create_options,
    UINT32 granted_access, PVOID *pcontext, FSP_FSCTL_FILE_INFO *info)
{
    mount_t *m = MOUNT(fsp);
    char *path = to_path(name);
    uint64_t object;
    NTSTATUS status;
    int error;

    (void) granted_access;
    if (path == NULL)
        return (STATUS_OBJECT_NAME_INVALID);
    SHARED(m);
    error = zum_fs_lookup(m->fs, path, &object);
    status = status_of(error);
    if (NT_SUCCESS(status))
        status = file_info(m, object, info);
    UNLOCK_SHARED(m);
    free(path);
    if (!NT_SUCCESS(status))
        return (status);
    return (new_context(object,
        (info->FileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0,
        create_options, pcontext));
}

static NTSTATUS
Overwrite(FSP_FILE_SYSTEM *fsp, PVOID context, UINT32 attributes,
    BOOLEAN replace_attributes, UINT64 allocation_size,
    FSP_FSCTL_FILE_INFO *info)
{
    mount_t *m = MOUNT(fsp);
    file_context_t *c = context;
    zum_attr_t attr;
    int error;

    (void) allocation_size;
    if (m->readonly)
        return (STATUS_MEDIA_WRITE_PROTECTED);
    EXCLUSIVE(m);
    error = zum_fs_truncate(m->fs, c->object, 0);
    if (error == 0)
        error = zum_fs_getattr(m->fs, c->object, &attr);
    if (error == 0) {
        uint32_t a = replace_attributes ? attributes :
            (attr.attributes | attributes);
        error = zum_fs_setattr(m->fs, c->object, a & ZUM_FA_SETTABLE,
            0, 0, 0, 0);
    }
    UNLOCK_EXCLUSIVE(m);
    if (error != 0)
        return (status_of(error));
    SHARED(m);
    NTSTATUS status = file_info(m, c->object, info);
    UNLOCK_SHARED(m);
    return (status);
}

static VOID
Cleanup(FSP_FILE_SYSTEM *fsp, PVOID context, PWSTR name, ULONG flags)
{
    mount_t *m = MOUNT(fsp);
    file_context_t *c = context;
    char *path;
    int error;

    if (!(flags & FspCleanupDelete) || name == NULL || m->readonly)
        return;
    if ((path = to_path(name)) == NULL)
        return;
    EXCLUSIVE(m);
    error = zum_fs_remove(m->fs, path, c->is_dir);
    UNLOCK_EXCLUSIVE(m);
    if (error != 0)
        FspServiceLog(EVENTLOG_WARNING_TYPE,
            L"delete of %s failed: error %d", name, error);
    free(path);
}

static VOID
Close(FSP_FILE_SYSTEM *fsp, PVOID context)
{
    file_context_t *c = context;

    (void) fsp;
    FspFileSystemDeleteDirectoryBuffer(&c->dir_buffer);
    free(c);
}

static NTSTATUS
Read(FSP_FILE_SYSTEM *fsp, PVOID context, PVOID buffer, UINT64 offset,
    ULONG length, PULONG pdone)
{
    mount_t *m = MOUNT(fsp);
    file_context_t *c = context;
    uint32_t done = 0;
    int error;

    SHARED(m);
    error = zum_fs_read(m->fs, c->object, offset, length, buffer, &done);
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
    zum_attr_t attr;
    uint64_t at = to_end ? ZUM_FS_APPEND : offset;
    NTSTATUS status;
    int error = 0;

    *pdone = 0;
    if (m->readonly)
        return (STATUS_MEDIA_WRITE_PROTECTED);
    EXCLUSIVE(m);
    /* Paging I/O never extends the file. */
    if (constrained) {
        error = zum_fs_getattr(m->fs, c->object, &attr);
        if (error == 0 && offset >= attr.size)
            length = 0;
        else if (error == 0 && offset + length > attr.size)
            length = (ULONG)(attr.size - offset);
    }
    if (error == 0 && length > 0)
        error = zum_fs_write(m->fs, c->object, &at, buffer, length);
    if (error == 0 && c->write_through)
        error = zum_fs_sync(m->fs);
    status = status_of(error);
    if (NT_SUCCESS(status)) {
        *pdone = length;
        status = file_info(m, c->object, info);
    }
    UNLOCK_EXCLUSIVE(m);
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
        status = file_info(m, c->object, info);
    UNLOCK_EXCLUSIVE(m);
    return (status);
}

static NTSTATUS
GetFileInfo(FSP_FILE_SYSTEM *fsp, PVOID context, FSP_FSCTL_FILE_INFO *info)
{
    mount_t *m = MOUNT(fsp);
    file_context_t *c = context;
    NTSTATUS status;

    SHARED(m);
    status = file_info(m, c->object, info);
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

    if (m->readonly)
        return (STATUS_MEDIA_WRITE_PROTECTED);
    EXCLUSIVE(m);
    error = zum_fs_setattr(m->fs, c->object,
        attributes == INVALID_FILE_ATTRIBUTES ? UINT32_MAX :
        attributes & ZUM_FA_SETTABLE, creation, access, write, change);
    status = status_of(error);
    if (NT_SUCCESS(status))
        status = file_info(m, c->object, info);
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

    if (m->readonly)
        return (STATUS_MEDIA_WRITE_PROTECTED);
    EXCLUSIVE(m);
    error = zum_fs_getattr(m->fs, c->object, &attr);
    /* Allocation size only shrinks a file; ZFS allocates on write. */
    if (error == 0 && (!allocation || size < attr.size))
        error = zum_fs_truncate(m->fs, c->object, size);
    status = status_of(error);
    if (NT_SUCCESS(status))
        status = file_info(m, c->object, info);
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
    if (m->readonly)
        return (STATUS_MEDIA_WRITE_PROTECTED);
    if (c->object == m->root)
        return (STATUS_ACCESS_DENIED);
    SHARED(m);
    if (zum_fs_getattr(m->fs, c->object, &attr) == 0 &&
        (attr.attributes & ZUM_FA_READONLY))
        status = ZUM_STATUS_CANNOT_DELETE;
    else if (c->is_dir && !zum_fs_dir_empty(m->fs, c->object))
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
    char *from = to_path(name), *to = to_path(new_name);
    zum_attr_t attr;
    uint64_t target;
    NTSTATUS status = STATUS_SUCCESS;
    int error;

    if (from == NULL || to == NULL) {
        free(from);
        free(to);
        return (STATUS_OBJECT_NAME_INVALID);
    }
    if (m->readonly) {
        free(from);
        free(to);
        return (STATUS_MEDIA_WRITE_PROTECTED);
    }
    EXCLUSIVE(m);
    error = zum_fs_lookup(m->fs, to, &target);
    /* The same object is a case-only rename on a case-insensitive name. */
    if (error == 0 && target != c->object) {
        if (!replace)
            status = STATUS_OBJECT_NAME_COLLISION;
        else if (zum_fs_getattr(m->fs, target, &attr) == 0 && attr.is_dir)
            status = STATUS_ACCESS_DENIED;
    }
    if (NT_SUCCESS(status))
        status = status_of(zum_fs_rename(m->fs, from, to));
    UNLOCK_EXCLUSIVE(m);
    free(from);
    free(to);
    return (status);
}

static NTSTATUS
GetSecurity(FSP_FILE_SYSTEM *fsp, PVOID context, PSECURITY_DESCRIPTOR sd,
    SIZE_T *psd_size)
{
    (void) fsp; (void) context;
    if (*psd_size < default_sd_size) {
        *psd_size = default_sd_size;
        return (STATUS_BUFFER_OVERFLOW);
    }
    *psd_size = default_sd_size;
    if (sd != NULL)
        memcpy(sd, default_sd, default_sd_size);
    return (STATUS_SUCCESS);
}

typedef struct dir_fill {
    PVOID *buffer;
    NTSTATUS result;
} dir_fill_t;

static BOOLEAN
add_dir_entry(dir_fill_t *f, PCWSTR name, size_t name_chars,
    const zum_attr_t *attr)
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
    to_file_info(attr, &entry.d.FileInfo);
    memcpy(entry.d.FileNameBuf, name, name_chars * sizeof (WCHAR));
    return (FspFileSystemFillDirectoryBuffer(f->buffer, &entry.d,
        &f->result));
}

static int
dirent_cb(void *arg, const char *name, const zum_attr_t *attr)
{
    WCHAR wname[256];
    int n = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, name, -1,
        wname, 256);

    if (n <= 1)
        return (0);		/* not valid UTF-8 or too long: skipped */
    return (add_dir_entry(arg, wname, (size_t)n - 1, attr) ? 0 : -1);
}

static NTSTATUS
ReadDirectory(FSP_FILE_SYSTEM *fsp, PVOID context, PWSTR pattern,
    PWSTR marker, PVOID buffer, ULONG length, PULONG pdone)
{
    mount_t *m = MOUNT(fsp);
    file_context_t *c = context;
    dir_fill_t fill = { &c->dir_buffer, STATUS_SUCCESS };
    zum_attr_t self;
    int error;

    (void) pattern;
    if (FspFileSystemAcquireDirectoryBuffer(&c->dir_buffer, marker == NULL,
        &fill.result)) {
        SHARED(m);
        error = zum_fs_getattr(m->fs, c->object, &self);
        if (error == 0 && c->object != m->root) {
            /* ".." carries this directory's times, as memfs does. */
            if (add_dir_entry(&fill, L".", 1, &self))
                (void) add_dir_entry(&fill, L"..", 2, &self);
        }
        if (error == 0)
            error = zum_fs_readdir(m->fs, c->object, dirent_cb, &fill);
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
    .ReadDirectory = ReadDirectory,
};

static char *
utf8_of(PCWSTR s)
{
    int n = WideCharToMultiByte(CP_UTF8, 0, s, -1, NULL, 0, NULL, NULL);
    char *out = n > 0 ? malloc(n) : NULL;

    if (out != NULL)
        WideCharToMultiByte(CP_UTF8, 0, s, -1, out, n, NULL, NULL);
    return (out);
}

static NTSTATUS
mount_one(PCWSTR source, PCWSTR pool, PCWSTR dataset, PCWSTR mountpoint,
    BOOLEAN readonly)
{
    FSP_FSCTL_VOLUME_PARAMS params;
    zum_fs_args_t args;
    zum_statfs_t st;
    mount_t *m;
    FILETIME now;
    NTSTATUS status;
    int error;

    if (nmounts == MAX_MOUNTS)
        return (STATUS_INSUFFICIENT_RESOURCES);
    m = &mounts[nmounts];
    memset(m, 0, sizeof (*m));
    InitializeSRWLock(&m->lock);
    m->readonly = readonly;
    wcsncpy_s(m->mountpoint, MAX_PATH, mountpoint, _TRUNCATE);

    args.search_path = utf8_of(source);
    args.pool = utf8_of(pool);
    args.dataset = utf8_of(dataset);
    args.readonly = readonly;
    error = args.search_path && args.pool && args.dataset ?
        zum_fs_open(&args, &m->fs) : ENOMEM;
    free((void *)args.search_path);
    free((void *)args.pool);
    free((void *)args.dataset);
    if (error != 0) {
        FspServiceLog(EVENTLOG_ERROR_TYPE,
            L"cannot import %s/%s from %s: error %d", pool, dataset, source,
            error);
        return (status_of(error));
    }
    (void) zum_fs_lookup(m->fs, "", &m->root);
    (void) zum_fs_statfs(m->fs, &st);

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
    params.PersistentAcls = 0;
    params.PostCleanupWhenModifiedOnly = 1;
    params.UmFileContextIsUserContext2 = 1;
    params.ReadOnlyVolume = readonly;
    wcscpy_s(params.FileSystemName, ARRAYSIZE(params.FileSystemName),
        L"ZFS");

    status = FspFileSystemCreate(L"" FSP_FSCTL_DISK_DEVICE_NAME, &params,
        &zum_interface, &m->fsp);
    if (NT_SUCCESS(status)) {
        m->fsp->UserContext = m;
        status = FspFileSystemSetMountPoint(m->fsp, m->mountpoint);
    }
    if (NT_SUCCESS(status))
        status = FspFileSystemStartDispatcher(m->fsp, 0);
    if (!NT_SUCCESS(status)) {
        FspServiceLog(EVENTLOG_ERROR_TYPE,
            L"cannot mount %s/%s on %s: 0x%08lx", pool, dataset, mountpoint,
            status);
        if (m->fsp != NULL)
            FspFileSystemDelete(m->fsp);
        (void) zum_fs_close(m->fs);
        return (status);
    }
    FspServiceLog(EVENTLOG_INFORMATION_TYPE, L"zfswin-um %hs: mounted %s/%s "
        L"on %s%s", ZFSWIN_VERSION, pool, dataset, mountpoint,
        readonly ? L" (read-only)" : L"");
    nmounts++;
    return (STATUS_SUCCESS);
}

static void
unmount_all(void)
{
    while (nmounts > 0) {
        mount_t *m = &mounts[--nmounts];
        int error;

        FspFileSystemStopDispatcher(m->fsp);
        FspFileSystemDelete(m->fsp);
        error = zum_fs_close(m->fs);	/* sync and export */
        FspServiceLog(error == 0 ? EVENTLOG_INFORMATION_TYPE :
            EVENTLOG_ERROR_TYPE, L"unmounted %s (export: %d)",
            m->mountpoint, error);
    }
}

static NTSTATUS
read_value(HKEY key, PCWSTR name, PWSTR out, DWORD chars)
{
    DWORD bytes = chars * sizeof (WCHAR);

    return (RegGetValueW(key, NULL, name, RRF_RT_REG_SZ, NULL, out,
        &bytes) == ERROR_SUCCESS ? STATUS_SUCCESS :
        STATUS_OBJECT_NAME_NOT_FOUND);
}

/* Service mode: every configured mount; one failure does not stop others. */
static NTSTATUS
mount_configured(void)
{
    WCHAR name[256], source[MAX_PATH], pool[256], dataset[256], mp[MAX_PATH];
    HKEY root, key;
    DWORD i, chars, readonly, size;

    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, MOUNTS_KEY, 0, KEY_READ, &root) !=
        ERROR_SUCCESS)
        return (STATUS_SUCCESS);	/* nothing configured */
    for (i = 0; chars = ARRAYSIZE(name), RegEnumKeyExW(root, i, name,
        &chars, NULL, NULL, NULL, NULL) == ERROR_SUCCESS; i++) {
        if (RegOpenKeyExW(root, name, 0, KEY_READ, &key) != ERROR_SUCCESS)
            continue;
        readonly = 0;
        size = sizeof (readonly);
        (void) RegGetValueW(key, NULL, L"ReadOnly", RRF_RT_REG_DWORD, NULL,
            &readonly, &size);
        if (NT_SUCCESS(read_value(key, L"Source", source, MAX_PATH)) &&
            NT_SUCCESS(read_value(key, L"Pool", pool, 256)) &&
            NT_SUCCESS(read_value(key, L"Dataset", dataset, 256)) &&
            NT_SUCCESS(read_value(key, L"MountPoint", mp, MAX_PATH)))
            (void) mount_one(source, pool, dataset, mp, readonly != 0);
        else
            FspServiceLog(EVENTLOG_ERROR_TYPE,
                L"mount %s: Source, Pool, Dataset and MountPoint are "
                L"required", name);
        RegCloseKey(key);
    }
    RegCloseKey(root);
    return (STATUS_SUCCESS);
}

static NTSTATUS
SvcStart(FSP_SERVICE *service, ULONG argc, PWSTR *argv)
{
    (void) service;
    if (argc >= 6 && wcscmp(argv[1], L"mount") == 0)
        return (mount_one(argv[2], argv[3], argv[4], argv[5],
            argc >= 7 && wcscmp(argv[6], L"--readonly") == 0));
    if (argc > 1) {
        FspServiceLog(EVENTLOG_ERROR_TYPE, L"usage: zfswin-um mount "
            L"<source> <pool> <dataset> <mountpoint> [--readonly]");
        return (STATUS_INVALID_PARAMETER);
    }
    return (mount_configured());
}

static NTSTATUS
SvcStop(FSP_SERVICE *service)
{
    (void) service;
    unmount_all();
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
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
        L"O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1301bf;;;AU)",
        SDDL_REVISION_1, &default_sd, &default_sd_size))
        return (GetLastError());
    return ((int)FspServiceRun(PROGNAME, SvcStart, SvcStop, 0));
}
