use_clang()

# ZfsUserModeTest.dll: the user-mode engine in a test process
# (tests/Zfs.UserMode.Tests).  The .def file exports zut.h and zum.h.
add_library(zfs-um-test SHARED
    zut.c
    ZfsUserModeTest.def
)
set_target_properties(zfs-um-test PROPERTIES OUTPUT_NAME ZfsUserModeTest)
target_link_libraries(zfs-um-test PRIVATE
    zfs-um-core
    zfs-um-ioctl
    zfs-um-internal
)
target_include_directories(zfs-um-test SYSTEM PRIVATE
    "${CMAKE_SOURCE_DIR}/module/zcommon")
