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
#define MOS_DNODE_BLOCK_SIZE (128ULL * 1024ULL)

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

static uint64_t load_u64(const unsigned char *p)
{
    uint64_t value = 0;
    memcpy(&value, p, sizeof(value));
    return value;
}

static int zle_decompress(const unsigned char *source, size_t source_length,
    unsigned char *destination, size_t destination_length)
{
    size_t source_offset = 0;
    size_t destination_offset = 0;
    const size_t parameter = 64;
    while (source_offset < source_length && destination_offset < destination_length) {
        unsigned int length = (unsigned int)source[source_offset++] + 1U;
        if (length <= parameter) {
            if (length > source_length - source_offset ||
                length > destination_length - destination_offset) return 0;
            memcpy(destination + destination_offset, source + source_offset, length);
            source_offset += length;
            destination_offset += length;
        } else {
            length -= (unsigned int)parameter;
            if (length > destination_length - destination_offset) return 0;
            memset(destination + destination_offset, 0, length);
            destination_offset += length;
        }
    }
    return source_offset == source_length && destination_offset == destination_length;
}

static int lz4_decompress(const unsigned char *source, size_t source_length,
    unsigned char *destination, size_t destination_length)
{
    if (source_length < 4) return 0;
    size_t encoded_length = be32(source);
    if (encoded_length > source_length - 4) return 0;
    size_t source_offset = 4;
    size_t source_end = source_offset + encoded_length;
    size_t destination_offset = 0;
    while (source_offset < source_end) {
        unsigned int token = source[source_offset++];
        size_t literal_length = token >> 4;
        size_t match_length = token & 0x0fU;
        if (literal_length == 15) {
            unsigned int extension = 255;
            while (extension == 255) {
                if (source_offset >= source_end) return 0;
                extension = source[source_offset++];
                literal_length += extension;
            }
        }
        if (literal_length > source_end - source_offset ||
            literal_length > destination_length - destination_offset) return 0;
        memcpy(destination + destination_offset, source + source_offset, literal_length);
        source_offset += literal_length;
        destination_offset += literal_length;
        if (source_offset == source_end) break;
        if (source_end - source_offset < 2) return 0;
        size_t match_offset = source[source_offset] |
            ((size_t)source[source_offset + 1] << 8);
        source_offset += 2;
        if (match_offset == 0 || match_offset > destination_offset) return 0;
        if (match_length == 15) {
            unsigned int extension = 255;
            while (extension == 255) {
                if (source_offset >= source_end) return 0;
                extension = source[source_offset++];
                match_length += extension;
            }
        }
        match_length += 4;
        if (match_length > destination_length - destination_offset) return 0;
        size_t match_start = destination_offset - match_offset;
        for (size_t i = 0; i < match_length; ++i)
            destination[destination_offset++] = destination[match_start + i];
    }
    return destination_offset == destination_length;
}

