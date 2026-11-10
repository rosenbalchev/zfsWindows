// SPDX-License-Identifier: CDDL-1.0
/*
 * Test entry points of the user-mode engine (zut.h).  Pool and dataset
 * operations are built the way libzfs builds them and sent through
 * zum_ioctl(); only the read-outs (pool status, ARC sizes) look at the
 * engine's state directly.
 */

#include <errno.h>
#include <stdlib.h>
#include <string.h>

#include <libnvpair.h>
#include <libzpool.h>
#include <sys/arc_impl.h>
#include <sys/fs/zfs.h>
#include <sys/zfs_ioctl.h>
#include <zfeature_common.h>
#include <zfs_prop.h>

#include "zum_impl.h"
#include "zut.h"

#define ZUT_MAX_VDEVS 16
#define ZUT_DST_SIZE (256 * 1024)

static boolean_t ioctls_ready;

int
zut_engine_start(const char *cachefile)
{
    int error = zum_engine_start();

    if (error != 0)
        return (error);
    if (cachefile != NULL)
        zum_engine_set_cachefile(cachefile);
    /* zfs_kmod_init() registers the ioctl table: once per process. */
    if (!ioctls_ready) {
        error = zum_bridge_init(NULL);
        ioctls_ready = error == 0;
    }
    return (error);
}

int
zut_engine_stop(void)
{
    return (zum_engine_stop());
}

/*
 * Outside the kernel arc_init() leaves zfs_arc_max to arc_tuning_update(),
 * which takes it only above arc_c_min (RAM/32): lower the floor with it,
 * to half the ceiling, as the kernel's arc_init() does.
 */
void
zut_arc_set_max(uint64_t bytes)
{
    zfs_arc_max = bytes;
    zfs_arc_min = bytes / 2;
}

void
zut_arc_stats(zut_arc_stats_t *stats)
{
    stats->size = aggsum_value(&arc_sums.arcstat_size);
    stats->target = arc_c;
    stats->min = arc_c_min;
    stats->max = arc_c_max;
}

/* One ioctl, as libzfs sends it: packed nvlists and a result buffer. */
static int
send_ioctl(uint64_t request, const char *name, nvlist_t *src, nvlist_t *conf,
    uint64_t cookie, uint64_t flags)
{
    zum_ioctl_buffers_t io = { 0 };
    zfs_cmd_t *zc = calloc(1, zum_ioctl_cmd_size());
    size_t src_size = 0, conf_size = 0;
    char *packed_src = NULL, *packed_conf = NULL;
    int error;

    if (zc == NULL)
        return (ENOMEM);
    (void) strlcpy(zc->zc_name, name, sizeof (zc->zc_name));
    zc->zc_cookie = cookie;
    zc->zc_flags = flags;
    if (src != NULL)
        packed_src = fnvlist_pack(src, &src_size);
    if (conf != NULL)
        packed_conf = fnvlist_pack(conf, &conf_size);
    io.cmd = zc;
    io.src = packed_src;
    io.src_size = src_size;
    io.conf = packed_conf;
    io.conf_size = conf_size;
    io.dst = malloc(ZUT_DST_SIZE);
    io.dst_cap = io.dst != NULL ? ZUT_DST_SIZE : 0;
    error = zum_ioctl(request, 1, &io);
    free(io.dst);
    if (packed_src != NULL)
        fnvlist_pack_free(packed_src, src_size);
    if (packed_conf != NULL)
        fnvlist_pack_free(packed_conf, conf_size);
    free(zc);
    return (error);
}

static int
parse_number(const char *text, uint64_t *value)
{
    char *end = NULL;
    uint64_t parsed;

    errno = 0;
    parsed = strtoull(text, &end, 0);
    if (errno != 0 || end == text)
        return (EINVAL);
    switch (*end) {
    case 'K': case 'k': parsed <<= 10; end++; break;
    case 'M': case 'm': parsed <<= 20; end++; break;
    case 'G': case 'g': parsed <<= 30; end++; break;
    default: break;
    }
    if (*end != '\0')
        return (EINVAL);
    *value = parsed;
    return (0);
}

/*
 * "name=value;..." to the nvlist the kernel expects: index properties by
 * number (as zfs_valid_proplist()/zpool_valid_proplist() convert them).
 * A pool's feature@name=disabled goes to disabled instead.
 */
