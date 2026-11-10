// SPDX-License-Identifier: CDDL-1.0
/*
 * The ZFS ioctl interface of the user-mode engine (user mode only).
 *
 * Upstream module/zfs/zfs_ioctl.c is compiled unchanged into this process
 * (with zum_ioctl_shim.h in place of the kernel VFS headers).  This file
 * gives it the kernel entry points it expects, and runs one ioctl for the
 * bridge server in zfswin-um.exe (sys/zfs_bridge.h), so zpool.exe and
 * zfs.exe administer the pools this service has imported exactly as they
 * administer the kernel driver's.
 *
 * What the kernel VFS would contribute is answered truthfully for user
 * mode: no dataset is mounted by a kernel zfsvfs (getzfsvfs() is ESRCH, so
 * every "mounted" path in zfs_ioctl.c is skipped), and there are no zvols.
 * Mounts are the front end's, through zum_bridge_ops_t.
 */

#include <libzpool.h>
#include <sys/zfs_ioctl.h>
#include <sys/zfs_ioctl_impl.h>
#include <sys/zfs_onexit.h>
#include <sys/fm/util.h>
#include <zfs_gitrev.h>

#include "zum_impl.h"
#include "zum.h"

static const zum_bridge_ops_t *bridge_ops;

/* Kernel VFS: nothing is mounted by a kernel zfsvfs in user mode. */
int zfsvfs_create(const char *name, boolean_t rd, zfsvfs_t **zfvp)
{ (void) name; (void) rd; (void) zfvp; return (SET_ERROR(ENOTSUP)); }
int zfsvfs_create_hold(const char *name, zfsvfs_t **zfvp)
{ (void) name; (void) zfvp; return (SET_ERROR(ENOTSUP)); }
void zfsvfs_free(zfsvfs_t *zfsvfs) { (void) zfsvfs; }
int zfs_suspend_fs(zfsvfs_t *zfsvfs) { (void) zfsvfs; return (ENOTSUP); }
int zfs_resume_fs(zfsvfs_t *zfsvfs, struct dsl_dataset *ds)
{ (void) zfsvfs; (void) ds; return (ENOTSUP); }
int zfs_end_fs(zfsvfs_t *zfsvfs, struct dsl_dataset *ds)
{ (void) zfsvfs; (void) ds; return (ENOTSUP); }
int zfs_set_version(zfsvfs_t *zfsvfs, uint64_t newvers)
{ (void) zfsvfs; (void) newvers; return (ENOTSUP); }
int zfs_userspace_one(zfsvfs_t *zfsvfs, zfs_userquota_prop_t type,
    const char *domain, uint64_t rid, uint64_t *valuep)
{ (void) zfsvfs; (void) type; (void) domain; (void) rid; (void) valuep;
    return (ENOTSUP); }
int zfs_userspace_many(zfsvfs_t *zfsvfs, zfs_userquota_prop_t type,
    uint64_t *cookiep, void *vbuf, uint64_t *bufsizep, uint64_t *flags)
{ (void) zfsvfs; (void) type; (void) cookiep; (void) vbuf; (void) bufsizep;
    (void) flags; return (ENOTSUP); }
int zfs_set_userquota(zfsvfs_t *zfsvfs, zfs_userquota_prop_t type,
    const char *domain, uint64_t rid, uint64_t quota)
{ (void) zfsvfs; (void) type; (void) domain; (void) rid; (void) quota;
    return (ENOTSUP); }
int zfs_set_default_quota(zfsvfs_t *zfsvfs, zfs_prop_t zfs_prop,
    uint64_t quota)
{ (void) zfsvfs; (void) zfs_prop; (void) quota; return (ENOTSUP); }
boolean_t zfs_vfs_held(zfsvfs_t *zfsvfs) { (void) zfsvfs; return (B_FALSE); }
int zfs_vfs_ref(zfsvfs_t **zfvp) { (void) zfvp; return (SET_ERROR(ESRCH)); }
void zfs_vfs_rele(zfsvfs_t *zfsvfs) { (void) zfsvfs; }
void zfs_ioctl_update_mount_cache(const char *dsname) { (void) dsname; }
void zfs_vss_snapshot_remove_by_name(const char *snapname) { (void) snapname; }
void zfs_vss_pool_add(struct spa *spa) { (void) spa; }
void zfs_init(void) { }
void zfs_fini(void) { }
void zfs_ereport_taskq_fini(void) { }
void *zfs_file_private(zfs_file_t *fp) { (void) fp; return (NULL); }

