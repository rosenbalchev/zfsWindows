/*
 * Decompression-only compilation unit for OpenZFS's vendored Zstandard
 * sources. The included files retain their upstream BSD/GPL dual-license
 * notices. This adapter deliberately omits compressors and debug.c.
 */
#define MEM_MODULE
#define XXH_NAMESPACE ZSTD_
#define XXH_PRIVATE_API
#define XXH_INLINE_ALL
#define ZSTD_LEGACY_SUPPORT 0
#define ZSTD_LIB_DICTBUILDER 0
#define ZSTD_LIB_DEPRECATED 0
#define ZSTD_NOBENCH

#include "common/entropy_common.c"
#include "common/error_private.c"
#include "common/fse_decompress.c"
#include "common/zstd_common.c"
#include "decompress/huf_decompress.c"
#include "decompress/zstd_ddict.c"
#include "decompress/zstd_decompress.c"
#include "decompress/zstd_decompress_block.c"
