// SPDX-License-Identifier: CDDL-1.0
/*
 * User-mode mutation adapter for disposable ZFS pool images.
 *
 * This program intentionally delegates allocation, COW, space accounting,
 * uberblock selection and TXG synchronization to the pinned OpenZFS libzpool.
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

static int
lookup_root_entry(write_context_t *context, const char *name,
    uint64_t *root_out, uint64_t *object_out)
{
    uint64_t root = 0, dirent = 0;
    int error;

    error = zap_lookup(context->os, MASTER_NODE_OBJ, ZFS_ROOT_OBJ,
        sizeof (root), 1, &root);
    if (error == 0)
        error = zap_lookup(context->os, root, name, sizeof (dirent), 1,
            &dirent);
    if (error != 0)
        return (error);
    *root_out = root;
    *object_out = ZFS_DIRENT_OBJ(dirent);
    return (0);
}

/*
 * Mirrors zfs_write() for one caller-owned regular file: block-size growth
 * while the file has a single block, DMU_MAX_ACCESS/2 chunking, and the
 * size/mtime/ctime/flags SA update in each chunk's transaction.  Quota
 * enforcement and setuid clearing are not applied (callers run as root).
 */
static int
write_file(write_context_t *context, const char *name, boolean_t append,
    uint64_t offset, const char *payload_path, uint64_t *written_at)
{
    dmu_object_info_t info = { 0 };
    sa_handle_t *handle = NULL;
    sa_bulk_attr_t attrs[4];
    timestruc_t now;
    void *payload = NULL;
    uint64_t root, object, payload_size = 0, file_size = 0, pflags = 0;
    uint64_t max_blksz = SPA_OLD_MAXBLOCKSIZE, mode = 0, done = 0;
    uint64_t mtime[2], ctime[2], end_size;
    u_longlong_t blocks;	/* required out-parameter; unused */
    uint32_t blksz;
    int count, error;

    if ((error = read_payload(payload_path, &payload, &payload_size)) != 0)
        return (error);
    error = lookup_root_entry(context, name, &root, &object);
    if (error == 0)
        error = dmu_object_info(context->os, object, &info);
    if (error == 0 && info.doi_bonus_type != DMU_OT_SA)
        error = ENOTSUP;
    if (error == 0)
        error = sa_handle_get(context->os, object, NULL, SA_HDL_PRIVATE,
            &handle);
    if (error == 0)
        error = sa_lookup(handle, context->sa_table[ZPL_MODE], &mode, 8);
    if (error == 0 && !S_ISREG(mode))
        error = EISDIR;
    if (error == 0)
        error = sa_lookup(handle, context->sa_table[ZPL_SIZE], &file_size,
            8);
    if (error == 0)
        error = sa_lookup(handle, context->sa_table[ZPL_FLAGS], &pflags, 8);
    if (error == 0)
        error = dsl_prop_get_integer(context->dataset_name,
            zfs_prop_to_name(ZFS_PROP_RECORDSIZE), &max_blksz, NULL);
    if (error == 0 && append)
        offset = file_size;
    if (error == 0 && (offset > MAXOFFSET_T ||
        payload_size > MAXOFFSET_T - offset))
        error = EFBIG;
    if (error != 0) {
        if (handle != NULL)
            sa_handle_destroy(handle);
        free(payload);
        return (error);
    }

    end_size = MAX(file_size, offset + payload_size);
    while (done < payload_size) {
        uint64_t woff = offset + done, nbytes;
        uint64_t grow_to = 0;
        dmu_tx_t *tx;

        dmu_object_size_from_db(sa_get_db(handle), &blksz, &blocks);
        /* zfs_write() block-size choice while the file has one block. */
        if (file_size <= blksz) {
            uint64_t target;

            if (blksz > max_blksz && !ISP2(blksz))
                target = 1ULL << highbit64(blksz);
            else
                target = max_blksz;
            target = MIN(target, P2ROUNDUP(end_size, SPA_MINBLOCKSIZE));
            grow_to = MAX(target, blksz);
        }
        nbytes = MIN(payload_size - done,
            (DMU_MAX_ACCESS >> 1) - P2PHASE(woff, (uint64_t)MAX(blksz,
            grow_to)));

        tx = dmu_tx_create(context->os);
        dmu_tx_hold_sa(tx, handle, B_FALSE);
        dmu_tx_hold_write(tx, object, woff, (int)nbytes);
        error = dmu_tx_assign(tx, DMU_TX_WAIT);
        if (error != 0) {
            dmu_tx_abort(tx);
            break;
        }
        /* zfs_grow_blocksize(): only a single-block file may change. */
        if (grow_to > blksz) {
            error = dmu_object_set_blocksize(context->os, object, grow_to,
                0, tx);
            if (error != ENOTSUP)
                VERIFY0(error);
            error = 0;
        }
        dmu_write(context->os, object, woff, nbytes,
            (char *)payload + done, tx, 0);

        file_size = MAX(file_size, woff + nbytes);
        pflags |= ZFS_ARCHIVE | ZFS_AV_MODIFIED;
        gethrestime(&now);
        mtime[0] = ctime[0] = (uint64_t)now.tv_sec;
        mtime[1] = ctime[1] = (uint64_t)now.tv_nsec;
        count = 0;
        SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_MTIME], NULL,
            mtime, 16);
        SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_CTIME], NULL,
            ctime, 16);
        SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_SIZE], NULL,
            &file_size, 8);
        SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_FLAGS], NULL,
            &pflags, 8);
        VERIFY0(sa_bulk_update(handle, attrs, count, tx));
        dmu_tx_commit(tx);
        done += nbytes;
    }

    sa_handle_destroy(handle);
    free(payload);
    if (error == 0) {
        txg_wait_synced(dmu_objset_pool(context->os), 0);
        *written_at = offset;
    }
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
 * Mirrors zfs_create() -> zfs_mknode() -> zfs_link_create() for a regular
 * file in the dataset root with a trivial, non-inherited ACL, producing the
 * same SA layout, flags and parent updates as the Linux ZPL.
 */