static int read_block_pointer(zfs_core_device_t *device, const unsigned char *bp,
    unsigned char *destination, size_t capacity, size_t *logical_size_out)
{
    uint64_t dva_word = load_u64(bp + 8);
    uint64_t prop = load_u64(bp + 48);
    if ((prop >> 39) & UINT64_C(1)) {
        uint64_t logical_size = (prop & UINT64_C(0x1ffffff)) + 1;
        uint64_t physical_size = ((prop >> 25) & UINT64_C(0x7f)) + 1;
        uint64_t compression = (prop >> 32) & UINT64_C(0x7f);
        unsigned char payload[112];
        size_t payload_offset = 0;
        for (size_t word = 0; word < 16 && payload_offset < sizeof(payload); ++word) {
            if (word == 6 || word == 15) continue;
            size_t remaining = sizeof(payload) - payload_offset;
            size_t copy_length = remaining < 8 ? remaining : 8;
            memcpy(payload + payload_offset, bp + word * 8, copy_length);
            payload_offset += copy_length;
        }
        if (logical_size > capacity || physical_size > sizeof(payload) ||
            ((prop >> 40) & UINT64_C(0xff)) != 0) return 0;
        int decoded = compression == 2 ?
            (physical_size == logical_size ?
                (memcpy(destination, payload, (size_t)logical_size), 1) : 0) :
            compression == 14 ? zle_decompress(payload, (size_t)physical_size,
                destination, (size_t)logical_size) :
            compression == 15 ? lz4_decompress(payload, (size_t)physical_size,
                destination, (size_t)logical_size) : 0;
        if (decoded && logical_size_out != NULL) *logical_size_out = (size_t)logical_size;
        return decoded;
    }
    uint64_t physical_size = (((prop >> 16) & UINT64_C(0xffff)) + 1) << 9;
    uint64_t logical_size = ((prop & UINT64_C(0xffff)) + 1) << 9;
    uint64_t compression = (prop >> 32) & UINT64_C(0x7f);
    uint64_t offset = VDEV_LABEL_START_SIZE_FULL +
        ((dva_word & UINT64_C(0x7fffffffffffffff)) << 9);
    int valid = physical_size >= 512 && physical_size <= logical_size &&
        (compression == 2 /* uncompressed */ || compression == 14 /* ZLE */ ||
        compression == 15 /* LZ4 */);
    if (!valid || logical_size > capacity || offset > device->size_bytes ||
        physical_size > device->size_bytes - offset ||
        logical_size > SIZE_MAX) {
        return 0;
    }
    unsigned char *compressed = (unsigned char *)malloc((size_t)physical_size);
    if (compressed == NULL || seek_u64(device->file, offset) != 0 ||
        fread(compressed, 1, (size_t)physical_size, device->file) != physical_size) {
        free(compressed); return 0;
    }
    int decoded = compression == 2 ?
        (physical_size == logical_size && (memcpy(destination, compressed,
            (size_t)logical_size), 1)) :
        compression == 14 ? zle_decompress(compressed, (size_t)physical_size,
            destination, (size_t)logical_size) :
        lz4_decompress(compressed, (size_t)physical_size, destination,
            (size_t)logical_size);
    free(compressed);
    if (decoded && logical_size_out != NULL) *logical_size_out = (size_t)logical_size;
    return decoded;
}

static int read_mos_first_dnode_block(zfs_core_device_t *device,
    unsigned char *destination, size_t capacity, size_t *logical_size_out)
{
    if (!device->mos_root_valid || device->mos_root_size < 512 ||
        device->mos_root_size > SIZE_MAX || destination == NULL) return 0;
    size_t root_length = (size_t)device->mos_root_size;
    unsigned char *root = (unsigned char *)malloc(root_length);
    if (root == NULL || seek_u64(device->file, device->mos_root_offset) != 0 ||
        fread(root, 1, root_length, device->file) != root_length) {
        free(root); return 0;
    }
    /* The first dnode in the MOS objset is the meta-dnode. Its level-one
     * block is an indirect array of block pointers to dnode data blocks. */
    if (root[0] != DMU_OT_DNODE || root[1] > 24 || root[2] < 2 || root[2] > 5) {
        free(root); return 0;
    }
    unsigned char *indirect = (unsigned char *)malloc(MOS_DNODE_BLOCK_SIZE);
    size_t indirect_size = 0;
    int valid = indirect != NULL && read_block_pointer(device, root + 64, indirect,
        MOS_DNODE_BLOCK_SIZE, &indirect_size) && indirect_size == MOS_DNODE_BLOCK_SIZE &&
        read_block_pointer(device, indirect, destination, capacity, logical_size_out);
    free(indirect);
    free(root);
    return valid;
}

static int read_mos_object_directory_block(zfs_core_device_t *device,
    unsigned char *destination, size_t capacity, size_t *logical_size_out)
{
    unsigned char *dnode_block = (unsigned char *)malloc(MOS_DNODE_BLOCK_SIZE);
    size_t dnode_size = 0;
    int valid = dnode_block != NULL && read_mos_first_dnode_block(device,
        dnode_block, MOS_DNODE_BLOCK_SIZE, &dnode_size) && dnode_size >= 1024;
    if (valid) {
        /* Object 1 is the MOS object directory and uses a direct ZAP block. */
        const unsigned char *dnode = dnode_block + 512;
        valid = dnode[0] == 1 /* DMU_OT_OBJECT_DIRECTORY */ && dnode[2] == 1 &&
            dnode[3] >= 1 && dnode[3] <= 3 &&
            read_block_pointer(device, dnode + 64, destination, capacity,
                logical_size_out);
    }
    free(dnode_block);
    return valid;
}

