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
#include <sys/dnode.h>
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

static int
read_payload(const char *path, void **data, uint64_t *size)
{
    FILE *file = NULL;
    long length;
    void *buffer = NULL;

    file = fopen(path, "rb");
    if (file == NULL)
        return (errno);
    if (fseek(file, 0, SEEK_END) != 0 ||
        (length = ftell(file)) < 0 || fseek(file, 0, SEEK_SET) != 0) {
        fclose(file);
        return (EIO);
    }
    if ((uint64_t)length > INT_MAX) {
        fclose(file);
        return (EFBIG);
    }
    if (length != 0) {
        buffer = malloc((size_t)length);
        if (buffer == NULL) {
            fclose(file);
            return (ENOMEM);
        }
        if (fread(buffer, 1, (size_t)length, file) != (size_t)length) {
            free(buffer);
            fclose(file);
            return (EIO);
        }
    }
    fclose(file);
    *data = buffer;
    *size = (uint64_t)length;
    return (0);
}

static int
write_object(write_context_t *context, uint64_t object, uint64_t offset,
    const char *payload_path)
{
    dmu_object_info_t info = { 0 };
    dmu_tx_t *tx = NULL;
    void *payload = NULL;
    uint64_t size = 0;
    int error;

    if ((error = read_payload(payload_path, &payload, &size)) != 0)
        return (error);
    if (size == 0) {
        free(payload);
        return (EINVAL);
    }
    if (offset > UINT64_MAX - size) {
        free(payload);
        return (EOVERFLOW);
    }
    if ((error = dmu_object_info(context->os, object, &info)) != 0) {
        free(payload);
        return (error);
    }
    if (info.doi_type == DMU_OT_NONE) {
        free(payload);
        return (ENOENT);
    }

    tx = dmu_tx_create(context->os);
    dmu_tx_hold_write(tx, object, offset, (int)size);
    error = dmu_tx_assign(tx, DMU_TX_WAIT);
    if (error != 0) {
        dmu_tx_abort(tx);
        free(payload);
        return (error);
    }
    dmu_write(context->os, object, offset, size, payload, tx, 0);
    dmu_tx_commit(tx);
    txg_wait_synced(dmu_objset_pool(context->os), 0);
    free(payload);
    return (0);
}

static int
create_file(write_context_t *context, const char *name, uint64_t *object_out)
{
    dmu_tx_t *tx = NULL;
    dmu_buf_t *db = NULL;
    sa_handle_t *handle = NULL;
    sa_bulk_attr_t attrs[12];
    uint64_t root = 0, object, mode, size = 0, generation;
    uint64_t uid = 0, gid = 0, parent, flags = ZFS_ACL_TRIVIAL;
    uint64_t links = 1, times[2] = { 0, 0 }, dirent;
    int count = 0;
    int error;

    error = zap_lookup(context->os, MASTER_NODE_OBJ, ZFS_ROOT_OBJ,
        sizeof (root), 1, &root);
    if (error != 0)
        return (error);
    if (name[0] == '\0' || strchr(name, '/') != NULL)
        return (EINVAL);

    mode = S_IFREG | 0644;
    parent = root;
    generation = 0;
    tx = dmu_tx_create(context->os);
    dmu_tx_hold_sa_create(tx, DN_BONUS_SIZE(dmu_objset_dnodesize(
        context->os)));
    dmu_tx_hold_zap(tx, root, TRUE, name);
    error = dmu_tx_assign(tx, DMU_TX_WAIT);
    if (error != 0) {
        dmu_tx_abort(tx);
        return (error);
    }

    object = dmu_object_alloc_dnsize(context->os, DMU_OT_PLAIN_FILE_CONTENTS,
        0, DMU_OT_SA, DN_BONUS_SIZE(dmu_objset_dnodesize(context->os)),
        dmu_objset_dnodesize(context->os), tx);
    error = sa_buf_hold(context->os, object, NULL, &db);
    if (error == 0)
        error = sa_handle_get_from_db(context->os, db, NULL, SA_HDL_SHARED,
            &handle);
    if (error == 0) {
        SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_MODE], NULL,
            &mode, sizeof (mode));
        SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_SIZE], NULL,
            &size, sizeof (size));
        SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_GEN], NULL,
            &generation, sizeof (generation));
        SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_UID], NULL,
            &uid, sizeof (uid));
        SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_GID], NULL,
            &gid, sizeof (gid));
        SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_PARENT], NULL,
            &parent, sizeof (parent));
        SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_FLAGS], NULL,
            &flags, sizeof (flags));
        SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_ATIME], NULL,
            times, sizeof (times));
        SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_MTIME], NULL,
            times, sizeof (times));
        SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_CTIME], NULL,
            times, sizeof (times));
        SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_CRTIME], NULL,
            times, sizeof (times));
        SA_ADD_BULK_ATTR(attrs, count, context->sa_table[ZPL_LINKS], NULL,
            &links, sizeof (links));
        error = sa_bulk_update(handle, attrs, count, tx);
    }
    if (error == 0) {
        dirent = object | ((uint64_t)IFTODT(S_IFREG) << 60);
        error = zap_add(context->os, root, name, sizeof (dirent), 1,
            &dirent, tx);
    }
    if (handle != NULL)
        sa_handle_destroy(handle);
    if (db != NULL)
        sa_buf_rele(db, NULL);
    if (error == 0) {
        dmu_tx_commit(tx);
        txg_wait_synced(dmu_objset_pool(context->os), 0);
        *object_out = object;
    } else {
        dmu_tx_abort(tx);
    }
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
        "Usage: %s <probe|sync|create-file|write-object> "
        "<image-directory> <pool> <dataset> [name|object offset payload-file]\n",
        program);
}

int
main(int argc, char **argv)
{
    write_context_t context = { 0 };
    uint64_t root_object = 0;
    int error;

    if ((argc != 5 && argc != 6 && argc != 8) ||
        (strcmp(argv[1], "probe") != 0 &&
        strcmp(argv[1], "sync") != 0 &&
        strcmp(argv[1], "create-file") != 0 &&
        strcmp(argv[1], "write-object") != 0) ||
        (strcmp(argv[1], "write-object") == 0 && argc != 8) ||
        (strcmp(argv[1], "create-file") == 0 && argc != 6) ||
        (strcmp(argv[1], "probe") != 0 && strcmp(argv[1], "sync") != 0 &&
        strcmp(argv[1], "create-file") != 0 && argc != 5)) {
        usage(argv[0]);
        return (2);
    }

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
        } else if (strcmp(argv[1], "write-object") == 0) {
            uint64_t object = 0;
            uint64_t offset = 0;

            error = parse_u64(argv[5], &object);
            if (error == 0)
                error = parse_u64(argv[6], &offset);
            if (error == 0)
                error = write_object(&context, object, offset, argv[7]);
            if (error == 0)
                (void) printf("pool=%s dataset=%s object=%llu offset=%llu "
                    "written=%s\n", context.pool_name,
                    context.dataset_name, (unsigned long long)object,
                    (unsigned long long)offset, argv[7]);
            else
                print_error("write object", error);
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

    close_context(&context);
    return (error == 0 ? 0 : 1);
}