/* zfs_max_nvlist_src_size default, as the Linux and FreeBSD modules cap it. */
uint64_t
zfs_max_nvlist_src_size_os(void)
{
    return (128ULL * 1024 * 1024);
}

/* The .zfs\snapshot\<name> view of a snapshot being destroyed or renamed. */
int
zfsctl_snapshot_unmount(const char *snapname, int flags)
{
    (void) flags;
    if (bridge_ops != NULL && bridge_ops->snapshot_unmount != NULL)
        bridge_ops->snapshot_unmount(snapname);
    return (0);
}

/* ZFS_IOC_CREATE's zfs_create_cb(): the portable layer builds the ZPL. */
void
zfs_create_fs(objset_t *os, cred_t *cr, nvlist_t *zplprops, dmu_tx_t *tx)
{
    (void) cr;
    zum_create_fs(os, zplprops, tx);
}

/* zvols are a kernel block-device feature: not offered in user mode. */
int zvol_check_volsize(uint64_t a, uint64_t b)
{ (void) a; (void) b; return (SET_ERROR(ENOTSUP)); }
int zvol_check_volblocksize(const char *n, uint64_t b)
{ (void) n; (void) b; return (SET_ERROR(ENOTSUP)); }
int zvol_get_stats(objset_t *os, nvlist_t *nv)
{ (void) os; (void) nv; return (SET_ERROR(ENOTSUP)); }
boolean_t zvol_is_zvol(const char *n) { (void) n; return (B_FALSE); }
void zvol_create_cb(objset_t *os, void *arg, cred_t *cr, dmu_tx_t *tx)
{ (void) os; (void) arg; (void) cr; (void) tx; }
int zvol_set_volsize(const char *n, uint64_t v)
{ (void) n; (void) v; return (SET_ERROR(ENOTSUP)); }
int zvol_set_volthreading(const char *n, boolean_t v)
{ (void) n; (void) v; return (0); }
int zvol_set_common(const char *n, zfs_prop_t p, zprop_source_t s, uint64_t v)
{ (void) n; (void) p; (void) s; (void) v; return (0); }
int zvol_set_ro(const char *n, boolean_t v) { (void) n; (void) v; return (0); }
int zvol_suspend(const char *n, zvol_state_handle_t **zv)
{ (void) n; *zv = NULL; return (SET_ERROR(ENOENT)); }
int zvol_resume(zvol_state_handle_t *zv) { (void) zv; return (0); }
void *zvol_tag(zvol_state_handle_t *zv) { (void) zv; return (NULL); }
int zvol_init(void) { return (0); }
void zvol_fini(void) { }
int zvol_busy(void) { return (0); }

/*
 * Kernel SPL.  The bridge server has copied the caller's buffers into this
 * process, so these are memory copies; the pipe's ACL (SYSTEM and
 * elevated Administrators) is the privilege check secpolicy_*() makes.
 */
int
ddi_copyin(const void *from, void *to, size_t len, int flags)
{
    (void) flags;
    memcpy(to, from, len);
    return (0);
}

int
ddi_copyout(const void *from, void *to, size_t len, int flags)
{
    (void) flags;
    memcpy(to, from, len);
    return (0);
}

int xcopyin(const void *from, void *to, size_t len)
{ return (ddi_copyin(from, to, len, 0)); }
int xcopyout(const void *from, void *to, size_t len)
{ return (ddi_copyout(from, to, len, 0)); }

