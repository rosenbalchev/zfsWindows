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

/*
 * User mode: import the pool if it is not imported yet (from search_path:
 * ';'-separated image directories, image files or devices; read-only if
 * asked) and own the dataset.
 */
int zum_fs_open(const zum_fs_args_t *args, zum_fs_t **fsp);
/* Sync and release the dataset; the pool stays imported. */
int zum_fs_close(zum_fs_t *fs);

/*
 * User-mode engine lifetime: start it before the first zum_fs_open() or
 * bridge request; stopping exports every imported pool (as `zpool export`,
 * so the disks import anywhere) and shuts the engine down.
 */
int zum_engine_start(void);
int zum_engine_stop(void);
/* The cache file created pools write (never read back; see zum_pool_um.c). */
void zum_engine_set_cachefile(const char *path);
/*
 * Import one pool by name from search_path (see zum_fs_open()); cachefile:
 * its cachefile property, NULL or "" for "none".
 */
int zum_pool_import(const char *pool, const char *search_path,
    const char *cachefile);
/* An imported pool's leaf vdev paths, ';'-separated ("" if not imported). */
void zum_pool_sources(const char *pool, char *out, size_t size);
/*
 * An imported pool's cachefile property when it names a file of the user's
 * ("" for the engine's own cache file, "none", or not imported).
 */
void zum_pool_cachefile(const char *pool, char *out, size_t size);

/*
 * The ZFS ioctl bridge (user mode): requests arrive from zpool.exe/zfs.exe
 * through the front end's pipe server and run upstream zfs_ioctl.c here.
 * Mount requests and the things a pool operation must not find busy go back
 * to the front end through these callbacks (any may be NULL).
 */
typedef struct zum_bridge_ops {
    /* ZFS_IOC_MOUNT: where is "X:", "?:" (any letter) or "C:\path". */
    int (*mount)(const char *dataset, const char *where, int readonly);
    int (*unmount)(const char *dataset);
    /* Before export/destroy: unmount every dataset of the pool. */
    void (*unmount_pool)(const char *pool);
    /* Around rollback: release a mounted dataset (1 if it was), remount. */
    int (*suspend)(const char *dataset);
    void (*resume)(const char *dataset);
    /* A snapshot is going away: drop its .zfs\snapshot view. */
    void (*snapshot_unmount)(const char *snapshot);
    /* Pool created or imported (sources: its leaf vdev paths), exported. */
    void (*pool_imported)(const char *pool, const char *sources);
    void (*pool_exported)(const char *pool);
    /*
     * After pool_imported and after `zpool set`: the pool's cachefile, as
     * zum_pool_cachefile() gives it, to import it with next time.
     */
    void (*pool_cachefile)(const char *pool, const char *cachefile);
} zum_bridge_ops_t;

int zum_bridge_init(const zum_bridge_ops_t *ops);

/* One ioctl's buffers, already in this process. */
typedef struct zum_ioctl_buffers {
    void *cmd;			/* zfs_cmd_t, zum_ioctl_cmd_size() bytes */
    void *src;
    uint64_t src_size;
    void *conf;
    uint64_t conf_size;
    void *history;		/* input string, or POOL_GET_HISTORY output */
    uint64_t history_cap;
    void *dst;
    uint64_t dst_cap;
    uint64_t dst_out;		/* on return: bytes of dst to send back */
    uint64_t history_out;
} zum_ioctl_buffers_t;

size_t zum_ioctl_cmd_size(void);
/* The engine's OpenZFS version string (as the driver's zfs_version). */
const char *zum_engine_version(void);
/* Returns the ioctl's errno; client_pid keeps history logging per caller. */
int zum_ioctl(uint64_t request, uint32_t client_pid, zum_ioctl_buffers_t *io);

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

/*
 * Release the dataset while its mount stays (a rollback must own it), and
 * own it again.  Object numbers held across may no longer exist.
 */
int zum_fs_suspend(zum_fs_t *fs);
int zum_fs_resume(zum_fs_t *fs);

/* The directory an object is linked from (a stream: its xattr directory). */
int zum_fs_parent(zum_fs_t *fs, uint64_t object, uint64_t *parent);
/* POSIX owner and group ids, and the parent, of an object. */
int zum_fs_owner(zum_fs_t *fs, uint64_t object, uint64_t *uid, uint64_t *gid,
    uint64_t *parent);

/*
 * Windows security descriptor (self-relative), stored where the kernel
 * driver stores it.  ENOENT: the object has none (written elsewhere), and
 * the front end derives one as the driver does.  ERANGE: *len is the size.
 */
int zum_fs_get_security(zum_fs_t *fs, uint64_t object, void *buf,
    uint32_t size, uint32_t *len);
int zum_fs_set_security(zum_fs_t *fs, uint64_t object, const void *sd,
    uint32_t len);

/*
 * Alternate data streams of a file.  A stream is an object like any other
 * file (read, write, truncate, getattr by its object number).
 */
int zum_fs_stream_open(zum_fs_t *fs, uint64_t object, const char *name,
    int create, uint64_t *stream);
int zum_fs_stream_remove(zum_fs_t *fs, uint64_t object, const char *name);
/* Each stream's name (without ":$DATA") and attributes. */
int zum_fs_stream_list(zum_fs_t *fs, uint64_t object, zum_dirent_cb_t cb,
    void *arg);

/*
 * Share length bytes of source at source_offset with target at
 * target_offset (block cloning).  Offsets and length follow
 * zfs_clone_range()'s block-alignment rules (EINVAL otherwise);
 * *length returns the bytes cloned.  EOPNOTSUPP without the pool feature.
 */
int zum_fs_clone(zum_fs_t *fs, uint64_t source, uint64_t source_offset,
    uint64_t target, uint64_t target_offset, uint64_t *length);

/* Mount-table flags for libzfs: 1 read-only, 2 noatime. */
int zum_fs_mount_flags(zum_fs_t *fs);

/* .zfs: the snapdir property (0 hidden, 1 visible) and the snapshots. */
int zum_fs_snapdir(zum_fs_t *fs);
typedef int (*zum_snapshot_cb_t)(void *arg, const char *name, uint64_t id,
    uint64_t creation_time);
int zum_fs_snapshot_list(zum_fs_t *fs, zum_snapshot_cb_t cb, void *arg);
/* Own a snapshot read-only as a filesystem of its own; close releases it. */
int zum_fs_snapshot_open(zum_fs_t *fs, const char *snapshot,
    zum_fs_t **snapfs);
void zum_fs_snapshot_close(zum_fs_t *snapfs);

uint32_t zum_fs_ntstatus(int error);

#ifdef __cplusplus
}
#endif

#endif
