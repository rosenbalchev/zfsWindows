[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [string]$BuildDirectory,
    # Other targets of the same configured tree, e.g. the kernel driver
    # (OpenZFS) and its tools (zpool.exe, zfs.exe) for the isolated test VM.
    [string[]]$Targets = @("zfs-write-core")
)

$ErrorActionPreference = "Stop"
# powershell -File passes "a,b" as one string.
$Targets = @($Targets | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$root = Split-Path -Parent $PSScriptRoot
$openZfs = Join-Path $root "third_party\openzfs-windows"
if (-not $BuildDirectory) {
    $BuildDirectory = Join-Path $root "out\openzfs-write-$($Configuration.ToLowerInvariant())"
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

& (Join-Path $PSScriptRoot "apply-openzfs-patches.ps1")

# OpenZFS currently generates this header into the source tree at configure time.
New-Item -ItemType Directory -Force -Path (Join-Path $openZfs "out\build") | Out-Null

$configure = @(
    # VsDevCmd resolves vswhere.exe from PATH, which a bare cmd.exe may lack.
    'set "PATH={0};%PATH%"' -f (Split-Path -Parent $vswhere)
    'call "{0}" -arch=x64 -host_arch=x64' -f $devShell
    'set "PATH=C:\Program Files\LLVM\bin;%PATH%"'
    '"{0}" -S "{1}" -B "{2}" -G Ninja -DCMAKE_MAKE_PROGRAM="{3}" -DCMAKE_BUILD_TYPE={4} -DCMAKE_C_COMPILER="{5}" -DCMAKE_CXX_COMPILER="{5}" -DCMAKE_ASM_COMPILER="{5}" -DOPENSSL_ROOT_DIR="C:/Program Files/OpenSSL-Win64" -DZFSWINDOWS_ROOT="{6}" -DCMAKE_PROJECT_INCLUDE="{7}"' -f $cmake, $openZfs, $BuildDirectory, $ninja, $Configuration, $clang, ($root -replace '\\','/'), ($overlay -replace '\\','/')
    '"{0}" --build "{1}" --target {2} --parallel 8' -f $cmake, $BuildDirectory, ($Targets -join ' ')
) -join ' && '

& cmd.exe /d /s /c $configure
if ($LASTEXITCODE -ne 0) { throw "OpenZFS write-core build failed with exit code $LASTEXITCODE." }

if ($Targets -contains "zfs-write-core") {
    $binary = Join-Path $BuildDirectory "zfs-write-core\zfs-write-core.exe"
    if (-not (Test-Path $binary)) { throw "Expected output was not produced: $binary" }
    Write-Output $binary
}
