#include "zfs_core.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define VDEV_LABEL_SIZE (256ULL * 1024ULL)
#define VDEV_LABEL_COUNT 4U
#define VDEV_LABEL_START_SIZE (2ULL * VDEV_LABEL_SIZE)
#define VDEV_LABEL_END_SIZE (2ULL * VDEV_LABEL_SIZE)
#define VDEV_LABEL_START_SIZE_FULL (4ULL * 1024ULL * 1024ULL)
#define VDEV_SKIP_SIZE (16ULL * 1024ULL)
#define VDEV_PHYS_SIZE (112ULL * 1024ULL)
#define VDEV_UBERBLOCK_RING_OFFSET (128ULL * 1024ULL)
#define VDEV_UBERBLOCK_RING_SIZE (128ULL * 1024ULL)
#define VDEV_UBERBLOCK_SLOT_SIZE (1ULL * 1024ULL)
#define UBERBLOCK_MAGIC UINT64_C(0x00bab10c)
#define ZEC_MAGIC UINT64_C(0x0210da7ab10c7a11)
#define DMU_OT_DNODE 10U

struct zfs_core_device {
    FILE *file;
    uint64_t size_bytes;
    uint32_t labels_found;
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
};

static int seek_u64(FILE *file, uint64_t offset);

static uint64_t swap_u64(uint64_t value)
{
    return ((value & UINT64_C(0x00000000000000ff)) << 56) |
        ((value & UINT64_C(0x000000000000ff00)) << 40) |
        ((value & UINT64_C(0x0000000000ff0000)) << 24) |
        ((value & UINT64_C(0x00000000ff000000)) << 8) |
        ((value & UINT64_C(0x000000ff00000000)) >> 8) |
        ((value & UINT64_C(0x0000ff0000000000)) >> 24) |
        ((value & UINT64_C(0x00ff000000000000)) >> 40) |
        ((value & UINT64_C(0xff00000000000000)) >> 56);
}

static uint32_t be32(const unsigned char *p)
{
    return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16) |
        ((uint32_t)p[2] << 8) | p[3];
}

static uint64_t be64(const unsigned char *p)
{
    return ((uint64_t)be32(p) << 32) | be32(p + 4);
}

static void fletcher4(const unsigned char *data, size_t length, uint64_t out[4])
{
    uint64_t a = 0, b = 0, c = 0, d = 0;
    for (size_t i = 0; i + sizeof(uint32_t) <= length; i += sizeof(uint32_t)) {
        uint32_t word = 0;
        memcpy(&word, data + i, sizeof(word));
        a += word; b += a; c += b; d += c;
    }
    out[0] = a; out[1] = b; out[2] = c; out[3] = d;
}

static int embedded_checksum_valid(const unsigned char *block, size_t length, uint64_t verifier)
{
    if (length < 40) return 0;
    const unsigned char *eck = block + length - 40;
    uint64_t magic = 0;
    memcpy(&magic, eck, sizeof(magic));
    if (magic != ZEC_MAGIC && magic != swap_u64(ZEC_MAGIC)) return 0;
    unsigned char *copy = (unsigned char *)malloc(length);
    if (copy == NULL) return 0;
    memcpy(copy, block, length);
    memset(copy + length - 32, 0, 32);
    memcpy(copy + length - 32, &verifier, sizeof(verifier));
    uint64_t calculated[4];
    fletcher4(copy, length, calculated);
    free(copy);
    for (size_t i = 0; i < 4; ++i) {
        uint64_t stored = 0;
        memcpy(&stored, eck + 8 + i * 8, sizeof(stored));
        if (stored != calculated[i] && stored != swap_u64(calculated[i])) return 0;
    }
    return 1;
}

