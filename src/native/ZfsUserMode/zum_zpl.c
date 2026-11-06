// SPDX-License-Identifier: CDDL-1.0
/*
 * Portable ZPL namespace and data operations (see zum_impl.h).
 *
 * Allocation, COW, space accounting, uberblock selection and TXG
 * synchronization stay in the pinned OpenZFS DMU.  The namespace operations
 * are ports of the Linux ZPL (zfs_dir.c, zfs_vnops_os.c, zfs_znode_os.c)
 * onto the DMU/ZAP/SA interfaces, because the Windows vnode sources are tied
 * to the I/O manager.  Only SPL interfaces are used (kmem, cmn_err), so this
 * file builds unchanged into libzpool and into the kernel module.
 */

#include "zum_impl.h"

static void
print_error(const char *operation, int error)
{
    cmn_err(CE_WARN, "%s: error %d", operation, error);
}

static int dir_remove(zum_context_t *, zum_znode_t *, const char *,
    dmu_tx_t *);

/* Per-operation durability, as the caller chose (zfs-write-core: always). */
void
zum_sync_op(zum_context_t *context)
{
    if (context->sync_each_op)
        txg_wait_synced(dmu_objset_pool(context->os), 0);
}

void
zum_sync(zum_context_t *context)
{
    txg_wait_synced(dmu_objset_pool(context->os), 0);
}

/* zfs_dir.h flags (kernel-only header); same meaning, local values. */
#define ZWC_NEW         0x0001
#define ZWC_RENAMING    0x0002
/* zfs_tstamp_update_setup() flags; STATE_CHANGED etc. are _KERNEL-only. */
#define ZWC_MTIME       0x0001
#define ZWC_CTIME       0x0002
#define ZWC_STATE_CHANGED       ZWC_CTIME
#define ZWC_CONTENT_MODIFIED    (ZWC_MTIME | ZWC_CTIME)

void
zum_zrele(zum_znode_t *zp)
{
    if (zp == NULL)
        return;
    if (zp->z_sa_hdl != NULL)
        sa_handle_destroy(zp->z_sa_hdl);
    kmem_free(zp, sizeof (*zp));
}

static void
znode_load_blksz(zum_znode_t *zp)
{
    u_longlong_t blocks;	/* required out-parameter; unused */

    dmu_object_size_from_db(sa_get_db(zp->z_sa_hdl), &zp->z_blksz, &blocks);
}

static int
znode_load(zum_context_t *context, zum_znode_t *zp)
{
    sa_bulk_attr_t attrs[4];
    int count = 0, error;

    SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_MODE], NULL,
        &zp->z_mode, 8);
    SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_SIZE], NULL,
        &zp->z_size, 8);
    SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_LINKS], NULL,
        &zp->z_links, 8);
    SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_FLAGS], NULL,
        &zp->z_pflags, 8);
    error = sa_bulk_lookup(zp->z_sa_hdl, attrs, count);
    zp->z_projid = ZFS_DEFAULT_PROJID;
    if (error == 0 && (zp->z_pflags & ZFS_PROJID) != 0)
        error = sa_lookup(zp->z_sa_hdl, context->sa_table[ZPL_PROJID],
            &zp->z_projid, sizeof (zp->z_projid));
    if (error == 0) {
        zp->z_xattr = 0;
        error = sa_lookup(zp->z_sa_hdl, context->sa_table[ZPL_XATTR],
            &zp->z_xattr, sizeof (zp->z_xattr));
        if (error == ENOENT)
            error = 0;
    }
    if (error == 0)
        znode_load_blksz(zp);
    return (error);
}

/* zfs_zget() for an SA-based znode. */
int
zum_zget(zum_context_t *context, uint64_t object, zum_znode_t **zpp)
{
    dmu_object_info_t info = { 0 };
    zum_znode_t *zp;
    int error;

    error = dmu_object_info(context->os, object, &info);
    if (error != 0)
        return (error);
    /* Pre-SA (DMU_OT_ZNODE) layouts are not supported for mutation. */
    if (info.doi_bonus_type != DMU_OT_SA)
        return (ENOTSUP);
    zp = kmem_zalloc(sizeof (*zp), KM_SLEEP);
    zp->z_id = object;
    error = sa_handle_get(context->os, object, NULL, SA_HDL_PRIVATE,
        &zp->z_sa_hdl);
    if (error == 0)
        error = znode_load(context, zp);
    if (error != 0) {
        zum_zrele(zp);
        return (error);
    }
    *zpp = zp;
    return (0);
}

/* zfs_tstamp_update_setup(). */
static void
tstamp_update_setup(zum_context_t *context, zum_znode_t *zp, uint_t flag,
    uint64_t mtime[2], uint64_t ctime[2])
{
    timestruc_t now;
    boolean_t use_fuids = context->zpl_version >= ZPL_VERSION_FUID;

    gethrestime(&now);
    if (flag & ZWC_MTIME) {
        mtime[0] = (uint64_t)now.tv_sec;
        mtime[1] = (uint64_t)now.tv_nsec;
        if (use_fuids)
            zp->z_pflags |= ZFS_ARCHIVE | ZFS_AV_MODIFIED;
    }
    if (flag & ZWC_CTIME) {
        ctime[0] = (uint64_t)now.tv_sec;
        ctime[1] = (uint64_t)now.tv_nsec;
        if (use_fuids)
            zp->z_pflags |= ZFS_ARCHIVE;
    }
}

static boolean_t
dirempty(zum_context_t *context, zum_znode_t *dzp)
{
    uint64_t count;

    return (zap_count(context->os, dzp->z_id, &count) == 0 && count == 0);
}

static void
unlinked_add(zum_context_t *context, zum_znode_t *zp, dmu_tx_t *tx)
{
    ASSERT(zp->z_unlinked);
    ASSERT0(zp->z_links);
    VERIFY0(zap_add_int(context->os, context->unlinked_obj, zp->z_id, tx));
}

/* zfs_link_create(). */
static int
link_create(zum_context_t *context, zum_znode_t *dzp, const char *name,
    zum_znode_t *zp, dmu_tx_t *tx, int flag)
{
    sa_bulk_attr_t bulk[5];
    uint64_t mtime[2], ctime[2], value;
    boolean_t zp_is_dir = S_ISDIR(zp->z_mode);
    int count = 0, error;

    if (!(flag & ZWC_RENAMING)) {
        if (zp->z_unlinked)
            return (ENOENT);
        if (!(flag & ZWC_NEW)) {
            zp->z_links++;
            SA_ADD_BULK_ATTR(bulk, count, context->sa_table[ZPL_LINKS],
                NULL, &zp->z_links, 8);
        }
    }

    value = zp->z_id;
    if (context->zpl_version >= ZPL_VERSION_DIRENT_TYPE)
        value |= (uint64_t)IFTODT(zp->z_mode) << 60;
    error = zap_add(context->os, dzp->z_id, name, 8, 1, &value, tx);
    if (error != 0) {
        if (!(flag & ZWC_RENAMING) && !(flag & ZWC_NEW))
            zp->z_links--;
        return (error);
    }

    SA_ADD_BULK_ATTR(bulk, count, context->sa_table[ZPL_PARENT], NULL,
        &dzp->z_id, 8);
    SA_ADD_BULK_ATTR(bulk, count, context->sa_table[ZPL_FLAGS], NULL,
        &zp->z_pflags, 8);
    if (!(flag & ZWC_NEW)) {
        SA_ADD_BULK_ATTR(bulk, count, context->sa_table[ZPL_CTIME], NULL,
            ctime, sizeof (ctime));
        tstamp_update_setup(context, zp, ZWC_STATE_CHANGED, mtime, ctime);
    }
    VERIFY0(sa_bulk_update(zp->z_sa_hdl, bulk, count, tx));

    dzp->z_size++;
    if (zp_is_dir)
        dzp->z_links++;
    count = 0;
    SA_ADD_BULK_ATTR(bulk, count, context->sa_table[ZPL_SIZE], NULL,
        &dzp->z_size, 8);
    SA_ADD_BULK_ATTR(bulk, count, context->sa_table[ZPL_LINKS], NULL,
        &dzp->z_links, 8);
    SA_ADD_BULK_ATTR(bulk, count, context->sa_table[ZPL_MTIME], NULL,
        mtime, sizeof (mtime));
    SA_ADD_BULK_ATTR(bulk, count, context->sa_table[ZPL_CTIME], NULL,
        ctime, sizeof (ctime));
    SA_ADD_BULK_ATTR(bulk, count, context->sa_table[ZPL_FLAGS], NULL,
        &dzp->z_pflags, 8);
    tstamp_update_setup(context, dzp, ZWC_CONTENT_MODIFIED, mtime, ctime);
    VERIFY0(sa_bulk_update(dzp->z_sa_hdl, bulk, count, tx));
    return (0);
}

