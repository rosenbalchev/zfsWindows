// SPDX-License-Identifier: CDDL-1.0
/*
 * File-level API of the portable ZPL layer, for filesystem front ends.
 *
 * Only <stdint.h> types cross this boundary, so a front end includes it
 * without the OpenZFS headers: the WinFsp bridge (zum_winfsp.c) next to
 * <windows.h>/<winfsp.h> in user mode, or an I/O-manager front end in the
 * kernel.  Paths are UTF-8, '/'-separated and relative to the dataset root
 * ("" or "/" is the root).  Functions return 0 or an errno value;
 * zum_fs_ntstatus() maps one to the NTSTATUS a Windows caller expects.
 * Calls are not internally serialised: the front end holds one lock per
 * filesystem around them.
 */
#ifndef ZUM_H
#define ZUM_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct zum_context zum_fs_t;

typedef struct zum_fs_args {
    const char *search_path;	/* image directory or device to import from */
    const char *pool;
    const char *dataset;
    int readonly;
} zum_fs_args_t;

/* Windows FILE_ATTRIBUTE_* values, as zfs_getwinflags() produces them. */
#define ZUM_FA_READONLY         0x00000001
#define ZUM_FA_HIDDEN           0x00000002
#define ZUM_FA_SYSTEM           0x00000004
#define ZUM_FA_DIRECTORY        0x00000010
#define ZUM_FA_ARCHIVE          0x00000020
#define ZUM_FA_NORMAL           0x00000080
#define ZUM_FA_SPARSE_FILE      0x00000200
#define ZUM_FA_REPARSE_POINT    0x00000400
/* The attributes a caller may change (zfs_setwinflags_xva()). */
#define ZUM_FA_SETTABLE (ZUM_FA_READONLY | ZUM_FA_HIDDEN | ZUM_FA_SYSTEM | \
    ZUM_FA_ARCHIVE)

typedef struct zum_attr {
    uint64_t object;		/* also the Windows file index */
    uint64_t size;
    uint64_t allocation;	/* bytes allocated on disk */
    uint64_t links;
    uint64_t mode;		/* POSIX mode */
    uint32_t attributes;	/* ZUM_FA_* */
    int is_dir;
    /* FILETIME: 100 ns units since 1601-01-01 UTC. */
    uint64_t creation_time;
    uint64_t access_time;
    uint64_t write_time;
    uint64_t change_time;
} zum_attr_t;

typedef struct zum_statfs {
    uint64_t total_bytes;
    uint64_t free_bytes;
    uint64_t fsid_guid;
    int case_sensitive;
    int readonly;
} zum_statfs_t;

/* User mode: import the pool (read-only if asked) and own the dataset. */
int zum_fs_open(const zum_fs_args_t *args, zum_fs_t **fsp);
/* Sync, release the dataset and export the pool. */
int zum_fs_close(zum_fs_t *fs);

int zum_fs_statfs(zum_fs_t *fs, zum_statfs_t *st);
int zum_fs_sync(zum_fs_t *fs);

int zum_fs_lookup(zum_fs_t *fs, const char *path, uint64_t *object);
int zum_fs_getattr(zum_fs_t *fs, uint64_t object, zum_attr_t *attr);
/*
 * Attributes (ZUM_FA_SETTABLE bits; UINT32_MAX leaves them) and times
 * (FILETIME; 0 leaves one unchanged).
 */
int zum_fs_setattr(zum_fs_t *fs, uint64_t object, uint32_t attributes,
    uint64_t creation_time, uint64_t access_time, uint64_t write_time,
    uint64_t change_time);

/* Reads stop at end of file; *done is the byte count read. */
int zum_fs_read(zum_fs_t *fs, uint64_t object, uint64_t offset,
    uint32_t length, void *buffer, uint32_t *done);
/* *offset is the offset, or ZUM_FS_APPEND; on return, where data went. */
#define ZUM_FS_APPEND UINT64_MAX
int zum_fs_write(zum_fs_t *fs, uint64_t object, uint64_t *offset,
    const void *buffer, uint32_t length);
int zum_fs_truncate(zum_fs_t *fs, uint64_t object, uint64_t size);

int zum_fs_create(zum_fs_t *fs, const char *path, int is_dir,
    uint64_t *object);
int zum_fs_remove(zum_fs_t *fs, const char *path, int is_dir);
/* Replaces an existing file or empty directory of the same kind. */
int zum_fs_rename(zum_fs_t *fs, const char *from, const char *to);
/* 1 if the directory has no entries. */
int zum_fs_dir_empty(zum_fs_t *fs, uint64_t object);

/*
 * Every entry of a directory ("." and ".." are not stored).  The callback
 * returns a negative value to stop early; that value is returned.
 * Otherwise the result is 0 or an errno value.
 */
typedef int (*zum_dirent_cb_t)(void *arg, const char *name,
    const zum_attr_t *attr);
int zum_fs_readdir(zum_fs_t *fs, uint64_t object, zum_dirent_cb_t cb,
    void *arg);

uint32_t zum_fs_ntstatus(int error);

#ifdef __cplusplus
}
#endif

#endif
