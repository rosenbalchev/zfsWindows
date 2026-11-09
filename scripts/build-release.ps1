[CmdletBinding()]
<#
.SYNOPSIS
Builds a numbered release of both variants into out\release\<version>\
(plan step 17).

.DESCRIPTION
1. Increments build\BUILD_NUMBER: every release build gets a new version,
   <prefix>.<build number> (1.0.N), which is what Windows Installer compares
   for upgrades.
2. Builds, with that version stamped in: the administration suite (tests
   first), zfs-write-core and zfswin-um.exe (ZFSWIN_VERSION), the
   kernel-driver MSI (test-signed driver package from out\driver-package;
   -BuildDriver rebuilds it first) and the user-mode MSI.
3. Checks that out\release\<version>\ has every MSI, SBOM and the test
   certificate, writes SHA256SUMS and VERSION.json there, and prints the
   result.  Nothing is committed: CI publishes the packages (README,
   "Download and install").

Nothing is installed on this machine.  -NoIncrement rebuilds the current
number (for a rebuild that must keep its version).
#>
param(
    [switch]$BuildDriver,
    [switch]$NoIncrement,
    # Both by default.  ARM64 is cross-compiled and statically checked
    # (machine type, signatures) but cannot be run on an x64 build host.
    [ValidateSet("x64", "arm64")]
    [string[]]$Architecture = @("x64", "arm64"),
    # Both by default, side by side in out\release\<version>.  Release: optimized native code (plain file names); Debug: unoptimized
    # with full checks (file names end in -debug).  The .NET tools are
    # Release builds in both.
    [ValidateSet("Debug", "Release")]
    [string[]]$Configuration = @("Release", "Debug"),
    # Skips the MSIs' ICE validation (CI); numbered releases keep it.
    [switch]$NoValidation,
    # Builds the combinations side by side (CI): every write they share
    # (patches, certificate, configures, WiX restore) first, one at a time;
    # then the administration suite and one lane per architecture and
    # configuration (driver, engine); then all MSIs.  Each lane logs to
    # out\logs\<lane>.log, printed when the phase ends.
    [switch]$Parallel
)

$ErrorActionPreference = "Stop"
$Architecture = @($Architecture | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Configuration = @($Configuration | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$root = Split-Path -Parent $PSScriptRoot
$version = if ($NoIncrement) { & (Join-Path $PSScriptRoot "Get-ZfsWinVersion.ps1") } else { & (Join-Path $PSScriptRoot "Get-ZfsWinVersion.ps1") -Next }
Write-Host "== OpenZFS for Windows $version ($($Architecture -join ', '))"
$env:ZFSWIN_VERSION = $version

function Step($name, [scriptblock]$block) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Write-Host "-- $name"
    & $block
    if ($LASTEXITCODE -ne 0) { throw "$name failed with exit code $LASTEXITCODE." }
    Write-Host ("   {0} done in {1:n0} s" -f $name, $watch.Elapsed.TotalSeconds)
}

# Each build script runs in a child process: native tools write progress to
# stderr, which a parent with $ErrorActionPreference=Stop would treat as fatal.
function Run($script, [string[]]$arguments) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot $script) @arguments
}

# zfs-um-test: the engine for the unit tests (scripts\test-usermode.ps1).
$engineTargets = "zfs-write-core,zfswin-um,zfs-um-test"
$validation = @(if ($NoValidation) { "-NoValidation" })

