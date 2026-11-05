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
    zum_pool_um.c
)
target_link_libraries(zfs-um-core PRIVATE zfs-um-internal)
target_include_directories(zfs-um-core PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}")
