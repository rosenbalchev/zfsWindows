#pragma once

#include <stdint.h>

#if defined(_WIN32)
#  if defined(ZFS_CORE_BUILD)
#    define ZFS_CORE_API __declspec(dllexport)
#  else
#    define ZFS_CORE_API __declspec(dllimport)
#  endif
#else
#  define ZFS_CORE_API
#endif

typedef struct zfs_core_device zfs_core_device_t;

typedef enum zfs_core_status {
    ZFS_CORE_OK = 0,
    ZFS_CORE_INVALID_ARGUMENT = 1,
    ZFS_CORE_IO_ERROR = 2,
    ZFS_CORE_OUT_OF_RANGE = 3,
    ZFS_CORE_NOT_SUPPORTED = 4,
    ZFS_CORE_NOT_A_ZFS_DEVICE = 5,
    ZFS_CORE_CORRUPT_METADATA = 6
} zfs_core_status_t;

typedef struct zfs_core_device_info {
    uint64_t size_bytes;
    uint32_t logical_sector_size;
    uint32_t physical_sector_size;
    uint32_t read_only;
    uint32_t vdev_labels_found;
    uint32_t uberblocks_found;
    uint32_t valid_labels_found;
    uint32_t unsupported_features;
    uint64_t pool_guid;
    uint64_t vdev_guid;
    uint64_t ashift;
    uint32_t mos_root_valid;
    uint64_t mos_root_offset;
    uint64_t mos_root_size;
    uint64_t selected_txg;
    uint64_t selected_uberblock_offset;
} zfs_core_device_info_t;

ZFS_CORE_API zfs_core_status_t zfs_core_open_readonly(
    const char *path, zfs_core_device_t **device);
ZFS_CORE_API void zfs_core_close(zfs_core_device_t *device);
ZFS_CORE_API zfs_core_status_t zfs_core_get_info(
    const zfs_core_device_t *device, zfs_core_device_info_t *info);
ZFS_CORE_API zfs_core_status_t zfs_core_probe_pool(
    const zfs_core_device_t *device);
ZFS_CORE_API zfs_core_status_t zfs_core_read(
    const zfs_core_device_t *device, uint64_t offset, void *buffer,
    uint64_t length, uint64_t *bytes_read);