int
copyinstr(const void *from, void *to, size_t len, size_t *done)
{
    size_t n = strnlen(from, len);

    if (n == len)
        return (SET_ERROR(ENAMETOOLONG));
    memcpy(to, from, n + 1);
    if (done != NULL)
        *done = n + 1;
    return (0);
}

int secpolicy_zfs(const cred_t *cr) { (void) cr; return (0); }
int secpolicy_sys_config(const cred_t *cr, boolean_t checkonly)
{ (void) cr; (void) checkonly; return (0); }
int secpolicy_zinject(const cred_t *cr) { (void) cr; return (0); }
int groupmember(gid_t gid, const cred_t *cr) { (void) gid; (void) cr; return (1); }
boolean_t zfs_proc_is_caller(proc_t *p) { (void) p; return (B_TRUE); }

/* zpool events: none are queued in user mode. */
void zfs_zevent_drain_all(uint_t *count) { if (count) *count = 0; }
zfs_file_t *
zfs_zevent_fd_hold(int fd, minor_t *minorp, zfs_zevent_t **ze)
{ (void) fd; *minorp = 0; *ze = NULL; return (NULL); }
void zfs_zevent_fd_rele(zfs_file_t *fp) { (void) fp; }
int zfs_zevent_next(zfs_zevent_t *ze, nvlist_t **e, uint64_t *s, uint64_t *d)
{ (void) ze; (void) e; (void) s; (void) d; return (SET_ERROR(ENOENT)); }
int zfs_zevent_wait(zfs_zevent_t *ze) { (void) ze; return (SET_ERROR(ENOENT)); }
int zfs_zevent_seek(zfs_zevent_t *ze, uint64_t eid)
{ (void) ze; (void) eid; return (SET_ERROR(ENOENT)); }
void zfs_zevent_init(zfs_zevent_t **ze) { *ze = NULL; }
void zfs_zevent_destroy(zfs_zevent_t *ze) { (void) ze; }
void zfs_onexit_init(zfs_onexit_t **zo) { *zo = NULL; }
void zfs_onexit_destroy(zfs_onexit_t *zo) { (void) zo; }

/* zfsdev: the bridge has no minor numbers or per-open state. */
int zfsdev_attach(void) { return (0); }
void zfsdev_detach(void) { }
void zfsdev_private_set_state(void *priv, zfsdev_state_t *zs)
{ (void) priv; (void) zs; }
zfsdev_state_t *zfsdev_private_get_state(void *priv)
{ (void) priv; return (NULL); }

/*
 * ZFS_IOC_MOUNT: zc_name is the dataset and zc_value "\??\X:" (a drive
 * letter; "?" for any free one) or "\??\C:\path" (a folder).
 */
static int
zfs_ioc_mount_um(zfs_cmd_t *zc)
{
    const char *where = zc->zc_value;
    uint64_t readonly = 0;

    if (bridge_ops == NULL || bridge_ops->mount == NULL)
        return (SET_ERROR(ENOTSUP));
    if (strchr(zc->zc_name, '@') != NULL)
        return (SET_ERROR(ENOTSUP));	/* snapshots: .zfs\snapshot */
    if (strncmp(where, "\\??\\", 4) == 0)
        where += 4;
    (void) dsl_prop_get_integer(zc->zc_name,
        zfs_prop_to_name(ZFS_PROP_READONLY), &readonly, NULL);
    return (bridge_ops->mount(zc->zc_name, where, readonly != 0));
}

static int
zfs_ioc_unmount_um(zfs_cmd_t *zc)
{
    if (bridge_ops == NULL || bridge_ops->unmount == NULL)
        return (SET_ERROR(ENOTSUP));
    return (bridge_ops->unmount(zc->zc_name));
}

static int
zfs_secpolicy_mount_um(zfs_cmd_t *zc, nvlist_t *innvl, cred_t *cr)
{
    (void) zc; (void) innvl; (void) cr;
    return (0);
}

