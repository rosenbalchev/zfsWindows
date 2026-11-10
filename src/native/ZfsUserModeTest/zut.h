// SPDX-License-Identifier: CDDL-1.0
/*
 * Test entry points of the user-mode engine (ZfsUserModeTest.dll, for
 * tests/Zfs.UserMode.Tests through Zfs.Interop).
 *
 * The DLL exports the file-level API of zum.h as it is, plus the pool and
 * dataset operations zpool.exe/zfs.exe would send: those go through
 * zum_ioctl(), so upstream zfs_ioctl.c runs them exactly as for the
 * service.  Properties are "name=value" pairs separated by ';'.  Functions
 * return 0 or an errno value.
 */
#ifndef ZUT_H
#define ZUT_H

#include <stdint.h>

#include "zum.h"

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Start the engine (and, once per process, the ioctl layer); the cache
 * file created pools write goes to cachefile (NULL: none is written).
 */
int zut_engine_start(const char *cachefile);
/* zum_engine_stop(): export every pool, then kernel_fini(). */
int zut_engine_stop(void);

/*
 * ARC ceiling for the next start, the floor at half of it (0: the engine's
 * defaults, RAM/4 and RAM/32).
 */
void zut_arc_set_max(uint64_t bytes);
typedef struct zut_arc_stats {
    uint64_t size;		/* bytes the ARC holds now */
    uint64_t target;		/* arc_c */
    uint64_t min;		/* arc_c_min */
    uint64_t max;		/* arc_c_max */
} zut_arc_stats_t;
void zut_arc_stats(zut_arc_stats_t *stats);

/*
 * Create a pool on image files (vdevs: ';'-separated paths, each at least
 * 64 MiB).  layout: "" (one top-level vdev per image), "mirror", "raidz1",
 * "raidz2" or "raidz3".  fs_props are the root dataset's (zpool create -O).
 * As zpool create: every feature is enabled unless pool_props has
 * feature@name=disabled or a version.
 */
int zut_pool_create(const char *pool, const char *layout, const char *vdevs,
    const char *pool_props, const char *fs_props);
/*
 * zum_pool_import(): by name from ';'-separated directories or images, with
 * that cachefile property (NULL: none).
 */
int zut_pool_import(const char *pool, const char *search_path,
    const char *cachefile);
/* zum_pool_cachefile(). */
void zut_pool_cachefile(const char *pool, char *out, int size);
int zut_pool_export(const char *pool, int force);
int zut_pool_destroy(const char *pool);
/* Start a scrub and wait for it to finish (zpool scrub -w). */
int zut_pool_scrub(const char *pool);

typedef struct zut_pool_status {
    uint64_t state;		/* vdev_state_t of the root vdev */
    uint64_t leaves;
    uint64_t read_errors;	/* summed over the leaf vdevs */
    uint64_t write_errors;
    uint64_t checksum_errors;
    uint64_t scan_state;	/* dsl_scan_state_t; 0 if never scanned */
    uint64_t scan_errors;
    uint64_t scan_repaired;	/* bytes ("scrub repaired") */
} zut_pool_status_t;
int zut_pool_status(const char *pool, zut_pool_status_t *status);

/* zfs create / zfs set. */
int zut_dataset_create(const char *name, const char *props);
int zut_dataset_set(const char *name, const char *props);
/* zfs snapshot / zfs destroy of "pool/fs@name". */
int zut_snapshot(const char *snapshot);
int zut_snapshot_destroy(const char *snapshot);
/* zfs rollback to the latest snapshot; the dataset must not be open. */
int zut_rollback(const char *dataset);

/* zum_fs_open() without the argument structure. */
int zut_fs_open(const char *search_path, const char *pool,
    const char *dataset, int readonly, zum_fs_t **fsp);

#ifdef __cplusplus
}
#endif

#endif
