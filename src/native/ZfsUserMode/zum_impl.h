// SPDX-License-Identifier: CDDL-1.0
/*
 * Portable ZPL layer shared by the user-mode filesystem (zfswin-um.exe over
 * WinFsp) and the zfs-write-core test tool.
 *
 * Everything behind this header is written against the OpenZFS kernel
 * interfaces only (DMU, ZAP, SA, SPL kmem/cmn_err), never against the C
 * runtime or Win32, so the same source compiles into libzpool (user mode)
 * and into the kernel module.  Front ends (WinFsp callbacks here, IRP
 * dispatch in the kernel driver) translate their requests into these calls.
 */
#ifndef ZUM_IMPL_H
#define ZUM_IMPL_H

#include <sys/zfs_context.h>
#include <sys/dmu.h>
#include <sys/dmu_objset.h>
#include <sys/dmu_tx.h>
#include <sys/dsl_prop.h>
#include <sys/dnode.h>
#include <sys/sa.h>
#include <sys/sa_impl.h>
#include <sys/spa.h>
#include <sys/spa_impl.h>
#include <sys/txg.h>
#include <sys/u8_textprep.h>
#include <sys/vdev_impl.h>
#include <sys/zap.h>
#include <sys/zfs_acl.h>
#include <sys/zfs_sa.h>
#include <sys/zfs_znode.h>
#include <sys/dbuf.h>
#include <sys/dsl_dataset.h>
#include <sys/dsl_pool.h>
#include <sys/zfeature.h>
#include <zfs_comutil.h>

#define ZUM_MAX_INJECTIONS 8

typedef struct zum_context {
    const char *pool_name;
    const char *dataset_name;
    const char *search_path;
    boolean_t readonly;		/* import and own read-only */
    boolean_t sync_each_op;	/* txg_wait_synced() after every mutation */
    spa_t *spa;
    objset_t *os;
    sa_attr_type_t *sa_table;
    uint64_t zpl_version;
    uint64_t root_obj;
    uint64_t unlinked_obj;
    uint64_t max_blksz;		/* recordsize, as zfsvfs->z_max_blksz */
    uint64_t norm;		/* zfsvfs->z_norm, including TOUPPER */
    uint64_t casesensitivity;
    uint64_t utf8;
    boolean_t xattr_sa;		/* xattr=sa: zfsvfs->z_xattr_sa */
    boolean_t imported;		/* this context imported the pool */
    boolean_t keep_pool;	/* close leaves the pool imported */
    boolean_t engine_held;
    int injections[ZUM_MAX_INJECTIONS];	/* zio_inject_fault() ids */
    int ninjections;
} zum_context_t;

/*
 * Minimal in-memory znode: the subset of znode_t state that the ported ZPL
 * routines read and maintain.  Every field is loaded from, and written back
 * to, the object's SA bonus buffer in the same transaction that changes it,
 * so no state outlives a call.
 */
typedef struct zum_znode {
    uint64_t z_id;
    sa_handle_t *z_sa_hdl;
    uint64_t z_mode;
    uint64_t z_size;
    uint64_t z_links;
    uint64_t z_pflags;
    uint64_t z_projid;
    uint64_t z_xattr;
    uint32_t z_blksz;
    boolean_t z_unlinked;
} zum_znode_t;

/*
 * Dataset lifetime (portable): own the objset of an imported pool and read
 * the zfsvfs_init() inputs; release it again.
 */
int zum_mount(zum_context_t *context);
void zum_unmount(zum_context_t *context);
void zum_sync(zum_context_t *context);
/* txg_wait_synced() when the context syncs every mutation. */
void zum_sync_op(zum_context_t *context);

/* Pool lifetime, user mode only (zum_pool_um.c): import + mount, export. */
int zum_open(zum_context_t *context);
int zum_close(zum_context_t *context);

/* Znodes. */
int zum_zget(zum_context_t *context, uint64_t object, zum_znode_t **zpp);
void zum_zrele(zum_znode_t *zp);
int zum_resolve(zum_context_t *context, const char *path, zum_znode_t **dzpp,
    char leaf[ZAP_MAXNAMELEN], zum_znode_t **zpp);
int zum_lookup_path(zum_context_t *context, const char *path,
    uint64_t *object);

/* Namespace and data, '/'-separated paths relative to the dataset root. */
int zum_create_path(zum_context_t *context, const char *path,
    boolean_t isdir, uint64_t *object_out);
int zum_create_entry(zum_context_t *context, zum_znode_t *dzp,
    const char *name, boolean_t isdir, uint64_t *object_out);
int zum_remove_entry(zum_context_t *context, zum_znode_t *dzp,
    const char *name);
int zum_dir_lookup(zum_context_t *context, zum_znode_t *dzp,
    const char *name, uint64_t *object);
int zum_xattrdir(zum_context_t *context, zum_znode_t *zp, boolean_t create,
    zum_znode_t **xzpp);
void zum_create_fs(objset_t *os, nvlist_t *zplprops, dmu_tx_t *tx);

int zum_clone_range(zum_context_t *context, uint64_t inobj, uint64_t inoff,
    uint64_t outobj, uint64_t outoff, uint64_t *lenp);

/* Extended attributes, as the Windows kernel driver stores them. */
int zum_xattr_get(zum_context_t *context, zum_znode_t *zp, const char *name,
    void *buf, size_t size, size_t *len);
int zum_xattr_set(zum_context_t *context, zum_znode_t *zp, const char *name,
    const void *value, size_t len);
int zum_remove_path(zum_context_t *context, const char *path,
    boolean_t isdir, boolean_t defer_reclaim);
int zum_rename_path(zum_context_t *context, const char *from,
    const char *to);
int zum_truncate_path(zum_context_t *context, const char *path,
    uint64_t size);
int zum_truncate_object(zum_context_t *context, uint64_t object,
    uint64_t size);
#define ZUM_APPEND UINT64_MAX
int zum_write_object(zum_context_t *context, uint64_t object,
    uint64_t *offsetp, const void *data, uint64_t length);

int zum_vdev_leaves(vdev_t *vd, vdev_t **leaves, int max, int count);

/* Upstream fault injection (the zinject device-fault path). */
int zum_inject_write_errors(zum_context_t *context, const char *match);
void zum_clear_injections(zum_context_t *context);

#endif
