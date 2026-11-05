[CmdletBinding()]
<#
.SYNOPSIS
Builds the user-mode (WinFsp) MSI (plan step 15) into out\release\<version>.

.DESCRIPTION
Called by build-installer.ps1 -Variant usermode.  Stages zfswin-um.exe from
the write-core build tree (scripts\build-write-core.ps1 -Targets zfswin-um),
the licenses (including WinFsp's, whose FLOSS exception requires its notice)
and symbols, signs the binary and the MSI with the zfsWindows code-signing
certificate, and adds the package to SHA256SUMS.txt.  Nothing requires that
signature to be trusted: there is no kernel driver of ours in this package.
Nothing is installed on this machine.
#>
param(
    [ValidateSet("x64", "arm64")]
    [string]$Architecture = "x64",
    [Parameter(Mandatory)][string]$Version
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$build = Join-Path $root ("out\openzfs-write-debug" + $(if ($Architecture -eq "x64") { "" } else { "-$Architecture" }) + "\zfswin-um")
$payload = Join-Path $root "out\installer-payload\usermode-$Architecture"
$release = Join-Path $root "out\release\$Version"
$winfspLicense = Join-Path $root "third_party\winfsp\sdk-2.1\DYNAMIC\License.txt"
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName

$exe = Join-Path $build "zfswin-um.exe"
if (-not (Test-Path $exe)) { throw "$exe is missing; run build-write-core.ps1 -Targets zfswin-um first." }
if (-not (Test-Path $winfspLicense)) { throw "The WinFsp SDK is missing; run scripts\fetch-winfsp.ps1 first." }
$bytes = [IO.File]::ReadAllBytes($exe)
$machine = [BitConverter]::ToUInt16($bytes, [BitConverter]::ToInt32($bytes, 0x3C) + 4)
if ($machine -ne @{ x64 = 0x8664; arm64 = 0xAA64 }[$Architecture]) { throw ("zfswin-um.exe is machine type 0x{0:X4}, not {1}." -f $machine, $Architecture) }

$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object Subject -eq "CN=zfsWindows Test Signing" | Select-Object -First 1
if (-not $cert) { throw "The zfsWindows code-signing certificate is missing; run build-driver.ps1 first." }

if (Test-Path $payload) { Remove-Item -Recurse -Force $payload }
foreach ($d in "bin", "licenses", "symbols") { New-Item -ItemType Directory -Force (Join-Path $payload $d) | Out-Null }
Copy-Item $exe (Join-Path $payload "bin")
# The administration tool (its User mode tab manages this edition).
$gui = Join-Path $root "out\admin-$Architecture\ZfsWinGui.exe"
if (-not (Test-Path $gui)) { throw "$gui is missing; run build-admin.ps1 -Architecture $Architecture first." }
Copy-Item $gui (Join-Path $payload "bin")
Copy-Item (Join-Path $build "zfswin-um.pdb") (Join-Path $payload "symbols")
Copy-Item (Join-Path $root "installer\ZfsWin.UserMode\README.html") $payload
Copy-Item (Join-Path $root "LICENSE.md") (Join-Path $payload "licenses\LICENSE.md")
Copy-Item (Join-Path $root "third_party\openzfs-windows\LICENSE") (Join-Path $payload "licenses\OpenZFS-LICENSE.txt")
Copy-Item (Join-Path $root "third_party\openzfs-windows\NOTICE") (Join-Path $payload "licenses\OpenZFS-NOTICE.txt")
Copy-Item $winfspLicense (Join-Path $payload "licenses\WinFsp-License.txt")
[ordered]@{
    product = "OpenZFS for Windows (user mode)"; version = $Version; architecture = $Architecture
    variant = "usermode"; requires = "WinFsp 2.1 or later (https://github.com/winfsp/winfsp)"
    repositoryCommit = (& git -C $root rev-parse HEAD).Trim()
    openzfsWindowsCommit = (& git -C (Join-Path $root "third_party\openzfs-windows") rev-parse HEAD).Trim()
    patches = @(Get-ChildItem (Join-Path $root "patches\openzfs-windows") -Filter *.patch | ForEach-Object Name)
} | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $payload "VERSION.json")

foreach ($bin in "zfswin-um.exe", "ZfsWinGui.exe") {
    & $signtool sign /q /fd sha256 /sha1 $cert.Thumbprint (Join-Path $payload "bin\$bin")
    if ($LASTEXITCODE -ne 0) { throw "signtool failed for $bin" }
}

$wixproj = Join-Path $root "installer\ZfsWin.UserMode\ZfsWin.UserMode.wixproj"
$bin = Join-Path $root "out\installer-build\usermode-$Architecture"
& dotnet build $wixproj -c Release "-p:Platform=$Architecture" "-p:Payload=$payload" "-p:ZfsWinVersion=$Version" "-p:OutputPath=$bin\"
if ($LASTEXITCODE -ne 0) { throw "WiX build failed." }
New-Item -ItemType Directory -Force $release | Out-Null
$msi = Join-Path $release "OpenZFS-for-Windows-$Version-$Architecture-usermode.msi"
Copy-Item (Join-Path $bin "OpenZFS-for-Windows-$Version-$Architecture-usermode.msi") $msi -Force
& $signtool sign /q /fd sha256 /sha1 $cert.Thumbprint /d "OpenZFS for Windows $Version (user mode)" $msi
if ($LASTEXITCODE -ne 0) { throw "signtool failed for the MSI" }

Get-ChildItem $release -File | Where-Object Name -ne "SHA256SUMS.txt" | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
} | Set-Content -Encoding ascii (Join-Path $release "SHA256SUMS.txt")
Write-Output $msi
