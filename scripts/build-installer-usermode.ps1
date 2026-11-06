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
    [Parameter(Mandatory)][string]$Version,
    # Debug: ...-usermode-debug.msi from the debug trees; Release: plain names.
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$tag = if ($Configuration -eq "Debug") { "-debug" } else { "" }
$build = Join-Path $root ("out\openzfs-write-$($Configuration.ToLowerInvariant())" + $(if ($Architecture -eq "x64") { "" } else { "-$Architecture" }) + "\zfswin-um")
$payload = Join-Path $root "out\installer-payload\usermode-$Architecture-$($Configuration.ToLowerInvariant())"
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
foreach ($d in "bin", "licenses", "symbols", "ZfsWin") { New-Item -ItemType Directory -Force (Join-Path $payload $d) | Out-Null }
Copy-Item $exe (Join-Path $payload "bin")
# Administration (plan step 18): the same broker, zfswin.exe, GUI and
# PowerShell module as the kernel edition, and the same zpool.exe/zfs.exe,
# which reach zfswin-um's engine through the ZFS bridge pipe.
$admin = Join-Path $root "out\admin-$Architecture"
$driver = Join-Path $root ("out\driver-package" + $(if ($Configuration -eq "Release") { "-release" } else { "" }) + $(if ($Architecture -eq "x64") { "" } else { "-$Architecture" }))
foreach ($required in (Join-Path $admin "ZfsWinGui.exe"), (Join-Path $admin "zfswin.exe"), (Join-Path $admin "ZfsWinBroker.exe"), (Join-Path $driver "zpool.exe"), (Join-Path $driver "zfs.exe")) {
    if (-not (Test-Path $required)) { throw "$required is missing; run build-admin.ps1 and build-driver.ps1 -Architecture $Architecture first." }
}
Copy-Item (Join-Path $admin "zfswin.exe"), (Join-Path $admin "ZfsWinBroker.exe"), (Join-Path $admin "ZfsWinGui.exe") (Join-Path $payload "bin")
foreach ($tool in "zpool.exe", "zfs.exe") { Copy-Item (Join-Path $driver $tool) (Join-Path $payload "bin") }
Copy-Item (Join-Path $build "zfswin-um.pdb") (Join-Path $payload "symbols")
Copy-Item (Join-Path $admin "symbols\*.pdb") (Join-Path $payload "symbols")
foreach ($pdb in "zpool.pdb", "zfs.pdb") {
    $p = Join-Path $driver "symbols\$pdb"
    if (Test-Path $p) { Copy-Item $p (Join-Path $payload "symbols") }
}
Copy-Item (Join-Path $root "src\powershell\ZfsWin\*") (Join-Path $payload "ZfsWin")
$psd1 = Join-Path $payload "ZfsWin\ZfsWin.psd1"
[IO.File]::WriteAllText($psd1, ([IO.File]::ReadAllText($psd1) -replace "ModuleVersion(\s*)=(\s*)'[^']*'", "ModuleVersion`$1=`$2'$Version'"))
Copy-Item (Join-Path $root "installer\ZfsWin.UserMode\README.html") $payload
Copy-Item (Join-Path $root "LICENSE.md") (Join-Path $payload "licenses\LICENSE.md")
Copy-Item (Join-Path $root "third_party\openzfs-windows\LICENSE") (Join-Path $payload "licenses\OpenZFS-LICENSE.txt")
Copy-Item (Join-Path $root "third_party\openzfs-windows\NOTICE") (Join-Path $payload "licenses\OpenZFS-NOTICE.txt")
Copy-Item $winfspLicense (Join-Path $payload "licenses\WinFsp-License.txt")
[ordered]@{
    product = "OpenZFS for Windows (user mode)"; version = $Version; architecture = $Architecture
    variant = "usermode"; configuration = $Configuration; requires = "WinFsp 2.1 or later (https://github.com/winfsp/winfsp)"
    repositoryCommit = (& git -C $root rev-parse HEAD).Trim()
    openzfsWindowsCommit = (& git -C (Join-Path $root "third_party\openzfs-windows") rev-parse HEAD).Trim()
    patches = @(Get-ChildItem (Join-Path $root "patches\openzfs-windows") -Filter *.patch | ForEach-Object Name)
} | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $payload "VERSION.json")

foreach ($exeFile in Get-ChildItem (Join-Path $payload "bin") -Filter *.exe) {
    if ((Get-AuthenticodeSignature $exeFile.FullName).SignerCertificate.Thumbprint -ne $cert.Thumbprint) {
        & $signtool sign /q /fd sha256 /sha1 $cert.Thumbprint $exeFile.FullName
        if ($LASTEXITCODE -ne 0) { throw "signtool failed for $($exeFile.Name)" }
    }
}
foreach ($script in Get-ChildItem (Join-Path $payload "ZfsWin") -Include *.psm1, *.psd1, *.ps1xml -Recurse) {
    $r = Set-AuthenticodeSignature -FilePath $script.FullName -Certificate $cert -HashAlgorithm SHA256
    if (-not $r.SignerCertificate) { throw "Signing $($script.Name) failed: $($r.StatusMessage)" }
}

$wixproj = Join-Path $root "installer\ZfsWin.UserMode\ZfsWin.UserMode.wixproj"
$bin = Join-Path $root "out\installer-build\usermode-$Architecture-$($Configuration.ToLowerInvariant())"
& dotnet build $wixproj -c Release "-p:Platform=$Architecture" "-p:Payload=$payload" "-p:ZfsWinVersion=$Version" "-p:OutputPath=$bin\" "-p:IntermediateOutputPath=$bin\obj\"
if ($LASTEXITCODE -ne 0) { throw "WiX build failed." }
New-Item -ItemType Directory -Force $release | Out-Null
$msi = Join-Path $release "OpenZFS-for-Windows-$Version-$Architecture-usermode$tag.msi"
Copy-Item (Join-Path $bin "OpenZFS-for-Windows-$Version-$Architecture-usermode.msi") $msi -Force
& $signtool sign /q /fd sha256 /sha1 $cert.Thumbprint /d "OpenZFS for Windows $Version (user mode)" $msi
if ($LASTEXITCODE -ne 0) { throw "signtool failed for the MSI" }

Get-ChildItem $release -File | Where-Object Name -ne "SHA256SUMS.txt" | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
} | Set-Content -Encoding ascii (Join-Path $release "SHA256SUMS.txt")
Write-Output $msi