void
zfs_ioctl_init_os(void)
{
    zfs_ioctl_register_legacy(ZFS_IOC_MOUNT, zfs_ioc_mount_um,
        zfs_secpolicy_mount_um, NO_NAME, B_FALSE, POOL_CHECK_NONE);
    zfs_ioctl_register_legacy(ZFS_IOC_UNMOUNT, zfs_ioc_unmount_um,
        zfs_secpolicy_mount_um, NO_NAME, B_FALSE, POOL_CHECK_NONE);
}

/*
 * Leaf vdev paths of an imported pool, ';'-separated (the pool's sources).
 * A disk's vdev_path is the display name "/dev/physicaldriveN"; the path it
 * is opened by, "#offset#length#\\?\PhysicalDriveN", is its physpath.
 */
void
zum_pool_sources(const char *pool, char *out, size_t size)
{
    vdev_t *leaves[64];
    spa_t *spa;
    const char *path;
    int count, i;

    out[0] = '\0';
    if (spa_open(pool, &spa, FTAG) != 0)
        return;
    spa_config_enter(spa, SCL_VDEV, FTAG, RW_READER);
    count = zum_vdev_leaves(spa->spa_root_vdev, leaves, 64, 0);
    for (i = 0; i < count; i++) {
        if (i > 0)
            (void) strlcat(out, ";", size);
        path = leaves[i]->vdev_path;
        if (leaves[i]->vdev_physpath != NULL &&
            leaves[i]->vdev_physpath[0] == '#')
            path = leaves[i]->vdev_physpath;
        (void) strlcat(out, path, size);
    }
    spa_config_exit(spa, SCL_VDEV, FTAG);
    spa_close(spa, FTAG);
}

/* Ioctls that pass file descriptors cannot cross into this process. */
static boolean_t
uses_descriptor(uint64_t request)
{
    switch (request) {
    case ZFS_IOC_SEND:
    case ZFS_IOC_SEND_NEW:
    case ZFS_IOC_SEND_PROGRESS:
    case ZFS_IOC_RECV:
    case ZFS_IOC_RECV_NEW:
    case ZFS_IOC_DIFF:
    case ZFS_IOC_EVENTS_NEXT:
    case ZFS_IOC_EVENTS_SEEK:
    case ZFS_IOC_NEXT_OBJ:
    case ZFS_IOC_TMP_SNAPSHOT:
        return (B_TRUE);
    default:
        return (B_FALSE);
    }
}

/*
 * The pool name an ioctl allowed history logging for, per client process:
 * the kernel keeps it in the calling thread's TSD between the command and
 * its ZFS_IOC_LOG_HISTORY, but every bridge request runs on a new thread.
 */
#define ZUM_LOG_SLOTS 64
static struct { uint32_t pid; char *pool; } log_slots[ZUM_LOG_SLOTS];
static kmutex_t log_lock;

static char *
log_take(uint32_t pid)
{
    char *pool = NULL;
    int i;

    mutex_enter(&log_lock);
    for (i = 0; i < ZUM_LOG_SLOTS; i++) {
        if (log_slots[i].pool != NULL && log_slots[i].pid == pid) {
            pool = log_slots[i].pool;
            log_slots[i].pool = NULL;
            break;
        }
    }
    mutex_exit(&log_lock);
    return (pool);
}

static void
log_put(uint32_t pid, char *pool)
{
    int i, slot = -1;

    if (pool == NULL)
        return;
    mutex_enter(&log_lock);
    for (i = 0; i < ZUM_LOG_SLOTS && slot < 0; i++)
        if (log_slots[i].pool == NULL)
            slot = i;
    if (slot < 0) {
        slot = (int)(pid % ZUM_LOG_SLOTS);	/* full: evict one */
        kmem_strfree(log_slots[slot].pool);
    }
    log_slots[slot].pid = pid;
    log_slots[slot].pool = pool;
    mutex_exit(&log_lock);
}

/* What the kernel driver reports as zfs_version (spl-windows.c). */
const char *
zum_engine_version(void)
{
    return (ZFS_META_GITREV);
}

size_t
zum_ioctl_cmd_size(void)
{
    return (sizeof (zfs_cmd_t));
}

