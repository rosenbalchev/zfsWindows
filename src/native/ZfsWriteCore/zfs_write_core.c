// SPDX-License-Identifier: CDDL-1.0
/*
 * User-mode mutation adapter for disposable ZFS pool images.
 *
 * This program intentionally delegates allocation, COW, space accounting,
 * uberblock selection and TXG synchronization to the pinned OpenZFS libzpool.
 * The namespace operations are ports of the Linux ZPL (zfs_dir.c,
 * zfs_vnops_os.c, zfs_znode_os.c) onto the DMU/ZAP/SA interfaces, because
 * the Windows vnode sources only build into the kernel driver.
 * It must never be pointed at a production pool or a pool visible to another
 * ZFS host.
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
#include <sys/dmu.h>
#include <sys/dmu_objset.h>
#include <sys/dmu_tx.h>
#include <sys/dsl_prop.h>
#include <sys/dnode.h>
#include <sys/sa.h>
#include <sys/sa_impl.h>
#include <sys/spa.h>
#include <sys/txg.h>
#include <sys/u8_textprep.h>
#include <sys/zap.h>
#include <sys/zfs_acl.h>
#include <sys/zfs_sa.h>
#include <sys/zfs_znode.h>

typedef struct write_context {
    const char *pool_name;
    const char *dataset_name;
    const char *search_path;
    spa_t *spa;
    objset_t *os;
    sa_attr_type_t *sa_table;
    uint64_t zpl_version;
    uint64_t root_obj;
    uint64_t unlinked_obj;
    uint64_t max_blksz;		/* recordsize, as zfsvfs->z_max_blksz */
    uint64_t norm;
    uint64_t casesensitivity;
    uint64_t utf8;
    boolean_t imported;
} write_context_t;

/*
 * libzpool does not include the kernel ZPL quota module, but its objset
 * accounting path still needs the same metadata decoder that zfs_quota.c
 * registers in a mounted ZFS implementation.
 */
static int
zfs_write_get_file_info(dmu_object_type_t bonustype, const void *data,
    zfs_file_info_t *info)
{
    const sa_hdr_phys_t *header;
    sa_hdr_phys_t decoded;
    uintptr_t attributes;
    uint64_t flags;
    boolean_t byteswap = B_FALSE;
    int header_size;

    if (bonustype != DMU_OT_ZNODE && bonustype != DMU_OT_SA)
        return (ENOENT);

    info->zfi_project = ZFS_DEFAULT_PROJID;
    if (data == NULL)
        return (EEXIST);

    if (bonustype == DMU_OT_ZNODE) {
        const znode_phys_t *znode = data;
        info->zfi_user = znode->zp_uid;
        info->zfi_group = znode->zp_gid;
        info->zfi_generation = znode->zp_gen;
        return (0);
    }

    header = data;
    if (header->sa_magic == 0) {
        info->zfi_user = 0;
        info->zfi_group = 0;
        info->zfi_generation = 0;
        return (0);
    }

    decoded = *header;
    if (decoded.sa_magic == BSWAP_32(SA_MAGIC)) {
        decoded.sa_magic = SA_MAGIC;
        decoded.sa_layout_info = BSWAP_16(decoded.sa_layout_info);
        byteswap = B_TRUE;
    }
    VERIFY3U(decoded.sa_magic, ==, SA_MAGIC);
    header_size = sa_hdrsize(&decoded);
    VERIFY3S(header_size, >=, sizeof (sa_hdr_phys_t));
    attributes = (uintptr_t)data + header_size;
    info->zfi_user = *((uint64_t *)(attributes + SA_UID_OFFSET));
    info->zfi_group = *((uint64_t *)(attributes + SA_GID_OFFSET));
    info->zfi_generation = *((uint64_t *)(attributes + SA_GEN_OFFSET));
    flags = *((uint64_t *)(attributes + SA_FLAGS_OFFSET));
    if (byteswap)
        flags = BSWAP_64(flags);
    if (flags & ZFS_PROJID)
        info->zfi_project = *((uint64_t *)(attributes + SA_PROJID_OFFSET));
    if (byteswap) {
        info->zfi_user = BSWAP_64(info->zfi_user);
        info->zfi_group = BSWAP_64(info->zfi_group);
        info->zfi_generation = BSWAP_64(info->zfi_generation);
        info->zfi_project = BSWAP_64(info->zfi_project);
    }
    return (0);
}

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

