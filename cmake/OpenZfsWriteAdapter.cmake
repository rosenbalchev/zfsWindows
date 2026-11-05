if(NOT DEFINED ZFSWINDOWS_ROOT)
    message(FATAL_ERROR "ZFSWINDOWS_ROOT must point to the zfsWindows checkout")
endif()

if(NOT COMMAND use_clang)
    function(use_clang)
        add_compile_options(
            /FI${CMAKE_SOURCE_DIR}/include/os/windows/zfs/zfs_config.h
            -Wno-nonportable-include-path
            -Wno-unknown-pragmas
            -Wno-unused-command-line-argument
            -Wno-unused-function
            -Wno-microsoft-anon-tag
        )
        add_definitions(-D_LP64 -D__LP64__)
    endfunction()
endif()

# OpenZFS's Windows user-mode ICP sources still select the AVX/GCM entry
# points, while the corresponding .S files are guarded by _KERNEL.  Enable
# that guard for assembly compilation only; do not expose it to the C ABI.
set(CMAKE_ASM_FLAGS "${CMAKE_ASM_FLAGS} -D_KERNEL")

# Static CRT, as upstream's tools: the root CMakeLists adds /MT(d) only after
# this overlay has run, and a clean Windows has no debug CRT DLLs.
string(APPEND CMAKE_C_FLAGS_DEBUG " /MTd")
string(APPEND CMAKE_C_FLAGS_RELEASE " /MT")

add_subdirectory("${ZFSWINDOWS_ROOT}/src/native/ZfsUserMode"
    "${CMAKE_BINARY_DIR}/zfs-user-mode")
add_subdirectory("${ZFSWINDOWS_ROOT}/src/native/ZfsWriteCore"
    "${CMAKE_BINARY_DIR}/zfs-write-core")
add_subdirectory("${ZFSWINDOWS_ROOT}/src/native/ZfsWinFsp"
    "${CMAKE_BINARY_DIR}/zfswin-um")
