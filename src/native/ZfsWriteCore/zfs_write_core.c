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
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <libnvpair.h>
#include <libzpool.h>
#include <libzutil.h>
#include <sys/dmu.h>
#include <sys/dmu_objset.h>
#include <sys/dmu_tx.h>
#include <sys/sa.h>
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
    boolean_t imported;
} write_context_t;

static void
print_error(const char *operation, int error)
{
    (void) fprintf(stderr, "%s: %s (%d)\n", operation,
        strerror(error), error);
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

static void
close_context(write_context_t *context)
{
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
    kernel_fini();
}

static void
usage(const char *program)
{
    (void) fprintf(stderr,
        "Usage: %s <probe|sync> <image-directory> <pool> <dataset>\n",
        program);
}

int
main(int argc, char **argv)
{
    write_context_t context = { 0 };
    uint64_t root_object = 0;
    int error;

    if (argc != 5 || (strcmp(argv[1], "probe") != 0 &&
        strcmp(argv[1], "sync") != 0)) {
        usage(argv[0]);
        return (2);
    }

    context.search_path = argv[2];
    context.pool_name = argv[3];
    context.dataset_name = argv[4];

    error = open_context(&context);
    if (error == 0) {
        if (strcmp(argv[1], "sync") == 0) {
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

    close_context(&context);
    return (error == 0 ? 0 : 1);
}