static int nvlist_has_key(const unsigned char *buffer, size_t length,
    const char *wanted, uint64_t *value)
{
    if (length < 12 || buffer[0] != 1 || buffer[1] != 1) return 0;
    size_t offset = 12;
    while (offset + 16 <= length) {
        uint32_t encoded = be32(buffer + offset);
        uint32_t decoded = be32(buffer + offset + 4);
        uint32_t name_length = be32(buffer + offset + 8);
        if (encoded < 24 || decoded < 16 || encoded > length - offset || name_length == 0) return 0;
        size_t name_offset = offset + 12;
        size_t name_bytes = (name_length + 3U) & ~3U;
        size_t type_offset = name_offset + name_bytes;
        if (name_bytes > encoded - 12 || type_offset + 4 > length) return 0;
        uint32_t type = be32(buffer + type_offset);
        if (strncmp((const char *)(buffer + name_offset), wanted, name_length) == 0 &&
            (type == 8U || (strcmp(wanted, "vdev_tree") == 0 && type == 19U)) &&
            value != NULL && type_offset + 12 <= offset + encoded) {
            /* nvpair payloads begin with a 32-bit element count. */
            *value = be64(buffer + type_offset + 8);
            return 1;
        }
        offset += encoded;
    }
    return 0;
}

static int decode_label(zfs_core_device_t *device, uint64_t label)
{
    unsigned char *buffer = (unsigned char *)malloc(VDEV_PHYS_SIZE);
    if (buffer == NULL || label > device->size_bytes ||
        VDEV_SKIP_SIZE + VDEV_PHYS_SIZE > device->size_bytes - label ||
        seek_u64(device->file, label + VDEV_SKIP_SIZE) != 0 ||
        fread(buffer, 1, VDEV_PHYS_SIZE, device->file) != VDEV_PHYS_SIZE) {
        free(buffer); return 0;
    }
    uint64_t pool_guid = 0, vdev_guid = 0, ashift = 0, state = 0;
    int valid = nvlist_has_key(buffer, VDEV_PHYS_SIZE - 40, "pool_guid", &pool_guid) &&
        nvlist_has_key(buffer, VDEV_PHYS_SIZE - 40, "guid", &vdev_guid) &&
        nvlist_has_key(buffer, VDEV_PHYS_SIZE - 40, "vdev_tree", &ashift) &&
        nvlist_has_key(buffer, VDEV_PHYS_SIZE - 40, "state", &state) &&
        pool_guid != 0 && vdev_guid != 0 && state <= 2;
    if (valid) {
        /* The leaf ashift is nested in vdev_tree; 12 is the OpenZFS default.
         * Full recursive topology decoding remains an explicit follow-up. */
        device->ashift = 12;
        device->pool_guid = pool_guid; device->vdev_guid = vdev_guid;
        ++device->valid_labels_found;
    }
    free(buffer);
    return valid;
}

static int validate_mos_root(zfs_core_device_t *device)
{
    if (!device->mos_root_valid || device->mos_root_size < 512 ||
        device->mos_root_size > SIZE_MAX) return 0;
    size_t length = (size_t)device->mos_root_size;
    unsigned char *block = (unsigned char *)malloc(length);
    if (block == NULL || seek_u64(device->file, device->mos_root_offset) != 0 ||
        fread(block, 1, length, device->file) != length) {
        free(block); return 0;
    }
    /* The MOS root block is a dnode block. Each legacy dnode is 512 bytes. */
    int valid = block[0] == DMU_OT_DNODE && block[2] >= 1 && block[2] <= 5 &&
        block[3] >= 1 && block[3] <= 16 && (length % 512U) == 0;
    free(block);
    return valid;
}

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
    if (seek_u64(file, offset + VDEV_SKIP_SIZE) != 0 || fread(header, 1, sizeof(header), file) != sizeof(header)) {
        return 0;
    }
    for (size_t i = 0; i < sizeof(header); ++i) {
        if (header[i] != 0) {
            return 1;
        }
    }
    return 0;
}

