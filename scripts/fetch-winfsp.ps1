[CmdletBinding()]
<#
.SYNOPSIS
Downloads the pinned WinFsp release and extracts its SDK (plan step 15).

.DESCRIPTION
The official, Microsoft-signed MSI is verified against its pinned SHA-256
and Authenticode signature, then unpacked with an administrative install
(msiexec /a), which copies files only: no driver or service is installed on
this machine.  The headers and import libraries build zfswin-um.exe; the MSI
itself is what the VM tests install.  Both stay out of git.
#>
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$version = "2.1.25156"
$sha256 = "073A70E00F77423E34BED98B86E600DEF93393BA5822204FAC57A29324DB9F7A"
$url = "https://github.com/winfsp/winfsp/releases/download/v2.1/winfsp-$version.msi"
$dir = Join-Path $root "third_party\winfsp"
$msi = Join-Path $dir "download\winfsp-$version.msi"
$sdk = Join-Path $dir "sdk-2.1"

New-Item -ItemType Directory -Force (Split-Path -Parent $msi) | Out-Null
if (-not (Test-Path $msi) -or (Get-FileHash $msi).Hash -ne $sha256) {
    Invoke-WebRequest $url -OutFile $msi
}
if ((Get-FileHash $msi).Hash -ne $sha256) { throw "winfsp-$version.msi does not match its pinned SHA-256." }
$signature = Get-AuthenticodeSignature $msi
if ($signature.Status -ne "Valid") { throw "winfsp-$version.msi signature: $($signature.Status)" }

if (-not (Test-Path (Join-Path $sdk "DYNAMIC\inc\winfsp\winfsp.h"))) {
    $p = Start-Process msiexec.exe -ArgumentList '/a', "`"$msi`"", '/qn', "TARGETDIR=`"$sdk`"" -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "msiexec /a failed with $($p.ExitCode)" }
}
Write-Output $sdk
