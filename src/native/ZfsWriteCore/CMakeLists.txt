use_clang()

add_executable(zfs-write-core
    zfs_write_core.c
)

# The portable ZPL layer, and the OpenZFS headers this tool also uses.
target_link_libraries(zfs-write-core PRIVATE
    zfs-um-core
    zfs-um-internal
)
