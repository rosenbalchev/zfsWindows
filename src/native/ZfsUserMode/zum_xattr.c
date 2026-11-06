// SPDX-License-Identifier: CDDL-1.0
/*
 * Extended attributes, Windows security descriptors and alternate data
 * streams for the portable ZPL layer, stored exactly as the Windows kernel
 * driver stores them (zfs_vnops_windows_xattr.c, zfs_vnops_windows_lib.c),
 * so a pool moves between the two editions with its ACLs and streams:
 *
 * - xattrs are written as "windows:<name>"; reads try that name, then the
 *   bare name other platforms write (zpl_xattr_get());
 * - with xattr=sa an xattr goes into the SA nvlist (ZPL_DXATTR, names
 *   lower-cased on case-insensitive datasets), falling back to the xattr
 *   directory when it does not fit (_zpl_xattr_set());
 * - the security descriptor is the self-relative SD in "security.NTACL"
 *   (zfs_save_ntsecurity());
 * - stream "name" is the file "name:$DATA" in the xattr directory.
 *
 * SPL/DMU only: builds into libzpool and the kernel module alike.
 */

#include "zum_impl.h"
#include "zum.h"

#define ZUM_XATTR_PREFIX "windows:"
#define ZUM_EA_NTACL "security.NTACL"
#define ZUM_STREAM_SUFFIX ":$DATA"

/* sys/zfs_sa.h, _KERNEL-only there; same values. */
#ifndef DXATTR_MAX_ENTRY_SIZE
#define DXATTR_MAX_ENTRY_SIZE (32768)
#define DXATTR_MAX_SA_SIZE (SPA_OLD_MAXBLOCKSIZE >> 1)
#endif

static void
lower(char *s)
{
    for (; *s != '\0'; s++)
        if (*s >= 'A' && *s <= 'Z')
            *s += 'a' - 'A';
}

/* The SA nvlist key: zpl_xattr_get_sa()/set_sa() fold case-insensitive. */
static char *
sa_name(zum_context_t *context, const char *name)
{
    char *n = spa_strdup(name);

    if (context->casesensitivity == ZFS_CASE_INSENSITIVE)
        lower(n);
    return (n);
}

/* zfs_sa_get_xattr(): the SA nvlist, empty when the object has none. */
static int
sa_xattr_load(zum_context_t *context, zum_znode_t *zp, nvlist_t **nvlp)
{
    char *obj;
    int size, error;

    error = sa_size(zp->z_sa_hdl, context->sa_table[ZPL_DXATTR], &size);
    if (error == ENOENT)
        return (nvlist_alloc(nvlp, NV_UNIQUE_NAME, KM_SLEEP));
    if (error != 0)
        return (error);
    obj = vmem_alloc(size, KM_SLEEP);
    error = sa_lookup(zp->z_sa_hdl, context->sa_table[ZPL_DXATTR], obj, size);
    if (error == 0)
        error = nvlist_unpack(obj, size, nvlp, KM_SLEEP);
    vmem_free(obj, size);
    return (error);
}

/* zfs_sa_set_xattr(): store the nvlist and update ctime in one tx. */
static int
sa_xattr_store(zum_context_t *context, zum_znode_t *zp, nvlist_t *nvl)
{
    sa_bulk_attr_t bulk[2];
    timestruc_t now;
    uint64_t ctime[2];
    dmu_tx_t *tx;
    char *obj = NULL;
    size_t size;
    int count = 0, error;

    error = nvlist_size(nvl, &size, NV_ENCODE_XDR);
    if (error == 0 && size > SA_ATTR_MAX_LEN)
        error = EFBIG;
    if (error != 0)
        return (error);
    obj = vmem_alloc(size, KM_SLEEP);
    if ((error = nvlist_pack(nvl, &obj, &size, NV_ENCODE_XDR,
        KM_SLEEP)) != 0) {
        vmem_free(obj, size);
        return (error);
    }
    tx = dmu_tx_create(context->os);
    dmu_tx_hold_sa_create(tx, size);
    dmu_tx_hold_sa(tx, zp->z_sa_hdl, B_TRUE);
    error = dmu_tx_assign(tx, DMU_TX_WAIT);
    if (error != 0) {
        dmu_tx_abort(tx);
    } else {
        gethrestime(&now);
        ctime[0] = (uint64_t)now.tv_sec;
        ctime[1] = (uint64_t)now.tv_nsec;
        SA_ADD_BULK_ATTR(bulk, count, context->sa_table[ZPL_DXATTR], NULL,
            obj, size);
        SA_ADD_BULK_ATTR(bulk, count, context->sa_table[ZPL_CTIME], NULL,
            ctime, 16);
        VERIFY0(sa_bulk_update(zp->z_sa_hdl, bulk, count, tx));
        dmu_tx_commit(tx);
    }
    vmem_free(obj, size);
    return (error);
}