static int
parse_props(const char *text, boolean_t pool, nvlist_t *props,
    nvlist_t *disabled)
{
    char *copy, *item, *next, *value;
    uint64_t number;
    int error = 0;

    if (text == NULL || *text == '\0')
        return (0);
    copy = strdup(text);
    if (copy == NULL)
        return (ENOMEM);
    for (item = copy; error == 0 && item != NULL; item = next) {
        if ((next = strchr(item, ';')) != NULL)
            *next++ = '\0';
        if (*item == '\0')
            continue;
        if ((value = strchr(item, '=')) == NULL) {
            error = EINVAL;
            break;
        }
        *value++ = '\0';
        if (pool) {
            zpool_prop_t prop = zpool_name_to_prop(item);

            if (prop == ZPOOL_PROP_INVAL && zpool_prop_feature(item)) {
                if (strcmp(value, "enabled") == 0)
                    fnvlist_add_uint64(props, item, 0);
                else if (strcmp(value, "disabled") == 0 &&
                    disabled != NULL)
                    fnvlist_add_boolean(disabled, item);
                else
                    error = EINVAL;
                continue;
            }
            if (prop == ZPOOL_PROP_INVAL) {
                error = EINVAL;
            } else if (zpool_prop_get_type(prop) == PROP_TYPE_STRING) {
                fnvlist_add_string(props, item, value);
            } else if (zpool_prop_get_type(prop) == PROP_TYPE_INDEX) {
                error = zpool_prop_string_to_index(prop, value, &number);
                if (error == 0)
                    fnvlist_add_uint64(props, item, number);
            } else if ((error = parse_number(value, &number)) == 0) {
                fnvlist_add_uint64(props, item, number);
            }
        } else {
            zfs_prop_t prop = zfs_name_to_prop(item);

            if (prop == ZPROP_INVAL) {
                if (zfs_prop_user(item))
                    fnvlist_add_string(props, item, value);
                else
                    error = EINVAL;
            } else if (zfs_prop_get_type(prop) == PROP_TYPE_STRING) {
                fnvlist_add_string(props, item, value);
            } else if (zfs_prop_get_type(prop) == PROP_TYPE_INDEX) {
                error = zfs_prop_string_to_index(prop, value, &number);
                if (error == 0)
                    fnvlist_add_uint64(props, item, number);
            } else if ((error = parse_number(value, &number)) == 0) {
                fnvlist_add_uint64(props, item, number);
            }
        }
    }
    free(copy);
    return (error != 0 ? EINVAL : 0);
}