/* zfs_drop_nlink_locked(). */
static int
drop_nlink(zum_context_t *context, zum_znode_t *zp, dmu_tx_t *tx,
    boolean_t *unlinkedp)
{
    sa_bulk_attr_t bulk[3];
    uint64_t mtime[2], ctime[2];
    uint64_t zp_is_dir = S_ISDIR(zp->z_mode) ? 1 : 0;
    boolean_t unlinked = B_FALSE;
    int count = 0;

    if (zp_is_dir && !dirempty(context, zp))
        return (ENOTEMPTY);
    if (zp->z_links <= zp_is_dir) {
        cmn_err(CE_WARN, "link count on %llu is %llu, "
            "should be at least %llu", (unsigned long long)zp->z_id,
            (unsigned long long)zp->z_links,
            (unsigned long long)(zp_is_dir + 1));
        zp->z_links = zp_is_dir + 1;
    }
    zp->z_links--;
    if (zp->z_links == zp_is_dir) {
        zp->z_unlinked = B_TRUE;
        zp->z_links = 0;
        unlinked = B_TRUE;
    } else {
        SA_ADD_BULK_ATTR(bulk, count, context->sa_table[ZPL_CTIME], NULL,
            ctime, sizeof (ctime));
        SA_ADD_BULK_ATTR(bulk, count, context->sa_table[ZPL_FLAGS], NULL,
            &zp->z_pflags, 8);
        tstamp_update_setup(context, zp, ZWC_STATE_CHANGED, mtime, ctime);
    }
    SA_ADD_BULK_ATTR(bulk, count, context->sa_table[ZPL_LINKS], NULL,
        &zp->z_links, 8);
    VERIFY0(sa_bulk_update(zp->z_sa_hdl, bulk, count, tx));
    *unlinkedp = unlinked;
    return (0);
}

/*
 * zfs_link_destroy().  With unlinkedp == NULL a last link puts the znode on
 * the unlinked set in this transaction.
 */
static int
link_destroy(zum_context_t *context, zum_znode_t *dzp, const char *name,
    zum_znode_t *zp, dmu_tx_t *tx, int flag, boolean_t *unlinkedp)
{
    sa_bulk_attr_t bulk[5];
    uint64_t mtime[2], ctime[2];
    boolean_t zp_is_dir = S_ISDIR(zp->z_mode);
    boolean_t unlinked = B_FALSE;
    int count = 0, error;

    if (!(flag & ZWC_RENAMING)) {
        if (zp_is_dir && !dirempty(context, zp))
            return (ENOTEMPTY);
        error = dir_remove(context, dzp, name, tx);
        if (error != 0)
            return (error);
        VERIFY0(drop_nlink(context, zp, tx, &unlinked));
    } else {
        error = dir_remove(context, dzp, name, tx);
        if (error != 0)
            return (error);
    }

    dzp->z_size--;
    if (zp_is_dir)
        dzp->z_links--;
    SA_ADD_BULK_ATTR(bulk, count, context->sa_table[ZPL_LINKS], NULL,
        &dzp->z_links, 8);
    SA_ADD_BULK_ATTR(bulk, count, context->sa_table[ZPL_SIZE], NULL,
        &dzp->z_size, 8);
    SA_ADD_BULK_ATTR(bulk, count, context->sa_table[ZPL_CTIME], NULL,
        ctime, sizeof (ctime));
    SA_ADD_BULK_ATTR(bulk, count, context->sa_table[ZPL_MTIME], NULL,
        mtime, sizeof (mtime));
    SA_ADD_BULK_ATTR(bulk, count, context->sa_table[ZPL_FLAGS], NULL,
        &dzp->z_pflags, 8);
    tstamp_update_setup(context, dzp, ZWC_CONTENT_MODIFIED, mtime, ctime);
    VERIFY0(sa_bulk_update(dzp->z_sa_hdl, bulk, count, tx));

    if (unlinkedp != NULL)
        *unlinkedp = unlinked;
    else if (unlinked)
        unlinked_add(context, zp, tx);
    return (0);
}

static int rmnode(zum_context_t *, zum_znode_t *);

/*
 * zfs_purgedir() for an xattr directory on the unlinked set: unlink every
 * entry (each goes to the unlinked set in its own transaction) and reclaim
 * it.  On error the directory stays on the unlinked set for the next mount.
 */
static int
purge_xattrdir(zum_context_t *context, zum_znode_t *xzp)
{
    zap_cursor_t zc;
    zap_attribute_t *za = zap_attribute_alloc();
    zum_znode_t *ezp;
    boolean_t unlinked;
    dmu_tx_t *tx;
    int error;

    for (;;) {
        /* The cursor restarts after each removal. */
        zap_cursor_init(&zc, context->os, xzp->z_id);
        error = zap_cursor_retrieve(&zc, za);
        zap_cursor_fini(&zc);
        if (error == ENOENT) {
            error = 0;
            break;
        }
        if (error != 0)
            break;
        if ((error = zum_zget(context, ZFS_DIRENT_OBJ(za->za_first_integer),
            &ezp)) != 0)
            break;
        tx = dmu_tx_create(context->os);
        dmu_tx_hold_zap(tx, xzp->z_id, FALSE, za->za_name);
        dmu_tx_hold_sa(tx, xzp->z_sa_hdl, B_FALSE);
        dmu_tx_hold_sa(tx, ezp->z_sa_hdl, B_FALSE);
        dmu_tx_hold_zap(tx, context->unlinked_obj, FALSE, NULL);
        dmu_tx_mark_netfree(tx);
        error = dmu_tx_assign(tx, DMU_TX_WAIT);
        if (error != 0) {
            dmu_tx_abort(tx);
            zum_zrele(ezp);
            break;
        }
        unlinked = B_FALSE;
        error = link_destroy(context, xzp, za->za_name, ezp, tx, 0,
            &unlinked);
        if (error == 0 && unlinked)
            unlinked_add(context, ezp, tx);
        dmu_tx_commit(tx);
        if (error == 0 && unlinked)
            error = rmnode(context, ezp);
        zum_zrele(ezp);
        if (error != 0)
            break;
    }
    zap_attribute_free(za);
    return (error);
}

/*
 * zfs_rmnode() for a znode on the unlinked set: purge an xattr directory,
 * free a regular file's data in bounded transactions, then remove it from
 * the set and free the object together, moving its own xattr directory (if
 * any) onto the set in the same transaction.  An interruption at any point
 * leaves the znodes on the unlinked set, which every OpenZFS implementation
 * drains on mount.
 */
static int
rmnode(zum_context_t *context, zum_znode_t *zp)
{
    zum_znode_t *xzp = NULL;
    uint64_t links = 0;
    dmu_tx_t *tx;
    int error;

    ASSERT(zp->z_unlinked);
    if (S_ISDIR(zp->z_mode) && (zp->z_pflags & ZFS_XATTR) != 0) {
        if ((error = purge_xattrdir(context, zp)) != 0)
            return (error);
    }
    if (S_ISREG(zp->z_mode)) {
        error = dmu_free_long_range(context->os, zp->z_id, 0,
            DMU_OBJECT_END);
        if (error != 0)
            return (error);
    }
    if (zp->z_xattr != 0 &&
        (error = zum_zget(context, zp->z_xattr, &xzp)) != 0)
        return (error);

    /*
     * Freeing the object is a net free; mark it as dmu_free_long_object()
     * does so a full pool can still reclaim space.
     */
    tx = dmu_tx_create(context->os);
    dmu_tx_hold_free(tx, zp->z_id, 0, DMU_OBJECT_END);
    dmu_tx_hold_zap(tx, context->unlinked_obj, FALSE, NULL);
    if (xzp != NULL) {
        dmu_tx_hold_zap(tx, context->unlinked_obj, TRUE, NULL);
        dmu_tx_hold_sa(tx, xzp->z_sa_hdl, B_FALSE);
    }
    dmu_tx_mark_netfree(tx);
    error = dmu_tx_assign(tx, DMU_TX_WAIT);
    if (error != 0) {
        dmu_tx_abort(tx);
        zum_zrele(xzp);
        return (error);
    }
    if (xzp != NULL) {
        xzp->z_unlinked = B_TRUE;
        xzp->z_links = 0;
        VERIFY0(sa_update(xzp->z_sa_hdl, context->sa_table[ZPL_LINKS],
            &links, sizeof (links), tx));
        unlinked_add(context, xzp, tx);
    }
    error = zap_remove_int(context->os, context->unlinked_obj, zp->z_id, tx);
    VERIFY(error == 0 || error == ENOENT);
    VERIFY0(dmu_object_free(context->os, zp->z_id, tx));
    dmu_tx_commit(tx);
    error = 0;
    if (xzp != NULL) {
        error = rmnode(context, xzp);
        zum_zrele(xzp);
    }
    return (error);
}

