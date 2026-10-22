#include "zfs_core.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#if defined(_WIN32)
#include <windows.h>
#include <bcrypt.h>
#pragma comment(lib, "bcrypt.lib")
#endif

#define VDEV_LABEL_SIZE (256ULL * 1024ULL)
#define VDEV_LABEL_COUNT 4U
#define VDEV_LABEL_START_SIZE (2ULL * VDEV_LABEL_SIZE)
#define VDEV_LABEL_END_SIZE (2ULL * VDEV_LABEL_SIZE)
#define VDEV_LABEL_START_SIZE_FULL (4ULL * 1024ULL * 1024ULL)
#define VDEV_SKIP_SIZE (16ULL * 1024ULL)
#define VDEV_PHYS_SIZE (112ULL * 1024ULL)
#define VDEV_UBERBLOCK_RING_OFFSET (128ULL * 1024ULL)
#define VDEV_UBERBLOCK_RING_SIZE (128ULL * 1024ULL)
#define UBERBLOCK_MIN_SHIFT 10U
#define UBERBLOCK_MAX_SHIFT 13U
#define UBERBLOCK_MAGIC UINT64_C(0x00bab10c)
#define ZEC_MAGIC UINT64_C(0x0210da7ab10c7a11)
#define DMU_OT_DNODE 10U
#define MOS_DNODE_BLOCK_SIZE (128ULL * 1024ULL)

int LZ4_uncompress_unknownOutputSize(const char *source, char *destination,
    int source_length, int destination_length);

static uint16_t load_u16(const unsigned char *p);
static uint32_t load_u32(const unsigned char *p);
static int read_sa_file_size(zfs_core_device_t *device,
    const unsigned char *dnode, size_t bonus_offset, uint16_t bonus_length,
    uint64_t *file_size);

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
    unsigned char mos_root_bp[128];
    uint64_t selected_txg;
    uint64_t selected_uberblock_offset;
};

static int seek_u64(FILE *file, uint64_t offset);
static int read_block_pointer(zfs_core_device_t *device, const unsigned char *bp,
    unsigned char *destination, size_t capacity, size_t *logical_size_out);

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