static void scan_uberblocks(zfs_core_device_t *device, uint64_t label)
{
    for (uint64_t slot = 0; slot < VDEV_UBERBLOCK_RING_SIZE / VDEV_UBERBLOCK_SLOT_SIZE; ++slot) {
        uint64_t offset = label + VDEV_UBERBLOCK_RING_OFFSET + slot * VDEV_UBERBLOCK_SLOT_SIZE;
        unsigned char fields[1024];
        if (offset > device->size_bytes || sizeof(fields) > device->size_bytes - offset ||
            seek_u64(device->file, offset) != 0 || fread(fields, 1, sizeof(fields), device->file) != sizeof(fields)) {
            continue;
        }
        uint64_t magic = 0;
        uint64_t txg = 0;
        memcpy(&magic, fields, sizeof(magic));
        memcpy(&txg, fields + 16, sizeof(txg));
        if (magic == UBERBLOCK_MAGIC) {
            /* native little-endian representation */
        } else if (swap_u64(magic) == UBERBLOCK_MAGIC) {
            txg = swap_u64(txg);
        } else {
            continue;
        }
        /* Uberblocks are fixed-size records; their embedded root block pointer
         * carries the metadata checksum. Full blkptr verification is handled
         * by the MOS reader in the next implementation stage. */
        if (txg == 0) continue;
        ++device->uberblocks_found;
        if (txg >= device->selected_txg) {
            device->selected_txg = txg;
            device->selected_uberblock_offset = offset;
            uint64_t dva_word = 0;
            uint64_t prop = 0;
            /* DVA word 1 contains the sector offset; word 0 is asize/vdev. */
            memcpy(&dva_word, fields + 48, sizeof(dva_word));
            memcpy(&prop, fields + 88, sizeof(prop));
            uint64_t dva_offset = (dva_word & UINT64_C(0x7fffffffffffffff)) << 9;
            uint64_t logical_size = (((prop >> 0) & UINT64_C(0xffff)) + 1) << 9;
            device->mos_root_offset = VDEV_LABEL_START_SIZE_FULL + dva_offset;
            device->mos_root_size = logical_size;
            device->mos_root_valid = logical_size >= 512 && logical_size <= 16 * 1024 * 1024 &&
                device->mos_root_offset <= device->size_bytes &&
                logical_size <= device->size_bytes - device->mos_root_offset;
        }
    }
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
            uint64_t label = label_offset(result->size_bytes, i);
            if (label_has_payload(file, label)) ++result->labels_found;
            if (decode_label(result, label)) scan_uberblocks(result, label);
        }
        if (result->mos_root_valid) result->mos_root_valid = validate_mos_root(result);
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
    info->uberblocks_found = device->uberblocks_found;
    info->valid_labels_found = device->valid_labels_found;
    info->unsupported_features = device->unsupported_features;
    info->pool_guid = device->pool_guid;
    info->vdev_guid = device->vdev_guid;
    info->ashift = device->ashift;
    info->mos_root_valid = device->mos_root_valid;
    info->mos_root_offset = device->mos_root_offset;
    info->mos_root_size = device->mos_root_size;
    info->selected_txg = device->selected_txg;
    info->selected_uberblock_offset = device->selected_uberblock_offset;
    return ZFS_CORE_OK;
}

zfs_core_status_t zfs_core_probe_pool(const zfs_core_device_t *device)
{
    if (device == NULL) return ZFS_CORE_INVALID_ARGUMENT;
    return device->valid_labels_found > 0 && device->uberblocks_found > 0 ? ZFS_CORE_OK : ZFS_CORE_NOT_A_ZFS_DEVICE;
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

zfs_core_status_t zfs_core_read_mos_root(const zfs_core_device_t *device,
    void *buffer, uint64_t length, uint64_t *bytes_read)
{
    if (device == NULL || buffer == NULL || bytes_read == NULL)
        return ZFS_CORE_INVALID_ARGUMENT;
    if (!device->mos_root_valid || length < device->mos_root_size)
        return ZFS_CORE_OUT_OF_RANGE;
    return zfs_core_read(device, device->mos_root_offset, buffer,
        device->mos_root_size, bytes_read);
}

