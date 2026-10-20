#include "zfs_native_api.h"

const char *zfs_native_status_name(zfs_core_status_t status)
{
    switch (status) {
    case ZFS_CORE_OK: return "ok";
    case ZFS_CORE_INVALID_ARGUMENT: return "invalid-argument";
    case ZFS_CORE_IO_ERROR: return "io-error";
    case ZFS_CORE_OUT_OF_RANGE: return "out-of-range";
    case ZFS_CORE_NOT_SUPPORTED: return "not-supported";
    case ZFS_CORE_NOT_A_ZFS_DEVICE: return "not-a-zfs-device";
    case ZFS_CORE_CORRUPT_METADATA: return "corrupt-metadata";
    default: return "unknown";
    }
}

