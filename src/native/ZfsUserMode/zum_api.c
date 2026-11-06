// SPDX-License-Identifier: CDDL-1.0
/*
 * File-level API (zum.h) over the portable ZPL layer.  SPL and DMU only, so
 * it builds into libzpool and into the kernel module alike.  Windows
 * attribute mapping follows the kernel driver (zfs_getwinflags() and
 * zfs_setwinflags_xva() in zfs_vnops_windows_lib.c), so a file looks the
 * same through either variant.
 */

#include "zum_impl.h"
#include "zum.h"

/* Seconds between 1601-01-01 (FILETIME) and 1970-01-01 (ZFS times). */
#define ZUM_EPOCH_DELTA 11644473600ULL
#define ZUM_TICKS 10000000ULL

static uint64_t
to_filetime(const uint64_t t[2])
{
    return ((t[0] + ZUM_EPOCH_DELTA) * ZUM_TICKS + t[1] / 100);
}

static void
from_filetime(uint64_t ft, uint64_t t[2])
{
    uint64_t sec = ft / ZUM_TICKS;

    t[0] = sec > ZUM_EPOCH_DELTA ? sec - ZUM_EPOCH_DELTA : 0;
    t[1] = sec > ZUM_EPOCH_DELTA ? (ft % ZUM_TICKS) * 100 : 0;
}

/* zfs_getwinflags(). */
static uint32_t
win_attributes(uint64_t pflags, boolean_t isdir)
{
    uint32_t a = 0;

    if (pflags & ZFS_READONLY)
        a |= ZUM_FA_READONLY;
    if (pflags & ZFS_HIDDEN)
        a |= ZUM_FA_HIDDEN;
    if (pflags & ZFS_SYSTEM)
        a |= ZUM_FA_SYSTEM;
    if (pflags & ZFS_ARCHIVE)
        a |= ZUM_FA_ARCHIVE;
    if (pflags & ZFS_SPARSE)
        a |= ZUM_FA_SPARSE_FILE;
    if (pflags & ZFS_REPARSE)
        a |= ZUM_FA_REPARSE_POINT;
    if (isdir) {
        a |= ZUM_FA_DIRECTORY;
        a &= ~ZUM_FA_ARCHIVE;
    }
    return (a == 0 ? ZUM_FA_NORMAL : a);
}

/* zfs_setwinflags_xva(): only these four are the caller's to change. */
static uint64_t
apply_win_attributes(uint64_t pflags, uint32_t a)
{
    static const struct { uint64_t z; uint32_t w; } map[] = {
        { ZFS_READONLY, ZUM_FA_READONLY },
        { ZFS_HIDDEN, ZUM_FA_HIDDEN },
        { ZFS_SYSTEM, ZUM_FA_SYSTEM },
        { ZFS_ARCHIVE, ZUM_FA_ARCHIVE },
    };
    int i;

    for (i = 0; i < 4; i++) {
        if (a & map[i].w)
            pflags |= map[i].z;
        else
            pflags &= ~map[i].z;
    }
    return (pflags);
}

static int
fill_attr(zum_context_t *context, zum_znode_t *zp, zum_attr_t *attr)
{
    sa_bulk_attr_t bulk[4];
    uint64_t atime[2], mtime[2], ctime[2], crtime[2];
    uint32_t blksz;
    u_longlong_t blocks;
    int count = 0, error;

    SA_ADD_BULK_ATTR(bulk, count, context->sa_table[ZPL_ATIME], NULL,
        atime, 16);
    SA_ADD_BULK_ATTR(bulk, count, context->sa_table[ZPL_MTIME], NULL,
        mtime, 16);
    SA_ADD_BULK_ATTR(bulk, count, context->sa_table[ZPL_CTIME], NULL,
        ctime, 16);
    SA_ADD_BULK_ATTR(bulk, count, context->sa_table[ZPL_CRTIME], NULL,
        crtime, 16);
    if ((error = sa_bulk_lookup(zp->z_sa_hdl, bulk, count)) != 0)
        return (error);
    dmu_object_size_from_db(sa_get_db(zp->z_sa_hdl), &blksz, &blocks);

    attr->object = zp->z_id;
    attr->mode = zp->z_mode;
    attr->is_dir = S_ISDIR(zp->z_mode);
    /* A directory's ZPL size counts entries; Windows expects 0. */
    attr->size = attr->is_dir ? 0 : zp->z_size;
    attr->allocation = attr->is_dir ? 0 : (uint64_t)blocks << SPA_MINBLOCKSHIFT;
    attr->links = zp->z_links;
    attr->attributes = win_attributes(zp->z_pflags, attr->is_dir);
    attr->creation_time = to_filetime(crtime);
    attr->access_time = to_filetime(atime);
    attr->write_time = to_filetime(mtime);
    attr->change_time = to_filetime(ctime);
    return (0);
}