/*
 * Reclaim after the unlink is durable.  As with zfs_zinactive() upstream, a
 * failure is not the caller's error: the znode stays on the unlinked set
 * and the next mount (on any OpenZFS host) finishes the job.
 */
static void
reclaim(zum_context_t *context, zum_znode_t *zp)
{
    int error = rmnode(context, zp);

    if (error != 0)
        cmn_err(CE_WARN, "reclaim of object %llu deferred to the next "
            "mount: error %d", (unsigned long long)zp->z_id, error);
    zum_sync_op(context);
}

static int
valid_name(zum_context_t *context, const char *name)
{
    int error = 0;

    if (name[0] == '\0' || strchr(name, '/') != NULL ||
        strcmp(name, ".") == 0 || strcmp(name, "..") == 0)
        return (EINVAL);
    /* Longer names would activate the longname feature; not yet covered. */
    if (strlen(name) >= ZAP_MAXNAMELEN)
        return (ENAMETOOLONG);
    if (context->utf8 && u8_validate(name, strlen(name), NULL,
        U8_VALIDATE_ENTIRE, &error) < 0)
        return (EILSEQ);
    return (0);
}

static int
dir_lookup(zum_context_t *context, zum_znode_t *dzp, const char *name,
    uint64_t *object)
{
    uint64_t dirent;
    int error;

    if (context->norm != 0)
        error = zap_lookup_norm(context->os, dzp->z_id, name,
            sizeof (dirent), 1, &dirent, MT_NORMALIZE, NULL, 0, NULL);
    else
        error = zap_lookup(context->os, dzp->z_id, name, sizeof (dirent), 1,
            &dirent);
    if (error == 0)
        *object = ZFS_DIRENT_OBJ(dirent);
    return (error);
}

/* zap_remove() of a directory entry, by its normalized name if need be. */
static int
dir_remove(zum_context_t *context, zum_znode_t *dzp, const char *name,
    dmu_tx_t *tx)
{
    if (context->norm != 0)
        return (zap_remove_norm(context->os, dzp->z_id, name, MT_NORMALIZE,
            tx));
    return (zap_remove(context->os, dzp->z_id, name, tx));
}

/*
 * Resolve a '/'-separated path relative to the dataset root to its parent
 * directory and final component.  "." and ".." components are rejected.
 */
static int
resolve_parent(zum_context_t *context, const char *path, zum_znode_t **dzpp,
    char leaf[ZAP_MAXNAMELEN])
{
    zum_znode_t *dzp = NULL, *next;
    const char *start = path, *slash;
    char component[ZAP_MAXNAMELEN];
    uint64_t object;
    size_t length;
    int error;

    if ((error = zum_zget(context, context->root_obj, &dzp)) != 0)
        return (error);
    while (*start == '/')
        start++;
    for (;;) {
        slash = strchr(start, '/');
        length = slash != NULL ? (size_t)(slash - start) : strlen(start);
        if (length >= ZAP_MAXNAMELEN) {
            error = ENAMETOOLONG;
            break;
        }
        memcpy(component, start, length);
        component[length] = '\0';
        if ((error = valid_name(context, component)) != 0)
            break;
        if (slash == NULL || slash[1] == '\0') {
            (void) strcpy(leaf, component);
            *dzpp = dzp;
            return (0);
        }
        if ((error = dir_lookup(context, dzp, component, &object)) != 0)
            break;
        if ((error = zum_zget(context, object, &next)) != 0)
            break;
        zum_zrele(dzp);
        dzp = next;
        if (!S_ISDIR(dzp->z_mode)) {
            error = ENOTDIR;
            break;
        }
        start = slash + 1;
    }
    zum_zrele(dzp);
    return (error);
}

/* Resolve a path to its znode, optionally also returning the parent. */
int
zum_resolve(zum_context_t *context, const char *path, zum_znode_t **dzpp,
    char leaf[ZAP_MAXNAMELEN], zum_znode_t **zpp)
{
    zum_znode_t *dzp;
    uint64_t object;
    int error;

    if ((error = resolve_parent(context, path, &dzp, leaf)) != 0)
        return (error);
    error = dir_lookup(context, dzp, leaf, &object);
    if (error == 0)
        error = zum_zget(context, object, zpp);
    if (error != 0 || dzpp == NULL)
        zum_zrele(dzp);
    else
        *dzpp = dzp;
    return (error);
}

int
zum_lookup_path(zum_context_t *context, const char *path, uint64_t *object)
{
    char leaf[ZAP_MAXNAMELEN];
    zum_znode_t *dzp;
    const char *p = path;
    int error;

    while (*p == '/')
        p++;
    if (*p == '\0') {
        *object = context->root_obj;
        return (0);
    }
    if ((error = resolve_parent(context, path, &dzp, leaf)) != 0)
        return (error);
    error = dir_lookup(context, dzp, leaf, object);
    zum_zrele(dzp);
    return (error);
}

/* Local to zfs_acl.c upstream; repeated here with identical values. */
#define ZWC_ALLOW ACE_ACCESS_ALLOWED_ACE_TYPE
#define ZWC_OWNING_GROUP (ACE_GROUP | ACE_IDENTIFIER_GROUP)

/*
 * Port of the owner@/group@/everyone@ allow masks from
 * acl_trivial_access_masks() in module/os/linux/zfs/zfs_acl.c.  Returns
 * B_FALSE for modes whose trivial ACL also needs allow0/deny1/deny2 entries.
 */
static boolean_t
trivial_allow_masks(uint64_t mode, boolean_t isdir, uint32_t masks[3])
{
    uint64_t bits[3] = {
        (mode >> 6) & 07, (mode >> 3) & 07, mode & 07
    };
    uint32_t write_mask = ACE_WRITE_DATA | ACE_APPEND_DATA;
    int who;

    if (((bits[1] | bits[2]) & ~bits[0]) != 0 || (bits[2] & ~bits[1]) != 0)
        return (B_FALSE);
    if (isdir)
        write_mask |= ACE_DELETE_CHILD;
    for (who = 0; who < 3; who++) {
        masks[who] = ACE_READ_ACL | ACE_READ_ATTRIBUTES |
            ACE_READ_NAMED_ATTRS | ACE_SYNCHRONIZE;
        if (who == 0)
            masks[who] |= ACE_WRITE_ATTRIBUTES | ACE_WRITE_OWNER |
                ACE_WRITE_ACL | ACE_WRITE_NAMED_ATTRS;
        if (bits[who] & 04)
            masks[who] |= ACE_READ_DATA;
        if (bits[who] & 02)
            masks[who] |= write_mask;
        if (bits[who] & 01)
            masks[who] |= ACE_EXECUTE;
    }
    return (B_TRUE);
}

/* The owner@/group@/everyone@ allow entries of a trivial ACL for mode. */
static int
trivial_aces(uint64_t mode, zfs_ace_hdr_t aces[3])
{
    static const uint16_t ace_who[3] = {
        ACE_OWNER, ZWC_OWNING_GROUP, ACE_EVERYONE
    };
    uint32_t masks[3];
    int i;

    if (!trivial_allow_masks(mode, S_ISDIR(mode), masks))
        return (ENOTSUP);
    for (i = 0; i < 3; i++) {
        aces[i].z_type = ZWC_ALLOW;
        aces[i].z_flags = ace_who[i];
        aces[i].z_access_mask = masks[i];
    }
    return (0);
}

#define ZUM_MK_XATTRDIR 0x1	/* dzp's xattr directory (IS_XATTR) */
#define ZUM_MK_ROOT     0x2	/* the root directory (IS_ROOT_NODE) */

/*
 * zfs_mknode() for a regular file or directory with a trivial,
 * non-inherited ACL, producing the same SA layout and flags as the Linux
 * ZPL.  Called in an assigned transaction; failures VERIFY.  With
 * ZUM_MK_XATTRDIR, dzp is the file whose xattr directory this is; with
 * ZUM_MK_ROOT, dzp is NULL and the node is its own parent.
 */