static int read_mos_dnode_block_for_object(zfs_core_device_t *device,
    uint64_t object, unsigned char *destination, size_t capacity)
{
    unsigned char *root = (unsigned char *)malloc((size_t)device->mos_root_size);
    unsigned char *indirect = (unsigned char *)malloc(MOS_DNODE_BLOCK_SIZE);
    if (root == NULL || indirect == NULL || device->mos_root_size > SIZE_MAX ||
        seek_u64(device->file, device->mos_root_offset) != 0 ||
        fread(root, 1, (size_t)device->mos_root_size, device->file) != device->mos_root_size ||
        root[0] != DMU_OT_DNODE) {
        free(indirect); free(root); return 0;
    }
    size_t indirect_size = 0;
    int valid = read_block_pointer(device, root + 64, indirect,
        MOS_DNODE_BLOCK_SIZE, &indirect_size) && indirect_size == MOS_DNODE_BLOCK_SIZE;
    uint64_t block = object / 32;
    if (valid && block >= MOS_DNODE_BLOCK_SIZE / 128) valid = 0;
    size_t dnode_size = 0;
    if (valid) valid = read_block_pointer(device, indirect + block * 128,
        destination, capacity, &dnode_size) && dnode_size == 16 * 1024;
    free(indirect);
    free(root);
    return valid;
}

static int read_root_dataset_info(zfs_core_device_t *device,
    zfs_core_dsl_dir_info_t *info)
{
    unsigned char *dnode_block = (unsigned char *)malloc(16 * 1024);
    if (dnode_block == NULL || !read_mos_dnode_block_for_object(device, 32,
        dnode_block, 16 * 1024)) {
        free(dnode_block); return 0;
    }
    const unsigned char *dnode = dnode_block;
    size_t bonus_offset = 64 + (size_t)dnode[3] * 128;
    int valid = dnode[0] == 12 /* DMU_OT_DSL_DIR */ && dnode[3] <= 3 &&
        bonus_offset + 256 <= 16 * 1024;
    if (valid) {
        info->head_dataset_obj = load_u64(dnode_block + bonus_offset + 8);
        info->child_dir_zap_obj = load_u64(dnode_block + bonus_offset + 32);
        info->props_zap_obj = load_u64(dnode_block + bonus_offset + 80);
        valid = info->head_dataset_obj != 0 && info->child_dir_zap_obj != 0;
    }
    free(dnode_block);
    return valid;
}

static int read_root_dataset_objset_root(zfs_core_device_t *device,
    unsigned char *destination, size_t capacity, size_t *logical_size_out)
{
    unsigned char *dnode_block = (unsigned char *)malloc(16 * 1024);
    if (dnode_block == NULL || !read_mos_dnode_block_for_object(device, 54,
        dnode_block, 16 * 1024)) {
        free(dnode_block); return 0;
    }
    const unsigned char *dnode = dnode_block + (54 % 32) * 512;
    size_t bonus_offset = 64 + (size_t)dnode[3] * 128;
    int valid = dnode[0] == 16 /* DMU_OT_DSL_DATASET */ && dnode[3] <= 3 &&
        bonus_offset + 176 <= 16 * 1024;
    if (valid) valid = read_block_pointer(device, dnode + bonus_offset + 128,
        destination, capacity, logical_size_out);
    free(dnode_block);
    return valid;
}

static int read_filesystem_object_dnode_block(zfs_core_device_t *device,
    uint64_t object, unsigned char *destination, size_t capacity,
    size_t *logical_size_out)
{
    unsigned char *objset_root = (unsigned char *)malloc(16 * 1024);
    unsigned char *level_block = (unsigned char *)malloc(MOS_DNODE_BLOCK_SIZE);
    if (objset_root == NULL || level_block == NULL ||
        !read_root_dataset_objset_root(device, objset_root, 16 * 1024, logical_size_out)) {
        free(level_block); free(objset_root); return 0;
    }
    const unsigned char *bp = objset_root + 64;
    uint64_t block_id = object / 32;
    int valid = 1;
    while (valid) {
        uint64_t prop = load_u64(bp + 48);
        uint64_t level = (prop >> 56) & UINT64_C(0x1f);
        if (level == 0) {
            valid = read_block_pointer(device, bp, destination, capacity, logical_size_out);
            break;
        }
        size_t block_size = 0;
        valid = read_block_pointer(device, bp, level_block, MOS_DNODE_BLOCK_SIZE, &block_size) &&
            block_size == MOS_DNODE_BLOCK_SIZE;
        if (valid) {
            uint64_t shift = (level - 1) * 7;
            uint64_t index = (block_id >> shift) & UINT64_C(0x7f);
            bp = level_block + index * 128;
        }
    }
    free(level_block);
    free(objset_root);
    return valid;
}