static int
sa_get(zum_context_t *context, zum_znode_t *zp, const char *name, void *buf,
    size_t size, size_t *len)
{
    nvlist_t *nvl = NULL;
    uchar_t *value;
    uint_t n;
    char *key = sa_name(context, name);
    int error = sa_xattr_load(context, zp, &nvl);

    if (error == 0)
        error = nvlist_lookup_byte_array(nvl, key, &value, &n);
    if (error == 0) {
        *len = n;
        if (buf != NULL && size < n)
            error = ERANGE;
        else if (buf != NULL)
            memcpy(buf, value, n);
    }
    nvlist_free(nvl);
    spa_strfree(key);
    return (error);
}

/* zpl_xattr_set_sa(); value NULL removes (ENOENT when absent). */
static int
sa_set(zum_context_t *context, zum_znode_t *zp, const char *name,
    const void *value, size_t len)
{
    nvlist_t *nvl = NULL;
    size_t used;
    char *key = sa_name(context, name);
    int error = sa_xattr_load(context, zp, &nvl);

    if (error == 0 && value == NULL) {
        error = nvlist_remove(nvl, key, DATA_TYPE_BYTE_ARRAY);
    } else if (error == 0) {
        /* 32k entries, and the SA region must keep room for the rest. */
        if (len > DXATTR_MAX_ENTRY_SIZE)
            error = EFBIG;
        else if ((error = nvlist_size(nvl, &used, NV_ENCODE_XDR)) == 0 &&
            used > DXATTR_MAX_SA_SIZE)
            error = EFBIG;
        if (error == 0)
            error = nvlist_add_byte_array(nvl, key, (uchar_t *)value,
                (uint_t)len);
    }
    if (error == 0)
        error = sa_xattr_store(context, zp, nvl);
    nvlist_free(nvl);
    spa_strfree(key);
    return (error);
}

static int
dir_get(zum_context_t *context, zum_znode_t *zp, const char *name, void *buf,
    size_t size, size_t *len)
{
    zum_znode_t *xzp = NULL, *ezp = NULL;
    uint64_t object;
    int error;

    if ((error = zum_xattrdir(context, zp, B_FALSE, &xzp)) != 0)
        return (error);
    error = zum_dir_lookup(context, xzp, name, &object);
    if (error == 0)
        error = zum_zget(context, object, &ezp);
    if (error == 0 && !S_ISREG(ezp->z_mode))
        error = ENOENT;
    if (error == 0) {
        *len = ezp->z_size;
        if (buf != NULL && size < ezp->z_size)
            error = ERANGE;
        else if (buf != NULL && ezp->z_size > 0)
            error = dmu_read(context->os, object, 0, ezp->z_size, buf,
                DMU_READ_PREFETCH);
    }
    zum_zrele(ezp);
    zum_zrele(xzp);
    return (error);
}

/* zpl_xattr_set_dir(); value NULL removes (ENOENT when absent). */
static int
dir_set(zum_context_t *context, zum_znode_t *zp, const char *name,
    const void *value, size_t len)
{
    zum_znode_t *xzp = NULL;
    uint64_t object, offset = 0;
    int error;

    error = zum_xattrdir(context, zp, value != NULL, &xzp);
    if (error != 0)
        return (error);
    if (value == NULL) {
        error = zum_remove_entry(context, xzp, name);
    } else {
        error = zum_dir_lookup(context, xzp, name, &object);
        if (error == ENOENT)
            error = zum_create_entry(context, xzp, name, B_FALSE, &object);
        if (error == 0)
            error = zum_truncate_object(context, object, 0);
        if (error == 0 && len > 0)
            error = zum_write_object(context, object, &offset, value, len);
    }
    zum_zrele(xzp);
    return (error);
}

