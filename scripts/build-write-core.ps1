[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [string]$BuildDirectory,
    # Other targets of the same configured tree, e.g. the kernel driver
    # (OpenZFS) and its tools (zpool.exe, zfs.exe) for the isolated test VM.
    [string[]]$Targets = @("zfs-write-core"),
    # arm64 cross-compiles (clang-cl --target, MSVC ARM64 libraries, vcpkg
    # arm64-windows-static OpenSSL); the tools only run on an ARM64 machine.
    [ValidateSet("x64", "arm64")]
    [string]$Architecture = "x64",
    # Clang source-based coverage (-fprofile-instr-generate
    # -fcoverage-mapping) in a tree of its own, out\openzfs-write-coverage,
    # for scripts\test-usermode.ps1 -Coverage.  x64 only; never packaged.
    [switch]$Coverage,
    # Ninja jobs (build-driver.ps1 -Jobs).
    [int]$Jobs = 8,
    # Patches and configure (when needed) only (build-driver.ps1 -ConfigureOnly).
    [switch]$ConfigureOnly
)

$ErrorActionPreference = "Stop"
# powershell -File passes "a,b" as one string.
$Targets = @($Targets | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$root = Split-Path -Parent $PSScriptRoot
$openZfs = Join-Path $root "third_party\openzfs-windows"
if ($Coverage -and $Architecture -ne "x64") { throw "-Coverage builds x64 only." }
if (-not $BuildDirectory) {
    $BuildDirectory = Join-Path $root ("out\openzfs-write-$(if ($Coverage) { "coverage" } else { $Configuration.ToLowerInvariant() })" + $(if ($Architecture -eq "x64") { "" } else { "-$Architecture" }))
}
$cmakeArch = '-DOPENSSL_ROOT_DIR="C:/Program Files/OpenSSL-Win64"'
if ($Architecture -eq "arm64") {
    . (Join-Path $PSScriptRoot "arm64-openssl.ps1")
    $sslRoot = Get-Arm64OpenSslRoot -RepositoryRoot $root
    if (-not $sslRoot) { throw "ARM64 OpenSSL is missing or incomplete in out\vcpkg-openssl and ZFSWIN_CI_DATA (see docs/building.md)." }
    $ssl = $sslRoot -replace '\\', '/'
    $sslLib = if ($Configuration -eq "Debug") { "$ssl/debug/lib" } else { "$ssl/lib" }
    $cmakeArch = ('-DCMAKE_SYSTEM_NAME=Windows -DCMAKE_SYSTEM_PROCESSOR=ARM64 -DCMAKE_C_COMPILER_TARGET=aarch64-pc-windows-msvc ' +
        '-DCMAKE_CXX_COMPILER_TARGET=aarch64-pc-windows-msvc -DCMAKE_ASM_COMPILER_TARGET=aarch64-pc-windows-msvc ' +
        '-DOPENSSL_ROOT_DIR="{0}" -DOPENSSL_INCLUDE_DIR="{0}/include" -DOPENSSL_CRYPTO_LIBRARY="{1}/libcrypto.lib" ' +
        '-DOPENSSL_SSL_LIBRARY="{1}/libssl.lib" -DCRYPTO_STATIC_TEST="{1}/libcrypto.lib"') -f $ssl, $sslLib
}

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -property installationPath
if (-not $vs) { throw "Visual Studio installation was not found." }
$devShell = Join-Path $vs "Common7\Tools\VsDevCmd.bat"
$cmake = Join-Path $vs "Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
$ninja = Join-Path $vs "Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe"
$clang = "C:\Program Files\LLVM\bin\clang-cl.exe"
$overlay = Join-Path $root "cmake\OpenZfsWriteAdapter.cmake"

if (-not (Test-Path $clang)) { throw "LLVM clang-cl was not found at $clang." }
if (-not (Test-Path "C:\Program Files\OpenSSL-Win64\include\openssl\ssl.h")) {
    throw "OpenSSL development headers were not found."
}
# Coverage: every C file instrumented (ours and OpenZFS), linked with
# clang's profile runtime from its resource directory.
if ($Coverage) {
    # Its 8.3 name: "Program Files" would split the linker option.
    $profileRuntime = (New-Object -ComObject Scripting.FileSystemObject).GetFolder(
        (Join-Path (& $clang --print-resource-dir).Trim() "lib\windows")).ShortPath -replace '\\', '/'
    $cmakeArch += (' -DCMAKE_C_FLAGS="/DWIN32 /D_WINDOWS /W3 -fprofile-instr-generate -fcoverage-mapping"' +
        ' -DCMAKE_SHARED_LINKER_FLAGS="/machine:x64 /LIBPATH:{0}" -DCMAKE_EXE_LINKER_FLAGS="/machine:x64 /LIBPATH:{0}"') -f $profileRuntime
}

& (Join-Path $PSScriptRoot "apply-openzfs-patches.ps1")

# OpenZFS currently generates this header into the source tree at configure time.
New-Item -ItemType Directory -Force -Path (Join-Path $openZfs "out\build") | Out-Null

$configure = '"{0}" -S "{1}" -B "{2}" -G Ninja -Wno-dev -DCMAKE_MAKE_PROGRAM="{3}" -DCMAKE_BUILD_TYPE={4} -DCMAKE_C_COMPILER="{5}" -DCMAKE_CXX_COMPILER="{5}" -DCMAKE_ASM_COMPILER="{5}" {8} -DZFSWINDOWS_ROOT="{6}" -DCMAKE_PROJECT_INCLUDE="{7}"' -f $cmake, $openZfs, $BuildDirectory, $ninja, $Configuration, $clang, ($root -replace '\\','/'), ($overlay -replace '\\','/'), $cmakeArch
# zfs_gitrev.h (git describe of the submodule) and zfswin-um's version are
# written at configure time too.
$configureKey = $configure + "`nopenzfs-windows " + (& git -C $openZfs rev-parse HEAD).Trim() + "`nZFSWIN_VERSION=$env:ZFSWIN_VERSION"
$configureStamp = Join-Path $BuildDirectory "zfswin-configure.txt"
$needsConfigure = -not (Test-Path (Join-Path $BuildDirectory "build.ninja")) -or -not (Test-Path $configureStamp) -or
    (Get-Content $configureStamp -Raw).Trim() -ne $configureKey
$commands = @(
    # VsDevCmd resolves vswhere.exe from PATH, which a bare cmd.exe may lack.
    'set "PATH={0};%PATH%"' -f (Split-Path -Parent $vswhere)
    'call "{0}" -arch={1} -host_arch=x64' -f $devShell, $Architecture
    'set "PATH=C:\Program Files\LLVM\bin;%PATH%"'
    # Configure only for a new tree or a changed command line (as in
    # build-driver.ps1); Ninja re-runs CMake itself when a CMakeLists.txt changes.
    if ($needsConfigure) { $configure }
    if (-not $ConfigureOnly) { '"{0}" --build "{1}" --target {2} --parallel {3}' -f $cmake, $BuildDirectory, ($Targets -join ' '), $Jobs }
) -join ' && '
if ($ConfigureOnly -and -not $needsConfigure) { return }

& cmd.exe /d /s /c $commands
if ($LASTEXITCODE -ne 0) { throw "OpenZFS write-core build failed with exit code $LASTEXITCODE." }
Set-Content -Encoding ascii $configureStamp $configureKey
if ($ConfigureOnly) { return }

if ($Targets -contains "zfs-write-core") {
    $binary = Join-Path $BuildDirectory "zfs-write-core\zfs-write-core.exe"
    if (-not (Test-Path $binary)) { throw "Expected output was not produced: $binary" }
    Write-Output $binary
}
