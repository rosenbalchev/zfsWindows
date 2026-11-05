// SPDX-License-Identifier: CDDL-1.0
/*
 * User-mode pool lifetime for the portable ZPL layer: libzpool's
 * kernel_init(), the userland pool search (libzutil) and spa_import()/
 * spa_export().  In the kernel module the driver's own import and zfsvfs
 * mount take this file's place; everything else in ZfsUserMode is shared.
 */

#include <libnvpair.h>
#include <libzpool.h>
#include <libzutil.h>
#include <sys/zfs_ioctl.h>

#include "zum_impl.h"
#include "zum.h"

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

static int zum_kernel_refs;

int
zum_open(zum_context_t *context)
{
    importargs_t args = { 0 };
    libpc_handle_t handle = {
        .lpc_lib_handle = NULL,
        .lpc_ops = &libzpool_config_ops,
        .lpc_printerr = B_TRUE
    };
    nvlist_t *config = NULL, *props = NULL;
    char *paths[1];
    int error;

    paths[0] = (char *)context->search_path;
    args.paths = 1;
    args.path = paths;
    args.can_be_active = B_TRUE;

    /* libzpool does not run the kernel ZFS module's zfs_init() entry point. */
    if (zum_kernel_refs++ == 0) {
        /*
         * No pool cache: kernel_init() would load the default zpool.cache
         * and imports would record themselves in it, so the next process
         * starts believing a pool is present that it never imported (the
         * import then "succeeds" with EEXIST and spa_open() fails, step-15
         * VM).  Pools are always found by scanning the configured source.
         */
        spa_config_path = (char *)"";
        dmu_objset_register_type(DMU_OST_ZFS, zfs_write_get_file_info);
        /* Global mode; a read-only pool is guarded by its import property. */
        kernel_init(SPA_MODE_READ | SPA_MODE_WRITE);
    }

    error = zpool_find_config(&handle, context->pool_name, &config, &args);
    if (error != 0) {
        cmn_err(CE_WARN, "locate pool %s: error %d", context->pool_name,
            error);
        return (error);
    }

    props = fnvlist_alloc();
    fnvlist_add_string(props, zpool_prop_to_name(ZPOOL_PROP_CACHEFILE), "none");
    /* A read-only import writes nothing, not even the import txg. */
    if (context->readonly)
        fnvlist_add_uint64(props, zpool_prop_to_name(ZPOOL_PROP_READONLY), 1);
    error = spa_import((char *)context->pool_name, config, props,
        ZFS_IMPORT_SKIP_MMP | ZFS_IMPORT_MISSING_LOG);
    fnvlist_free(config);
    if (props != NULL)
        fnvlist_free(props);
    if (error != 0 && error != EEXIST) {
        cmn_err(CE_WARN, "import pool %s: error %d", context->pool_name,
            error);
        return (error);
    }
    context->imported = B_TRUE;

    error = spa_open(context->pool_name, &context->spa, context);
    if (error != 0) {
        cmn_err(CE_WARN, "open pool %s: error %d", context->pool_name, error);
        return (error);
    }
    return (zum_mount(context));
}

int
zum_close(zum_context_t *context)
{
    int error = 0;

    /* An injection holds a spa reference that would make export fail. */
    if (context->spa != NULL)
        zum_clear_injections(context);
    zum_unmount(context);
    if (context->spa != NULL) {
        /*
         * The disowned objset is evicted asynchronously and holds the spa
         * until then.  spa_export() waits for that only while the pool
         * syncs, so a read-only pool would export with EBUSY (step-15 VM).
         */
        spa_evicting_os_wait(context->spa);
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
            cmn_err(CE_WARN, "export pool %s: error %d", context->pool_name,
                error);
        context->imported = B_FALSE;
    }
    if (zum_kernel_refs > 0 && --zum_kernel_refs == 0)
        kernel_fini();
    return (error);
}

int
zum_fs_open(const zum_fs_args_t *args, zum_fs_t **fsp)
{
    zum_context_t *context = kmem_zalloc(sizeof (*context), KM_SLEEP);
    int error;

    context->search_path = spa_strdup(args->search_path);
    context->pool_name = spa_strdup(args->pool);
    context->dataset_name = spa_strdup(args->dataset);
    context->readonly = args->readonly != 0;
    /* Durability comes from Flush and write-through, as on any volume. */
    context->sync_each_op = B_FALSE;
    error = zum_open(context);
    if (error != 0) {
        (void) zum_fs_close(context);
        return (error);
    }
    *fsp = context;
    return (0);
}

int
zum_fs_close(zum_fs_t *context)
{
    int error = zum_close(context);

    spa_strfree((char *)context->search_path);
    spa_strfree((char *)context->pool_name);
    spa_strfree((char *)context->dataset_name);
    kmem_free(context, sizeof (*context));
    return (error);
}