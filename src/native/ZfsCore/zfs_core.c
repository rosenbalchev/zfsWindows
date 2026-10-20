#include "zfs_core.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define VDEV_LABEL_SIZE (256ULL * 1024ULL)
#define VDEV_LABEL_COUNT 4U
#define VDEV_LABEL_START_SIZE (2ULL * VDEV_LABEL_SIZE)
#define VDEV_LABEL_END_SIZE (2ULL * VDEV_LABEL_SIZE)

struct zfs_core_device {
    FILE *file;
    uint64_t size_bytes;
    uint32_t labels_found;
};

static int seek_u64(FILE *file, uint64_t offset)
{
#if defined(_WIN32)
    return _fseeki64(file, (__int64)offset, SEEK_SET);
#else
    return fseeko(file, (off_t)offset, SEEK_SET);
#endif
}

static uint64_t label_offset(uint64_t size, uint32_t label)
{
    if (label < 2U) {
        return (uint64_t)label * VDEV_LABEL_SIZE;
    }
    return size - (uint64_t)(VDEV_LABEL_COUNT - label) * VDEV_LABEL_SIZE;
}

static int label_has_payload(FILE *file, uint64_t offset)
{
    unsigned char header[8];
    if (seek_u64(file, offset) != 0 || fread(header, 1, sizeof(header), file) != sizeof(header)) {
        return 0;
    }
    for (size_t i = 0; i < sizeof(header); ++i) {
        if (header[i] != 0) {
            return 1;
        }
    }
    return 0;
}

zfs_core_status_t zfs_core_open_readonly(const char *path, zfs_core_device_t **device)
{
    if (path == NULL || device == NULL) return ZFS_CORE_INVALID_ARGUMENT;
    *device = NULL;
    FILE *file = NULL;
#if defined(_WIN32)
    if (fopen_s(&file, path, "rb") != 0) file = NULL;
#else
    file = fopen(path, "rb");
#endif
    if (file == NULL) return ZFS_CORE_IO_ERROR;
    if (fseek(file, 0, SEEK_END) != 0) {
        fclose(file);
        return ZFS_CORE_IO_ERROR;
    }
#if defined(_WIN32)
    __int64 end = _ftelli64(file);
#else
    off_t end = ftello(file);
#endif
    if (end <= 0) {
        fclose(file);
        return ZFS_CORE_INVALID_ARGUMENT;
    }
    zfs_core_device_t *result = (zfs_core_device_t *)calloc(1, sizeof(*result));
    if (result == NULL) {
        fclose(file);
        return ZFS_CORE_IO_ERROR;
    }
    result->file = file;
    result->size_bytes = (uint64_t)end;
    if (result->size_bytes >= VDEV_LABEL_START_SIZE + VDEV_LABEL_END_SIZE) {
        for (uint32_t i = 0; i < VDEV_LABEL_COUNT; ++i) {
            if (label_has_payload(file, label_offset(result->size_bytes, i))) ++result->labels_found;
        }
    }
    (void)seek_u64(file, 0);
    *device = result;
    return ZFS_CORE_OK;
}

void zfs_core_close(zfs_core_device_t *device)
{
    if (device == NULL) return;
    if (device->file != NULL) fclose(device->file);
    free(device);
}

zfs_core_status_t zfs_core_get_info(const zfs_core_device_t *device, zfs_core_device_info_t *info)
{
    if (device == NULL || info == NULL) return ZFS_CORE_INVALID_ARGUMENT;
    info->size_bytes = device->size_bytes;
    info->logical_sector_size = 512U;
    info->physical_sector_size = 4096U;
    info->read_only = 1U;
    info->vdev_labels_found = device->labels_found;
    return ZFS_CORE_OK;
}

zfs_core_status_t zfs_core_read(const zfs_core_device_t *device, uint64_t offset,
    void *buffer, uint64_t length, uint64_t *bytes_read)
{
    if (device == NULL || buffer == NULL || bytes_read == NULL) return ZFS_CORE_INVALID_ARGUMENT;
    *bytes_read = 0;
    if (offset > device->size_bytes || length > device->size_bytes - offset) return ZFS_CORE_OUT_OF_RANGE;
    if (length > (uint64_t)SIZE_MAX || seek_u64(device->file, offset) != 0) return ZFS_CORE_OUT_OF_RANGE;
    size_t actual = fread(buffer, 1, (size_t)length, device->file);
    *bytes_read = (uint64_t)actual;
    return actual == (size_t)length ? ZFS_CORE_OK : ZFS_CORE_IO_ERROR;
}