# Runs each lane (name -> list of @(script, arguments)) as its own process,
# its steps in order, output to out\logs\<lane>.log.  The first failed lane
# stops the others; the logs are printed (grouped in GitHub Actions) when
# all have ended.
function Invoke-Lanes($phase, $lanes) {
    $logs = Join-Path $root "out\logs"
    New-Item -ItemType Directory -Force $logs | Out-Null
    Write-Host "-- $phase ($($lanes.Keys -join ', '))"
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $running = [ordered]@{}
    foreach ($name in $lanes.Keys) {
        $steps = foreach ($step in $lanes[$name]) {
            'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "{0}" {1}' -f (Join-Path $PSScriptRoot $step[0]), ($step[1] -join ' ')
        }
        $log = Join-Path $logs "$name.log"
        $process = Start-Process cmd.exe -ArgumentList ('/d /s /c "({0}) > "{1}" 2>&1"' -f ($steps -join ' && '), $log) -NoNewWindow -PassThru
        $null = $process.Handle  # keeps ExitCode readable after the exit
        $running[$name] = [pscustomobject]@{ Process = $process; Log = $log; Seconds = $null; ExitCode = $null }
    }
    $failed = $null
    while (@($running.Values | Where-Object { $null -eq $_.ExitCode }).Count) {
        foreach ($name in @($running.Keys)) {
            $lane = $running[$name]
            if ($null -ne $lane.ExitCode -or -not $lane.Process.HasExited) { continue }
            $lane.ExitCode = $lane.Process.ExitCode
            $lane.Seconds = $watch.Elapsed.TotalSeconds
            if ($lane.ExitCode -ne 0 -and -not $failed) {
                $failed = $name
                foreach ($other in $running.Values | Where-Object { -not $_.Process.HasExited }) {
                    & cmd.exe /d /c "taskkill /T /F /PID $($other.Process.Id) >nul 2>&1"
                }
            }
        }
        Start-Sleep -Milliseconds 500
    }
    foreach ($name in $running.Keys) {
        $lane = $running[$name]
        $title = "{0}: {1}, {2:n0} s" -f $name, $(if ($lane.ExitCode -eq 0) { "done" } elseif ($name -eq $failed) { "failed ($($lane.ExitCode))" } else { "stopped" }), $lane.Seconds
        if ($env:GITHUB_ACTIONS) { Write-Host "::group::$title" } else { Write-Host "---- $title" }
        Get-Content $lane.Log | Write-Host
        if ($env:GITHUB_ACTIONS) { Write-Host "::endgroup::" }
    }
    foreach ($name in $running.Keys) { Write-Host ("   {0} {1:n0} s" -f $name, $running[$name].Seconds) }
    if ($failed) {
        Write-Host "== $failed failed; the end of $($running[$failed].Log):"
        Get-Content $running[$failed].Log -Tail 40 | Write-Host
        throw "$phase failed: $failed."
    }
    Write-Host ("   {0} done in {1:n0} s" -f $phase, $watch.Elapsed.TotalSeconds)
}

if (-not $Parallel) {
    Step "administration suite" { Run "build-admin.ps1" @("-Architecture", ($Architecture -join ',')) }
    foreach ($config in $Configuration) {
        foreach ($arch in $Architecture) {
            if ($BuildDriver) { Step "driver ($arch, $config)" { Run "build-driver.ps1" @("-Architecture", $arch, "-Configuration", $config) } }
            Step "user-mode file system ($arch, $config)" { Run "build-write-core.ps1" @("-Targets", $engineTargets, "-Architecture", $arch, "-Configuration", $config) }
            Step "kernel-driver MSI ($arch, $config)" { Run "build-installer.ps1" (@("-Architecture", $arch, "-Variant", "kernel", "-Configuration", $config) + $validation) }
            Step "user-mode MSI ($arch, $config)" { Run "build-installer.ps1" (@("-Architecture", $arch, "-Variant", "usermode", "-Configuration", $config) + $validation) }
        }
    }
} else {
    $combinations = foreach ($config in $Configuration) { foreach ($arch in $Architecture) { [pscustomobject]@{ Arch = $arch; Config = $config; Name = "$arch-$($config.ToLowerInvariant())" } } }
    # Configures write into the shared submodule (generated headers, WPP
    # files), so they run here, one tree at a time; the lanes then only build.
    Step "prepare" {
        foreach ($c in $combinations) {
            if ($BuildDriver) {
                Run "build-driver.ps1" @("-Architecture", $c.Arch, "-Configuration", $c.Config, "-ConfigureOnly")
                if ($LASTEXITCODE -ne 0) { throw "Configuring the driver ($($c.Name)) failed." }
            }
            Run "build-write-core.ps1" @("-Targets", $engineTargets, "-Architecture", $c.Arch, "-Configuration", $c.Config, "-ConfigureOnly")
            if ($LASTEXITCODE -ne 0) { throw "Configuring the engine ($($c.Name)) failed." }
        }
        foreach ($project in "ZfsWin.Installer", "ZfsWin.UserMode") {
            & dotnet restore (Join-Path $root "installer\$project\$project.wixproj")
            if ($LASTEXITCODE -ne 0) { throw "Restoring $project failed." }
        }
    }
    # The processor's threads shared between the lanes that compile.
    $jobs = [string][math]::Max(2, [math]::Ceiling([Environment]::ProcessorCount / @($combinations).Count))
    $lanes = [ordered]@{ admin = , @("build-admin.ps1", @("-Architecture", ($Architecture -join ','))) }
    foreach ($c in $combinations) {
        $lanes[$c.Name] = @(
            if ($BuildDriver) { , @("build-driver.ps1", @("-Architecture", $c.Arch, "-Configuration", $c.Config, "-Jobs", $jobs)) }
            , @("build-write-core.ps1", @("-Targets", $engineTargets, "-Architecture", $c.Arch, "-Configuration", $c.Config, "-Jobs", $jobs))
        )
    }
    Invoke-Lanes "build" $lanes
    $lanes = [ordered]@{}
    foreach ($c in $combinations) {
        foreach ($variant in "kernel", "usermode") {
            $lanes["msi-$($c.Name)-$variant"] = , @("build-installer.ps1", (@("-Architecture", $c.Arch, "-Variant", $variant, "-Configuration", $c.Config, "-ParallelLane") + $validation))
        }
    }
    Invoke-Lanes "MSIs" $lanes
    # Left to this script by the lanes (build-installer.ps1 -ParallelLane).
    $c = @($combinations)[0]
    Copy-Item (Join-Path $root ("out\driver-package" + $(if ($c.Config -eq "Release") { "-release" } else { "" }) + $(if ($c.Arch -eq "x64") { "" } else { "-$($c.Arch)" }) + "\zfswindows-test.cer")) (Join-Path $root "out\release\$version") -Force
}