static zum_znode_t *
mknode(zum_context_t *context, zum_znode_t *dzp, uint64_t mode,
    const zfs_ace_hdr_t aces[3], int flags, dmu_tx_t *tx)
{
    dmu_buf_t *db = NULL;
    sa_bulk_attr_t attrs[16];
    timestruc_t now;
    zum_znode_t *zp;
    uint64_t generation, uid = 0, gid = 0;
    uint64_t parent = dzp != NULL ? dzp->z_id : 0;
    uint64_t crtime[2], ctime[2], atime[2], mtime[2];
    uint64_t acl_count = 3;
    boolean_t project = dmu_objset_projectquota_enabled(context->os);
    int dnodesize = dmu_objset_dnodesize(context->os);
    int count = 0;

    if (dnodesize == 0)
        dnodesize = DNODE_MIN_SIZE;
    zp = kmem_zalloc(sizeof (*zp), KM_SLEEP);
    gethrestime(&now);
    crtime[0] = ctime[0] = atime[0] = mtime[0] = (uint64_t)now.tv_sec;
    crtime[1] = ctime[1] = atime[1] = mtime[1] = (uint64_t)now.tv_nsec;
    generation = dmu_tx_get_txg(tx);

    if (S_ISDIR(mode)) {
        zp->z_id = zap_create_norm_dnsize(context->os, context->norm,
            DMU_OT_DIRECTORY_CONTENTS, DMU_OT_SA, DN_BONUS_SIZE(dnodesize),
            dnodesize, tx);
        zp->z_size = 2;		/* contents ("." and "..") */
        zp->z_links = 2;
    } else {
        zp->z_id = dmu_object_alloc_dnsize(context->os,
            DMU_OT_PLAIN_FILE_CONTENTS, 0, DMU_OT_SA,
            DN_BONUS_SIZE(dnodesize), dnodesize, tx);
        zp->z_size = 0;
        zp->z_links = 1;
    }
    if (flags & ZUM_MK_ROOT)
        parent = zp->z_id;
    zp->z_mode = mode;
    zp->z_pflags = ZFS_ARCHIVE | ZFS_AV_MODIFIED | ZFS_ACL_TRIVIAL;
    /* An xattr directory and everything in one carry ZFS_XATTR. */
    if ((flags & ZUM_MK_XATTRDIR) ||
        (dzp != NULL && (dzp->z_pflags & ZFS_XATTR)))
        zp->z_pflags |= ZFS_XATTR;
    zp->z_projid = ZFS_DEFAULT_PROJID;
    if (project)
        zp->z_pflags |= ZFS_PROJID;
    if (dzp != NULL && (dzp->z_pflags & ZFS_PROJINHERIT)) {
        zp->z_projid = dzp->z_projid;
        zp->z_pflags |= ZFS_PROJINHERIT;
    }
    /* zfs_acl_ids_create(): a new directory's chmod-built ACL hint. */
    if (S_ISDIR(mode))
        zp->z_pflags |= ZFS_ACL_AUTO_INHERIT;
    /*
     * zfs_mode_compute() over the allow-only trivial ACL: execute is
     * denied to nobody exactly when every execute bit is set.
     */
    if ((mode & (S_IXUSR | S_IXGRP | S_IXOTH)) ==
        (S_IXUSR | S_IXGRP | S_IXOTH))
        zp->z_pflags |= ZFS_NO_EXECS_DENIED;

    VERIFY0(sa_buf_hold(context->os, zp->z_id, NULL, &db));
    VERIFY0(sa_handle_get_from_db(context->os, db, NULL, SA_HDL_SHARED,
        &zp->z_sa_hdl));

    /* Attribute order matches zfs_mknode() so the SA layout is shared. */
    SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_MODE], NULL,
        &zp->z_mode, 8);
    SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_SIZE], NULL,
        &zp->z_size, 8);
    SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_GEN], NULL,
        &generation, 8);
    SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_UID], NULL,
        &uid, 8);
    SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_GID], NULL,
        &gid, 8);
    SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_PARENT], NULL,
        &parent, 8);
    SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_FLAGS], NULL,
        &zp->z_pflags, 8);
    SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_ATIME], NULL,
        atime, 16);
    SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_MTIME], NULL,
        mtime, 16);
    SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_CTIME], NULL,
        ctime, 16);
    SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_CRTIME], NULL,
        crtime, 16);
    SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_LINKS], NULL,
        &zp->z_links, 8);
    if (project)
        SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_PROJID], NULL,
            &zp->z_projid, 8);
    SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_DACL_COUNT], NULL,
        &acl_count, 8);
    SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_DACL_ACES], NULL,
        (void *)aces, 3 * sizeof (zfs_ace_hdr_t));
    VERIFY0(sa_replace_all_by_template(zp->z_sa_hdl, attrs, count, tx));
    znode_load_blksz(zp);
    return (zp);
}

/*
 * zfs_create()/zfs_mkdir(): mknode plus zfs_link_create(ZNEW) in one
 * transaction.  Mode is 0644 for files and 0755 for directories, as for a
 * root caller with umask 022.
 */
int
zum_create_entry(zum_context_t *context, zum_znode_t *dzp, const char *name,
    boolean_t isdir, uint64_t *object_out)
{
    zfs_ace_hdr_t aces[3];
    zum_znode_t *zp;
    uint64_t mode = isdir ? (S_IFDIR | 0755) : (S_IFREG | 0644), existing;
    dmu_tx_t *tx;
    int error;

    if (context->readonly)
        return (EROFS);
    if (context->zpl_version < ZPL_VERSION_SA)
        return (ENOTSUP);
    if ((error = valid_name(context, name)) != 0)
        return (error);
    error = dir_lookup(context, dzp, name, &existing);
    if (error == 0)
        error = EEXIST;
    else if (error == ENOENT)
        error = 0;
    /* zfs_mkdir() refuses directories inside an xattr directory. */
    if (error == 0 && isdir && (dzp->z_pflags & ZFS_XATTR) != 0)
        error = EINVAL;
    /* Inheritable parent ACEs need zfs_acl_inherit(), which is not ported. */
    if (error == 0 && (dzp->z_pflags & ZFS_INHERIT_ACE) != 0)
        error = ENOTSUP;
    if (error == 0)
        error = trivial_aces(mode, aces);
    if (error != 0)
        return (error);

    tx = dmu_tx_create(context->os);
    dmu_tx_hold_zap(tx, dzp->z_id, TRUE, name);
    if (isdir)
        dmu_tx_hold_zap(tx, DMU_NEW_OBJECT, FALSE, NULL);
    dmu_tx_hold_sa_create(tx, sizeof (aces) + ZFS_SA_BASE_ATTR_SIZE);
    dmu_tx_hold_sa(tx, dzp->z_sa_hdl, B_FALSE);
    error = dmu_tx_assign(tx, DMU_TX_WAIT);
    if (error != 0) {
        dmu_tx_abort(tx);
        return (error);
    }

    /* An assigned transaction cannot be aborted; failures past here VERIFY. */
    zp = mknode(context, dzp, mode, aces, 0, tx);
    error = link_create(context, dzp, name, zp, tx, ZWC_NEW);
    if (error != 0)
        VERIFY0(dmu_object_free(context->os, zp->z_id, tx));
    dmu_tx_commit(tx);
    zum_sync_op(context);
    if (error == 0)
        *object_out = zp->z_id;
    zum_zrele(zp);
    return (error);
}

int
zum_create_path(zum_context_t *context, const char *path, boolean_t isdir,
    uint64_t *object_out)
{
    char name[ZAP_MAXNAMELEN];
    zum_znode_t *dzp = NULL;
    int error;

    if (context->readonly)
        return (EROFS);
    if ((error = resolve_parent(context, path, &dzp, name)) != 0)
        return (error);
    error = zum_create_entry(context, dzp, name, isdir, object_out);
    zum_zrele(dzp);
    return (error);
}

/*
 * zfs_get_xattrdir(): the xattr directory of zp, created (zfs_make_xattrdir,
 * mode S_IFDIR | S_ISVTX | 0777) when asked to and missing.
 */
