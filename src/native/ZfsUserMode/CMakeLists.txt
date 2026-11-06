use_clang()

# What code that includes the OpenZFS headers needs (libspl, libzpool).
add_library(zfs-um-internal INTERFACE)
target_include_directories(zfs-um-internal INTERFACE
    "${CMAKE_CURRENT_SOURCE_DIR}"
    "${CMAKE_SOURCE_DIR}/lib/libspl/include/os/windows"
    "${CMAKE_SOURCE_DIR}/lib/libspl/include"
    "${CMAKE_SOURCE_DIR}/lib/libzpool/include"
    "${CMAKE_SOURCE_DIR}/lib/libzutil"
    "${CMAKE_SOURCE_DIR}/include"
    "${CMAKE_SOURCE_DIR}/lib/os/windows/libpthread"
    "${OPENSSL_INCLUDE_DIR}"
)
target_compile_definitions(zfs-um-internal INTERFACE
    _CRT_SECURE_NO_WARNINGS
    _CRT_LOADCFG_DISABLE_CET
    LIB_ZPOOL_BUILD
    DEBUG
    ZFS_DEBUG
)
target_link_libraries(zfs-um-internal INTERFACE
    libzpool
    libzutil
    libnvpair
)

# The portable ZPL layer (zum_zpl.c, zum_api.c) plus its user-mode pool
# lifetime (zum_pool_um.c).  Front ends see only zum.h: the OpenZFS headers
# and their include paths stay private.
add_library(zfs-um-core STATIC
    zum_zpl.c
    zum_api.c
    zum_xattr.c
    zum_pool_um.c
)
target_link_libraries(zfs-um-core PRIVATE zfs-um-internal)
target_include_directories(zfs-um-core PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}")

# The ZFS ioctl interface of the user-mode engine: upstream zfs_ioctl.c,
# unchanged, with zum_ioctl_shim.h standing in for the kernel VFS headers,
# and zum_ioctl_um.c supplying the kernel entry points it calls.  Served to
# zpool.exe/zfs.exe by zfswin-um's bridge (sys/zfs_bridge.h).
add_library(zfs-um-ioctl STATIC
    "${CMAKE_SOURCE_DIR}/module/zfs/zfs_ioctl.c"
    zum_ioctl_um.c
)
target_link_libraries(zfs-um-ioctl PRIVATE zfs-um-internal)
target_include_directories(zfs-um-ioctl PRIVATE
    "${CMAKE_SOURCE_DIR}/module/zcommon")
target_include_directories(zfs-um-ioctl PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}")
target_compile_options(zfs-um-ioctl PRIVATE
    "/FI${CMAKE_CURRENT_SOURCE_DIR}/zum_ioctl_shim.h")