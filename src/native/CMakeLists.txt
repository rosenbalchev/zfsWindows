cmake_minimum_required(VERSION 3.24)
project(ZfsWindowsNative LANGUAGES C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_WINDOWS_EXPORT_ALL_SYMBOLS OFF)

add_library(ZfsCore SHARED
    ZfsCore/zfs_core.c
    ZfsCore/zfs_core.h
    ../../OpenZFS/module/zfs/lz4.c
)
target_compile_definitions(ZfsCore PRIVATE ZFS_CORE_BUILD)
target_include_directories(ZfsCore PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/ZfsCore)
if (MSVC)
    target_compile_options(ZfsCore PRIVATE /W4 /WX)
else()
    target_compile_options(ZfsCore PRIVATE -Wall -Wextra -Werror)
endif()

add_library(ZfsNativeApi SHARED
    ZfsNativeApi/zfs_native_api.c
    ZfsNativeApi/zfs_native_api.h
)
target_link_libraries(ZfsNativeApi PRIVATE ZfsCore)
target_include_directories(ZfsNativeApi PUBLIC
    ${CMAKE_CURRENT_SOURCE_DIR}/ZfsCore
    ${CMAKE_CURRENT_SOURCE_DIR}/ZfsNativeApi
)
if (MSVC)
    target_compile_options(ZfsNativeApi PRIVATE /W4 /WX)
else()
    target_compile_options(ZfsNativeApi PRIVATE -Wall -Wextra -Werror)
endif()