int
zum_xattrdir(zum_context_t *context, zum_znode_t *zp, boolean_t create,
    zum_znode_t **xzpp)
{
    zfs_ace_hdr_t aces[3];
    zum_znode_t *xzp;
    dmu_tx_t *tx;
    int error;

    if (zp->z_xattr != 0)
        return (zum_zget(context, zp->z_xattr, xzpp));
    if (!create)
        return (ENOENT);
    if (context->readonly)
        return (EROFS);
    if ((zp->z_pflags & ZFS_XATTR) != 0)
        return (EINVAL);	/* no xattrs on xattrs */
    VERIFY0(trivial_aces(S_IFDIR | S_ISVTX | 0777, aces));

    tx = dmu_tx_create(context->os);
    dmu_tx_hold_sa_create(tx, sizeof (aces) + ZFS_SA_BASE_ATTR_SIZE);
    dmu_tx_hold_sa(tx, zp->z_sa_hdl, B_TRUE);
    dmu_tx_hold_zap(tx, DMU_NEW_OBJECT, FALSE, NULL);
    error = dmu_tx_assign(tx, DMU_TX_WAIT);
    if (error != 0) {
        dmu_tx_abort(tx);
        return (error);
    }
    xzp = mknode(context, zp, S_IFDIR | S_ISVTX | 0777, aces,
        ZUM_MK_XATTRDIR, tx);
    VERIFY0(sa_update(zp->z_sa_hdl, context->sa_table[ZPL_XATTR],
        &xzp->z_id, sizeof (xzp->z_id), tx));
    zp->z_xattr = xzp->z_id;
    dmu_tx_commit(tx);
    zum_sync_op(context);
    *xzpp = xzp;
    return (0);
}

/* Look up name in directory dzp (normalized as the dataset requires). */
int
zum_dir_lookup(zum_context_t *context, zum_znode_t *dzp, const char *name,
    uint64_t *object)
{
    return (dir_lookup(context, dzp, name, object));
}

/*
 * zfs_remove() of one entry of directory dzp, reclaiming its last link
 * (and its own xattr directory) after the unlink commits.
 */
int
zum_remove_entry(zum_context_t *context, zum_znode_t *dzp, const char *name)
{
    zum_znode_t *zp = NULL;
    boolean_t unlinked = B_FALSE;
    uint64_t object;
    dmu_tx_t *tx;
    int error;

    if (context->readonly)
        return (EROFS);
    if ((error = dir_lookup(context, dzp, name, &object)) != 0)
        return (error);
    if ((error = zum_zget(context, object, &zp)) != 0)
        return (error);
    if (S_ISDIR(zp->z_mode)) {
        zum_zrele(zp);
        return (EPERM);
    }
    tx = dmu_tx_create(context->os);
    dmu_tx_hold_zap(tx, dzp->z_id, FALSE, name);
    dmu_tx_hold_sa(tx, zp->z_sa_hdl, B_FALSE);
    dmu_tx_hold_sa(tx, dzp->z_sa_hdl, B_FALSE);
    dmu_tx_hold_zap(tx, context->unlinked_obj, FALSE, NULL);
    dmu_tx_mark_netfree(tx);
    error = dmu_tx_assign(tx, DMU_TX_WAIT);
    if (error != 0) {
        dmu_tx_abort(tx);
        zum_zrele(zp);
        return (error);
    }
    VERIFY0(link_destroy(context, dzp, name, zp, tx, 0, &unlinked));
    if (unlinked)
        unlinked_add(context, zp, tx);
    dmu_tx_commit(tx);
    zum_sync_op(context);
    if (unlinked)
        reclaim(context, zp);
    zum_zrele(zp);
    return (0);
}

/*
 * The parts of zfs_create_fs() that build a new filesystem: the master node
 * with the ZPL properties, the SA registration and unlinked set, and the
 * root directory (mode 0755, trivial ACL).  Called from the ioctl path's
 * zfs_create_cb() in the dataset-creating transaction.
 */
void
zum_create_fs(objset_t *os, nvlist_t *zplprops, dmu_tx_t *tx)
{
    zum_context_t context = { 0 };
    zfs_ace_hdr_t aces[3];
    zum_znode_t *rootzp;
    nvpair_t *elem = NULL;
    uint64_t moid = MASTER_NODE_OBJ, sa_obj = 0, obj, version;
    uint64_t sense = ZFS_CASE_SENSITIVE, norm = 0;

    VERIFY0(zap_create_claim(os, moid, DMU_OT_MASTER_NODE, DMU_OT_NONE, 0,
        tx));
    version = zfs_zpl_version_map(spa_version(dmu_objset_spa(os)));
    while ((elem = nvlist_next_nvpair(zplprops, elem)) != NULL) {
        uint64_t val = fnvpair_value_uint64(elem);
        const char *name = nvpair_name(elem);

        if (strcmp(name, zfs_prop_to_name(ZFS_PROP_VERSION)) == 0) {
            if (val < version)
                version = val;
        } else {
            VERIFY0(zap_update(os, moid, name, 8, 1, &val, tx));
        }
        if (strcmp(name, zfs_prop_to_name(ZFS_PROP_NORMALIZE)) == 0)
            norm = val;
        else if (strcmp(name, zfs_prop_to_name(ZFS_PROP_CASE)) == 0)
            sense = val;
    }
    VERIFY3U(version, >=, ZPL_VERSION_SA);	/* every pool since 2010 */
    VERIFY0(zap_update(os, moid, ZPL_VERSION_STR, 8, 1, &version, tx));
    sa_obj = zap_create(os, DMU_OT_SA_MASTER_NODE, DMU_OT_NONE, 0, tx);
    VERIFY0(zap_add(os, moid, ZFS_SA_ATTRS, 8, 1, &sa_obj, tx));
    obj = zap_create(os, DMU_OT_UNLINKED_SET, DMU_OT_NONE, 0, tx);
    VERIFY0(zap_add(os, moid, ZFS_UNLINKED_SET, 8, 1, &obj, tx));

    context.os = os;
    context.zpl_version = version;
    context.norm = norm;
    if (sense == ZFS_CASE_INSENSITIVE || sense == ZFS_CASE_MIXED)
        context.norm |= U8_TEXTPREP_TOUPPER;
    VERIFY0(sa_setup(os, sa_obj, zfs_attr_table, ZPL_END,
        &context.sa_table));
    VERIFY0(trivial_aces(S_IFDIR | 0755, aces));
    rootzp = mknode(&context, NULL, S_IFDIR | 0755, aces, ZUM_MK_ROOT, tx);
    VERIFY0(zap_add(os, moid, ZFS_ROOT_OBJ, 8, 1, &rootzp->z_id, tx));
    zum_zrele(rootzp);
}

/*
 * zfs_remove()/zfs_rmdir(): unlink the name and, for the last link, put the
 * znode on the unlinked set in the same transaction; then reclaim it with
 * rmnode().  defer_reclaim stops after the unlinked-set transaction, which
 * is the on-disk state of a crash before reclaim (test hook).
 */
int
zum_remove_path(zum_context_t *context, const char *path, boolean_t isdir,
    boolean_t defer_reclaim)
{
    char name[ZAP_MAXNAMELEN];
    zum_znode_t *dzp = NULL, *zp = NULL;
    boolean_t unlinked = B_FALSE;
    dmu_tx_t *tx;
    int error;

    if (context->readonly)
        return (EROFS);
    if ((error = zum_resolve(context, path, &dzp, name, &zp)) != 0)
        return (error);
    if (isdir && !S_ISDIR(zp->z_mode))
        error = ENOTDIR;
    else if (!isdir && S_ISDIR(zp->z_mode))
        error = EPERM;		/* zfs_remove(): directories need rmdir */
    else if (isdir && !dirempty(context, zp))
        error = ENOTEMPTY;
    if (error != 0) {
        zum_zrele(zp);
        zum_zrele(dzp);
        return (error);
    }

    tx = dmu_tx_create(context->os);
    dmu_tx_hold_zap(tx, dzp->z_id, FALSE, name);
    dmu_tx_hold_sa(tx, zp->z_sa_hdl, B_FALSE);
    dmu_tx_hold_sa(tx, dzp->z_sa_hdl, B_FALSE);
    dmu_tx_hold_zap(tx, context->unlinked_obj, FALSE, NULL);
    dmu_tx_mark_netfree(tx);
    error = dmu_tx_assign(tx, DMU_TX_WAIT);
    if (error != 0) {
        dmu_tx_abort(tx);
        zum_zrele(zp);
        zum_zrele(dzp);
        return (error);
    }
    VERIFY0(link_destroy(context, dzp, name, zp, tx, 0, &unlinked));
    if (unlinked)
        unlinked_add(context, zp, tx);
    dmu_tx_commit(tx);
    zum_sync_op(context);

    if (unlinked && !defer_reclaim)
        reclaim(context, zp);
    zum_zrele(zp);
    zum_zrele(dzp);
    return (error);
}

/*
 * zfs_rename() without RENAME_EXCHANGE/WHITEOUT: an existing target of the
 * same kind is replaced (an empty directory, or a file whose last link goes
 * to the unlinked set and is reclaimed after the rename commits).
 */
