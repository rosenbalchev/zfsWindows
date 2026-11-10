// SPDX-License-Identifier: CDDL-1.0
/*
 * The user-mode engine's kstats (user mode only).
 *
 * libspl's kstat_create() is a stub that returns NULL, so the engine in
 * zfswin-um.exe never published arcstats: the ARC lives in this process,
 * and the kernel driver's KSTAT ioctls on \\.\ZFS have no counterpart
 * here.  Patch 0037 lets a process install kstat hooks; this registry
 * keeps the kstats it is asked for, refreshes them through ks_update as
 * the SPL does, and prints them for the bridge (ZFS_BRIDGE_OP_KSTAT) in
 * the "kstat -p" form, so the broker reads both editions the same way.
 *
 * Only the kstats listed in zum_kstat_wanted[] are kept.  The others stay
 * NULL as before: code that created them never relied on them existing,
 * and some use kstat features (raw ops, ks_lock) this registry does not
 * provide.
 */

#include "zum_impl.h"
#include "zum.h"

#include <sys/kstat.h>

typedef struct zum_kstat {
    kstat_t zk_ks;		/* first: kstat_t * is a zum_kstat_t * */
    char zk_module[KSTAT_STRLEN];
    char zk_name[KSTAT_STRLEN];
    int zk_instance;
    uchar_t zk_type;
    boolean_t zk_installed;
    void *zk_owned;		/* ks_data allocated here (not VIRTUAL) */
    struct zum_kstat *zk_next;
} zum_kstat_t;

static const struct { const char *module, *name; } zum_kstat_wanted[] = {
    { "zfs", "arcstats" },
};

static kmutex_t zum_kstat_lock;
static zum_kstat_t *zum_kstat_list;
static boolean_t zum_kstat_ready;

static kstat_t *
zum_kstat_create(const char *module, int instance, const char *name,
    const char *class, uchar_t type, ulong_t ndata, uchar_t flags)
{
    zum_kstat_t *k;
    size_t i;
    boolean_t wanted = B_FALSE;

    (void) class;
    for (i = 0; i < ARRAY_SIZE(zum_kstat_wanted); i++)
        if (strcmp(module, zum_kstat_wanted[i].module) == 0 &&
            strcmp(name, zum_kstat_wanted[i].name) == 0)
            wanted = B_TRUE;
    if (!wanted || type != KSTAT_TYPE_NAMED)
        return (NULL);
    k = kmem_zalloc(sizeof (*k), KM_SLEEP);
    (void) strlcpy(k->zk_module, module, sizeof (k->zk_module));
    (void) strlcpy(k->zk_name, name, sizeof (k->zk_name));
    k->zk_instance = instance;
    k->zk_type = type;
    k->zk_ks.ks_flags = flags;
    k->zk_ks.ks_ndata = (uint_t)ndata;
    k->zk_ks.ks_data_size = ndata * sizeof (kstat_named_t);
    if (!(flags & KSTAT_FLAG_VIRTUAL) && ndata > 0) {
        k->zk_owned = kmem_zalloc(k->zk_ks.ks_data_size, KM_SLEEP);
        k->zk_ks.ks_data = k->zk_owned;
    }
    mutex_enter(&zum_kstat_lock);
    k->zk_next = zum_kstat_list;
    zum_kstat_list = k;
    mutex_exit(&zum_kstat_lock);
    return (&k->zk_ks);
}

static void
zum_kstat_install(kstat_t *ks)
{
    mutex_enter(&zum_kstat_lock);
    ((zum_kstat_t *)ks)->zk_installed = B_TRUE;
    mutex_exit(&zum_kstat_lock);
}

static void
zum_kstat_delete(kstat_t *ks)
{
    zum_kstat_t *k = (zum_kstat_t *)ks, **p;

    mutex_enter(&zum_kstat_lock);
    for (p = &zum_kstat_list; *p != NULL; p = &(*p)->zk_next) {
        if (*p == k) {
            *p = k->zk_next;
            break;
        }
    }
    mutex_exit(&zum_kstat_lock);
    if (k->zk_owned != NULL)
        kmem_free(k->zk_owned, k->zk_ks.ks_data_size);
    kmem_free(k, sizeof (*k));
}

static const kstat_hooks_t zum_kstat_hooks = {
    .kh_create = zum_kstat_create,
    .kh_install = zum_kstat_install,
    .kh_delete = zum_kstat_delete,
};

/* Before kernel_init(): arc_init() creates arcstats there. */
void
zum_kstat_init(void)
{
    if (zum_kstat_ready)
        return;
    mutex_init(&zum_kstat_lock, NULL, MUTEX_DEFAULT, NULL);
    kstat_hooks = &zum_kstat_hooks;
    zum_kstat_ready = B_TRUE;
}

int
zum_kstat_text(const char *spec, char *out, size_t size, size_t *used)
{
    char module[KSTAT_STRLEN], name[KSTAT_STRLEN];
    int instance, error = ENOENT;
    zum_kstat_t *k;

    *used = 0;
    if (size > 0)
        out[0] = '\0';
    if (!zum_kstat_ready)
        return (ENOENT);
    if (sscanf(spec, "%254[^:]:%d:%254[^:]", module, &instance, name) != 3)
        return (EINVAL);
    mutex_enter(&zum_kstat_lock);
    for (k = zum_kstat_list; k != NULL; k = k->zk_next) {
        kstat_named_t *kn;
        uint_t i;

        if (!k->zk_installed || k->zk_instance != instance ||
            strcmp(k->zk_module, module) != 0 ||
            strcmp(k->zk_name, name) != 0 || k->zk_ks.ks_data == NULL)
            continue;
        if (k->zk_ks.ks_update != NULL &&
            (error = k->zk_ks.ks_update(&k->zk_ks, KSTAT_READ)) != 0)
            break;
        error = 0;
        kn = KSTAT_NAMED_PTR(&k->zk_ks);
        for (i = 0; i < k->zk_ks.ks_ndata; i++, kn++) {
            int n;

            switch (kn->data_type) {
            case KSTAT_DATA_UINT64:
                n = snprintf(out + *used, size - *used, "%s:%d:%s:%s\t%llu\n",
                    module, instance, name, kn->name,
                    (u_longlong_t)kn->value.ui64);
                break;
            case KSTAT_DATA_INT64:
                n = snprintf(out + *used, size - *used, "%s:%d:%s:%s\t%lld\n",
                    module, instance, name, kn->name,
                    (longlong_t)kn->value.i64);
                break;
            case KSTAT_DATA_UINT32:
                n = snprintf(out + *used, size - *used, "%s:%d:%s:%s\t%u\n",
                    module, instance, name, kn->name,
                    (uint32_t)kn->value.ui64);
                break;
            default:
                continue;
            }
            if (n < 0 || (size_t)n >= size - *used) {
                error = ENOMEM;
                break;
            }
            *used += (size_t)n;
        }
        break;
    }
    mutex_exit(&zum_kstat_lock);
    return (error);
}
