# zfswin-um.exe: the WinFsp front end.  No use_clang() here: this file
# includes <windows.h> and <winfsp.h>, not the OpenZFS headers, so it gets
# neither their forced zfs_config.h nor libspl's replacement headers.

if(NOT DEFINED WINFSP_SDK)
    set(WINFSP_SDK "${ZFSWINDOWS_ROOT}/third_party/winfsp/sdk-2.1/DYNAMIC")
endif()
if(CMAKE_C_COMPILER_ARCHITECTURE_ID STREQUAL "ARM64")
    set(WINFSP_ARCH a64)
else()
    set(WINFSP_ARCH x64)
endif()

# The product version (scripts/Get-ZfsWinVersion.ps1), from the environment
# so a release build stamps every binary with the same build number.
if(DEFINED ENV{ZFSWIN_VERSION})
    set(ZFSWIN_VERSION "$ENV{ZFSWIN_VERSION}")
else()
    set(ZFSWIN_VERSION "0.0.0")
endif()
string(REPLACE "." ";" _zum_parts "${ZFSWIN_VERSION}")
list(GET _zum_parts 0 ZUM_V1)
list(GET _zum_parts 1 ZUM_V2)
list(GET _zum_parts 2 ZUM_V3)
configure_file(version.rc.in "${CMAKE_CURRENT_BINARY_DIR}/version.rc" @ONLY)

add_executable(zfswin-um
    zum_winfsp.c
    "${CMAKE_CURRENT_BINARY_DIR}/version.rc"
)
target_compile_definitions(zfswin-um PRIVATE ZFSWIN_VERSION="${ZFSWIN_VERSION}")
target_include_directories(zfswin-um PRIVATE "${WINFSP_SDK}/inc")
target_compile_definitions(zfswin-um PRIVATE
    _CRT_SECURE_NO_WARNINGS
    UNICODE
    _UNICODE
)
# WinFsp's DLL lives in its install folder, not on PATH: delay-load it and
# let FspLoad() find it there.
target_link_libraries(zfswin-um PRIVATE
    zfs-um-core
    "${WINFSP_SDK}/lib/winfsp-${WINFSP_ARCH}.lib"
    delayimp
    advapi32
)
target_link_options(zfswin-um PRIVATE "/DELAYLOAD:winfsp-${WINFSP_ARCH}.dll")