int
zut_pool_create(const char *pool, const char *layout, const char *vdevs,
    const char *pool_props, const char *fs_props)
{
    nvlist_t *leaves[ZUT_MAX_VDEVS], *root, *top = NULL, *props, *rootprops;
    nvlist_t *disabled;
    char *copy, *path, *next, feature[MAXPATHLEN];
    uint64_t ashift = 0, nparity = 0;
    int count = 0, i, error;

    if (layout == NULL)
        layout = "";
    if (strcmp(layout, "raidz1") == 0)
        nparity = 1;
    else if (strcmp(layout, "raidz2") == 0)
        nparity = 2;
    else if (strcmp(layout, "raidz3") == 0)
        nparity = 3;
    else if (*layout != '\0' && strcmp(layout, "mirror") != 0)
        return (EINVAL);

    props = fnvlist_alloc();
    rootprops = fnvlist_alloc();
    disabled = fnvlist_alloc();
    if ((error = parse_props(pool_props, B_TRUE, props, disabled)) != 0 ||
        (error = parse_props(fs_props, B_FALSE, rootprops, NULL)) != 0) {
        fnvlist_free(props);
        fnvlist_free(rootprops);
        fnvlist_free(disabled);
        return (error);
    }
    /* As zpool create without -d: every feature not disabled is enabled. */
    if (!nvlist_exists(props, zpool_prop_to_name(ZPOOL_PROP_VERSION))) {
        for (i = 0; i < SPA_FEATURES; i++) {
            (void) snprintf(feature, sizeof (feature), "feature@%s",
                spa_feature_table[i].fi_uname);
            if (!nvlist_exists(props, feature) &&
                !nvlist_exists(disabled, feature))
                fnvlist_add_uint64(props, feature, 0);
        }
    }
    fnvlist_free(disabled);
    /* No cache file unless asked: pools are imported by their images. */
    if (!nvlist_exists(props, zpool_prop_to_name(ZPOOL_PROP_CACHEFILE)))
        fnvlist_add_string(props, zpool_prop_to_name(ZPOOL_PROP_CACHEFILE),
            "none");
    (void) nvlist_lookup_uint64(props, zpool_prop_to_name(ZPOOL_PROP_ASHIFT),
        &ashift);
    if (!nvlist_empty(rootprops))
        fnvlist_add_nvlist(props, ZPOOL_ROOTFS_PROPS, rootprops);
    fnvlist_free(rootprops);

    /* make_leaf_vdev(): path, type and, if given, the ashift. */
    copy = strdup(vdevs != NULL ? vdevs : "");
    for (path = copy; path != NULL && *path != '\0' &&
        count < ZUT_MAX_VDEVS; path = next) {
        if ((next = strchr(path, ';')) != NULL)
            *next++ = '\0';
        if (*path == '\0')
            continue;
        leaves[count] = fnvlist_alloc();
        fnvlist_add_string(leaves[count], ZPOOL_CONFIG_PATH, path);
        fnvlist_add_string(leaves[count], ZPOOL_CONFIG_TYPE, VDEV_TYPE_FILE);
        if (ashift != 0)
            fnvlist_add_uint64(leaves[count], ZPOOL_CONFIG_ASHIFT, ashift);
        count++;
    }
    free(copy);
    if (count == 0 || (*layout != '\0' && count < 2)) {
        for (i = 0; i < count; i++)
            fnvlist_free(leaves[i]);
        fnvlist_free(props);
        return (EINVAL);
    }

    /* make_root_vdev(): every top-level vdev marked as not a log. */
    root = fnvlist_alloc();
    fnvlist_add_string(root, ZPOOL_CONFIG_TYPE, VDEV_TYPE_ROOT);
    if (*layout != '\0') {
        top = fnvlist_alloc();
        fnvlist_add_string(top, ZPOOL_CONFIG_TYPE,
            nparity != 0 ? VDEV_TYPE_RAIDZ : VDEV_TYPE_MIRROR);
        if (nparity != 0)
            fnvlist_add_uint64(top, ZPOOL_CONFIG_NPARITY, nparity);
        fnvlist_add_uint64(top, ZPOOL_CONFIG_IS_LOG, 0);
        fnvlist_add_nvlist_array(top, ZPOOL_CONFIG_CHILDREN,
            (const nvlist_t **)leaves, count);
        fnvlist_add_nvlist_array(root, ZPOOL_CONFIG_CHILDREN,
            (const nvlist_t **)&top, 1);
    } else {
        for (i = 0; i < count; i++)
            fnvlist_add_uint64(leaves[i], ZPOOL_CONFIG_IS_LOG, 0);
        fnvlist_add_nvlist_array(root, ZPOOL_CONFIG_CHILDREN,
            (const nvlist_t **)leaves, count);
    }

    error = send_ioctl(ZFS_IOC_POOL_CREATE, pool, props, root, 0, 0);
    for (i = 0; i < count; i++)
        fnvlist_free(leaves[i]);
    if (top != NULL)
        fnvlist_free(top);
    fnvlist_free(root);
    fnvlist_free(props);
    return (error);
}

int
zut_pool_import(const char *pool, const char *search_path,
    const char *cachefile)
{
    return (zum_pool_import(pool, search_path, cachefile));
}

void
zut_pool_cachefile(const char *pool, char *out, int size)
{
    zum_pool_cachefile(pool, out, (size_t)size);
}

int
zut_pool_export(const char *pool, int force)
{
    spa_t *spa;

    /* As zum_close(): disowned objsets still hold the spa until evicted. */
    if (spa_open(pool, &spa, FTAG) == 0) {
        spa_evicting_os_wait(spa);
        spa_close(spa, FTAG);
    }
    return (send_ioctl(ZFS_IOC_POOL_EXPORT, pool, NULL, NULL,
        force != 0, 0));
}

int
zut_pool_destroy(const char *pool)
{
    return (send_ioctl(ZFS_IOC_POOL_DESTROY, pool, NULL, NULL, 0, 0));
}