# --- publish: out\release\<version>, self-contained ---------------------------
# Never committed: CI uploads this folder's packages as workflow artifacts and,
# for master, attaches them to the GitHub release v<version>.
$release = Join-Path $root "out\release\$version"
$dirty = [bool](& git -C $root status --porcelain --untracked-files=no -- . ':!third_party' ':!OpenZFS')
$artifacts = @("zfswindows-test.cer")
foreach ($config in $Configuration) {
    $tag = if ($config -eq "Debug") { "-debug" } else { "" }
    foreach ($arch in $Architecture) {
        $artifacts += "OpenZFS-for-Windows-$version-$arch-testsigned$tag.msi", "OpenZFS-for-Windows-$version-$arch-usermode$tag.msi", "OpenZFS-for-Windows-$version-$arch$tag.cdx.json"
    }
}
foreach ($a in $artifacts) {
    if (-not (Test-Path (Join-Path $release $a))) { throw "Missing release artifact: $(Join-Path $release $a)" }
}
[ordered]@{
    product = "OpenZFS for Windows"; version = $version; configurations = $Configuration; architectures = $Architecture
    built = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    builtFromCommit = (& git -C $root rev-parse HEAD).Trim(); workingTreeModified = $dirty
    openzfs = "zfs-2.4.4 (OpenZFS submodule); openzfs-windows zfswin-2.4.1rc17 + patches/openzfs-windows"
    variants = [ordered]@{
        kernel = "OpenZFS-for-Windows-$version-<arch>-testsigned[-debug].msi: kernel driver, test-signed (needs test signing and Secure Boot off)"
        usermode = "OpenZFS-for-Windows-$version-<arch>-usermode[-debug].msi: WinFsp user-mode file system (needs WinFsp 2.1+; no driver signature of ours)"
    }
    builds = [ordered]@{
        release = "optimized native code (/O2); OpenZFS keeps its own consistency checks (ZFS_DEBUG) in the user-mode engine, as upstream libzpool does"
        debug = "file names end in -debug: unoptimized native code with full debug checks; larger and slower, for diagnosing problems"
    }
} | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $release "VERSION.json")
# One SHA256SUMS for everything in the folder; it replaces the build
# scripts' SHA256SUMS.txt.
Remove-Item -ErrorAction SilentlyContinue (Join-Path $release "SHA256SUMS.txt")
Get-ChildItem $release -File | Where-Object Name -ne "SHA256SUMS" | Sort-Object Name | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
} | Set-Content -Encoding ascii (Join-Path $release "SHA256SUMS")
Get-ChildItem $release -File | Select-Object Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize | Out-String | Write-Host
Write-Output $release