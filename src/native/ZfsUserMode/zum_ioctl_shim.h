// SPDX-License-Identifier: CDDL-1.0
/*
 * Force-included (and only) into upstream module/zfs/zfs_ioctl.c when it is
 * built into the user-mode engine (zum_ioctl_um.c is its other half).
 *
 * zfs_ioctl.c is the platform-independent ZFS ioctl dispatcher: pool
 * create/import/export, vdev changes, scrub, datasets, snapshots, properties
 * and history are DSL/SPA calls that libzpool already contains.  It also
 * reaches into the kernel VFS (zfsvfs_t, zvols, .zfs control directory) for
 * datasets that the kernel has mounted.  In user mode the kernel mounts
 * nothing, so those headers are replaced by the declarations below and
 * zum_ioctl_um.c answers "not mounted" (ESRCH) / "not a zvol".  Every
 * guard name here is the real header's, so the real one is skipped.
 */
#ifndef ZUM_IOCTL_SHIM_H
#define ZUM_IOCTL_SHIM_H

#define _ZFS_CTLDIR_H
#define _SYS_FS_ZFS_DIR_H
#define _SYS_ZFS_VFSOPS_H
#define _SYS_FS_ZFS_VFSOPS_OS_H
#define _ZFS_VSS_H
#define _SYS_ZVOL_H
#define _SYS_ZVOL_OS_h

#include <sys/zfs_context.h>
#include <sys/fs/zfs.h>
#include <sys/rrwlock.h>
#include <sys/zfs_ioctl.h>
#include <sys/zfs_quota.h>

struct dsl_dataset;
struct objset;
struct spa;
struct dmu_tx;
struct nvlist;

/* The _KERNEL part of sys/zfs_ioctl.h, verbatim. */
typedef struct zfs_creat {
    nvlist_t *zct_zplprops;
    nvlist_t *zct_props;
} zfs_creat_t;

int zfs_secpolicy_snapshot_perms(const char *, cred_t *);
int zfs_secpolicy_rename_perms(const char *, const char *, cred_t *);
int zfs_secpolicy_destroy_perms(const char *, cred_t *);
void zfs_unmount_snap(const char *);
void zfs_destroy_unmount_origin(const char *);
int getzfsvfs_impl(struct objset *, struct zfsvfs **);
int getzfsvfs(const char *, struct zfsvfs **);

enum zfsdev_state_type {
    ZST_ONEXIT,
    ZST_ZEVENT,
    ZST_ALL,
};

typedef struct zfsdev_state {
    struct zfsdev_state *zs_next;
    minor_t zs_minor;
    void *zs_onexit;
    void *zs_zevent;
} zfsdev_state_t;

void *zfsdev_get_state(minor_t minor, enum zfsdev_state_type which);
int zfsdev_getminor(zfs_file_t *fp, minor_t *minorp);
extern uint_t zfs_allow_log_key;

/* Never instantiated in user mode: getzfsvfs() always reports ESRCH. */
typedef struct zfsvfs {
    struct objset *z_os;
    void *z_sb;
    boolean_t z_unmounted;
    boolean_t z_use_hold;
    rrmlock_t z_teardown_lock;
} zfsvfs_t;

/* 2.4.4: zfsvfs_hold() creates one with this when getzfsvfs() fails. */
int zfsvfs_create_hold(const char *name, zfsvfs_t **zfvp);

/*
 * 2.4.4: zoned_uid delegation (Linux user namespaces).  The userland
 * libspl sys/zone.h has none of it; these are the Windows kernel SPL's
 * (include/os/windows/spl/sys/zone.h): not applicable, so the existing
 * permission checks decide, and attach/detach fail with ENXIO.
 */
typedef enum zone_uid_op {
    ZONE_OP_CREATE,
    ZONE_OP_SNAPSHOT,
    ZONE_OP_CLONE,
    ZONE_OP_DESTROY,
    ZONE_OP_RENAME,
    ZONE_OP_SETPROP
} zone_uid_op_t;

typedef enum zone_admin_result {
    ZONE_ADMIN_NOT_APPLICABLE,
    ZONE_ADMIN_ALLOWED,
    ZONE_ADMIN_DENIED
} zone_admin_result_t;

typedef uid_t (*zone_get_zoned_uid_fn_t)(const char *dataset,
    char *root_out, size_t root_size);

static inline zone_admin_result_t
zone_dataset_admin_check(const char *dataset, zone_uid_op_t op,
    const char *aux_dataset)
{
    (void) dataset, (void) op, (void) aux_dataset;
    return (ZONE_ADMIN_NOT_APPLICABLE);
}

static inline int
zone_dataset_attach_uid(cred_t *cred, const char *dataset, uid_t uid)
{
    (void) cred, (void) dataset, (void) uid;
    return (ENXIO);
}

static inline int
zone_dataset_detach_uid(cred_t *cred, const char *dataset, uid_t uid)
{
    (void) cred, (void) dataset, (void) uid;
    return (ENXIO);
}

static inline void
zone_register_zoned_uid_callback(zone_get_zoned_uid_fn_t fn)
{
    (void) fn;
}

static inline void
zone_unregister_zoned_uid_callback(void)
{
}

/*
 * Kernel SPL entry points (zum_ioctl_um.c).  The bridge server has already
 * copied the caller's buffers into this process, so copyin/copyout are
 * memory copies; the pipe's ACL (SYSTEM and Administrators) is the
 * privilege check that secpolicy_*() performs in the kernel.
 */