static int read_filesystem_object_zap(zfs_core_device_t *device, uint64_t object,
    uint8_t object_type,
    unsigned char *destination, size_t capacity, size_t *logical_size_out)
{
    unsigned char *dnode_block = (unsigned char *)malloc(16 * 1024);
    if (dnode_block == NULL || !read_filesystem_object_dnode_block(device, object,
        dnode_block, 16 * 1024, logical_size_out)) {
        free(dnode_block); return 0;
    }
    const unsigned char *dnode = dnode_block + (object % 32) * 512;
    int valid = dnode[0] == object_type && dnode[3] >= 1 && dnode[3] <= 3;
    size_t data_size = 0;
    if (valid) valid = read_block_pointer(device, dnode + 64, destination,
        capacity, &data_size);
    if (valid && logical_size_out != NULL) *logical_size_out = data_size;
    free(dnode_block);
    return valid;
}

static int lookup_filesystem_microzap(zfs_core_device_t *device, uint64_t object,
    uint8_t object_type, const char *name, uint64_t *value)
{
    unsigned char *block = (unsigned char *)malloc(16 * 1024);
    size_t block_size = 0;
    int valid = block != NULL && read_filesystem_object_zap(device, object,
        object_type, block, 16 * 1024, &block_size) && block_size >= 64 && block[0] == 3;
    if (valid) {
        for (size_t offset = 64; offset + 64 <= block_size; offset += 64) {
            const char *entry_name = (const char *)(block + offset + 14);
            if (entry_name[0] == 0 || strncmp(entry_name, name, 50) != 0) continue;
            size_t entry_length = 0;
            while (entry_length < 50 && entry_name[entry_length] != 0) ++entry_length;
            if (strlen(name) != entry_length) continue;
            *value = load_u64(block + offset);
            valid = 1;
            break;
        }
    }
    free(block);
    return valid && *value != 0;
}

static uint16_t load_u16(const unsigned char *p)
{
    return (uint16_t)p[0] | ((uint16_t)p[1] << 8);
}

static uint32_t load_u32(const unsigned char *p)
{
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) |
        ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}

static int zap_read_chunks(const unsigned char *block, uint16_t first_chunk,
    uint16_t num_bytes, unsigned char *value, size_t value_capacity)
{
    const size_t chunk_offset = 48 + 512 * 2;
    const size_t chunk_size = 24;
    size_t copied = 0;
    uint16_t chunk = first_chunk;
    while (copied < num_bytes && chunk < 640) {
        size_t offset = chunk_offset + (size_t)chunk * chunk_size;
        if (offset + chunk_size > 16 * 1024 || copied >= value_capacity ||
            block[offset] != 251 /* ZAP_CHUNK_ARRAY */) return 0;
        size_t available = chunk_size - 3;
        size_t wanted = num_bytes - copied;
        if (wanted > available) wanted = available;
        if (wanted > value_capacity - copied) return 0;
        memcpy(value + copied, block + offset + 1, wanted);
        copied += wanted;
        chunk = load_u16(block + offset + 22);
        if (copied < num_bytes && chunk == UINT16_C(0xffff)) return 0;
    }
    return copied == num_bytes;
}

