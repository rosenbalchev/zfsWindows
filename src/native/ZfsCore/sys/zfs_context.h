#pragma once

/* Minimal user-space compatibility surface required by OpenZFS's standalone
 * LZ4 decompressor when it is compiled as part of the Windows reader. */
#include <assert.h>
#include <limits.h>
#include <stddef.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#define ASSERT(condition) assert(condition)
#define MIN(left, right) ((left) < (right) ? (left) : (right))

