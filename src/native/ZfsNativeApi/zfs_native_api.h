#pragma once
#include "zfs_core.h"

#if defined(_WIN32)
#  define ZFS_NATIVE_API __declspec(dllexport)
#else
#  define ZFS_NATIVE_API
#endif

ZFS_NATIVE_API const char *zfs_native_status_name(zfs_core_status_t status);