int
zut_pool_scrub(const char *pool)
{
    nvlist_t *wait;
    int error;

    error = send_ioctl(ZFS_IOC_POOL_SCAN, pool, NULL, NULL, POOL_SCAN_SCRUB,
        POOL_SCRUB_NORMAL);
    if (error != 0)
        return (error);
    wait = fnvlist_alloc();
    fnvlist_add_int32(wait, ZPOOL_WAIT_ACTIVITY, ZPOOL_WAIT_SCRUB);
    error = send_ioctl(ZFS_IOC_WAIT, pool, wait, NULL, 0, 0);
    fnvlist_free(wait);
    return (error);
}

int
zut_pool_status(const char *pool, zut_pool_status_t *status)
{
    vdev_t *leaves[64];
    vdev_stat_t vs;
    pool_scan_stat_t ps;
    spa_t *spa;
    int count, i, error;

    memset(status, 0, sizeof (*status));
    if ((error = spa_open(pool, &spa, FTAG)) != 0)
        return (error);
    spa_config_enter(spa, SCL_VDEV, FTAG, RW_READER);
    status->state = spa->spa_root_vdev->vdev_state;
    count = zum_vdev_leaves(spa->spa_root_vdev, leaves, 64, 0);
    status->leaves = count;
    for (i = 0; i < count; i++) {
        vdev_get_stats(leaves[i], &vs);
        status->read_errors += vs.vs_read_errors;
        status->write_errors += vs.vs_write_errors;
        status->checksum_errors += vs.vs_checksum_errors;
    }
    spa_config_exit(spa, SCL_VDEV, FTAG);
    if (spa_scan_get_stats(spa, &ps) == 0) {
        status->scan_state = ps.pss_state;
        status->scan_errors = ps.pss_errors;
        status->scan_repaired = ps.pss_processed;
    }
    spa_close(spa, FTAG);
    return (0);
}

int
zut_dataset_create(const char *name, const char *props)
{
    nvlist_t *args = fnvlist_alloc(), *nvprops = fnvlist_alloc();
    int error;

    if ((error = parse_props(props, B_FALSE, nvprops, NULL)) == 0) {
        fnvlist_add_int32(args, "type", DMU_OST_ZFS);
        fnvlist_add_nvlist(args, "props", nvprops);
        error = send_ioctl(ZFS_IOC_CREATE, name, args, NULL, 0, 0);
    }
    fnvlist_free(nvprops);
    fnvlist_free(args);
    return (error);
}

int
zut_dataset_set(const char *name, const char *props)
{
    nvlist_t *nvprops = fnvlist_alloc();
    int error;

    if ((error = parse_props(props, B_FALSE, nvprops, NULL)) == 0)
        error = send_ioctl(ZFS_IOC_SET_PROP, name, nvprops, NULL, 0, 0);
    fnvlist_free(nvprops);
    return (error);
}

/* ZFS_IOC_SNAPSHOT and ZFS_IOC_DESTROY_SNAPS: {"snaps": {name}} on the pool. */
static int
snapshot_ioctl(uint64_t request, const char *snapshot)
{
    char pool[ZFS_MAX_DATASET_NAME_LEN];
    nvlist_t *args, *snaps;
    int error;

    if (strchr(snapshot, '@') == NULL)
        return (EINVAL);
    (void) strlcpy(pool, snapshot, sizeof (pool));
    pool[strcspn(pool, "/@")] = '\0';
    args = fnvlist_alloc();
    snaps = fnvlist_alloc();
    fnvlist_add_boolean(snaps, snapshot);
    fnvlist_add_nvlist(args, "snaps", snaps);
    error = send_ioctl(request, pool, args, NULL, 0, 0);
    fnvlist_free(snaps);
    fnvlist_free(args);
    return (error);
}

int
zut_snapshot(const char *snapshot)
{
    return (snapshot_ioctl(ZFS_IOC_SNAPSHOT, snapshot));
}

int
zut_snapshot_destroy(const char *snapshot)
{
    return (snapshot_ioctl(ZFS_IOC_DESTROY_SNAPS, snapshot));
}

int
zut_rollback(const char *dataset)
{
    nvlist_t *args = fnvlist_alloc();
    int error = send_ioctl(ZFS_IOC_ROLLBACK, dataset, args, NULL, 0, 0);

    fnvlist_free(args);
    return (error);
}

int
zut_fs_open(const char *search_path, const char *pool, const char *dataset,
    int readonly, zum_fs_t **fsp)
{
    zum_fs_args_t args = {
        .search_path = search_path,
        .pool = pool,
        .dataset = dataset,
        .readonly = readonly
    };

    return (zum_fs_open(&args, fsp));
}
