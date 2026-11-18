# find_package(WDK) in third_party/openzfs-windows/CMakeLists.txt looks for
# FindWDK.cmake, but the module there is FindWdk.cmake: found on NTFS, not
# on a case-sensitive volume such as a default ZFS dataset (GitHub issue #37).
# OpenZfsQuietUpstream.cmake and OpenZfsWriteAdapter.cmake put this
# directory first on CMAKE_MODULE_PATH.
include("${CMAKE_SOURCE_DIR}/contrib/windows/cmake/FindWdk.cmake")