int ddi_copyin(const void *from, void *to, size_t len, int flags);
int ddi_copyout(const void *from, void *to, size_t len, int flags);
int xcopyin(const void *from, void *to, size_t len);
int xcopyout(const void *from, void *to, size_t len);
int copyinstr(const void *from, void *to, size_t len, size_t *done);
int secpolicy_zfs(const cred_t *cr);
int secpolicy_sys_config(const cred_t *cr, boolean_t checkonly);
int secpolicy_zinject(const cred_t *cr);
int groupmember(gid_t gid, const cred_t *cr);
boolean_t zfs_proc_is_caller(proc_t *p);
#define is_system_labeled() 0
#ifndef MNT_FORCE
#define MNT_FORCE 0x00080000
#endif

/* Kernel zevents (zpool events): none are posted in user mode. */
typedef struct zfs_zevent zfs_zevent_t;
void zfs_zevent_drain_all(uint_t *);
zfs_file_t *zfs_zevent_fd_hold(int, minor_t *, zfs_zevent_t **);
void zfs_zevent_fd_rele(zfs_file_t *);
int zfs_zevent_next(zfs_zevent_t *, nvlist_t **, uint64_t *, uint64_t *);
int zfs_zevent_wait(zfs_zevent_t *);
int zfs_zevent_seek(zfs_zevent_t *, uint64_t);

void zfs_zevent_init(zfs_zevent_t **);
void zfs_zevent_destroy(zfs_zevent_t *);

/* sys/zfs_onexit.h (_KERNEL part): cleanup-fd holds are not bridged. */
typedef struct zfs_onexit zfs_onexit_t;
void zfs_onexit_init(zfs_onexit_t **zo);
void zfs_onexit_destroy(zfs_onexit_t *zo);
#ifndef FKIOCTL
#define FKIOCTL 0x80000000
#endif

/* ZPL module init (zfs_kmod_init() only; the bridge does not call it). */
void zfs_init(void);
void zfs_fini(void);

int zfs_set_default_quota(zfsvfs_t *zfsvfs, zfs_prop_t zfs_prop,
    uint64_t quota);
void zfs_create_fs(struct objset *os, cred_t *cr, nvlist_t *, dmu_tx_t *tx);

/*
 * zfs_kmod_init() is the only way in to the (static) zfs_ioctl_init().  In
 * this process libzpool's kernel_init() has already run spa_init() and
 * created the rrw TSD key, so inside zfs_ioctl.c those become no-ops.
 */
#include <sys/spa.h>
#undef tsd_create
int zum_tsd_create_once(uint_t *key, void (*destructor)(void *));
#define tsd_create(kp, d) ((void) zum_tsd_create_once((kp), (d)))
#define spa_init(mode) ((void) (mode))
#define spa_fini() ((void) 0)
int zfs_kmod_init(void);


#define ZFS_TEARDOWN_ENTER_READ(z, t)   rrm_enter_read(&(z)->z_teardown_lock, t)
#define ZFS_TEARDOWN_ENTER_WRITE(z, t)  rrm_enter(&(z)->z_teardown_lock, RW_WRITER, t)
#define ZFS_TEARDOWN_EXIT(z, t)         rrm_exit(&(z)->z_teardown_lock, t)

#define ZFSCTL_INO_ROOT 0x0000FFFFFFFFFFFFULL

int zfsvfs_create(const char *name, boolean_t rd, zfsvfs_t **zfvp);
void zfsvfs_free(zfsvfs_t *zfsvfs);
int zfs_suspend_fs(zfsvfs_t *zfsvfs);
int zfs_resume_fs(zfsvfs_t *zfsvfs, struct dsl_dataset *ds);
int zfs_end_fs(zfsvfs_t *zfsvfs, struct dsl_dataset *ds);
int zfs_set_version(zfsvfs_t *zfsvfs, uint64_t newvers);
int zfs_get_temporary_prop(struct dsl_dataset *ds, zfs_prop_t zfs_prop,
    uint64_t *val, char *setpoint);
void zfsvfs_update_fromname(const char *, const char *);
void zfs_vss_snapshot_remove_by_name(const char *snapname);
void zfs_vss_pool_add(struct spa *spa);
int zfsctl_snapshot_unmount(const char *, int flags);

typedef struct zvol_state zvol_state_handle_t;
void zvol_create_minors(const char *);
void zvol_remove_minors(struct spa *, const char *, boolean_t);
void zvol_rename_minors(struct spa *, const char *, const char *, boolean_t);
int zvol_check_volsize(uint64_t, uint64_t);
int zvol_check_volblocksize(const char *, uint64_t);
int zvol_get_stats(struct objset *, struct nvlist *);
boolean_t zvol_is_zvol(const char *);
void zvol_create_cb(struct objset *, void *, cred_t *, struct dmu_tx *);
int zvol_set_volsize(const char *, uint64_t);
int zvol_set_volthreading(const char *, boolean_t);
int zvol_set_common(const char *, zfs_prop_t, zprop_source_t, uint64_t);
int zvol_set_ro(const char *, boolean_t);
int zvol_suspend(const char *, zvol_state_handle_t **);
int zvol_resume(zvol_state_handle_t *);
void *zvol_tag(zvol_state_handle_t *);
int zvol_init(void);
void zvol_fini(void);
int zvol_busy(void);

#endif