int
zum_fs_getattr(zum_fs_t *fs, uint64_t object, zum_attr_t *attr)
{
    zum_znode_t *zp;
    int error;

    if ((error = zum_zget(fs, object, &zp)) != 0)
        return (error);
    error = fill_attr(fs, zp, attr);
    zum_zrele(zp);
    return (error);
}

int
zum_fs_setattr(zum_fs_t *fs, uint64_t object, uint32_t attributes,
    uint64_t creation_time, uint64_t access_time, uint64_t write_time,
    uint64_t change_time)
{
    sa_bulk_attr_t bulk[5];
    uint64_t crtime[2], atime[2], mtime[2], ctime[2];
    zum_znode_t *zp;
    dmu_tx_t *tx;
    int count = 0, error;

    if (fs->readonly)
        return (EROFS);
    if ((error = zum_zget(fs, object, &zp)) != 0)
        return (error);
    if (attributes != UINT32_MAX) {
        zp->z_pflags = apply_win_attributes(zp->z_pflags, attributes);
        SA_ADD_BULK_ATTR(bulk, count, fs->sa_table[ZPL_FLAGS], NULL,
            &zp->z_pflags, 8);
    }
    if (creation_time != 0) {
        from_filetime(creation_time, crtime);
        SA_ADD_BULK_ATTR(bulk, count, fs->sa_table[ZPL_CRTIME], NULL,
            crtime, 16);
    }
    if (access_time != 0) {
        from_filetime(access_time, atime);
        SA_ADD_BULK_ATTR(bulk, count, fs->sa_table[ZPL_ATIME], NULL,
            atime, 16);
    }
    if (write_time != 0) {
        from_filetime(write_time, mtime);
        SA_ADD_BULK_ATTR(bulk, count, fs->sa_table[ZPL_MTIME], NULL,
            mtime, 16);
    }
    if (change_time != 0) {
        from_filetime(change_time, ctime);
        SA_ADD_BULK_ATTR(bulk, count, fs->sa_table[ZPL_CTIME], NULL,
            ctime, 16);
    }
    if (count == 0) {
        zum_zrele(zp);
        return (0);
    }
    tx = dmu_tx_create(fs->os);
    dmu_tx_hold_sa(tx, zp->z_sa_hdl, B_FALSE);
    error = dmu_tx_assign(tx, DMU_TX_WAIT);
    if (error != 0) {
        dmu_tx_abort(tx);
    } else {
        VERIFY0(sa_bulk_update(zp->z_sa_hdl, bulk, count, tx));
        dmu_tx_commit(tx);
        if (fs->sync_each_op)
            zum_sync(fs);
    }
    zum_zrele(zp);
    return (error);
}

int
zum_fs_lookup(zum_fs_t *fs, const char *path, uint64_t *object)
{
    return (zum_lookup_path(fs, path, object));
}

int
zum_fs_read(zum_fs_t *fs, uint64_t object, uint64_t offset, uint32_t length,
    void *buffer, uint32_t *done)
{
    zum_znode_t *zp;
    uint64_t n;
    int error;

    *done = 0;
    if ((error = zum_zget(fs, object, &zp)) != 0)
        return (error);
    if (S_ISDIR(zp->z_mode)) {
        zum_zrele(zp);
        return (EISDIR);
    }
    if (offset < zp->z_size) {
        n = MIN((uint64_t)length, zp->z_size - offset);
        error = dmu_read(fs->os, object, offset, n, buffer,
            DMU_READ_PREFETCH);
        if (error == 0)
            *done = (uint32_t)n;
    }
    zum_zrele(zp);
    return (error);
}

int
zum_fs_write(zum_fs_t *fs, uint64_t object, uint64_t *offset,
    const void *buffer, uint32_t length)
{
    return (zum_write_object(fs, object, offset, buffer, length));
}

int
zum_fs_truncate(zum_fs_t *fs, uint64_t object, uint64_t size)
{
    return (zum_truncate_object(fs, object, size));
}

int
zum_fs_create(zum_fs_t *fs, const char *path, int is_dir, uint64_t *object)
{
    return (zum_create_path(fs, path, is_dir != 0, object));
}