static int
get_one(zum_context_t *context, zum_znode_t *zp, const char *name, void *buf,
    size_t size, size_t *len)
{
    int error = sa_get(context, zp, name, buf, size, len);

    if (error == ENOENT)
        error = dir_get(context, zp, name, buf, size, len);
    return (error);
}

/* _zpl_xattr_set(): SA first when xattr=sa, else (or if full) the dir. */
static int
set_one(zum_context_t *context, zum_znode_t *zp, const char *name,
    const void *value, size_t len)
{
    size_t n;
    boolean_t in_sa = sa_get(context, zp, name, NULL, 0, &n) == 0;
    boolean_t in_dir = dir_get(context, zp, name, NULL, 0, &n) == 0;
    int error;

    if (value == NULL && !in_sa && !in_dir)
        return (ENOENT);
    if (context->xattr_sa || (value == NULL && in_sa)) {
        error = sa_set(context, zp, name, value, len);
        if (error == 0) {
            if (in_dir)
                (void) dir_set(context, zp, name, NULL, 0);
            return (0);
        }
    }
    error = dir_set(context, zp, name, value, len);
    if (error == 0 && in_sa)
        (void) sa_set(context, zp, name, NULL, 0);
    return (error);
}

int
zum_xattr_get(zum_context_t *context, zum_znode_t *zp, const char *name,
    void *buf, size_t size, size_t *len)
{
    char *prefixed = kmem_asprintf("%s%s", ZUM_XATTR_PREFIX, name);
    int error = get_one(context, zp, prefixed, buf, size, len);

    kmem_strfree(prefixed);
    if (error == ENOENT)
        error = get_one(context, zp, name, buf, size, len);
    return (error);
}

/* zpl_xattr_set(): drop the bare-name copy, write the prefixed one. */
int
zum_xattr_set(zum_context_t *context, zum_znode_t *zp, const char *name,
    const void *value, size_t len)
{
    char *prefixed = kmem_asprintf("%s%s", ZUM_XATTR_PREFIX, name);
    int error;

    if (context->readonly)
        return (EROFS);
    error = set_one(context, zp, name, NULL, 0);
    if (error == 0 || error == ENOENT)
        error = set_one(context, zp, prefixed, value, len);
    kmem_strfree(prefixed);
    if (error == 0)
        zum_sync_op(context);
    return (error);
}

int
zum_fs_get_security(zum_fs_t *fs, uint64_t object, void *buf,
    uint32_t size, uint32_t *len)
{
    zum_znode_t *zp;
    size_t n = 0;
    int error;

    if ((error = zum_zget(fs, object, &zp)) != 0)
        return (error);
    error = zum_xattr_get(fs, zp, ZUM_EA_NTACL, buf, size, &n);
    zum_zrele(zp);
    *len = (uint32_t)n;
    return (error);
}

int
zum_fs_set_security(zum_fs_t *fs, uint64_t object, const void *sd,
    uint32_t len)
{
    zum_znode_t *zp;
    int error;

    if (fs->readonly)
        return (EROFS);
    if ((error = zum_zget(fs, object, &zp)) != 0)
        return (error);
    error = zum_xattr_set(fs, zp, ZUM_EA_NTACL, sd, len);
    zum_zrele(zp);
    return (error);
}

int
zum_fs_owner(zum_fs_t *fs, uint64_t object, uint64_t *uid, uint64_t *gid,
    uint64_t *parent)
{
    sa_bulk_attr_t bulk[3];
    zum_znode_t *zp;
    int count = 0, error;

    if ((error = zum_zget(fs, object, &zp)) != 0)
        return (error);
    SA_ADD_BULK_ATTR(bulk, count, fs->sa_table[ZPL_UID], NULL, uid, 8);
    SA_ADD_BULK_ATTR(bulk, count, fs->sa_table[ZPL_GID], NULL, gid, 8);
    SA_ADD_BULK_ATTR(bulk, count, fs->sa_table[ZPL_PARENT], NULL, parent, 8);
    error = sa_bulk_lookup(zp->z_sa_hdl, bulk, count);
    zum_zrele(zp);
    return (error);
}