int
zum_rename_path(zum_context_t *context, const char *from, const char *to)
{
    char snm[ZAP_MAXNAMELEN], tnm[ZAP_MAXNAMELEN];
    zum_znode_t *sdzp = NULL, *tdzp = NULL, *szp = NULL, *tzp = NULL;
    zum_znode_t *walk = NULL;
    uint64_t object, parent;
    boolean_t t_unlinked = B_FALSE;
    dmu_tx_t *tx;
    int error;

    if (context->readonly)
        return (EROFS);
    if ((error = zum_resolve(context, from, &sdzp, snm, &szp)) != 0)
        return (error);
    error = resolve_parent(context, to, &tdzp, tnm);
    if (error == 0 && tdzp->z_id == sdzp->z_id) {
        /* One znode per object, so both roles see every update. */
        zum_zrele(tdzp);
        tdzp = sdzp;
        if (strcmp(snm, tnm) == 0)
            goto out;		/* rename to itself */
    }
    if (error == 0 && (tdzp->z_pflags & ZFS_XATTR) !=
        (sdzp->z_pflags & ZFS_XATTR))
        error = EINVAL;
    if (error == 0) {
        error = dir_lookup(context, tdzp, tnm, &object);
        if (error == 0)
            error = zum_zget(context, object, &tzp);
        else if (error == ENOENT)
            error = 0;
    }
    if (error == 0 && (tdzp->z_pflags & ZFS_PROJINHERIT) &&
        tdzp->z_projid != szp->z_projid)
        error = EXDEV;
    /* zfs_rename_lock(): a directory may not move below itself. */
    if (error == 0 && S_ISDIR(szp->z_mode) && tdzp != sdzp) {
        parent = tdzp->z_id;
        while (error == 0 && parent != context->root_obj &&
            parent != sdzp->z_id) {
            if (parent == szp->z_id) {
                error = EINVAL;
                break;
            }
            error = zum_zget(context, parent, &walk);
            if (error == 0)
                error = sa_lookup(walk->z_sa_hdl,
                    context->sa_table[ZPL_PARENT], &parent, 8);
            zum_zrele(walk);
            walk = NULL;
        }
    }
    if (error == 0 && tzp != NULL) {
        if (S_ISDIR(szp->z_mode) != S_ISDIR(tzp->z_mode))
            error = S_ISDIR(szp->z_mode) ? ENOTDIR : EISDIR;
        else if (szp->z_id == tzp->z_id && (context->norm == 0 ||
            sdzp != tdzp))
            goto out;		/* hard links to the same file */
        else if (szp->z_id == tzp->z_id) {
            zum_zrele(tzp);	/* same entry, new spelling */
            tzp = NULL;
        }
        else if (S_ISDIR(tzp->z_mode) && !dirempty(context, tzp))
            error = ENOTEMPTY;
    }
    if (error != 0)
        goto out;

    tx = dmu_tx_create(context->os);
    dmu_tx_hold_sa(tx, szp->z_sa_hdl, B_FALSE);
    dmu_tx_hold_sa(tx, sdzp->z_sa_hdl, B_FALSE);
    dmu_tx_hold_zap(tx, sdzp->z_id, FALSE, snm);
    dmu_tx_hold_zap(tx, tdzp->z_id, TRUE, tnm);
    if (sdzp != tdzp)
        dmu_tx_hold_sa(tx, tdzp->z_sa_hdl, B_FALSE);
    if (tzp != NULL)
        dmu_tx_hold_sa(tx, tzp->z_sa_hdl, B_FALSE);
    dmu_tx_hold_zap(tx, context->unlinked_obj, FALSE, NULL);
    error = dmu_tx_assign(tx, DMU_TX_WAIT);
    if (error != 0) {
        dmu_tx_abort(tx);
        goto out;
    }

    szp->z_pflags |= ZFS_AV_MODIFIED;
    if (tdzp->z_pflags & ZFS_PROJINHERIT)
        szp->z_pflags |= ZFS_PROJINHERIT;
    VERIFY0(sa_update(szp->z_sa_hdl, context->sa_table[ZPL_FLAGS],
        &szp->z_pflags, 8, tx));
    VERIFY0(link_destroy(context, sdzp, snm, szp, tx, ZWC_RENAMING, NULL));
    if (tzp != NULL)
        VERIFY0(link_destroy(context, tdzp, tnm, tzp, tx, 0, NULL));
    VERIFY0(link_create(context, tdzp, tnm, szp, tx, ZWC_RENAMING));
    t_unlinked = tzp != NULL && tzp->z_unlinked;
    dmu_tx_commit(tx);
    zum_sync_op(context);

    if (t_unlinked)
        reclaim(context, tzp);
out:
    zum_zrele(tzp);
    zum_zrele(szp);
    if (tdzp != sdzp)
        zum_zrele(tdzp);
    zum_zrele(sdzp);
    return (error);
}

/* zfs_grow_blocksize(). */
static void
grow_blocksize(zum_context_t *context, zum_znode_t *zp, uint64_t size,
    dmu_tx_t *tx)
{
    int error;

    if (size <= zp->z_blksz)
        return;
    /* A file with more than one block cannot change its block size. */
    if (zp->z_blksz && zp->z_size > zp->z_blksz)
        return;
    error = dmu_object_set_blocksize(context->os, zp->z_id, size, 0, tx);
    if (error == ENOTSUP)
        return;
    VERIFY0(error);
    znode_load_blksz(zp);
}

/* zfs_extend(). */
static int
extend_file(zum_context_t *context, zum_znode_t *zp, uint64_t end)
{
    uint64_t newblksz = 0, max_blksz = context->max_blksz;
    dmu_tx_t *tx;
    int error;

    if (end <= zp->z_size)
        return (0);
    tx = dmu_tx_create(context->os);
    dmu_tx_hold_sa(tx, zp->z_sa_hdl, B_FALSE);
    if (end > zp->z_blksz &&
        (!ISP2(zp->z_blksz) || zp->z_blksz < max_blksz)) {
        if (zp->z_blksz > max_blksz)
            newblksz = MIN(end, 1ULL << highbit64(zp->z_blksz));
        else
            newblksz = MIN(end, max_blksz);
        dmu_tx_hold_write(tx, zp->z_id, 0, (int)newblksz);
    }
    error = dmu_tx_assign(tx, DMU_TX_WAIT);
    if (error != 0) {
        dmu_tx_abort(tx);
        return (error);
    }
    if (newblksz)
        grow_blocksize(context, zp, newblksz, tx);
    zp->z_size = end;
    VERIFY0(sa_update(zp->z_sa_hdl, context->sa_table[ZPL_SIZE],
        &zp->z_size, 8, tx));
    dmu_tx_commit(tx);
    return (0);
}

/* zfs_trunc(). */
static int
trunc_file(zum_context_t *context, zum_znode_t *zp, uint64_t end)
{
    sa_bulk_attr_t bulk[2];
    dmu_tx_t *tx;
    int count = 0, error;

    if (end >= zp->z_size)
        return (0);
    error = dmu_free_long_range(context->os, zp->z_id, end, DMU_OBJECT_END);
    if (error != 0)
        return (error);
    tx = dmu_tx_create(context->os);
    dmu_tx_hold_sa(tx, zp->z_sa_hdl, B_FALSE);
    dmu_tx_mark_netfree(tx);
    error = dmu_tx_assign(tx, DMU_TX_WAIT);
    if (error != 0) {
        dmu_tx_abort(tx);
        return (error);
    }
    zp->z_size = end;
    SA_ADD_BULK_ATTR(bulk, count, context->sa_table[ZPL_SIZE], NULL,
        &zp->z_size, 8);
    if (end == 0) {
        zp->z_pflags &= ~ZFS_SPARSE;
        SA_ADD_BULK_ATTR(bulk, count, context->sa_table[ZPL_FLAGS], NULL,
            &zp->z_pflags, 8);
    }
    VERIFY0(sa_bulk_update(zp->z_sa_hdl, bulk, count, tx));
    dmu_tx_commit(tx);
    return (0);
}

/*
 * zfs_freesp(zp, size, 0, flag, B_TRUE): set the file length, then record
 * the content modification (mtime, ctime, flags) as ftruncate() does.
 */
int
zum_truncate_path(zum_context_t *context, const char *path, uint64_t size)
{
    uint64_t object;
    int error;

    if ((error = zum_lookup_path(context, path, &object)) != 0)
        return (error);
    return (zum_truncate_object(context, object, size));
}