int
zum_ioctl(uint64_t request, uint32_t client_pid, zum_ioctl_buffers_t *io)
{
    zfs_cmd_t *zc = io->cmd;
    char name[sizeof (zc->zc_name)];
    char sources[4096];
    int error, suspended = 0;

    io->dst_out = 0;
    io->history_out = 0;
    if (request >= ZFS_IOC_LAST || uses_descriptor(request))
        return (SET_ERROR(ENOTSUP));
    (void) strlcpy(name, zc->zc_name, sizeof (name));

    /* Release what the front end holds that the operation must own. */
    if (bridge_ops != NULL) {
        if ((request == ZFS_IOC_POOL_EXPORT ||
            request == ZFS_IOC_POOL_DESTROY) && bridge_ops->unmount_pool)
            bridge_ops->unmount_pool(name);
        if (request == ZFS_IOC_ROLLBACK && bridge_ops->suspend)
            suspended = bridge_ops->suspend(name);
    }

    zc->zc_nvlist_src = (uint64_t)(uintptr_t)io->src;
    zc->zc_nvlist_src_size = io->src_size;
    zc->zc_nvlist_conf = (uint64_t)(uintptr_t)io->conf;
    zc->zc_nvlist_conf_size = io->conf_size;
    zc->zc_nvlist_dst = (uint64_t)(uintptr_t)io->dst;
    zc->zc_nvlist_dst_size = io->dst_cap;
    zc->zc_history = (uint64_t)(uintptr_t)io->history;
    if (request == ZFS_IOC_POOL_GET_HISTORY)
        zc->zc_history_len = io->history_cap;

    (void) tsd_set(zfs_allow_log_key, log_take(client_pid));
    error = zfsdev_ioctl_common((uint_t)request, zc, 0);
    log_put(client_pid, tsd_get(zfs_allow_log_key));
    (void) tsd_set(zfs_allow_log_key, NULL);

    if (suspended)
        bridge_ops->resume(name);
    if (error == 0 && bridge_ops != NULL) {
        if ((request == ZFS_IOC_POOL_CREATE ||
            request == ZFS_IOC_POOL_IMPORT) && bridge_ops->pool_imported) {
            zum_pool_sources(name, sources, sizeof (sources));
            bridge_ops->pool_imported(name, sources);
        }
        if ((request == ZFS_IOC_POOL_EXPORT ||
            request == ZFS_IOC_POOL_DESTROY) && bridge_ops->pool_exported)
            bridge_ops->pool_exported(name);
    }

    /*
     * An nvlist put_nvlist() wrote (also on failure: a partly failed
     * property set reports its errors there), or a raw buffer some legacy
     * ioctls fill (error log, user space) on success.
     */
    if (io->dst != NULL && zc->zc_nvlist_dst_filled)
        io->dst_out = MIN(zc->zc_nvlist_dst_size, io->dst_cap);
    else if (io->dst != NULL && error == 0)
        io->dst_out = io->dst_cap;
    if (request == ZFS_IOC_POOL_GET_HISTORY && error == 0)
        io->history_out = MIN(zc->zc_history_len, io->history_cap);
    return (error);
}

/*
 * zfs_kmod_init(): ioctl table, zfsdev state and the allow-log TSD key.
 * Called once, after zum_engine_start().
 */
int
zum_tsd_create_once(uint_t *key, void (*destructor)(void *))
{
    extern uint_t rrw_tsd_key;

    if (key == &rrw_tsd_key)
        return (0);		/* kernel_init() created it */
    return (pthread_key_create((pthread_key_t *)key, destructor));
}

int
zum_bridge_init(const zum_bridge_ops_t *ops)
{
    bridge_ops = ops;
    mutex_init(&log_lock, NULL, MUTEX_DEFAULT, NULL);
    /* ZFS_IOC_POOL_CREATE: the root file system, as zfs_create_fs() (0025). */
    dsl_pool_create_fs_hook = zum_create_fs;
    return (zfs_kmod_init());
}
