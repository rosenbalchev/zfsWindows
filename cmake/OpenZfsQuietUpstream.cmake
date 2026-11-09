# Warnings off for the OpenZFS-on-Windows sources (third_party), which are
# upstream's to fix.  Every setting here changes diagnostics only, never the
# generated code.  The driver tree takes this file as CMAKE_PROJECT_INCLUDE
# (scripts/build-driver.ps1); the write tree includes it from
# OpenZfsWriteAdapter.cmake after our own directories, which keep their
# warnings.

# /W0: cl reports nothing, and clang-cl takes it as -w, which no later -W
# option turns back on (use_clang() re-enables -Wunknown-pragmas).  From
# /W0 module/CMakeLists.txt's /W3 -> /W4 replacement no longer applies.
foreach(_lang C CXX)
    if(CMAKE_${_lang}_FLAGS MATCHES "/W[0-4]")
        string(REGEX REPLACE "/W[0-4]" "/W0" CMAKE_${_lang}_FLAGS "${CMAKE_${_lang}_FLAGS}")
    else()
        string(APPEND CMAKE_${_lang}_FLAGS " /W0")
    endif()
endforeach()

# libspl has strlcpy/strlcat both in posix.c and in their own files; the
# librarian keeps the first (LNK4006), as it does without this.
string(APPEND CMAKE_STATIC_LINKER_FLAGS " /IGNORE:4006")

# The driver target is built by cl but gets the clang target options of
# WDK_COMPILE_FLAGS (-m64, --target=...), which cl ignores (D9002).
function(_zfswin_drop_clang_options_from_cl_targets)
    if(TARGET OpenZFS)
        get_target_property(_options OpenZFS COMPILE_OPTIONS)
        if(_options)
            list(FILTER _options EXCLUDE REGEX "^(-m32|-m64|--target=.*)$")
            set_target_properties(OpenZFS PROPERTIES COMPILE_OPTIONS "${_options}")
        endif()
    endif()
endfunction()
cmake_language(DEFER DIRECTORY "${CMAKE_SOURCE_DIR}"
    CALL _zfswin_drop_clang_options_from_cl_targets)
