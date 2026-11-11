function Get-Arm64OpenSslCandidates {
    param([Parameter(Mandatory)][string]$RepositoryRoot)

    $paths = [System.Collections.Generic.List[string]]::new()
    $paths.Add((Join-Path $RepositoryRoot "out\vcpkg-openssl"))
    if ($env:ZFSWIN_CI_DATA) {
        $dataPath = Join-Path $env:ZFSWIN_CI_DATA "out\vcpkg-openssl"
        if (-not $paths.Contains($dataPath)) { $paths.Add($dataPath) }
    }
    return $paths
}

function Get-Arm64OpenSslStatus {
    param([Parameter(Mandatory)][string]$InstallBase)

    $prefix = Join-Path $InstallBase "installed\arm64-windows-static"
    $requiredFiles = @(
        "include\openssl\ssl.h"
        "debug\lib\libcrypto.lib"
        "debug\lib\libssl.lib"
        "lib\libcrypto.lib"
        "lib\libssl.lib"
    )
    $missing = @($requiredFiles | Where-Object {
        -not (Test-Path -LiteralPath (Join-Path $prefix $_) -PathType Leaf)
    })
    [pscustomobject]@{
        InstallBase = $InstallBase
        InstallRoot = $prefix
        IsComplete = ($missing.Count -eq 0)
        MissingFiles = $missing
    }
}

function Get-Arm64OpenSslRoot {
    param([Parameter(Mandatory)][string]$RepositoryRoot)

    foreach ($candidate in Get-Arm64OpenSslCandidates -RepositoryRoot $RepositoryRoot) {
        $status = Get-Arm64OpenSslStatus -InstallBase $candidate
        if ($status.IsComplete) { return $status.InstallRoot }
    }
    return $null
}

function Ensure-Arm64OpenSsl {
    param([Parameter(Mandatory)][string]$RepositoryRoot)

    $candidates = @(Get-Arm64OpenSslCandidates -RepositoryRoot $RepositoryRoot)
    foreach ($candidate in $candidates) {
        $status = Get-Arm64OpenSslStatus -InstallBase $candidate
        if ($status.IsComplete) {
            Write-Host "ARM64 OpenSSL cache hit: $($status.InstallRoot)"
            return $status.InstallRoot
        }
    }

    $installBase = $candidates[0]
    if ($env:ZFSWIN_CI_DATA) {
        $dataRoot = $env:ZFSWIN_CI_DATA
        if (Test-Path -LiteralPath $dataRoot -PathType Container) {
            $installBase = Join-Path $dataRoot "out\vcpkg-openssl"
        } else {
            Write-Host "ZFSWIN_CI_DATA is set but unavailable; provisioning in the workspace."
        }
    }
    $target = Get-Arm64OpenSslStatus -InstallBase $installBase
    $missingSummary = $target.MissingFiles | Select-Object -First 5
    Write-Host "ARM64 OpenSSL cache miss at $installBase; missing: $($missingSummary -join ', ')"

    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
        throw "vswhere.exe was not found; Visual Studio vcpkg is required to provision ARM64 OpenSSL."
    }
    $vs = & $vswhere -latest -products * -property installationPath
    if (-not $vs) { throw "Visual Studio installation was not found." }
    $vcpkg = Join-Path $vs "VC\vcpkg\vcpkg.exe"
    if (-not (Test-Path -LiteralPath $vcpkg -PathType Leaf)) {
        throw "Visual Studio vcpkg was not found at $vcpkg."
    }

    New-Item -ItemType Directory -Force -Path $installBase | Out-Null
    $manifest = @'
{
  "name": "zfswin-openssl",
  "version": "1.0.0",
  "dependencies": [ "openssl" ],
  "builtin-baseline": "29718cfec805208b4e2d80ed91e681143b488dd7"
}
'@
    Set-Content -LiteralPath (Join-Path $installBase "vcpkg.json") -Value $manifest -Encoding utf8

    Write-Host "Building ARM64 OpenSSL with the pinned vcpkg baseline."
    Push-Location -LiteralPath $installBase
    try {
        & $vcpkg install --triplet arm64-windows-static "--x-install-root=$(Join-Path $installBase 'installed')"
        $vcpkgExitCode = $LASTEXITCODE
    } finally {
        Pop-Location
    }
    if ($vcpkgExitCode -ne 0) { throw "vcpkg failed to install ARM64 OpenSSL (exit code $vcpkgExitCode)." }

    $status = Get-Arm64OpenSslStatus -InstallBase $installBase
    if (-not $status.IsComplete) {
        throw "vcpkg completed but ARM64 OpenSSL is incomplete at $installBase; missing: $($status.MissingFiles -join ', ')."
    }
    Write-Host "ARM64 OpenSSL provisioned: $($status.InstallRoot)"
    return $status.InstallRoot
}
