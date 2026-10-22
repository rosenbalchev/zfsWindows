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

typedef struct zfs_core_dsl_dir_info {
    uint64_t head_dataset_obj;
    uint64_t child_dir_zap_obj;
    uint64_t props_zap_obj;
} zfs_core_dsl_dir_info_t;

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
ZFS_CORE_API zfs_core_status_t zfs_core_read_mos_root(
    const zfs_core_device_t *device, void *buffer, uint64_t length,
    uint64_t *bytes_read);
ZFS_CORE_API zfs_core_status_t zfs_core_read_mos_first_dnode_block(
    const zfs_core_device_t *device, void *buffer, uint64_t length,
    uint64_t *bytes_read);
ZFS_CORE_API zfs_core_status_t zfs_core_read_mos_object_directory_block(
    const zfs_core_device_t *device, void *buffer, uint64_t length,
    uint64_t *bytes_read);
ZFS_CORE_API zfs_core_status_t zfs_core_lookup_mos_object_directory(
    const zfs_core_device_t *device, const char *name, uint64_t *value);
ZFS_CORE_API zfs_core_status_t zfs_core_get_root_dataset_info(
    const zfs_core_device_t *device, zfs_core_dsl_dir_info_t *info);
ZFS_CORE_API zfs_core_status_t zfs_core_read_root_dataset_objset_root(
    const zfs_core_device_t *device, void *buffer, uint64_t length,
    uint64_t *bytes_read);
ZFS_CORE_API zfs_core_status_t zfs_core_read_root_filesystem_master_block(
    const zfs_core_device_t *device, void *buffer, uint64_t length,
    uint64_t *bytes_read);
ZFS_CORE_API zfs_core_status_t zfs_core_lookup_root_filesystem_master(
    const zfs_core_device_t *device, const char *name, uint64_t *value);
ZFS_CORE_API zfs_core_status_t zfs_core_read_root_filesystem_object_dnode_block(
    const zfs_core_device_t *device, uint64_t object, void *buffer,
    uint64_t length, uint64_t *bytes_read);
ZFS_CORE_API zfs_core_status_t zfs_core_read_root_directory_data_block(
    const zfs_core_device_t *device, void *buffer, uint64_t length,
    uint64_t *bytes_read);
ZFS_CORE_API zfs_core_status_t zfs_core_lookup_root_directory(
    const zfs_core_device_t *device, const char *name, uint64_t *object);
ZFS_CORE_API zfs_core_status_t zfs_core_read_root_file(
    const zfs_core_device_t *device, uint64_t object, void *buffer,
    uint64_t length, uint64_t *bytes_read);