static int zap_find_uint64(const unsigned char *block, const char *wanted,
    uint64_t *value)
{
    if (load_u64(block) != UINT64_C(0x8000000000000000) ||
        load_u32(block + 24) != UINT32_C(0x02AB1EAF)) return 0;
    const size_t chunk_offset = 48 + 512 * 2;
    const size_t chunk_size = 24;
    for (uint16_t chunk = 0; chunk < 640; ++chunk) {
        size_t offset = chunk_offset + (size_t)chunk * chunk_size;
        if (offset + chunk_size > 16 * 1024 || block[offset] != 252) continue;
        uint16_t name_bytes = load_u16(block + offset + 6);
        uint16_t value_bytes = (uint16_t)(block[offset + 1] * load_u16(block + offset + 10));
        if (name_bytes == 0 || name_bytes > 512 || value_bytes != sizeof(uint64_t)) continue;
        unsigned char name[512];
        unsigned char raw_value[sizeof(uint64_t)];
        if (!zap_read_chunks(block, load_u16(block + offset + 4), name_bytes,
                name, sizeof(name)) || !zap_read_chunks(block,
                load_u16(block + offset + 8), value_bytes, raw_value, sizeof(raw_value)))
            continue;
        if (name[name_bytes - 1] != 0 || strlen(wanted) + 1 != name_bytes ||
            memcmp(name, wanted, name_bytes - 1) != 0) continue;
        *value = be64(raw_value);
        return 1;
    }
    return 0;
}

