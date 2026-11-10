[CmdletBinding()]
<#
.SYNOPSIS
Builds the OpenZFS for Windows MSI (plan step 14) from out\driver-package
(scripts\build-driver.ps1) and out\admin-<arch> (scripts\build-admin.ps1),
into out\release\<version>.

.DESCRIPTION
Stages the payload, Authenticode-signs every binary, the PowerShell module
and the MSI with the zfsWindows test-signing certificate, refuses to package
any binary whose signature is missing or from another signer, then writes
SHA256SUMS.txt and a CycloneDX SBOM.  Nothing is installed on this machine;
install tests run only in the isolated VM (scripts\vm\Test-InstallerInVm.ps1).

The driver in out\driver-package is the test-signed build: the MSI is named
-testsigned and its preflight requires test signing and the certificate to
be trusted on the target.  Release signing (attestation through the Partner
Center) is a step-15 prerequisite.
#>
param(
    [ValidateSet("x64", "arm64")]
    [string]$Architecture = "x64",
    # kernel: the driver package (test-signed); usermode: zfswin-um.exe over
    # WinFsp (plan step 15), which needs no driver signature of ours.
    [ValidateSet("kernel", "usermode")]
    [string]$Variant = "kernel",
    # Overrides build\ZfsWinVersion.props for the MSI only, to produce a
    # second package for the VM upgrade test.
    [string]$Version = "",
    # Which native build to package: Debug (out\driver-package, the MSI named
    # ...-debug.msi) or Release (out\driver-package-release, plain names).
    # The .NET tools are always Release builds.
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    # Skips Windows Installer validation (the ICE checks) for a faster CI
    # build; numbered releases keep it.
    [switch]$NoValidation,
    # Run by build-release.ps1 -Parallel beside the other MSIs: the WiX
    # projects are restored already, and out\release\<version> is shared, so
    # the certificate copy and the checksums are left to build-release.ps1.
    [switch]$ParallelLane
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$version = if ($Version) { $Version } else { & (Join-Path $PSScriptRoot "Get-ZfsWinVersion.ps1") }
$tag = if ($Configuration -eq "Debug") { "-debug" } else { "" }
$driver = Join-Path $root ("out\driver-package" + $(if ($Configuration -eq "Release") { "-release" } else { "" }) + $(if ($Architecture -eq "x64") { "" } else { "-$Architecture" }))
$admin = Join-Path $root "out\admin-$Architecture"
$payload = Join-Path $root "out\installer-payload\$Architecture-$($Configuration.ToLowerInvariant())"
$release = Join-Path $root "out\release\$version"
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName

if ($Variant -eq "usermode") {
    & (Join-Path $PSScriptRoot "build-installer-usermode.ps1") -Architecture $Architecture -Version $version -Configuration $Configuration -NoValidation:$NoValidation -ParallelLane:$ParallelLane
    return
}

foreach ($required in (Join-Path $driver "OpenZFS.sys"), (Join-Path $admin "zfswin.exe"), (Join-Path $admin "ZfsWinBroker.exe")) {
    if (-not (Test-Path $required)) { throw "$required is missing; run build-driver.ps1 and build-admin.ps1 -Architecture $Architecture first." }
}
# The driver package must match the MSI's architecture.
$sys = [IO.File]::ReadAllBytes((Join-Path $driver "OpenZFS.sys"))
$machine = [BitConverter]::ToUInt16($sys, [BitConverter]::ToInt32($sys, 0x3C) + 4)
$expected = @{ x64 = 0x8664; arm64 = 0xAA64 }[$Architecture]
if ($machine -ne $expected) { throw ("out\driver-package\OpenZFS.sys is machine type 0x{0:X4}, not {1}." -f $machine, $Architecture) }

$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object Subject -eq "CN=zfsWindows Test Signing" | Select-Object -First 1
if (-not $cert) { throw "The zfsWindows test-signing certificate is missing; run build-driver.ps1 first." }

# --- stage --------------------------------------------------------------------
if (Test-Path $payload) { Remove-Item -Recurse -Force $payload }
foreach ($d in "bin", "driver", "licenses", "symbols", "ZfsWin") { New-Item -ItemType Directory -Force (Join-Path $payload $d) | Out-Null }
Copy-Item (Join-Path $admin "zfswin.exe"), (Join-Path $admin "ZfsWinBroker.exe"), (Join-Path $admin "ZfsWinGui.exe") (Join-Path $payload "bin")
foreach ($tool in "zpool.exe", "zfs.exe", "zdb.exe", "kstat.exe", "zfsinstaller.exe") { Copy-Item (Join-Path $driver $tool) (Join-Path $payload "bin") }
foreach ($file in "OpenZFS.inf", "OpenZFS.sys", "OpenZFS.cat", "OpenZFS.man", "OpenZVOL.inf", "OpenZVOL.sys", "OpenZVOL.cat") {
    Copy-Item (Join-Path $driver $file) (Join-Path $payload "driver")
}
# PDBs in the Debug package only (the .wixproj leaves them out of Release).
if ($Configuration -eq "Debug") {
    Copy-Item (Join-Path $admin "symbols\*.pdb") (Join-Path $payload "symbols")
    Copy-Item (Join-Path $driver "symbols\*.pdb") (Join-Path $payload "symbols")
}
Copy-Item (Join-Path $root "src\powershell\ZfsWin\*") (Join-Path $payload "ZfsWin")
# The module manifest carries the build's version (stamped before signing).
$psd1 = Join-Path $payload "ZfsWin\ZfsWin.psd1"
[IO.File]::WriteAllText($psd1, ([IO.File]::ReadAllText($psd1) -replace "ModuleVersion(\s*)=(\s*)'[^']*'", "ModuleVersion`$1=`$2'$version'"))
Copy-Item (Join-Path $root "installer\ZfsWin.Installer\README.html") $payload
Copy-Item (Join-Path $root "LICENSE.md") (Join-Path $payload "licenses\LICENSE.md")
Copy-Item (Join-Path $root "third_party\openzfs-windows\LICENSE") (Join-Path $payload "licenses\OpenZFS-LICENSE.txt")
Copy-Item (Join-Path $root "third_party\openzfs-windows\NOTICE") (Join-Path $payload "licenses\OpenZFS-NOTICE.txt")
$commit = (& git -C $root rev-parse HEAD).Trim()
$openzfsCommit = (& git -C (Join-Path $root "third_party\openzfs-windows") rev-parse HEAD).Trim()
[ordered]@{
    product = "OpenZFS for Windows"; version = $version; architecture = $Architecture
    configuration = $Configuration; signing = "test"; testSigningThumbprint = $cert.Thumbprint
    repositoryCommit = $commit; openzfsWindowsCommit = $openzfsCommit
    driverVersion = ((Get-Content (Join-Path $driver "OpenZFS.inf") | Select-String '^DriverVer').ToString() -replace '^DriverVer\s*=\s*', '')
    patches = @(Get-ChildItem (Join-Path $root "patches\openzfs-windows") -Filter *.patch | ForEach-Object Name)
} | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $payload "VERSION.json")

