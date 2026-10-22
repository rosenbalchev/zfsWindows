use_clang()

add_executable(zfs-write-core
    zfs_write_core.c
)

target_include_directories(zfs-write-core PRIVATE
    "${CMAKE_SOURCE_DIR}/lib/libspl/include/os/windows"
    "${CMAKE_SOURCE_DIR}/lib/libspl/include"
    "${CMAKE_SOURCE_DIR}/lib/libzpool/include"
    "${CMAKE_SOURCE_DIR}/lib/libzutil"
    "${CMAKE_SOURCE_DIR}/include"
    "${CMAKE_SOURCE_DIR}/lib/os/windows/libpthread"
    "${OPENSSL_INCLUDE_DIR}"
)

target_compile_definitions(zfs-write-core PRIVATE
    _CRT_SECURE_NO_WARNINGS
    _CRT_LOADCFG_DISABLE_CET
    LIB_ZPOOL_BUILD
    DEBUG
    ZFS_DEBUG
)

target_link_libraries(zfs-write-core PRIVATE
    libzpool
    libzutil
    libnvpair
)