int
zum_truncate_object(zum_context_t *context, uint64_t object, uint64_t size)
{
    sa_bulk_attr_t bulk[3];
    uint64_t mtime[2], ctime[2];
    zum_znode_t *zp = NULL;
    dmu_tx_t *tx;
    int count = 0, error;

    if (context->readonly)
        return (EROFS);
    if ((error = zum_zget(context, object, &zp)) != 0)
        return (error);
    if (!S_ISREG(zp->z_mode))
        error = S_ISDIR(zp->z_mode) ? EISDIR : EINVAL;
    else if (size > MAXOFFSET_T)
        error = EFBIG;
    if (error == 0)
        error = size > zp->z_size ? extend_file(context, zp, size) :
            trunc_file(context, zp, size);
    if (error == 0) {
        tx = dmu_tx_create(context->os);
        dmu_tx_hold_sa(tx, zp->z_sa_hdl, B_FALSE);
        error = dmu_tx_assign(tx, DMU_TX_WAIT);
        if (error != 0) {
            dmu_tx_abort(tx);
        } else {
            SA_ADD_BULK_ATTR(bulk, count, context->sa_table[ZPL_MTIME],
                NULL, mtime, 16);
            SA_ADD_BULK_ATTR(bulk, count, context->sa_table[ZPL_CTIME],
                NULL, ctime, 16);
            SA_ADD_BULK_ATTR(bulk, count, context->sa_table[ZPL_FLAGS],
                NULL, &zp->z_pflags, 8);
            tstamp_update_setup(context, zp, ZWC_CONTENT_MODIFIED, mtime,
                ctime);
            VERIFY0(sa_bulk_update(zp->z_sa_hdl, bulk, count, tx));
            dmu_tx_commit(tx);
        }
    }
    zum_sync_op(context);
    zum_zrele(zp);
    return (error);
}

/*
 * Mirrors zfs_write() for one regular file: block-size growth while the file
 * has a single block, DMU_MAX_ACCESS/2 chunking, and the size/mtime/ctime/
 * flags SA update in each chunk's transaction.  *offsetp is the offset, or
 * ZUM_APPEND for the end of file; on return it is where the data went.
 * Quota enforcement and setuid clearing are not applied.
 */
int
zum_write_object(zum_context_t *context, uint64_t object, uint64_t *offsetp,
    const void *payload, uint64_t payload_size)
{
    sa_bulk_attr_t attrs[4];
    zum_znode_t *zp = NULL;
    uint64_t max_blksz = context->max_blksz, done = 0, offset = *offsetp;
    uint64_t mtime[2], ctime[2], end_size;
    int count, error;

    if (context->readonly)
        return (EROFS);
    error = zum_zget(context, object, &zp);
    if (error == 0 && !S_ISREG(zp->z_mode))
        error = S_ISDIR(zp->z_mode) ? EISDIR : EINVAL;
    if (error == 0 && offset == ZUM_APPEND)
        offset = zp->z_size;
    if (error == 0 && (offset > MAXOFFSET_T ||
        payload_size > MAXOFFSET_T - offset))
        error = EFBIG;
    if (error != 0) {
        zum_zrele(zp);
        return (error);
    }

    end_size = MAX(zp->z_size, offset + payload_size);
    while (done < payload_size) {
        uint64_t woff = offset + done, nbytes;
        uint64_t grow_to = 0;
        dmu_tx_t *tx;

        /* zfs_write() block-size choice while the file has one block. */
        if (zp->z_size <= zp->z_blksz) {
            uint64_t target;

            if (zp->z_blksz > max_blksz && !ISP2(zp->z_blksz))
                target = 1ULL << highbit64(zp->z_blksz);
            else
                target = max_blksz;
            target = MIN(target, P2ROUNDUP(end_size, SPA_MINBLOCKSIZE));
            grow_to = MAX(target, zp->z_blksz);
        }
        nbytes = MIN(payload_size - done,
            (DMU_MAX_ACCESS >> 1) - P2PHASE(woff, (uint64_t)MAX(zp->z_blksz,
            grow_to)));

        tx = dmu_tx_create(context->os);
        dmu_tx_hold_sa(tx, zp->z_sa_hdl, B_FALSE);
        dmu_tx_hold_write(tx, zp->z_id, woff, (int)nbytes);
        error = dmu_tx_assign(tx, DMU_TX_WAIT);
        if (error != 0) {
            dmu_tx_abort(tx);
            break;
        }
        if (grow_to > zp->z_blksz)
            grow_blocksize(context, zp, grow_to, tx);
        dmu_write(context->os, zp->z_id, woff, nbytes,
            (const char *)payload + done, tx, 0);

        zp->z_size = MAX(zp->z_size, woff + nbytes);
        count = 0;
        SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_MTIME], NULL,
            mtime, 16);
        SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_CTIME], NULL,
            ctime, 16);
        SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_SIZE], NULL,
            &zp->z_size, 8);
        SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_FLAGS], NULL,
            &zp->z_pflags, 8);
        tstamp_update_setup(context, zp, ZWC_CONTENT_MODIFIED, mtime, ctime);
        VERIFY0(sa_bulk_update(zp->z_sa_hdl, attrs, count, tx));
        dmu_tx_commit(tx);
        done += nbytes;
    }

    zum_zrele(zp);
    if (error == 0) {
        zum_sync_op(context);
        *offsetp = offset;
    }
    return (error);
}

/* Block pointers per clone transaction (zil_max_log_data() upstream). */
#define ZUM_CLONE_MAXBLOCKS 1024

/*
 * zfs_clone_range() within one dataset: share the source's L0 blocks with
 * the target through the BRT instead of copying them.  The caller holds
 * the filesystem lock, which stands in for the two range locks; without a
 * ZIL here, durability is the next txg sync, as for every other write.
 * *lenp is the requested length on entry and the cloned length on return.
 */