# --- sign and verify ----------------------------------------------------------------
foreach ($exe in Get-ChildItem (Join-Path $payload "bin") -Filter *.exe) {
    if ((Get-AuthenticodeSignature $exe.FullName).SignerCertificate.Thumbprint -ne $cert.Thumbprint) {
        & $signtool sign /q /fd sha256 /sha1 $cert.Thumbprint $exe.FullName
        if ($LASTEXITCODE -ne 0) { throw "signtool failed for $($exe.Name)" }
    }
}
foreach ($script in Get-ChildItem (Join-Path $payload "ZfsWin") -Include *.psm1, *.psd1, *.ps1xml -Recurse) {
    $r = Set-AuthenticodeSignature -FilePath $script.FullName -Certificate $cert -HashAlgorithm SHA256
    if (-not $r.SignerCertificate) { throw "Signing $($script.Name) failed: $($r.StatusMessage)" }
}
# Never package unsigned bits: every binary, catalog and script must carry
# this build's signature.  (The test root is not trusted on the build host,
# so the status is UnknownError; the signer is what is checked.)
$unsigned = Get-ChildItem $payload -Recurse -Include *.exe, *.sys, *.cat, *.psm1, *.psd1, *.ps1xml |
    Where-Object { (Get-AuthenticodeSignature $_.FullName).SignerCertificate.Thumbprint -ne $cert.Thumbprint }