static uint32_t swap_u32(uint32_t value)
{
    return ((value & UINT32_C(0x000000ff)) << 24) |
        ((value & UINT32_C(0x0000ff00)) << 8) |
        ((value & UINT32_C(0x00ff0000)) >> 8) |
        ((value & UINT32_C(0xff000000)) >> 24);
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

static int sha256(const unsigned char *data, size_t length,
    unsigned char digest[32])
{
#if defined(_WIN32)
    if (length > ULONG_MAX) return 0;
    BCRYPT_ALG_HANDLE algorithm = NULL;
    NTSTATUS status = BCryptOpenAlgorithmProvider(&algorithm,
        BCRYPT_SHA256_ALGORITHM, NULL, 0);
    if (status >= 0) status = BCryptHash(algorithm, NULL, 0,
        (PUCHAR)data, (ULONG)length, digest, 32);
    if (algorithm != NULL) BCryptCloseAlgorithmProvider(algorithm, 0);
    return status >= 0;
#else
    (void)data; (void)length; (void)digest;
    return 0;
#endif
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
    unsigned char digest[32];
    int hashed = sha256(copy, length, digest);
    free(copy);
    if (!hashed) return 0;
    for (size_t i = 0; i < 4; ++i) {
        uint64_t stored = 0;
        memcpy(&stored, eck + 8 + i * 8, sizeof(stored));
        uint64_t calculated = be64(digest + i * sizeof(uint64_t));
        if (stored != calculated && stored != swap_u64(calculated)) return 0;
    }
    return 1;
}

#define DATA_TYPE_UINT64 8U
#define DATA_TYPE_STRING 9U
#define DATA_TYPE_NVLIST 19U

typedef struct nvpair_view {
    const unsigned char *name;
    uint32_t name_length;
    uint32_t type;
    uint32_t elements;
    const unsigned char *value;
    size_t value_length;
} nvpair_view_t;

static int nvlist_next_pair(const unsigned char *buffer, size_t length,
    size_t *offset, nvpair_view_t *pair)
{
    if (*offset > length || length - *offset < 8) return -1;
    uint32_t encoded = be32(buffer + *offset);
    uint32_t decoded = be32(buffer + *offset + 4);
    if (encoded == 0 && decoded == 0) return 0;
    if (encoded < 24 || decoded < 16 || encoded > length - *offset ||
        length - *offset < 12) return -1;
    uint32_t name_length = be32(buffer + *offset + 8);
    if (name_length == 0 || name_length > encoded - 12) return -1;
    size_t name_bytes = ((size_t)name_length + 3U) & ~(size_t)3U;
    size_t name_offset = *offset + 12;
    size_t type_offset = name_offset + name_bytes;
    size_t pair_end = *offset + encoded;
    if (type_offset > pair_end || pair_end - type_offset < 8 ||
        memchr(buffer + name_offset, 0, name_length) != NULL) return -1;
    pair->name = buffer + name_offset;
    pair->name_length = name_length;
    pair->type = be32(buffer + type_offset);
    pair->elements = be32(buffer + type_offset + 4);
    pair->value = buffer + type_offset + 8;
    pair->value_length = pair_end - (type_offset + 8);
    *offset = pair_end;
    return 1;
}

static int nvlist_start(const unsigned char *buffer, size_t length,
    int packed_header, size_t *offset)
{
    size_t header = packed_header ? 12U : 8U;
    if (length < header) return 0;
    if (packed_header && (buffer[0] != 1 || buffer[1] != 1)) return 0;
    size_t version_offset = packed_header ? 4U : 0U;
    if (be32(buffer + version_offset) != 0) return 0;
    *offset = header;
    return 1;
}

static int nvlist_find(const unsigned char *buffer, size_t length,
    int packed_header, const char *wanted, nvpair_view_t *result)
{
    size_t offset = 0;
    if (!nvlist_start(buffer, length, packed_header, &offset)) return 0;
    for (;;) {
        nvpair_view_t pair;
        int next = nvlist_next_pair(buffer, length, &offset, &pair);
        if (next <= 0) return 0;
        size_t wanted_length = strlen(wanted);
        if (wanted_length == pair.name_length &&
            memcmp(pair.name, wanted, wanted_length) == 0) {
            *result = pair;
            return 1;
        }
    }
}

static int nvpair_get_uint64(const nvpair_view_t *pair, uint64_t *value)
{
    if (pair->type != DATA_TYPE_UINT64 || pair->elements != 1 ||
        pair->value_length < sizeof(uint64_t)) return 0;
    *value = be64(pair->value);
    return 1;
}

static int nvpair_string_equals(const nvpair_view_t *pair, const char *wanted)
{
    if (pair->type != DATA_TYPE_STRING || pair->elements != 1 ||
        pair->value_length < 4) return 0;
    uint32_t string_length = be32(pair->value);
    return string_length == strlen(wanted) &&
        string_length <= pair->value_length - 4 &&
        memcmp(pair->value + 4, wanted, string_length) == 0;
}

static int feature_is_supported(const unsigned char *name, size_t length)
{
    static const char *supported[] = {
        "com.delphix:hole_birth",
        "com.delphix:embedded_data",
        "com.klarasystems:vdev_zaps_v2"
    };
    for (size_t i = 0; i < sizeof(supported) / sizeof(supported[0]); ++i) {
        if (length == strlen(supported[i]) &&
            memcmp(name, supported[i], length) == 0) return 1;
    }
    return 0;
}

static int nvlist_count_unsupported_features(const unsigned char *buffer,
    size_t length, uint32_t *unsupported)
{
    size_t offset = 0;
    uint32_t count = 0;
    if (!nvlist_start(buffer, length, 0, &offset)) return 0;
    for (;;) {
        nvpair_view_t pair;
        int next = nvlist_next_pair(buffer, length, &offset, &pair);
        if (next < 0) return 0;
        if (next == 0) break;
        if (!feature_is_supported(pair.name, pair.name_length)) ++count;
    }
    *unsupported = count;
    return 1;
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
    nvpair_view_t pair;
    nvpair_view_t vdev_tree;
    nvpair_view_t features;
    uint64_t pool_guid = 0, vdev_guid = 0, top_guid = 0, ashift = 0;
    uint64_t state = 0, vdev_children = 0, vdev_id = 0, nested_guid = 0;
    uint32_t unsupported = 0;
    int valid = embedded_checksum_valid(buffer, VDEV_PHYS_SIZE,
            label + VDEV_SKIP_SIZE) &&
        nvlist_find(buffer, VDEV_PHYS_SIZE - 40, 1, "pool_guid", &pair) &&
        nvpair_get_uint64(&pair, &pool_guid) &&
        nvlist_find(buffer, VDEV_PHYS_SIZE - 40, 1, "guid", &pair) &&
        nvpair_get_uint64(&pair, &vdev_guid) &&
        nvlist_find(buffer, VDEV_PHYS_SIZE - 40, 1, "top_guid", &pair) &&
        nvpair_get_uint64(&pair, &top_guid) &&
        nvlist_find(buffer, VDEV_PHYS_SIZE - 40, 1, "state", &pair) &&
        nvpair_get_uint64(&pair, &state) &&
        nvlist_find(buffer, VDEV_PHYS_SIZE - 40, 1, "vdev_children", &pair) &&
        nvpair_get_uint64(&pair, &vdev_children) &&
        nvlist_find(buffer, VDEV_PHYS_SIZE - 40, 1, "vdev_tree", &vdev_tree) &&
        vdev_tree.type == DATA_TYPE_NVLIST && vdev_tree.elements == 1 &&
        nvlist_find(vdev_tree.value, vdev_tree.value_length, 0, "type", &pair) &&
        nvpair_string_equals(&pair, "file") &&
        nvlist_find(vdev_tree.value, vdev_tree.value_length, 0, "id", &pair) &&
        nvpair_get_uint64(&pair, &vdev_id) &&
        nvlist_find(vdev_tree.value, vdev_tree.value_length, 0, "guid", &pair) &&
        nvpair_get_uint64(&pair, &nested_guid) &&
        nvlist_find(vdev_tree.value, vdev_tree.value_length, 0, "ashift", &pair) &&
        nvpair_get_uint64(&pair, &ashift) &&
        nvlist_find(buffer, VDEV_PHYS_SIZE - 40, 1, "features_for_read", &features) &&
        features.type == DATA_TYPE_NVLIST && features.elements == 1 &&
        nvlist_count_unsupported_features(features.value, features.value_length,
            &unsupported) && pool_guid != 0 && vdev_guid != 0 &&
        top_guid == vdev_guid && nested_guid == vdev_guid && vdev_children == 1 &&
        vdev_id == 0 && ashift >= 9 && ashift <= 24 && state <= 2;
    if (valid) {
        device->ashift = ashift;
        device->unsupported_features = unsupported;
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
    size_t logical_size = 0;
    if (block == NULL || !read_block_pointer(device, device->mos_root_bp,
        block, length, &logical_size) || logical_size != length) {
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

static int block_pointer_checksum_valid(const unsigned char *bp,
    const unsigned char *data, size_t length)
{
    uint64_t prop = load_u64(bp + 48);
    uint64_t checksum = (prop >> 40) & UINT64_C(0xff);
    if (checksum != 7 /* ZIO_CHECKSUM_FLETCHER_4 */ ||
        (length & (sizeof(uint32_t) - 1)) != 0) return 0;
    int byteswap = ((prop >> 63) & UINT64_C(1)) == 0;
    uint64_t calculated[4] = { 0, 0, 0, 0 };
    for (size_t offset = 0; offset < length; offset += sizeof(uint32_t)) {
        uint32_t word = 0;
        memcpy(&word, data + offset, sizeof(word));
        if (byteswap) word = swap_u32(word);
        calculated[0] += word;
        calculated[1] += calculated[0];
        calculated[2] += calculated[1];
        calculated[3] += calculated[2];
    }
    for (size_t word = 0; word < 4; ++word) {
        uint64_t stored = load_u64(bp + 96 + word * sizeof(uint64_t));
        if (byteswap) stored = swap_u64(stored);
        if (stored != calculated[word]) return 0;
    }
    return 1;
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
    if (source_length < 4 || source_length > INT_MAX ||
        destination_length > INT_MAX) return 0;
    size_t encoded_length = be32(source);
    if (encoded_length > source_length - 4 || encoded_length > INT_MAX) return 0;
    int decoded = LZ4_uncompress_unknownOutputSize((const char *)source + 4,
        (char *)destination, (int)encoded_length, (int)destination_length);
    return decoded == (int)destination_length;
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
            if (word == 6 || word == 10) continue;
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
    if (!block_pointer_checksum_valid(bp, compressed, (size_t)physical_size)) {
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

static int read_dsl_dir_info(zfs_core_device_t *device, uint64_t object,
    zfs_core_dsl_dir_info_t *info)
{
    unsigned char *dnode_block = (unsigned char *)malloc(16 * 1024);
    if (dnode_block == NULL || !read_mos_dnode_block_for_object(device, object,
        dnode_block, 16 * 1024)) {
        free(dnode_block); return 0;
    }
    const unsigned char *dnode = dnode_block + (object % 32) * 512;
    size_t bonus_offset = 64 + (size_t)dnode[3] * 128;
    int valid = dnode[0] == 12 /* DMU_OT_DSL_DIR */ && dnode[3] <= 3 &&
        bonus_offset + 256 <= 512;
    if (valid) {
        info->head_dataset_obj = load_u64(dnode + bonus_offset + 8);
        info->child_dir_zap_obj = load_u64(dnode + bonus_offset + 32);
        info->props_zap_obj = load_u64(dnode + bonus_offset + 80);
        valid = info->head_dataset_obj != 0 && info->child_dir_zap_obj != 0;
    }
    free(dnode_block);
    return valid;
}

static int read_root_dataset_info(zfs_core_device_t *device,
    zfs_core_dsl_dir_info_t *info)
{
    return read_dsl_dir_info(device, 32, info);
}

static int lookup_mos_microzap(zfs_core_device_t *device, uint64_t object,
    uint8_t object_type, const char *name, uint64_t *value)
{
    unsigned char *dnode_block = (unsigned char *)malloc(16 * 1024);
    unsigned char block[16 * 1024];
    if (dnode_block == NULL || name == NULL || value == NULL ||
        !read_mos_dnode_block_for_object(device, object, dnode_block, 16 * 1024)) {
        free(dnode_block); return 0;
    }
    const unsigned char *dnode = dnode_block + (object % 32) * 512;
    size_t block_size = 0;
    int valid = dnode[0] == object_type && dnode[2] == 1 && dnode[3] >= 1 &&
        dnode[3] <= 3 && read_block_pointer(device, dnode + 64, block,
            sizeof(block), &block_size) && block_size >= 64 && block[0] == 3;
    if (valid) {
        for (size_t offset = 64; offset + 64 <= block_size; offset += 64) {
            const char *entry_name = (const char *)(block + offset + 14);
            size_t entry_length = 0;
            while (entry_length < 50 && entry_name[entry_length] != 0) ++entry_length;
            if (entry_length == 0 || entry_length == 50 ||
                strlen(name) != entry_length || memcmp(entry_name, name, entry_length) != 0)
                continue;
            *value = load_u64(block + offset);
            break;
        }
    }
    free(dnode_block);
    return valid && *value != 0;
}

static int read_dataset_objset_root(zfs_core_device_t *device,
    uint64_t dataset_object,
    unsigned char *destination, size_t capacity, size_t *logical_size_out)
{
    unsigned char *dnode_block = (unsigned char *)malloc(16 * 1024);
    if (dnode_block == NULL || !read_mos_dnode_block_for_object(device, dataset_object,
        dnode_block, 16 * 1024)) {
        free(dnode_block); return 0;
    }
    const unsigned char *dnode = dnode_block + (dataset_object % 32) * 512;
    size_t bonus_offset = 64 + (size_t)dnode[3] * 128;
    int valid = dnode[4] == 16 /* DMU_OT_DSL_DATASET bonus */ && dnode[3] <= 3 &&
        bonus_offset + 176 <= 512;
    if (valid) valid = read_block_pointer(device, dnode + bonus_offset + 128,
        destination, capacity, logical_size_out);
    free(dnode_block);
    return valid;
}

static int read_root_dataset_objset_root(zfs_core_device_t *device,
    unsigned char *destination, size_t capacity, size_t *logical_size_out)
{
    zfs_core_dsl_dir_info_t root_info;
    zfs_core_dsl_dir_info_t child_info;
    uint64_t child_dir_object = 0;
    return read_root_dataset_info(device, &root_info) &&
        lookup_mos_microzap(device, root_info.child_dir_zap_obj, 13,
            "data", &child_dir_object) &&
        read_dsl_dir_info(device, child_dir_object, &child_info) &&
        read_dataset_objset_root(device, child_info.head_dataset_obj,
            destination, capacity, logical_size_out);
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
            uint64_t shift = (level - 1) * 10;
            uint64_t index = (block_id >> shift) & UINT64_C(0x3ff);
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

static int block_pointer_is_hole(const unsigned char *bp)
{
    if ((load_u64(bp + 48) >> 39) & UINT64_C(1)) return 0;
    for (size_t offset = 0; offset < 48; offset += sizeof(uint64_t)) {
        if (load_u64(bp + offset) != 0) return 0;
    }
    return 1;
}

static int get_root_file_size(zfs_core_device_t *device, uint64_t object,
    uint64_t *file_size)
{
    unsigned char *dnode_block = (unsigned char *)malloc(16 * 1024);
    if (dnode_block == NULL || file_size == NULL ||
        !read_filesystem_object_dnode_block(device, object, dnode_block,
            16 * 1024, NULL)) {
        free(dnode_block); return 0;
    }
    const unsigned char *dnode = dnode_block + (object % 32) * 512;
    size_t bonus_offset = 64 + (size_t)dnode[3] * 128;
    uint16_t bonus_length = load_u16(dnode + 10);
    int valid = dnode[0] == 19 /* DMU_OT_PLAIN_FILE_CONTENTS */ &&
        dnode[2] >= 1 && dnode[2] <= 6 && dnode[3] >= 1 && dnode[3] <= 3 &&
        dnode[4] == 44 /* DMU_OT_SA */ && bonus_offset + bonus_length <= 512 &&
        bonus_length >= 8 && read_sa_file_size(device, dnode, bonus_offset,
            bonus_length, file_size);
    free(dnode_block);
    return valid;
}

static int read_root_file(zfs_core_device_t *device, uint64_t object,
    unsigned char *destination, size_t capacity, size_t *file_size_out)
{
    unsigned char *dnode_block = (unsigned char *)malloc(16 * 1024);
    unsigned char *data_block = (unsigned char *)malloc(MOS_DNODE_BLOCK_SIZE);
    unsigned char *indirect = (unsigned char *)malloc(MOS_DNODE_BLOCK_SIZE);
    if (dnode_block == NULL || data_block == NULL || indirect == NULL || destination == NULL ||
        file_size_out == NULL || !read_filesystem_object_dnode_block(device,
            object, dnode_block, 16 * 1024, NULL)) {
        free(indirect); free(data_block); free(dnode_block); return 0;
    }
    const unsigned char *dnode = dnode_block + (object % 32) * 512;
    size_t bonus_offset = 64 + (size_t)dnode[3] * 128;
    uint16_t bonus_length = load_u16(dnode + 10);
    int valid = dnode[0] == 19 /* DMU_OT_PLAIN_FILE_CONTENTS */ &&
        dnode[2] >= 1 && dnode[2] <= 6 && dnode[3] >= 1 && dnode[3] <= 3 &&
        dnode[4] == 44 /* DMU_OT_SA */ && bonus_offset + bonus_length <= 512 &&
        bonus_length >= 8 && dnode[1] >= 8 && dnode[1] <= 24;
    uint64_t file_size = 0;
    if (valid) valid = read_sa_file_size(device, dnode, bonus_offset,
        bonus_length, &file_size);
    uint64_t data_block_size = (uint64_t)load_u16(dnode + 8) << 9;
    uint8_t fanout_bits = (uint8_t)(dnode[1] - 7);
    uint64_t fanout = UINT64_C(1) << fanout_bits;
    uint64_t top_span = 1;
    for (uint8_t level = 1; valid && level < dnode[2]; ++level) {
        if (top_span > UINT64_MAX / fanout) valid = 0;
        else top_span *= fanout;
    }
    uint64_t block_count = data_block_size == 0 || file_size == 0 ? 0 :
        1 + (file_size - 1) / data_block_size;
    if (valid) valid = file_size <= capacity && file_size <= SIZE_MAX &&
        data_block_size >= 512 && data_block_size <= MOS_DNODE_BLOCK_SIZE &&
        (block_count == 0 || block_count - 1 <= load_u64(dnode + 16));
    size_t copied = 0;
    for (uint64_t block_id = 0; valid && block_id < block_count; ++block_id) {
        uint64_t top_index = block_id / top_span;
        uint64_t relative = block_id % top_span;
        if (top_index >= dnode[3]) { valid = 0; break; }
        const unsigned char *bp = dnode + 64 + top_index * 128;
        int hole = 0;
        for (uint8_t level = (uint8_t)(dnode[2] - 1); level > 0; --level) {
            if (block_pointer_is_hole(bp)) { hole = 1; break; }
            size_t indirect_size = 0;
            if (!read_block_pointer(device, bp, indirect, MOS_DNODE_BLOCK_SIZE,
                    &indirect_size) || indirect_size != MOS_DNODE_BLOCK_SIZE) {
                valid = 0; break;
            }
            uint64_t shift = (uint64_t)(level - 1) * fanout_bits;
            uint64_t index = (relative >> shift) & (fanout - 1);
            bp = indirect + index * 128;
        }
        size_t wanted = (size_t)(file_size - copied);
        if (wanted > data_block_size) wanted = (size_t)data_block_size;
        if (!valid) break;
        if (hole || block_pointer_is_hole(bp)) {
            memset(destination + copied, 0, wanted);
        } else {
            size_t logical_size = 0;
            if (!read_block_pointer(device, bp, data_block,
                    (size_t)data_block_size, &logical_size) ||
                logical_size != data_block_size) {
                valid = 0; break;
            }
            memcpy(destination + copied, data_block, wanted);
        }
        copied += wanted;
    }
    if (valid) *file_size_out = copied;
    free(indirect);
    free(data_block);
    free(dnode_block);
    return valid;
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

static int zap_find_uint16_array(const unsigned char *block,
    const char *wanted, uint16_t *values, size_t capacity, size_t *count)
{
    if (load_u64(block) != UINT64_C(0x8000000000000000) ||
        load_u32(block + 24) != UINT32_C(0x02AB1EAF)) return 0;
    const size_t chunk_offset = 48 + 512 * 2;
    const size_t chunk_size = 24;
    for (uint16_t chunk = 0; chunk < 640; ++chunk) {
        size_t offset = chunk_offset + (size_t)chunk * chunk_size;
        if (offset + chunk_size > 16 * 1024 || block[offset] != 252) continue;
        uint16_t name_bytes = load_u16(block + offset + 6);
        uint8_t integer_size = block[offset + 1];
        uint16_t elements = load_u16(block + offset + 10);
        size_t value_bytes = (size_t)integer_size * elements;
        if (name_bytes == 0 || name_bytes > 512 || integer_size != 2 ||
            elements == 0 || elements > capacity || value_bytes > 512) continue;
        unsigned char name[512];
        unsigned char raw_value[512];
        if (!zap_read_chunks(block, load_u16(block + offset + 4), name_bytes,
                name, sizeof(name)) || !zap_read_chunks(block,
                load_u16(block + offset + 8), (uint16_t)value_bytes,
                raw_value, sizeof(raw_value))) continue;
        if (name[name_bytes - 1] != 0 || strlen(wanted) + 1 != name_bytes ||
            memcmp(name, wanted, name_bytes) != 0) continue;
        for (size_t i = 0; i < elements; ++i)
            values[i] = (uint16_t)(((uint16_t)raw_value[i * 2] << 8) |
                raw_value[i * 2 + 1]);
        *count = elements;
        return 1;
    }
    return 0;
}

static int lookup_filesystem_fatzap_uint16_array(zfs_core_device_t *device,
    uint64_t object, uint8_t object_type, const char *name, uint16_t *values,
    size_t capacity, size_t *count)
{
    unsigned char *dnode_block = (unsigned char *)malloc(16 * 1024);
    unsigned char *zap_block = (unsigned char *)malloc(16 * 1024);
    if (dnode_block == NULL || zap_block == NULL || name == NULL ||
        values == NULL || count == NULL ||
        !read_filesystem_object_dnode_block(device, object, dnode_block,
            16 * 1024, NULL)) {
        free(zap_block); free(dnode_block); return 0;
    }
    const unsigned char *dnode = dnode_block + (object % 32) * 512;
    int valid = dnode[0] == object_type && dnode[2] == 1 &&
        dnode[3] >= 1 && dnode[3] <= 3;
    int found = 0;
    for (uint8_t block = 0; valid && block < dnode[3] && !found; ++block) {
        size_t block_size = 0;
        valid = read_block_pointer(device, dnode + 64 + block * 128,
            zap_block, 16 * 1024, &block_size) && block_size == 16 * 1024;
        if (valid) found = zap_find_uint16_array(zap_block, name, values,
            capacity, count);
    }
    free(zap_block);
    free(dnode_block);
    return valid && found;
}

static uint16_t zpl_attribute_size(uint16_t attribute)
{
    static const uint16_t sizes[] = {
        16, 16, 16, 16, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 32,
        88, 8, 0, 32, 0, 0, 8
    };
    return attribute < sizeof(sizes) / sizeof(sizes[0]) ? sizes[attribute] : 0;
}

static int read_sa_file_size(zfs_core_device_t *device,
    const unsigned char *dnode, size_t bonus_offset, uint16_t bonus_length,
    uint64_t *file_size)
{
    const unsigned char *bonus = dnode + bonus_offset;
    if (load_u32(bonus) != UINT32_C(0x002f505a)) return 0;
    uint16_t layout_info = load_u16(bonus + 4);
    uint16_t layout_number = layout_info & UINT16_C(0x03ff);
    size_t header_size = (size_t)((layout_info >> 10) & UINT16_C(0x003f)) << 3;
    uint64_t sa_master = 0;
    uint64_t layouts_object = 0;
    uint16_t attributes[128];
    size_t attribute_count = 0;
    char layout_name[16];
    if (header_size < 8 || header_size > bonus_length ||
        sprintf_s(layout_name, sizeof(layout_name), "%u", layout_number) < 0 ||
        !lookup_filesystem_microzap(device, 1, 21, "SA_ATTRS", &sa_master) ||
        !lookup_filesystem_microzap(device, sa_master, 45, "LAYOUTS",
            &layouts_object) ||
        !lookup_filesystem_fatzap_uint16_array(device, layouts_object, 47,
            layout_name, attributes,
            sizeof(attributes) / sizeof(attributes[0]), &attribute_count))
        return 0;
    size_t variable_index = 0;
    size_t data_offset = header_size;
    for (size_t i = 0; i < attribute_count; ++i) {
        uint16_t size = zpl_attribute_size(attributes[i]);
        if (size == 0) {
            size_t length_offset = 6 + variable_index * sizeof(uint16_t);
            if (length_offset + sizeof(uint16_t) > header_size) return 0;
            size = load_u16(bonus + length_offset);
            ++variable_index;
        }
        if (data_offset > bonus_length || size > bonus_length - data_offset)
            return 0;
        if (attributes[i] == 6 /* ZPL_SIZE */) {
            if (size != sizeof(uint64_t)) return 0;
            *file_size = load_u64(bonus + data_offset);
            return 1;
        }
        data_offset = (data_offset + size + 7U) & ~(size_t)7U;
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
    uint64_t shift = device->ashift;
    if (shift < UBERBLOCK_MIN_SHIFT) shift = UBERBLOCK_MIN_SHIFT;
    if (shift > UBERBLOCK_MAX_SHIFT) shift = UBERBLOCK_MAX_SHIFT;
    size_t slot_size = (size_t)UINT64_C(1) << shift;
    unsigned char fields[1U << UBERBLOCK_MAX_SHIFT];
    for (uint64_t slot = 0; slot < VDEV_UBERBLOCK_RING_SIZE / slot_size; ++slot) {
        uint64_t offset = label + VDEV_UBERBLOCK_RING_OFFSET + slot * slot_size;
        if (offset > device->size_bytes || slot_size > device->size_bytes - offset ||
            seek_u64(device->file, offset) != 0 ||
            fread(fields, 1, slot_size, device->file) != slot_size) {
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
        if (txg == 0 || !embedded_checksum_valid(fields, slot_size, offset))
            continue;
        ++device->uberblocks_found;
        if (txg >= device->selected_txg) {
            device->selected_txg = txg;
            device->selected_uberblock_offset = offset;
            memcpy(device->mos_root_bp, fields + 40, sizeof(device->mos_root_bp));
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
    if (device->unsupported_features > 0) return ZFS_CORE_NOT_SUPPORTED;
    return device->valid_labels_found > 0 && device->uberblocks_found > 0 &&
        device->mos_root_valid ? ZFS_CORE_OK : ZFS_CORE_NOT_A_ZFS_DEVICE;
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

zfs_core_status_t zfs_core_lookup_root_directory(
    const zfs_core_device_t *device, const char *name, uint64_t *object)
{
    if (device == NULL || name == NULL || object == NULL) return ZFS_CORE_INVALID_ARGUMENT;
    uint64_t directory_entry = 0;
    *object = 0;
    if (!lookup_filesystem_microzap((zfs_core_device_t *)device, 34, 20,
        name, &directory_entry)) return ZFS_CORE_CORRUPT_METADATA;
    *object = directory_entry & UINT64_C(0x0000ffffffffffff);
    return *object != 0 ? ZFS_CORE_OK : ZFS_CORE_CORRUPT_METADATA;
}

zfs_core_status_t zfs_core_read_root_file(
    const zfs_core_device_t *device, uint64_t object, void *buffer,
    uint64_t length, uint64_t *bytes_read)
{
    if (device == NULL || buffer == NULL || bytes_read == NULL || length > SIZE_MAX)
        return ZFS_CORE_INVALID_ARGUMENT;
    *bytes_read = 0;
    size_t file_size = 0;
    if (!read_root_file((zfs_core_device_t *)device, object,
        (unsigned char *)buffer, (size_t)length, &file_size))
        return ZFS_CORE_CORRUPT_METADATA;
    *bytes_read = file_size;
    return ZFS_CORE_OK;
}

zfs_core_status_t zfs_core_get_root_file_size(
    const zfs_core_device_t *device, uint64_t object, uint64_t *size)
{
    if (device == NULL || size == NULL) return ZFS_CORE_INVALID_ARGUMENT;
    *size = 0;
    return get_root_file_size((zfs_core_device_t *)device, object, size) ?
        ZFS_CORE_OK : ZFS_CORE_CORRUPT_METADATA;
}