int
zum_clone_range(zum_context_t *context, uint64_t inobj, uint64_t inoff,
    uint64_t outobj, uint64_t outoff, uint64_t *lenp)
{
    sa_bulk_attr_t bulk[4];
    zum_znode_t *inzp = NULL, *outzp = NULL;
    uint64_t len = *lenp, done = 0, size, mtime[2], ctime[2];
    uint64_t last_synced_txg;
    blkptr_t *bps = NULL;
    size_t nbps;
    uint32_t inblksz;
    boolean_t grow;
    dmu_buf_impl_t *db;
    dmu_tx_t *tx;
    int count = 0, error;

    *lenp = 0;
    if (context->readonly)
        return (EROFS);
    if (!spa_feature_is_enabled(dmu_objset_spa(context->os),
        SPA_FEATURE_BLOCK_CLONING))
        return (EOPNOTSUPP);
    if ((error = zum_zget(context, inobj, &inzp)) != 0)
        return (error);
    if ((error = zum_zget(context, outobj, &outzp)) != 0)
        goto out;
    if (!S_ISREG(inzp->z_mode) || !S_ISREG(outzp->z_mode)) {
        error = EINVAL;
        goto out;
    }
    if ((outzp->z_pflags & (ZFS_IMMUTABLE | ZFS_READONLY)) != 0) {
        error = EPERM;
        goto out;
    }
    if (inoff >= inzp->z_size)
        goto out;
    len = MIN(len, inzp->z_size - inoff);
    if (len == 0)
        goto out;
    if (inobj == outobj && inoff < outoff + len && outoff < inoff + len) {
        error = EINVAL;
        goto out;
    }

    /* zfs_rangelock_enter(): a one-block target may still change size. */
    grow = outzp->z_size <= outzp->z_blksz;
    inblksz = inzp->z_blksz;
    if (inblksz < outzp->z_blksz ||
        (inblksz != outzp->z_blksz && (outzp->z_size > outzp->z_blksz ||
        !grow)) ||
        (outoff != 0 && !ISP2(inblksz)) ||
        (inoff % inblksz) != 0 || (outoff % inblksz) != 0 ||
        ((len % inblksz) != 0 && (len < inzp->z_size - inoff ||
        len < outzp->z_size - outoff)) ||
        (len <= inblksz && inblksz < context->max_blksz &&
        outzp->z_size <= inblksz && outoff + len > inblksz)) {
        error = EINVAL;
        goto out;
    }
    if (inoff >= MAXOFFSET_T || outoff >= MAXOFFSET_T) {
        error = EFBIG;
        goto out;
    }

    SA_ADD_BULK_ATTR(bulk, count, context->sa_table[ZPL_MTIME], NULL,
        mtime, 16);
    SA_ADD_BULK_ATTR(bulk, count, context->sa_table[ZPL_CTIME], NULL,
        ctime, 16);
    SA_ADD_BULK_ATTR(bulk, count, context->sa_table[ZPL_SIZE], NULL,
        &outzp->z_size, 8);
    SA_ADD_BULK_ATTR(bulk, count, context->sa_table[ZPL_FLAGS], NULL,
        &outzp->z_pflags, 8);
    bps = vmem_alloc(sizeof (bps[0]) * ZUM_CLONE_MAXBLOCKS, KM_SLEEP);

    while (len > 0) {
        size = MIN((uint64_t)inblksz * ZUM_CLONE_MAXBLOCKS, len);
        nbps = ZUM_CLONE_MAXBLOCKS;
        last_synced_txg = spa_last_synced_txg(dmu_objset_spa(context->os));
        error = dmu_read_l0_bps(context->os, inobj, inoff, size, bps, &nbps);
        if (error == EAGAIN) {
            /* Dirty source blocks have no block pointer until synced. */
            txg_wait_synced(dmu_objset_pool(context->os),
                last_synced_txg + 1);
            continue;
        }
        if (error != 0)
            break;

        tx = dmu_tx_create(context->os);
        dmu_tx_hold_sa(tx, outzp->z_sa_hdl, B_FALSE);
        db = (dmu_buf_impl_t *)sa_get_db(outzp->z_sa_hdl);
        DB_DNODE_ENTER(db);
        dmu_tx_hold_clone_by_dnode(tx, DB_DNODE(db), outoff, size, inblksz);
        DB_DNODE_EXIT(db);
        error = dmu_tx_assign(tx, DMU_TX_WAIT);
        if (error != 0) {
            dmu_tx_abort(tx);
            break;
        }
        if (grow) {
            grow_blocksize(context, outzp, inblksz, tx);
            if (inblksz != outzp->z_blksz) {
                error = EINVAL;
                dmu_tx_commit(tx);
                break;
            }
        }
        error = dmu_brt_clone(context->os, outobj, outoff, size, tx, bps,
            nbps);
        if (error != 0) {
            dmu_tx_commit(tx);
            break;
        }
        tstamp_update_setup(context, outzp, ZWC_CONTENT_MODIFIED, mtime,
            ctime);
        outzp->z_size = MAX(outzp->z_size, outoff + size);
        error = sa_bulk_update(outzp->z_sa_hdl, bulk, count, tx);
        dmu_tx_commit(tx);
        if (error != 0)
            break;
        inoff += size;
        outoff += size;
        len -= size;
        done += size;
    }
    vmem_free(bps, sizeof (bps[0]) * ZUM_CLONE_MAXBLOCKS);
    if (done > 0) {
        error = 0;
        zum_sync_op(context);
    }
out:
    *lenp = done;
    zum_zrele(outzp);
    zum_zrele(inzp);
    return (error);
}

static int
master_lookup(zum_context_t *context, const char *name, uint64_t *value,
    boolean_t optional)
{
    int error;

    error = zap_lookup(context->os, MASTER_NODE_OBJ, name, sizeof (*value), 1,
        value);
    if (error == ENOENT && optional) {
        *value = 0;
        error = 0;
    }
    if (error != 0)
        cmn_err(CE_WARN, "read master node %s: error %d", name, error);
    return (error);
}

/*
 * zfsvfs_init() for an objset the caller has imported: own the dataset and
 * read the inputs the ported ZPL routines consult.
 */
int
zum_mount(zum_context_t *context)
{
    uint64_t sa_object = 0;
    int error;

    error = dmu_objset_own(context->dataset_name, DMU_OST_ZFS,
        context->readonly, B_TRUE, context, &context->os);
    if (error != 0) {
        print_error("own dataset", error);
        return (error);
    }

    if ((error = master_lookup(context, ZPL_VERSION_STR,
        &context->zpl_version, B_FALSE)) != 0 ||
        (error = master_lookup(context, ZFS_ROOT_OBJ, &context->root_obj,
        B_FALSE)) != 0 ||
        (error = master_lookup(context, ZFS_UNLINKED_SET,
        &context->unlinked_obj, B_FALSE)) != 0 ||
        (error = master_lookup(context, zfs_prop_to_name(ZFS_PROP_NORMALIZE),
        &context->norm, B_TRUE)) != 0 ||
        (error = master_lookup(context, zfs_prop_to_name(ZFS_PROP_CASE),
        &context->casesensitivity, B_TRUE)) != 0 ||
        (error = master_lookup(context, zfs_prop_to_name(ZFS_PROP_UTF8ONLY),
        &context->utf8, B_TRUE)) != 0 ||
        (error = master_lookup(context, ZFS_SA_ATTRS, &sa_object,
        B_TRUE)) != 0)
        return (error);
    /* zfsvfs_init(): case-insensitive and mixed datasets fold to upper. */
    if (context->casesensitivity == ZFS_CASE_INSENSITIVE ||
        context->casesensitivity == ZFS_CASE_MIXED)
        context->norm |= U8_TEXTPREP_TOUPPER;

    error = dsl_prop_get_integer(context->dataset_name,
        zfs_prop_to_name(ZFS_PROP_RECORDSIZE), &context->max_blksz, NULL);
    if (error != 0) {
        print_error("read recordsize", error);
        return (error);
    }
    {
        uint64_t xattr = ZFS_XATTR_SA;

        (void) dsl_prop_get_integer(context->dataset_name,
            zfs_prop_to_name(ZFS_PROP_XATTR), &xattr, NULL);
        context->xattr_sa = xattr == ZFS_XATTR_SA;
    }

    error = sa_setup(context->os, sa_object, zfs_attr_table, ZPL_END,
        &context->sa_table);
    if (error != 0)
        print_error("initialize SA", error);
    return (error);
}

/* Sync and release the dataset; the pool stays imported. */
void
zum_unmount(zum_context_t *context)
{
    if (context->os == NULL)
        return;
    if (!context->readonly)
        zum_sync(context);
    if (context->os->os_sa != NULL)
        sa_tear_down(context->os);
    dmu_objset_disown(context->os, B_TRUE, context);
    context->os = NULL;
}

/* Leaf vdevs whose path contains 'match' ("*" for every leaf). */
static int
collect_leaves(vdev_t *vd, const char *match, vdev_t **leaves, int max,
    int count)
{
    uint64_t c;

    if (vd->vdev_ops->vdev_op_leaf) {
        if (count < max && vd->vdev_path != NULL &&
            (strcmp(match, "*") == 0 || strstr(vd->vdev_path, match) != NULL))
            leaves[count++] = vd;
        return (count);
    }
    for (c = 0; c < vd->vdev_children; c++)
        count = collect_leaves(vd->vdev_child[c], match, leaves, max, count);
    return (count);
}

/* Every leaf vdev with a path, under the SCL_VDEV lock the caller holds. */
int
zum_vdev_leaves(vdev_t *vd, vdev_t **leaves, int max, int count)
{
    return (collect_leaves(vd, "*", leaves, max, count));
}

/*
 * Upstream fault injection (the zinject device-fault path): from now on,
 * writes to the matching leaves complete with EIO.
 */
int
zum_inject_write_errors(zum_context_t *context, const char *match)
{
    vdev_t *leaves[ZUM_MAX_INJECTIONS];
    zinject_record_t record;
    int count, i, error;

    spa_config_enter(context->spa, SCL_VDEV, FTAG, RW_READER);
    count = collect_leaves(context->spa->spa_root_vdev, match, leaves,
        ZUM_MAX_INJECTIONS - context->ninjections, 0);
    spa_config_exit(context->spa, SCL_VDEV, FTAG);
    if (count == 0)
        return (ENOENT);
    for (i = 0; i < count; i++) {
        memset(&record, 0, sizeof (record));
        record.zi_cmd = ZINJECT_DEVICE_FAULT;
        record.zi_guid = leaves[i]->vdev_guid;
        record.zi_iotype = ZINJECT_IOTYPE_WRITE;
        record.zi_error = EIO;
        record.zi_freq = 0;	/* always */
        error = zio_inject_fault((char *)context->pool_name, 0,
            &context->injections[context->ninjections], &record);
        if (error != 0)
            return (error);
        context->ninjections++;
        cmn_err(CE_NOTE, "inject-write-errors %s guid=%llu",
            leaves[i]->vdev_path, (unsigned long long)leaves[i]->vdev_guid);
    }
    return (0);
}

void
zum_clear_injections(zum_context_t *context)
{
    while (context->ninjections > 0)
        (void) zio_clear_fault(context->injections[--context->ninjections]);
}