if ($unsigned) { throw "Unsigned or foreign-signed files in the payload: $($unsigned.Name -join ', ')" }

# --- MSI ------------------------------------------------------------------------------
$wixproj = Join-Path $root "installer\ZfsWin.Installer\ZfsWin.Installer.wixproj"
$bin = Join-Path $root "out\installer-build\$Architecture-$($Configuration.ToLowerInvariant())"
# Its own intermediate folder per build: a shared obj\ makes WiX skip the
# link step for the second configuration and then miss the MSI (MSB3030).
$restore = @(if ($ParallelLane) { "--no-restore" })
& dotnet build $wixproj -c Release @restore "-p:Platform=$Architecture" "-p:Payload=$payload" "-p:TestSigningThumbprint=$($cert.Thumbprint)" "-p:ZfsWinVersion=$version" "-p:PackageConfiguration=$Configuration" "-p:SuppressValidation=$([bool]$NoValidation)" "-p:OutputPath=$bin\" "-p:IntermediateOutputPath=$bin\obj\"
if ($LASTEXITCODE -ne 0) { throw "WiX build failed." }
New-Item -ItemType Directory -Force $release | Out-Null
$msi = Join-Path $release "OpenZFS-for-Windows-$version-$Architecture-testsigned$tag.msi"
Copy-Item (Join-Path $bin "OpenZFS-for-Windows-$version-$Architecture.msi") $msi -Force
& $signtool sign /q /fd sha256 /sha1 $cert.Thumbprint /d "OpenZFS for Windows $version" $msi
if ($LASTEXITCODE -ne 0) { throw "signtool failed for the MSI" }
if (-not $ParallelLane) { Copy-Item (Join-Path $driver "zfswindows-test.cer") $release -Force }

# --- checksums and SBOM ------------------------------------------------------------
$components = foreach ($f in Get-ChildItem $payload -Recurse -File | Where-Object Extension -in ".exe", ".sys", ".cat", ".inf", ".psm1", ".psd1") {
    $license = if ($f.Name -in "zfswin.exe", "ZfsWinBroker.exe", "ZfsWinGui.exe" -or $f.Extension -in ".psm1", ".psd1") { "MIT" } else { "CDDL-1.0" }
    [ordered]@{
        type = if ($f.Extension -in ".exe", ".sys") { "application" } else { "file" }
        name = $f.Name; version = $version
        hashes = @([ordered]@{ alg = "SHA-256"; content = (Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant() })
        licenses = @(@{ license = @{ id = $license } })
    }
}
$components += [ordered]@{ type = "library"; name = "OpenZFS on Windows"; version = "zfswin-2.4.4rc9+patches"; purl = "pkg:github/openzfsonwindows/openzfs@$openzfsCommit"; licenses = @(@{ license = @{ id = "CDDL-1.0" } }) }
$components += [ordered]@{ type = "library"; name = "OpenSSL libcrypto (static, in zpool/zfs/zdb)"; licenses = @(@{ license = @{ id = "Apache-2.0" } }) }
$components += [ordered]@{ type = "framework"; name = ".NET NativeAOT runtime (in zfswin/ZfsWinBroker)"; version = "10.0"; licenses = @(@{ license = @{ id = "MIT" } }) }
[ordered]@{
    bomFormat = "CycloneDX"; specVersion = "1.5"; version = 1
    metadata = [ordered]@{
        timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
        component = [ordered]@{ type = "application"; name = "OpenZFS for Windows"; version = $version }
    }
    components = @($components)
} | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 (Join-Path $release "OpenZFS-for-Windows-$version-$Architecture$tag.cdx.json")

if (-not $ParallelLane) {
    Get-ChildItem $release -File | Where-Object Name -ne "SHA256SUMS.txt" | ForEach-Object {
        "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
    } | Set-Content -Encoding ascii (Join-Path $release "SHA256SUMS.txt")
    Get-ChildItem $release -File | Select-Object Name, Length | Format-Table -AutoSize | Out-String | Write-Host
}
Write-Output $msi
