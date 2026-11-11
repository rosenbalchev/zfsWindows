$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "arm64-openssl.ps1")
$root = Split-Path -Parent $PSScriptRoot
Ensure-Arm64OpenSsl -RepositoryRoot $root | Out-Null