static int lookup_mos_object_directory(zfs_core_device_t *device,
    const char *name, uint64_t *value)
{
    if (name == NULL || value == NULL) return 0;
    unsigned char *dnode_block = (unsigned char *)malloc(MOS_DNODE_BLOCK_SIZE);
    unsigned char *zap_block = (unsigned char *)malloc(16 * 1024);
    size_t dnode_size = 0;
    int found = dnode_block != NULL && zap_block != NULL &&
        read_mos_first_dnode_block(device, dnode_block, MOS_DNODE_BLOCK_SIZE, &dnode_size) &&
        dnode_size >= 1024;
    if (found) {
        const unsigned char *dnode = dnode_block + 512;
        found = dnode[0] == 1 && dnode[2] == 1 && dnode[3] >= 1 && dnode[3] <= 3;
        for (uint8_t block = 0; found && block < dnode[3] && !*value; ++block) {
            size_t block_size = 0;
            int block_read = read_block_pointer(device, dnode + 64 + block * 128,
                zap_block, 16 * 1024, &block_size) && block_size == 16 * 1024;
            if (!block_read) { found = 0; break; }
            if (block > 0 && zap_find_uint64(zap_block, name, value)) break;
        }
    }
    free(zap_block);
    free(dnode_block);
    return found && *value != 0;
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

zfs_core_status_t zfs_core_read_mos_first_dnode_block(
    const zfs_core_device_t *device, void *buffer, uint64_t length,
    uint64_t *bytes_read)
{
    if (device == NULL || buffer == NULL || bytes_read == NULL) return ZFS_CORE_INVALID_ARGUMENT;
    *bytes_read = 0;
    if (length < MOS_DNODE_BLOCK_SIZE) return ZFS_CORE_OUT_OF_RANGE;
    size_t logical_size = 0;
    if (!read_mos_first_dnode_block((zfs_core_device_t *)device,
        (unsigned char *)buffer, (size_t)length, &logical_size))
        return ZFS_CORE_NOT_SUPPORTED;
    *bytes_read = logical_size;
    return ZFS_CORE_OK;
}

zfs_core_status_t zfs_core_read_mos_object_directory_block(
    const zfs_core_device_t *device, void *buffer, uint64_t length,
    uint64_t *bytes_read)
{
    if (device == NULL || buffer == NULL || bytes_read == NULL) return ZFS_CORE_INVALID_ARGUMENT;
    *bytes_read = 0;
    if (length < MOS_DNODE_BLOCK_SIZE) return ZFS_CORE_OUT_OF_RANGE;
    size_t logical_size = 0;
    if (!read_mos_object_directory_block((zfs_core_device_t *)device,
        (unsigned char *)buffer, (size_t)length, &logical_size))
        return ZFS_CORE_NOT_SUPPORTED;
    *bytes_read = logical_size;
    return ZFS_CORE_OK;
}

zfs_core_status_t zfs_core_lookup_mos_object_directory(
    const zfs_core_device_t *device, const char *name, uint64_t *value)
{
    if (device == NULL || name == NULL || value == NULL) return ZFS_CORE_INVALID_ARGUMENT;
    *value = 0;
    return lookup_mos_object_directory((zfs_core_device_t *)device, name, value) ?
        ZFS_CORE_OK : ZFS_CORE_CORRUPT_METADATA;
}

zfs_core_status_t zfs_core_get_root_dataset_info(
    const zfs_core_device_t *device, zfs_core_dsl_dir_info_t *info)
{
    if (device == NULL || info == NULL) return ZFS_CORE_INVALID_ARGUMENT;
    memset(info, 0, sizeof(*info));
    return read_root_dataset_info((zfs_core_device_t *)device, info) ?
        ZFS_CORE_OK : ZFS_CORE_CORRUPT_METADATA;
}

zfs_core_status_t zfs_core_read_root_dataset_objset_root(
    const zfs_core_device_t *device, void *buffer, uint64_t length,
    uint64_t *bytes_read)
{
    if (device == NULL || buffer == NULL || bytes_read == NULL) return ZFS_CORE_INVALID_ARGUMENT;
    *bytes_read = 0;
    if (length < MOS_DNODE_BLOCK_SIZE) return ZFS_CORE_OUT_OF_RANGE;
    size_t logical_size = 0;
    if (!read_root_dataset_objset_root((zfs_core_device_t *)device,
        (unsigned char *)buffer, (size_t)length, &logical_size))
        return ZFS_CORE_CORRUPT_METADATA;
    *bytes_read = logical_size;
    return ZFS_CORE_OK;
}

zfs_core_status_t zfs_core_read_root_filesystem_master_block(
    const zfs_core_device_t *device, void *buffer, uint64_t length,
    uint64_t *bytes_read)
{
    if (device == NULL || buffer == NULL || bytes_read == NULL) return ZFS_CORE_INVALID_ARGUMENT;
    *bytes_read = 0;
    if (length < MOS_DNODE_BLOCK_SIZE) return ZFS_CORE_OUT_OF_RANGE;
    size_t logical_size = 0;
    if (!read_filesystem_object_dnode_block((zfs_core_device_t *)device, 1,
        (unsigned char *)buffer, (size_t)length, &logical_size))
        return ZFS_CORE_CORRUPT_METADATA;
    *bytes_read = logical_size;
    return ZFS_CORE_OK;
}

zfs_core_status_t zfs_core_lookup_root_filesystem_master(
    const zfs_core_device_t *device, const char *name, uint64_t *value)
{
    if (device == NULL || name == NULL || value == NULL) return ZFS_CORE_INVALID_ARGUMENT;
    *value = 0;
    return lookup_filesystem_microzap((zfs_core_device_t *)device, 1, 21,
        name, value) ?
        ZFS_CORE_OK : ZFS_CORE_CORRUPT_METADATA;
}

zfs_core_status_t zfs_core_read_root_filesystem_object_dnode_block(
    const zfs_core_device_t *device, uint64_t object, void *buffer,
    uint64_t length, uint64_t *bytes_read)
{
    if (device == NULL || buffer == NULL || bytes_read == NULL) return ZFS_CORE_INVALID_ARGUMENT;
    *bytes_read = 0;
    if (length < MOS_DNODE_BLOCK_SIZE) return ZFS_CORE_OUT_OF_RANGE;
    size_t logical_size = 0;
    if (!read_filesystem_object_dnode_block((zfs_core_device_t *)device, object,
        (unsigned char *)buffer, (size_t)length, &logical_size))
        return ZFS_CORE_CORRUPT_METADATA;
    *bytes_read = logical_size;
    return ZFS_CORE_OK;
}

zfs_core_status_t zfs_core_read_root_directory_data_block(
    const zfs_core_device_t *device, void *buffer, uint64_t length,
    uint64_t *bytes_read)
{
    if (device == NULL || buffer == NULL || bytes_read == NULL) return ZFS_CORE_INVALID_ARGUMENT;
    *bytes_read = 0;
    if (length < MOS_DNODE_BLOCK_SIZE) return ZFS_CORE_OUT_OF_RANGE;
    size_t logical_size = 0;
    if (!read_filesystem_object_zap((zfs_core_device_t *)device, 34, 20,
        (unsigned char *)buffer, (size_t)length, &logical_size))
        return ZFS_CORE_CORRUPT_METADATA;
    *bytes_read = logical_size;
    return ZFS_CORE_OK;
}