/*
 * Minimal in-memory znode: the subset of znode_t state that the ported ZPL
 * routines below read and maintain.  Every field is loaded from, and written
 * back to, the object's SA bonus buffer in the same transaction that changes
 * it, so no state outlives a command.
 */
typedef struct wznode {
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
} wznode_t;

/* zfs_dir.h flags (kernel-only header); same meaning, local values. */
#define ZWC_NEW         0x0001
#define ZWC_RENAMING    0x0002
/* zfs_tstamp_update_setup() flags; STATE_CHANGED etc. are _KERNEL-only. */
#define ZWC_MTIME       0x0001
#define ZWC_CTIME       0x0002
#define ZWC_STATE_CHANGED       ZWC_CTIME
#define ZWC_CONTENT_MODIFIED    (ZWC_MTIME | ZWC_CTIME)

static void
zrele(wznode_t *zp)
{
    if (zp == NULL)
        return;
    if (zp->z_sa_hdl != NULL)
        sa_handle_destroy(zp->z_sa_hdl);
    free(zp);
}

static void
znode_load_blksz(wznode_t *zp)
{
    u_longlong_t blocks;	/* required out-parameter; unused */

    dmu_object_size_from_db(sa_get_db(zp->z_sa_hdl), &zp->z_blksz, &blocks);
}

static int
znode_load(write_context_t *context, wznode_t *zp)
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
static int
zget(write_context_t *context, uint64_t object, wznode_t **zpp)
{
    dmu_object_info_t info = { 0 };
    wznode_t *zp;
    int error;

    error = dmu_object_info(context->os, object, &info);
    if (error != 0)
        return (error);
    /* Pre-SA (DMU_OT_ZNODE) layouts are not supported for mutation. */
    if (info.doi_bonus_type != DMU_OT_SA)
        return (ENOTSUP);
    zp = calloc(1, sizeof (*zp));
    if (zp == NULL)
        return (ENOMEM);
    zp->z_id = object;
    error = sa_handle_get(context->os, object, NULL, SA_HDL_PRIVATE,
        &zp->z_sa_hdl);
    if (error == 0)
        error = znode_load(context, zp);
    if (error != 0) {
        zrele(zp);
        return (error);
    }
    *zpp = zp;
    return (0);
}