int
zum_fs_remove(zum_fs_t *fs, const char *path, int is_dir)
{
    return (zum_remove_path(fs, path, is_dir != 0, B_FALSE));
}

int
zum_fs_rename(zum_fs_t *fs, const char *from, const char *to)
{
    return (zum_rename_path(fs, from, to));
}

int
zum_fs_dir_empty(zum_fs_t *fs, uint64_t object)
{
    uint64_t count;

    return (zap_count(fs->os, object, &count) == 0 && count == 0);
}

int
zum_fs_readdir(zum_fs_t *fs, uint64_t object, zum_dirent_cb_t cb, void *arg)
{
    zap_cursor_t zc;
    zap_attribute_t *za = zap_attribute_alloc();
    zum_attr_t attr;
    zum_znode_t *zp;
    int error = 0, stop = 0;

    for (zap_cursor_init(&zc, fs->os, object);
        (error = zap_cursor_retrieve(&zc, za)) == 0;
        zap_cursor_advance(&zc)) {
        if (za->za_integer_length != 8 || za->za_num_integers != 1)
            continue;
        if (zum_zget(fs, ZFS_DIRENT_OBJ(za->za_first_integer), &zp) != 0)
            continue;	/* a dangling entry is skipped, as zfs_readdir() */
        error = fill_attr(fs, zp, &attr);
        zum_zrele(zp);
        if (error != 0)
            break;
        if ((stop = cb(arg, za->za_name, &attr)) < 0)
            break;
    }
    zap_cursor_fini(&zc);
    zap_attribute_free(za);
    if (stop != 0)
        return (stop);
    return (error == ENOENT ? 0 : error);
}

int
zum_fs_statfs(zum_fs_t *fs, zum_statfs_t *st)
{
    uint64_t refd, avail, usedobjs, availobjs;

    dmu_objset_space(fs->os, &refd, &avail, &usedobjs, &availobjs);
    st->total_bytes = refd + avail;
    st->free_bytes = avail;
    st->fsid_guid = dmu_objset_fsid_guid(fs->os);
    st->case_sensitive = fs->casesensitivity == ZFS_CASE_SENSITIVE;
    st->readonly = fs->readonly;
    return (0);
}

int
zum_fs_sync(zum_fs_t *fs)
{
    if (!fs->readonly)
        zum_sync(fs);
    return (0);
}

int
zum_fs_clone(zum_fs_t *fs, uint64_t source, uint64_t source_offset,
    uint64_t target, uint64_t target_offset, uint64_t *length)
{
    return (zum_clone_range(fs, source, source_offset, target, target_offset,
        length));
}

/* Release the dataset (for a rollback) and own it again afterwards. */
int
zum_fs_suspend(zum_fs_t *fs)
{
    zum_unmount(fs);
    return (0);
}

int
zum_fs_resume(zum_fs_t *fs)
{
    return (zum_mount(fs));
}

int
zum_fs_parent(zum_fs_t *fs, uint64_t object, uint64_t *parent)
{
    zum_znode_t *zp;
    int error;

    if ((error = zum_zget(fs, object, &zp)) != 0)
        return (error);
    error = sa_lookup(zp->z_sa_hdl, fs->sa_table[ZPL_PARENT], parent, 8);
    zum_zrele(zp);
    return (error);
}

/*
 * The mount flags libzfs reads back from the mount table: 1 read-only, 2
 * noatime.  Without them `zfs get atime` reports the mount's (temporary)
 * value, as if it had been mounted with atime.
 */
int
zum_fs_mount_flags(zum_fs_t *fs)
{
    uint64_t atime = 1, readonly = 0;

    (void) dsl_prop_get_integer(fs->dataset_name,
        zfs_prop_to_name(ZFS_PROP_ATIME), &atime, NULL);
    (void) dsl_prop_get_integer(fs->dataset_name,
        zfs_prop_to_name(ZFS_PROP_READONLY), &readonly, NULL);
    return ((fs->readonly || readonly ? 1 : 0) | (atime ? 0 : 2));
}

int
zum_fs_snapdir(zum_fs_t *fs)
{
    uint64_t value = ZFS_SNAPDIR_HIDDEN;

    (void) dsl_prop_get_integer(fs->dataset_name,
        zfs_prop_to_name(ZFS_PROP_SNAPDIR), &value, NULL);
    return ((int)value);
}