static int
create_file(write_context_t *context, const char *name, uint64_t *object_out)
{
    static const uint16_t ace_who[3] = {
        ACE_OWNER, ZWC_OWNING_GROUP, ACE_EVERYONE
    };
    dmu_tx_t *tx = NULL;
    dmu_buf_t *db = NULL;
    sa_handle_t *handle = NULL, *parent_handle = NULL;
    sa_bulk_attr_t attrs[16], parent_attrs[5];
    zfs_ace_hdr_t aces[3];
    uint32_t masks[3];
    timestruc_t now;
    uint64_t root = 0, object, existing, mode, size = 0, generation;
    uint64_t uid = 0, gid = 0, parent, pflags;
    uint64_t parent_flags = 0, parent_size = 0, parent_links = 0;
    uint64_t parent_projid = ZFS_DEFAULT_PROJID, projid = ZFS_DEFAULT_PROJID;
    uint64_t links = 1, crtime[2], ctime[2], atime[2], mtime[2];
    uint64_t acl_count = 3, dirent;
    boolean_t project = dmu_objset_projectquota_enabled(context->os);
    int count = 0, parent_count = 0, i;
    int dnodesize = dmu_objset_dnodesize(context->os);
    int error;

    if (name[0] == '\0' || strchr(name, '/') != NULL ||
        strcmp(name, ".") == 0 || strcmp(name, "..") == 0)
        return (EINVAL);
    /* Longer names would activate the longname feature; not yet covered. */
    if (strlen(name) >= ZAP_MAXNAMELEN)
        return (ENAMETOOLONG);
    if (context->zpl_version < ZPL_VERSION_SA)
        return (ENOTSUP);

    error = zap_lookup(context->os, MASTER_NODE_OBJ, ZFS_ROOT_OBJ,
        sizeof (root), 1, &root);
    if (error != 0)
        return (error);
    error = zap_lookup(context->os, root, name, sizeof (existing), 1,
        &existing);
    if (error == 0)
        return (EEXIST);
    if (error != ENOENT)
        return (error);

    error = sa_handle_get(context->os, root, NULL, SA_HDL_PRIVATE,
        &parent_handle);
    if (error != 0)
        return (error);
    SA_ADD_BULK_ATTR(parent_attrs, parent_count,
        context->sa_table[ZPL_FLAGS], NULL, &parent_flags, 8);
    SA_ADD_BULK_ATTR(parent_attrs, parent_count,
        context->sa_table[ZPL_SIZE], NULL, &parent_size, 8);
    SA_ADD_BULK_ATTR(parent_attrs, parent_count,
        context->sa_table[ZPL_LINKS], NULL, &parent_links, 8);
    error = sa_bulk_lookup(parent_handle, parent_attrs, parent_count);
    if (error == 0 && (parent_flags & ZFS_PROJID) != 0)
        error = sa_lookup(parent_handle, context->sa_table[ZPL_PROJID],
            &parent_projid, sizeof (parent_projid));
    /* Inheritable parent ACEs need zfs_acl_inherit(), which is not ported. */
    if (error == 0 && (parent_flags & (ZFS_INHERIT_ACE | ZFS_XATTR)) != 0)
        error = ENOTSUP;
    mode = S_IFREG | 0644;
    if (error == 0 && !trivial_allow_masks(mode, B_FALSE, masks))
        error = ENOTSUP;
    if (error != 0) {
        sa_handle_destroy(parent_handle);
        return (error);
    }
    for (i = 0; i < 3; i++) {
        aces[i].z_type = ZWC_ALLOW;
        aces[i].z_flags = ace_who[i];
        aces[i].z_access_mask = masks[i];
    }

    tx = dmu_tx_create(context->os);
    dmu_tx_hold_sa_create(tx, sizeof (aces) + ZFS_SA_BASE_ATTR_SIZE);
    dmu_tx_hold_zap(tx, root, TRUE, name);
    dmu_tx_hold_sa(tx, parent_handle, B_FALSE);
    error = dmu_tx_assign(tx, DMU_TX_WAIT);
    if (error != 0) {
        dmu_tx_abort(tx);
        sa_handle_destroy(parent_handle);
        return (error);
    }

    /* An assigned transaction cannot be aborted; failures past here VERIFY. */
    gethrestime(&now);
    crtime[0] = ctime[0] = atime[0] = mtime[0] = (uint64_t)now.tv_sec;
    crtime[1] = ctime[1] = atime[1] = mtime[1] = (uint64_t)now.tv_nsec;
    generation = dmu_tx_get_txg(tx);
    parent = root;
    pflags = ZFS_ARCHIVE | ZFS_AV_MODIFIED | ZFS_ACL_TRIVIAL;
    if (project)
        pflags |= ZFS_PROJID;
    if (parent_flags & ZFS_PROJINHERIT) {
        projid = parent_projid;
        pflags |= ZFS_PROJINHERIT;
    }

    object = dmu_object_alloc_dnsize(context->os, DMU_OT_PLAIN_FILE_CONTENTS,
        0, DMU_OT_SA, DN_BONUS_SIZE(dnodesize), dnodesize, tx);
    VERIFY0(sa_buf_hold(context->os, object, NULL, &db));
    VERIFY0(sa_handle_get_from_db(context->os, db, NULL, SA_HDL_SHARED,
        &handle));

    /* Attribute order matches zfs_mknode() so the SA layout is shared. */
    SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_MODE], NULL,
        &mode, 8);
    SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_SIZE], NULL,
        &size, 8);
    SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_GEN], NULL,
        &generation, 8);
    SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_UID], NULL,
        &uid, 8);
    SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_GID], NULL,
        &gid, 8);
    SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_PARENT], NULL,
        &parent, 8);
    SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_FLAGS], NULL,
        &pflags, 8);
    SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_ATIME], NULL,
        atime, 16);
    SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_MTIME], NULL,
        mtime, 16);
    SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_CTIME], NULL,
        ctime, 16);
    SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_CRTIME], NULL,
        crtime, 16);
    SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_LINKS], NULL,
        &links, 8);
    if (project)
        SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_PROJID], NULL,
            &projid, 8);
    SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_DACL_COUNT], NULL,
        &acl_count, 8);
    SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_DACL_ACES], NULL,
        aces, sizeof (aces));
    VERIFY0(sa_replace_all_by_template(handle, attrs, count, tx));

    dirent = object | ((uint64_t)IFTODT(mode) << 60);
    VERIFY0(zap_add(context->os, root, name, sizeof (dirent), 1, &dirent,
        tx));

    /* zfs_link_create(): parent size, mtime/ctime and archive flags. */
    parent_size++;
    parent_flags |= ZFS_ARCHIVE | ZFS_AV_MODIFIED;
    parent_count = 0;
    SA_ADD_BULK_ATTR(parent_attrs, parent_count,
        context->sa_table[ZPL_SIZE], NULL, &parent_size, 8);
    SA_ADD_BULK_ATTR(parent_attrs, parent_count,
        context->sa_table[ZPL_LINKS], NULL, &parent_links, 8);
    SA_ADD_BULK_ATTR(parent_attrs, parent_count,
        context->sa_table[ZPL_MTIME], NULL, mtime, 16);
    SA_ADD_BULK_ATTR(parent_attrs, parent_count,
        context->sa_table[ZPL_CTIME], NULL, ctime, 16);
    SA_ADD_BULK_ATTR(parent_attrs, parent_count,
        context->sa_table[ZPL_FLAGS], NULL, &parent_flags, 8);
    VERIFY0(sa_bulk_update(parent_handle, parent_attrs, parent_count, tx));

    /* A shared SA handle owns the bonus hold and releases it on destroy. */
    sa_handle_destroy(handle);
    sa_handle_destroy(parent_handle);
    dmu_tx_commit(tx);
    txg_wait_synced(dmu_objset_pool(context->os), 0);
    *object_out = object;
    return (0);
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

    error = zap_lookup(context->os, MASTER_NODE_OBJ, ZPL_VERSION_STR,
        sizeof (uint64_t), 1, &context->zpl_version);
    if (error != 0) {
        print_error("read ZPL version", error);
        return (error);
    }

    error = zap_lookup(context->os, MASTER_NODE_OBJ, ZFS_SA_ATTRS,
        sizeof (uint64_t), 1, &sa_object);
    if (error == ENOENT)
        sa_object = 0;
    else if (error != 0) {
        print_error("locate SA registry", error);
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

static void
usage(const char *program)
{
    (void) fprintf(stderr,
        "Usage: %s probe|sync <image-directory> <pool> <dataset>\n"
        "       %s create-file <image-directory> <pool> <dataset> <name>\n"
        "       %s write-file <image-directory> <pool> <dataset> <name> "
        "<offset|append> <payload-file>\n",
        program, program, program);
}

int
main(int argc, char **argv)
{
    write_context_t context = { 0 };
    uint64_t root_object = 0;
    int error, close_error;

    if (argc < 2 ||
        ((strcmp(argv[1], "probe") == 0 || strcmp(argv[1], "sync") == 0) ?
        argc != 5 :
        strcmp(argv[1], "create-file") == 0 ? argc != 6 :
        strcmp(argv[1], "write-file") == 0 ? argc != 8 : B_TRUE)) {
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
        if (strcmp(argv[1], "create-file") == 0) {
            uint64_t object = 0;

            error = create_file(&context, argv[5], &object);
            if (error == 0)
                (void) printf("pool=%s dataset=%s name=%s object=%llu\n",
                    context.pool_name, context.dataset_name, argv[5],
                    (unsigned long long)object);
            else
                print_error("create file", error);
        } else if (strcmp(argv[1], "write-file") == 0) {
            boolean_t append = strcmp(argv[6], "append") == 0;
            uint64_t offset = 0;

            error = append ? 0 : parse_u64(argv[6], &offset);
            if (error == 0)
                error = write_file(&context, argv[5], append, offset,
                    argv[7], &offset);
            if (error == 0)
                (void) printf("pool=%s dataset=%s name=%s offset=%llu\n",
                    context.pool_name, context.dataset_name, argv[5],
                    (unsigned long long)offset);
            else
                print_error("write file", error);
        } else if (strcmp(argv[1], "sync") == 0) {
            txg_wait_synced(dmu_objset_pool(context.os), 0);
            (void) printf("pool=%s dataset=%s synced\n",
                context.pool_name, context.dataset_name);
        } else {
            error = zap_lookup(context.os, MASTER_NODE_OBJ, ZFS_ROOT_OBJ,
                sizeof (uint64_t), 1, &root_object);
            if (error == 0) {
                (void) printf("pool=%s dataset=%s root=%llu\n",
                    context.pool_name, context.dataset_name,
                    (unsigned long long)root_object);
            } else {
                print_error("locate root directory", error);
            }
        }
    }

    (void) fflush(stdout);
    close_error = close_context(&context);
    return (error == 0 && close_error == 0 ? 0 : 1);
}