/*
 * A stream name as the kernel driver accepts it: no separators, and not
 * one of the names it reserves for its own metadata.
 */
static int
stream_entry(const char *name, char entry[ZAP_MAXNAMELEN])
{
    static const char *reserved[] = {
        "DOSATTRIB", "EA", "reparse", "casesensitive"
    };
    size_t i;

    if (name[0] == '\0' || strchr(name, ':') != NULL ||
        strchr(name, '/') != NULL || strchr(name, '\\') != NULL)
        return (EINVAL);
    for (i = 0; i < sizeof (reserved) / sizeof (reserved[0]); i++)
        if (strcasecmp(name, reserved[i]) == 0)
            return (EINVAL);
    if (strlen(name) + sizeof (ZUM_STREAM_SUFFIX) > ZAP_MAXNAMELEN)
        return (ENAMETOOLONG);
    (void) snprintf(entry, ZAP_MAXNAMELEN, "%s%s", name, ZUM_STREAM_SUFFIX);
    return (0);
}

int
zum_fs_stream_open(zum_fs_t *fs, uint64_t object, const char *name,
    int create, uint64_t *stream)
{
    char entry[ZAP_MAXNAMELEN];
    zum_znode_t *zp = NULL, *xzp = NULL;
    int error;

    if ((error = stream_entry(name, entry)) != 0)
        return (error);
    if (create && fs->readonly)
        return (EROFS);
    if ((error = zum_zget(fs, object, &zp)) != 0)
        return (error);
    error = zum_xattrdir(fs, zp, create != 0, &xzp);
    if (error == 0) {
        error = zum_dir_lookup(fs, xzp, entry, stream);
        if (error == ENOENT && create)
            error = zum_create_entry(fs, xzp, entry, B_FALSE, stream);
    }
    zum_zrele(xzp);
    zum_zrele(zp);
    return (error);
}

int
zum_fs_stream_remove(zum_fs_t *fs, uint64_t object, const char *name)
{
    char entry[ZAP_MAXNAMELEN];
    zum_znode_t *zp = NULL, *xzp = NULL;
    int error;

    if ((error = stream_entry(name, entry)) != 0)
        return (error);
    if (fs->readonly)
        return (EROFS);
    if ((error = zum_zget(fs, object, &zp)) != 0)
        return (error);
    error = zum_xattrdir(fs, zp, B_FALSE, &xzp);
    if (error == 0)
        error = zum_remove_entry(fs, xzp, entry);
    zum_zrele(xzp);
    zum_zrele(zp);
    return (error);
}

typedef struct stream_walk {
    zum_dirent_cb_t cb;
    void *arg;
} stream_walk_t;

/* Only "<name>:$DATA" entries are streams; other xattrs stay hidden. */
static int
stream_dirent(void *arg, const char *name, const zum_attr_t *attr)
{
    stream_walk_t *w = arg;
    size_t n = strlen(name), s = strlen(ZUM_STREAM_SUFFIX);
    char base[ZAP_MAXNAMELEN], check[ZAP_MAXNAMELEN];

    if (attr->is_dir || n <= s || strcmp(name + n - s, ZUM_STREAM_SUFFIX) != 0)
        return (0);
    memcpy(base, name, n - s);
    base[n - s] = '\0';
    if (stream_entry(base, check) != 0)
        return (0);
    return (w->cb(w->arg, base, attr));
}

int
zum_fs_stream_list(zum_fs_t *fs, uint64_t object, zum_dirent_cb_t cb,
    void *arg)
{
    stream_walk_t w = { cb, arg };
    zum_znode_t *zp = NULL;
    uint64_t xattr;
    int error;

    if ((error = zum_zget(fs, object, &zp)) != 0)
        return (error);
    xattr = zp->z_xattr;
    zum_zrele(zp);
    if (xattr == 0)
        return (0);
    return (zum_fs_readdir(fs, xattr, stream_dirent, &w));
}