/* zfsctl_snapdir_readdir(): every snapshot of the dataset, in ZAP order. */
int
zum_fs_snapshot_list(zum_fs_t *fs, zum_snapshot_cb_t cb, void *arg)
{
    dsl_pool_t *dp = dmu_objset_pool(fs->os);
    char name[ZFS_MAX_DATASET_NAME_LEN];
    uint64_t id, offset = 0, creation;
    boolean_t conflict;
    dsl_dataset_t *ds;
    int error, stop = 0;

    for (;;) {
        dsl_pool_config_enter(dp, FTAG);
        error = dmu_snapshot_list_next(fs->os, sizeof (name), name, &id,
            &offset, &conflict);
        creation = 0;
        if (error == 0 && dsl_dataset_hold_obj(dp, id, FTAG, &ds) == 0) {
            creation = dsl_dataset_phys(ds)->ds_creation_time;
            dsl_dataset_rele(ds, FTAG);
        }
        dsl_pool_config_exit(dp, FTAG);
        if (error != 0)
            break;
        if ((stop = cb(arg, name, id,
            (creation + ZUM_EPOCH_DELTA) * ZUM_TICKS)) < 0)
            return (stop);
    }
    return (error == ENOENT ? 0 : error);
}

/* A snapshot of fs's dataset, owned read-only as zfsctl mounts it. */
int
zum_fs_snapshot_open(zum_fs_t *fs, const char *snapshot, zum_fs_t **snapfs)
{
    zum_context_t *context;
    int error;

    if (snapshot[0] == '\0' || strchr(snapshot, '@') != NULL ||
        strchr(snapshot, '/') != NULL)
        return (EINVAL);
    context = kmem_zalloc(sizeof (*context), KM_SLEEP);
    context->pool_name = spa_strdup(fs->pool_name);
    context->dataset_name = kmem_asprintf("%s@%s", fs->dataset_name,
        snapshot);
    context->readonly = B_TRUE;
    error = zum_mount(context);
    if (error != 0) {
        zum_fs_snapshot_close(context);
        return (error == EINVAL ? ENOENT : error);
    }
    *snapfs = context;
    return (0);
}

void
zum_fs_snapshot_close(zum_fs_t *snapfs)
{
    zum_unmount(snapfs);
    spa_strfree((char *)snapfs->pool_name);
    kmem_strfree((char *)snapfs->dataset_name);
    kmem_free(snapfs, sizeof (*snapfs));
}

/* errno to the NTSTATUS a Windows caller expects for the same failure. */
uint32_t
zum_fs_ntstatus(int error)
{
    switch (error) {
    case 0:		return (0x00000000);	/* STATUS_SUCCESS */
    case ENOENT:	return (0xC0000034);	/* OBJECT_NAME_NOT_FOUND */
    case EEXIST:	return (0xC0000035);	/* OBJECT_NAME_COLLISION */
    case ENOTEMPTY:	return (0xC0000101);	/* DIRECTORY_NOT_EMPTY */
    case ENOTDIR:	return (0xC0000103);	/* NOT_A_DIRECTORY */
    case EISDIR:	return (0xC00000BA);	/* FILE_IS_A_DIRECTORY */
    case ENOSPC:	return (0xC000007F);	/* DISK_FULL */
    case EFBIG:		return (0xC000007F);	/* DISK_FULL */
#ifdef EDQUOT
    case EDQUOT:	return (0xC0000044);	/* QUOTA_EXCEEDED */
#endif
    case EROFS:		return (0xC00000A2);	/* MEDIA_WRITE_PROTECTED */
    case ENAMETOOLONG:	return (0xC0000106);	/* NAME_TOO_LONG */
    case EILSEQ:	return (0xC0000033);	/* OBJECT_NAME_INVALID */
    case EINVAL:	return (0xC000000D);	/* INVALID_PARAMETER */
    case ENOTSUP:	return (0xC00000BB);	/* NOT_SUPPORTED */
    case EPERM:
    case EACCES:	return (0xC0000022);	/* ACCESS_DENIED */
    case EXDEV:		return (0xC00000D4);	/* NOT_SAME_DEVICE */
    case ENOMEM:	return (0xC000009A);	/* INSUFFICIENT_RESOURCES */
    case EBUSY:		return (0x80000011);	/* DEVICE_BUSY */
#ifdef ECKSUM
    case ECKSUM:	return (0xC0000102);	/* FILE_CORRUPT_ERROR */
#endif
    case EIO:		return (0xC0000185);	/* IO_DEVICE_ERROR */
    default:		return (0xC0000001);	/* UNSUCCESSFUL */
    }
}
