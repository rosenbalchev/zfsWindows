[CmdletBinding()]
<#
.SYNOPSIS
Builds the pinned OpenZFS-on-Windows kernel driver and its tools, and
packages them test-signed for the isolated driver VM (plan step 12).

.DESCRIPTION
The kernel sources must be compiled with MSVC (driver.c refuses clang), so
this uses a separate build tree configured the way upstream CI does: Ninja
with MSVC as the default compiler; upstream's use_clang() still selects
clang-cl for the userland libraries.  The repository patches are applied
first.

The package (out\driver-package) is signed with a self-signed code-signing
certificate created in the current user's store (CN=zfsWindows Test
Signing); its public part is exported as zfswindows-test.cer for the VM to
trust.  Nothing is installed or loaded on this machine.
#>
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    # arm64 cross-compiles with the MSVC/WDK ARM64 tools and the vcpkg
    # arm64-windows-static OpenSSL in out\vcpkg-openssl (plan step 17).
    [ValidateSet("x64", "arm64")]
    [string]$Architecture = "x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$openZfs = Join-Path $root "third_party\openzfs-windows"
$suffix = if ($Architecture -eq "x64") { "" } else { "-$Architecture" }
$build = Join-Path $root "out\openzfs-driver-$($Configuration.ToLowerInvariant())$suffix"
# Debug keeps the original folder (the VM tests' default); Release has its own.
$package = Join-Path $root ("out\driver-package" + $(if ($Configuration -eq "Release") { "-release" } else { "" }) + $suffix)
$cmakeArch = '-DOPENSSL_ROOT_DIR="C:/Program Files/OpenSSL-Win64"'
if ($Architecture -eq "arm64") {
    $ssl = (Join-Path $root "out\vcpkg-openssl\installed\arm64-windows-static") -replace '\\', '/'
    if (-not (Test-Path "$ssl/debug/lib/libcrypto.lib")) { throw "ARM64 OpenSSL is missing: build it with vcpkg into out\vcpkg-openssl (see docs/building.md)." }
    $sslLib = if ($Configuration -eq "Debug") { "$ssl/debug/lib" } else { "$ssl/lib" }
    $cmakeArch = ('-DCMAKE_SYSTEM_NAME=Windows -DCMAKE_SYSTEM_PROCESSOR=ARM64 -DOPENSSL_ROOT_DIR="{0}" -DOPENSSL_INCLUDE_DIR="{0}/include" ' +
        '-DOPENSSL_CRYPTO_LIBRARY="{1}/libcrypto.lib" -DOPENSSL_SSL_LIBRARY="{1}/libssl.lib" -DCRYPTO_STATIC_TEST="{1}/libcrypto.lib"') -f $ssl, $sslLib
}
$targets = "OpenZFS", "OpenZVOL", "zfsinstaller", "zpool", "zfs", "zdb", "kstat"

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -property installationPath
$devShell = Join-Path $vs "Common7\Tools\VsDevCmd.bat"
$cmake = Join-Path $vs "Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
$ninja = Join-Path $vs "Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe"
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName

& (Join-Path $PSScriptRoot "apply-openzfs-patches.ps1")
New-Item -ItemType Directory -Force -Path (Join-Path $openZfs "out\build") | Out-Null

$commands = @(
    'set "PATH={0};C:\Program Files\LLVM\bin;%PATH%"' -f (Split-Path -Parent $vswhere)
    'call "{0}" -arch={1} -host_arch=x64' -f $devShell, $Architecture
    '"{0}" -S "{1}" -B "{2}" -G Ninja -DCMAKE_MAKE_PROGRAM="{3}" -DCMAKE_BUILD_TYPE={4} {5}' -f $cmake, $openZfs, $build, $ninja, $Configuration, $cmakeArch
    '"{0}" --build "{1}" --target {2} --parallel 8' -f $cmake, $build, ($targets -join ' ')
) -join ' && '
& cmd.exe /d /s /c $commands
if ($LASTEXITCODE -ne 0) { throw "Driver build failed with exit code $LASTEXITCODE." }

# --- package --------------------------------------------------------------
if (Test-Path $package) { Remove-Item -Recurse -Force $package }
New-Item -ItemType Directory -Force -Path (Join-Path $package "symbols") | Out-Null
$driver = Join-Path $build "module\os\windows\driver"
$zvol = Join-Path $build "module\os\windows\zvol\driver"
$files = @(
    "$driver\OpenZFS.sys", "$driver\OpenZFS.inf", "$driver\OpenZFS.cat", "$driver\OpenZFS.man",
    # zfsinstaller installs the zvol driver alongside the filesystem driver.
    "$zvol\OpenZVOL.sys", "$zvol\OpenZVOL.inf", "$zvol\OpenZVOL.cat",
    "$build\cmd\os\windows\zfsinstaller\zfsinstaller.exe",
    "$build\cmd\os\windows\kstat\kstat.exe",
    "$build\cmd\zpool\zpool.exe", "$build\cmd\zfs\zfs.exe", "$build\cmd\zdb\zdb.exe")
foreach ($file in $files) {
    if (-not (Test-Path $file)) { throw "Build output missing: $file" }
    Copy-Item $file $package
}
Get-ChildItem $driver, $zvol, "$build\cmd" -Recurse -Filter *.pdb |
    Where-Object { $_.BaseName -in @('OpenZFS', 'OpenZVOL', 'zfsinstaller', 'kstat', 'zpool', 'zfs', 'zdb') } |
    Copy-Item -Destination (Join-Path $package "symbols")

# --- test signing -----------------------------------------------------------
$subject = "CN=zfsWindows Test Signing"
$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
    Where-Object Subject -eq $subject | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject `
        -CertStoreLocation Cert:\CurrentUser\My -KeyExportPolicy NonExportable `
        -NotAfter (Get-Date).AddYears(3)
}
Export-Certificate -Cert $cert -FilePath (Join-Path $package "zfswindows-test.cer") | Out-Null
foreach ($file in "OpenZFS.sys", "OpenZFS.cat", "OpenZVOL.sys", "OpenZVOL.cat", "zfsinstaller.exe") {
    & $signtool sign /q /fd sha256 /sha1 $cert.Thumbprint (Join-Path $package $file)
    if ($LASTEXITCODE -ne 0) { throw "signtool failed for $file" }
}
Get-ChildItem $package -File | Select-Object Name, Length | Format-Table -AutoSize | Out-String | Write-Host
Write-Output $package