/* zfs_tstamp_update_setup(). */
static void
tstamp_update_setup(write_context_t *context, wznode_t *zp, uint_t flag,
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
dirempty(write_context_t *context, wznode_t *dzp)
{
    uint64_t count;

    return (zap_count(context->os, dzp->z_id, &count) == 0 && count == 0);
}

static void
unlinked_add(write_context_t *context, wznode_t *zp, dmu_tx_t *tx)
{
    ASSERT(zp->z_unlinked);
    ASSERT0(zp->z_links);
    VERIFY0(zap_add_int(context->os, context->unlinked_obj, zp->z_id, tx));
}

/* zfs_link_create(). */
static int
link_create(write_context_t *context, wznode_t *dzp, const char *name,
    wznode_t *zp, dmu_tx_t *tx, int flag)
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
drop_nlink(write_context_t *context, wznode_t *zp, dmu_tx_t *tx,
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
        (void) fprintf(stderr, "warning: link count on %llu is %llu, "
            "should be at least %llu\n", (unsigned long long)zp->z_id,
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
link_destroy(write_context_t *context, wznode_t *dzp, const char *name,
    wznode_t *zp, dmu_tx_t *tx, int flag, boolean_t *unlinkedp)
{
    sa_bulk_attr_t bulk[5];
    uint64_t mtime[2], ctime[2];
    boolean_t zp_is_dir = S_ISDIR(zp->z_mode);
    boolean_t unlinked = B_FALSE;
    int count = 0, error;

    if (!(flag & ZWC_RENAMING)) {
        if (zp_is_dir && !dirempty(context, zp))
            return (ENOTEMPTY);
        error = zap_remove(context->os, dzp->z_id, name, tx);
        if (error != 0)
            return (error);
        VERIFY0(drop_nlink(context, zp, tx, &unlinked));
    } else {
        error = zap_remove(context->os, dzp->z_id, name, tx);
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

/*
 * zfs_rmnode() for a znode on the unlinked set: free a regular file's data
 * in bounded transactions, then remove it from the set and free the object
 * together.  An interruption at any point leaves the znode on the unlinked
 * set, which every OpenZFS implementation drains on mount.
 */
static int
rmnode(write_context_t *context, wznode_t *zp)
{
    dmu_tx_t *tx;
    int error;

    ASSERT(zp->z_unlinked);
    if (S_ISREG(zp->z_mode)) {
        error = dmu_free_long_range(context->os, zp->z_id, 0,
            DMU_OBJECT_END);
        if (error != 0)
            return (error);
    }

    tx = dmu_tx_create(context->os);
    dmu_tx_hold_free(tx, zp->z_id, 0, DMU_OBJECT_END);
    dmu_tx_hold_zap(tx, context->unlinked_obj, FALSE, NULL);
    error = dmu_tx_assign(tx, DMU_TX_WAIT);
    if (error != 0) {
        dmu_tx_abort(tx);
        return (error);
    }
    error = zap_remove_int(context->os, context->unlinked_obj, zp->z_id, tx);
    VERIFY(error == 0 || error == ENOENT);
    VERIFY0(dmu_object_free(context->os, zp->z_id, tx));
    dmu_tx_commit(tx);
    return (0);
}

static int
valid_name(write_context_t *context, const char *name)
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
dir_lookup(write_context_t *context, wznode_t *dzp, const char *name,
    uint64_t *object)
{
    uint64_t dirent;
    int error;

    error = zap_lookup(context->os, dzp->z_id, name, sizeof (dirent), 1,
        &dirent);
    if (error == 0)
        *object = ZFS_DIRENT_OBJ(dirent);
    return (error);
}

/*
 * Resolve a '/'-separated path relative to the dataset root to its parent
 * directory and final component.  "." and ".." components are rejected.
 */
static int
resolve_parent(write_context_t *context, const char *path, wznode_t **dzpp,
    char leaf[ZAP_MAXNAMELEN])
{
    wznode_t *dzp = NULL, *next;
    const char *start = path, *slash;
    char component[ZAP_MAXNAMELEN];
    uint64_t object;
    size_t length;
    int error;

    /* Mixed-case or normalized lookups need zap_*_norm(); not ported. */
    if (context->norm != 0 || context->casesensitivity != ZFS_CASE_SENSITIVE)
        return (ENOTSUP);
    if ((error = zget(context, context->root_obj, &dzp)) != 0)
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
        if ((error = zget(context, object, &next)) != 0)
            break;
        zrele(dzp);
        dzp = next;
        if (!S_ISDIR(dzp->z_mode)) {
            error = ENOTDIR;
            break;
        }
        start = slash + 1;
    }
    zrele(dzp);
    return (error);
}

/* Resolve a path to its znode, optionally also returning the parent. */
static int
resolve(write_context_t *context, const char *path, wznode_t **dzpp,
    char leaf[ZAP_MAXNAMELEN], wznode_t **zpp)
{
    wznode_t *dzp;
    uint64_t object;
    int error;

    if ((error = resolve_parent(context, path, &dzp, leaf)) != 0)
        return (error);
    error = dir_lookup(context, dzp, leaf, &object);
    if (error == 0)
        error = zget(context, object, zpp);
    if (error != 0 || dzpp == NULL)
        zrele(dzp);
    else
        *dzpp = dzp;
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

/*
 * zfs_mknode() for a regular file or directory with a trivial,
 * non-inherited ACL, producing the same SA layout and flags as the Linux
 * ZPL.  Called in an assigned transaction; failures VERIFY.
 */
static wznode_t *
mknode(write_context_t *context, wznode_t *dzp, uint64_t mode,
    const zfs_ace_hdr_t aces[3], dmu_tx_t *tx)
{
    dmu_buf_t *db = NULL;
    sa_bulk_attr_t attrs[16];
    timestruc_t now;
    wznode_t *zp;
    uint64_t generation, uid = 0, gid = 0, parent = dzp->z_id;
    uint64_t crtime[2], ctime[2], atime[2], mtime[2];
    uint64_t acl_count = 3;
    boolean_t project = dmu_objset_projectquota_enabled(context->os);
    int dnodesize = dmu_objset_dnodesize(context->os);
    int count = 0;

    if (dnodesize == 0)
        dnodesize = DNODE_MIN_SIZE;
    VERIFY3P(zp = calloc(1, sizeof (*zp)), !=, NULL);
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
    zp->z_mode = mode;
    zp->z_pflags = ZFS_ARCHIVE | ZFS_AV_MODIFIED | ZFS_ACL_TRIVIAL;
    zp->z_projid = ZFS_DEFAULT_PROJID;
    if (project)
        zp->z_pflags |= ZFS_PROJID;
    if (dzp->z_pflags & ZFS_PROJINHERIT) {
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
static int
create_node(write_context_t *context, const char *path, boolean_t isdir,
    uint64_t *object_out)
{
    static const uint16_t ace_who[3] = {
        ACE_OWNER, ZWC_OWNING_GROUP, ACE_EVERYONE
    };
    zfs_ace_hdr_t aces[3];
    uint32_t masks[3];
    char name[ZAP_MAXNAMELEN];
    wznode_t *dzp = NULL, *zp;
    uint64_t mode = isdir ? (S_IFDIR | 0755) : (S_IFREG | 0644), existing;
    dmu_tx_t *tx;
    int error, i;

    if (context->zpl_version < ZPL_VERSION_SA)
        return (ENOTSUP);
    if ((error = resolve_parent(context, path, &dzp, name)) != 0)
        return (error);
    error = dir_lookup(context, dzp, name, &existing);
    if (error == 0)
        error = EEXIST;
    else if (error == ENOENT)
        error = 0;
    /* zfs_mkdir() refuses directories inside an xattr directory. */
    if (error == 0 && (dzp->z_pflags & ZFS_XATTR) != 0)
        error = isdir ? EINVAL : ENOTSUP;
    /* Inheritable parent ACEs need zfs_acl_inherit(), which is not ported. */
    if (error == 0 && (dzp->z_pflags & ZFS_INHERIT_ACE) != 0)
        error = ENOTSUP;
    if (error == 0 && !trivial_allow_masks(mode, isdir, masks))
        error = ENOTSUP;
    if (error != 0) {
        zrele(dzp);
        return (error);
    }
    for (i = 0; i < 3; i++) {
        aces[i].z_type = ZWC_ALLOW;
        aces[i].z_flags = ace_who[i];
        aces[i].z_access_mask = masks[i];
    }

    tx = dmu_tx_create(context->os);
    dmu_tx_hold_zap(tx, dzp->z_id, TRUE, name);
    if (isdir)
        dmu_tx_hold_zap(tx, DMU_NEW_OBJECT, FALSE, NULL);
    dmu_tx_hold_sa_create(tx, sizeof (aces) + ZFS_SA_BASE_ATTR_SIZE);
    dmu_tx_hold_sa(tx, dzp->z_sa_hdl, B_FALSE);
    error = dmu_tx_assign(tx, DMU_TX_WAIT);
    if (error != 0) {
        dmu_tx_abort(tx);
        zrele(dzp);
        return (error);
    }

    /* An assigned transaction cannot be aborted; failures past here VERIFY. */
    zp = mknode(context, dzp, mode, aces, tx);
    error = link_create(context, dzp, name, zp, tx, ZWC_NEW);
    if (error != 0)
        VERIFY0(dmu_object_free(context->os, zp->z_id, tx));
    dmu_tx_commit(tx);
    txg_wait_synced(dmu_objset_pool(context->os), 0);
    if (error == 0)
        *object_out = zp->z_id;
    zrele(zp);
    zrele(dzp);
    return (error);
}

/*
 * zfs_remove()/zfs_rmdir(): unlink the name and, for the last link, put the
 * znode on the unlinked set in the same transaction; then reclaim it with
 * rmnode().  defer_reclaim stops after the unlinked-set transaction, which
 * is the on-disk state of a crash before reclaim (test hook).
 */
static int
remove_node(write_context_t *context, const char *path, boolean_t isdir,
    boolean_t defer_reclaim)
{
    char name[ZAP_MAXNAMELEN];
    wznode_t *dzp = NULL, *zp = NULL;
    boolean_t unlinked = B_FALSE;
    dmu_tx_t *tx;
    int error;

    if ((error = resolve(context, path, &dzp, name, &zp)) != 0)
        return (error);
    if (isdir && !S_ISDIR(zp->z_mode))
        error = ENOTDIR;
    else if (!isdir && S_ISDIR(zp->z_mode))
        error = EPERM;		/* zfs_remove(): directories need rmdir */
    else if (isdir && !dirempty(context, zp))
        error = ENOTEMPTY;
    else if (zp->z_xattr != 0)
        error = ENOTSUP;	/* xattr directory reclaim is not ported */
    if (error != 0) {
        zrele(zp);
        zrele(dzp);
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
        zrele(zp);
        zrele(dzp);
        return (error);
    }
    VERIFY0(link_destroy(context, dzp, name, zp, tx, 0, &unlinked));
    if (unlinked)
        unlinked_add(context, zp, tx);
    dmu_tx_commit(tx);
    txg_wait_synced(dmu_objset_pool(context->os), 0);

    if (unlinked && !defer_reclaim) {
        error = rmnode(context, zp);
        txg_wait_synced(dmu_objset_pool(context->os), 0);
    }
    zrele(zp);
    zrele(dzp);
    return (error);
}

/*
 * zfs_rename() without RENAME_EXCHANGE/WHITEOUT: an existing target of the
 * same kind is replaced (an empty directory, or a file whose last link goes
 * to the unlinked set and is reclaimed after the rename commits).
 */
static int
rename_node(write_context_t *context, const char *from, const char *to)
{
    char snm[ZAP_MAXNAMELEN], tnm[ZAP_MAXNAMELEN];
    wznode_t *sdzp = NULL, *tdzp = NULL, *szp = NULL, *tzp = NULL;
    wznode_t *walk = NULL;
    uint64_t object, parent;
    boolean_t t_unlinked = B_FALSE;
    dmu_tx_t *tx;
    int error;

    if ((error = resolve(context, from, &sdzp, snm, &szp)) != 0)
        return (error);
    error = resolve_parent(context, to, &tdzp, tnm);
    if (error == 0 && tdzp->z_id == sdzp->z_id) {
        /* One znode per object, so both roles see every update. */
        zrele(tdzp);
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
            error = zget(context, object, &tzp);
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
            error = zget(context, parent, &walk);
            if (error == 0)
                error = sa_lookup(walk->z_sa_hdl,
                    context->sa_table[ZPL_PARENT], &parent, 8);
            zrele(walk);
            walk = NULL;
        }
    }
    if (error == 0 && tzp != NULL) {
        if (S_ISDIR(szp->z_mode) != S_ISDIR(tzp->z_mode))
            error = S_ISDIR(szp->z_mode) ? ENOTDIR : EISDIR;
        else if (szp->z_id == tzp->z_id)
            goto out;		/* hard links to the same file */
        else if (S_ISDIR(tzp->z_mode) && !dirempty(context, tzp))
            error = ENOTEMPTY;
        else if (tzp->z_xattr != 0)
            error = ENOTSUP;
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
    txg_wait_synced(dmu_objset_pool(context->os), 0);

    if (t_unlinked) {
        error = rmnode(context, tzp);
        txg_wait_synced(dmu_objset_pool(context->os), 0);
    }
out:
    zrele(tzp);
    zrele(szp);
    if (tdzp != sdzp)
        zrele(tdzp);
    zrele(sdzp);
    return (error);
}

/* zfs_grow_blocksize(). */
static void
grow_blocksize(write_context_t *context, wznode_t *zp, uint64_t size,
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
extend_file(write_context_t *context, wznode_t *zp, uint64_t end)
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
trunc_file(write_context_t *context, wznode_t *zp, uint64_t end)
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
static int
truncate_file(write_context_t *context, const char *path, uint64_t size)
{
    char name[ZAP_MAXNAMELEN];
    sa_bulk_attr_t bulk[3];
    uint64_t mtime[2], ctime[2];
    wznode_t *zp = NULL;
    dmu_tx_t *tx;
    int count = 0, error;

    if ((error = resolve(context, path, NULL, name, &zp)) != 0)
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
    txg_wait_synced(dmu_objset_pool(context->os), 0);
    zrele(zp);
    return (error);
}

/*
 * Mirrors zfs_write() for one caller-owned regular file: block-size growth
 * while the file has a single block, DMU_MAX_ACCESS/2 chunking, and the
 * size/mtime/ctime/flags SA update in each chunk's transaction.  Quota
 * enforcement and setuid clearing are not applied (callers run as root).
 */
static int
write_file(write_context_t *context, const char *path, boolean_t append,
    uint64_t offset, const char *payload_path, uint64_t *written_at)
{
    char name[ZAP_MAXNAMELEN];
    sa_bulk_attr_t attrs[4];
    wznode_t *zp = NULL;
    void *payload = NULL;
    uint64_t payload_size = 0, max_blksz = context->max_blksz, done = 0;
    uint64_t mtime[2], ctime[2], end_size;
    int count, error;

    if ((error = read_payload(payload_path, &payload, &payload_size)) != 0)
        return (error);
    error = resolve(context, path, NULL, name, &zp);
    if (error == 0 && !S_ISREG(zp->z_mode))
        error = S_ISDIR(zp->z_mode) ? EISDIR : EINVAL;
    if (error == 0 && append)
        offset = zp->z_size;
    if (error == 0 && (offset > MAXOFFSET_T ||
        payload_size > MAXOFFSET_T - offset))
        error = EFBIG;
    if (error != 0) {
        zrele(zp);
        free(payload);
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
            (char *)payload + done, tx, 0);

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

    zrele(zp);
    free(payload);
    if (error == 0) {
        txg_wait_synced(dmu_objset_pool(context->os), 0);
        *written_at = offset;
    }
    return (error);
}

/* Print the persisted ZPL state of a path (used to check reopen cycles). */
static int
stat_path(write_context_t *context, const char *path)
{
    char name[ZAP_MAXNAMELEN];
    wznode_t *zp = NULL;
    int error;

    if (strcmp(path, "/") == 0 || path[0] == '\0')
        error = zget(context, context->root_obj, &zp);
    else
        error = resolve(context, path, NULL, name, &zp);
    if (error != 0)
        return (error);
    (void) printf("path=%s object=%llu mode=%llo size=%llu links=%llu "
        "blksz=%u\n", path, (unsigned long long)zp->z_id,
        (unsigned long long)zp->z_mode, (unsigned long long)zp->z_size,
        (unsigned long long)zp->z_links, zp->z_blksz);
    zrele(zp);
    return (0);
}

static int
master_lookup(write_context_t *context, const char *name, uint64_t *value,
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
        (void) fprintf(stderr, "read master node %s: %s (%d)\n", name,
            strerror(error), error);
    return (error);
}

static int
open_context(write_context_t *context)
{
    importargs_t args = { 0 };
    libpc_handle_t handle = {
        .lpc_lib_handle = NULL,
        .lpc_ops = &libzpool_config_ops,
        .lpc_printerr = B_TRUE
    };
    nvlist_t *config = NULL;
    char *paths[1];
    uint64_t sa_object = 0;
    int error;

    paths[0] = (char *)context->search_path;
    args.paths = 1;
    args.path = paths;
    args.can_be_active = B_TRUE;

    /* libzpool does not run the kernel ZFS module's zfs_init() entry point. */
    dmu_objset_register_type(DMU_OST_ZFS, zfs_write_get_file_info);
    kernel_init(SPA_MODE_READ | SPA_MODE_WRITE);

    error = zpool_find_config(&handle, context->pool_name, &config, &args);
    if (error != 0) {
        print_error("locate pool", error);
        return (error);
    }

    error = spa_import((char *)context->pool_name, config, NULL,
        ZFS_IMPORT_SKIP_MMP | ZFS_IMPORT_MISSING_LOG);
    fnvlist_free(config);
    if (error != 0 && error != EEXIST) {
        print_error("import pool", error);
        return (error);
    }
    context->imported = B_TRUE;

    error = spa_open(context->pool_name, &context->spa, context);
    if (error != 0) {
        print_error("open pool", error);
        return (error);
    }

    error = dmu_objset_own(context->dataset_name, DMU_OST_ZFS, B_FALSE,
        B_TRUE, context, &context->os);
    if (error != 0) {
        print_error("own dataset", error);
        return (error);
    }

    /* The zfsvfs_init() inputs that the ported ZPL routines consult. */
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

    error = dsl_prop_get_integer(context->dataset_name,
        zfs_prop_to_name(ZFS_PROP_RECORDSIZE), &context->max_blksz, NULL);
    if (error != 0) {
        print_error("read recordsize", error);
        return (error);
    }

    error = sa_setup(context->os, sa_object, zfs_attr_table, ZPL_END,
        &context->sa_table);
    if (error != 0)
        print_error("initialize SA", error);
    return (error);
}

static int
close_context(write_context_t *context)
{
    int error = 0;

    if (context->os != NULL) {
        txg_wait_synced(dmu_objset_pool(context->os), 0);
        if (context->os->os_sa != NULL)
            sa_tear_down(context->os);
        dmu_objset_disown(context->os, B_TRUE, context);
        context->os = NULL;
    }
    if (context->spa != NULL) {
        spa_close(context->spa, context);
        context->spa = NULL;
    }
    /*
     * Export so the image carries POOL_STATE_EXPORTED and imports on another
     * host without -f, exactly as after `zpool export`.
     */
    if (context->imported) {
        error = spa_export(context->pool_name, NULL, B_FALSE, B_FALSE);
        if (error != 0)
            print_error("export pool", error);
        context->imported = B_FALSE;
    }
    kernel_fini();
    return (error);
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
};

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
        error = create_node(context, argv[0],
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
            error = truncate_file(context, argv[0], value);
        if (error == 0)
            (void) printf("truncate %s size=%llu\n", argv[0],
                (unsigned long long)value);
        return (error);
    }
    if (strcmp(command, "remove") == 0 || strcmp(command, "rmdir") == 0) {
        error = remove_node(context, argv[0],
            strcmp(command, "rmdir") == 0, defer);
        if (error == 0)
            (void) printf("%s %s%s\n", command, argv[0],
                defer ? " (reclaim deferred)" : "");
        return (error);
    }
    if (strcmp(command, "rename") == 0) {
        error = rename_node(context, argv[0], argv[1]);
        if (error == 0)
            (void) printf("rename %s -> %s\n", argv[0], argv[1]);
        return (error);
    }
    return (EINVAL);
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

    context.search_path = argv[2];
    context.pool_name = argv[3];
    context.dataset_name = argv[4];

    error = open_context(&context);
    if (error == 0) {
        error = run_command(&context, command->name, argc - 5, argv + 5);
        if (error != 0)
            print_error(command->name, error);
    }

    (void) fflush(stdout);
    close_error = close_context(&context);
    return (error == 0 && close_error == 0 ? 0 : 1);
}
