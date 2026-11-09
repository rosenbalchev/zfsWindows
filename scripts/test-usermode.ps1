[CmdletBinding()]
<#
.SYNOPSIS
Runs the user-mode engine unit tests (tests\Zfs.UserMode.Tests): OpenZFS in
the test process, on image files, without WinFsp, a driver or a VM.

.DESCRIPTION
Needs ZfsUserModeTest.dll from the write-core tree of the same
configuration and architecture (scripts\build-write-core.ps1 -Targets
zfs-um-test; build-release.ps1 builds it).  The images go to
%TEMP%\zfswin-um-tests.  The Linux reference-image tests run only where
the 5 GiB images are (ZFSWIN_LINUX_IMAGES or TestVolumes\); elsewhere they
are skipped.  ARM64 runs on an ARM64 machine only.  Results (.trx and a
summary.md) go to out\test-results\usermode-<arch>-<configuration>; in
GitHub Actions the summary is also the job's summary page.

-Coverage runs them on the instrumented engine instead
(build-write-core.ps1 -Coverage, x64) and writes clang source-based
coverage to out\coverage: summary.md (also on the job's summary page),
coverage.json, and an HTML report of our code and OpenZFS's module\zfs in
out\coverage\html.
#>
param(
    [ValidateSet("x64", "arm64")]
    [string]$Architecture = "x64",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    # A dotnet test --filter expression, e.g. "FullyQualifiedName~MemoryTests".
    [string]$Filter = "",
    [switch]$Coverage
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if ($Coverage -and $Architecture -ne "x64") { throw "-Coverage is x64 only." }
$treeName = if ($Coverage) { "coverage" } else { $Configuration.ToLowerInvariant() }
$tree = Join-Path $root ("out\openzfs-write-$treeName" + $(if ($Architecture -eq "x64") { "" } else { "-$Architecture" }))
$dll = Join-Path $tree "zfs-um-test\ZfsUserModeTest.dll"
$flag = if ($Coverage) { " -Coverage" } else { "" }
if (-not (Test-Path $dll)) { throw "$dll is missing; run build-write-core.ps1 -Targets zfs-um-test -Architecture $Architecture -Configuration $Configuration$flag first." }
$label = if ($Coverage) { "coverage" } else { "$Architecture-$($Configuration.ToLowerInvariant())" }
$results = Join-Path $root "out\test-results\usermode-$label"
if (Test-Path $results) { Remove-Item -Recurse -Force $results }
$coverageDir = Join-Path $root "out\coverage"
if ($Coverage) {
    if (Test-Path $coverageDir) { Remove-Item -Recurse -Force $coverageDir }
    New-Item -ItemType Directory -Force (Join-Path $coverageDir "raw") | Out-Null
    # Every process that loads the engine (the test host and each scenario
    # in a process of its own) writes its own profile.
    $env:LLVM_PROFILE_FILE = Join-Path $coverageDir "raw\zut-%p-%m.profraw"
    $env:ZFSWIN_UM_TEST_LIBRARY = $dll
}

$arguments = @(
    "test", (Join-Path $root "tests\Zfs.UserMode.Tests\Zfs.UserMode.Tests.csproj"), "-c", "Release",
    "-p:ZfsUmConfiguration=$Configuration", "-p:ZfsUmArchitecture=$Architecture",
    "--logger", "trx", "--logger", "console;verbosity=normal", "--results-directory", $results)
if ($Filter) { $arguments += @("--filter", $Filter) }
& dotnet @arguments
$testExit = $LASTEXITCODE
if ($Coverage) {
    Remove-Item Env:\LLVM_PROFILE_FILE, Env:\ZFSWIN_UM_TEST_LIBRARY
}

# Writes to the job's summary page in GitHub Actions, and next to the results.
function Publish-Summary([string]$markdown, [string]$file) {
    Set-Content -Encoding utf8 $file $markdown
    if ($env:GITHUB_STEP_SUMMARY) { Add-Content -Encoding utf8 $env:GITHUB_STEP_SUMMARY $markdown }
}

# --- test summary -------------------------------------------------------------
$trx = Get-ChildItem $results -Filter *.trx -ErrorAction SilentlyContinue | Select-Object -First 1
if ($trx) {
    [xml]$run = Get-Content -Raw $trx.FullName
    $classOf = @{}
    foreach ($test in $run.TestRun.TestDefinitions.UnitTest) {
        $classOf[$test.id] = ($test.TestMethod.className -split '\.')[-1]
    }
    $tests = foreach ($r in $run.TestRun.Results.UnitTestResult) {
        [pscustomobject]@{
            Class = $classOf[$r.testId]
            Name = ($r.testName -replace '^Zfs\.UserMode\.Tests\.', '')
            Outcome = $r.outcome
            Seconds = [TimeSpan]::Parse($r.duration).TotalSeconds
            Message = if ($r.Output.ErrorInfo.Message) { [string]$r.Output.ErrorInfo.Message } else { "" }
        }
    }
    $passed = @($tests | Where-Object Outcome -eq "Passed").Count
    $failed = @($tests | Where-Object Outcome -eq "Failed").Count
    $skipped = @($tests | Where-Object Outcome -eq "NotExecuted").Count
    $wall = ([datetime]$run.TestRun.Times.finish - [datetime]$run.TestRun.Times.start).TotalSeconds
    $title = if ($Coverage) { "Unit tests (x64, instrumented for coverage)" } else { "Unit tests ($Architecture, $Configuration)" }
    $state = if ($failed -gt 0) { ":x:" } else { ":white_check_mark:" }
    $md = @("## $state $title", "",
        ("**$passed passed, $failed failed, $skipped skipped** in {0:n0} s (tests\Zfs.UserMode.Tests: the user-mode engine on image files)." -f $wall), "",
        "| Class | Passed | Failed | Skipped | Time |", "| --- | ---: | ---: | ---: | ---: |")
    foreach ($group in $tests | Group-Object Class | Sort-Object Name) {
        $g = $group.Group
        $mark = if (@($g | Where-Object Outcome -eq "Failed").Count) { ":x: " } else { "" }
        $md += "| $mark{0} | {1} | {2} | {3} | {4:n1} s |" -f $group.Name,
            @($g | Where-Object Outcome -eq "Passed").Count, @($g | Where-Object Outcome -eq "Failed").Count,
            @($g | Where-Object Outcome -eq "NotExecuted").Count, ($g | Measure-Object Seconds -Sum).Sum
    }
    foreach ($section in @(@("Failed", "Failed"), @("NotExecuted", "Skipped"))) {
        $list = @($tests | Where-Object Outcome -eq $section[0] | Sort-Object Name)
        if ($list.Count -eq 0) { continue }
        $md += @("", "### $($section[1])", "")
        foreach ($t in $list) {
            $first = ($t.Message -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 3) -join " "
            $md += "- ``$($t.Name)``" + $(if ($first) { ": " + ($first -replace '\|', '\|') } else { "" })
        }
    }
    Publish-Summary (($md + "") -join "`n") (Join-Path $results "summary.md")
}

# --- coverage -------------------------------------------------------------------
if ($Coverage) {
    $llvm = "C:\Program Files\LLVM\bin"
    $profdata = Join-Path $coverageDir "coverage.profdata"
    $raw = @(Get-ChildItem (Join-Path $coverageDir "raw") -Filter *.profraw | ForEach-Object FullName)
    if ($raw.Count -eq 0) { throw "No coverage profiles were written (LLVM_PROFILE_FILE)." }
    & "$llvm\llvm-profdata.exe" merge -sparse -o $profdata @raw
    if ($LASTEXITCODE -ne 0) { throw "llvm-profdata merge failed." }

    $ours = Join-Path $root "src\native"
    $openZfs = Join-Path $root "third_party\openzfs-windows"
    $json = Join-Path $coverageDir "coverage.json"
    & "$llvm\llvm-cov.exe" export -summary-only -instr-profile $profdata $dll | Set-Content -Encoding utf8 $json
    if ($LASTEXITCODE -ne 0) { throw "llvm-cov export failed." }
    $files = (Get-Content -Raw $json | ConvertFrom-Json).data[0].files

    function Sum($set) {
        $t = [ordered]@{}
        foreach ($k in "lines", "functions", "regions", "branches") {
            $count = ($set | ForEach-Object { $_.summary.$k.count } | Measure-Object -Sum).Sum
            $covered = ($set | ForEach-Object { $_.summary.$k.covered } | Measure-Object -Sum).Sum
            $t[$k] = "{0:n1} % ({1:n0} / {2:n0})" -f $(if ($count) { 100.0 * $covered / $count } else { 0 }), $covered, $count
        }
        $t
    }
    function Under($dir) { $p = ($dir -replace '/', '\').TrimEnd('\') + '\'; $files | Where-Object { ($_.filename -replace '/', '\') -like "$p*" } }
    $md = @("## Coverage: user-mode engine (x64)", "",
        "Clang source-based coverage of ZfsUserModeTest.dll while the unit tests ran.  The HTML report (our code and OpenZFS module\zfs) is the **coverage-html** artifact.", "",
        "| Code | Lines | Functions | Regions | Branches |", "| --- | ---: | ---: | ---: | ---: |")
    foreach ($row in @(@("Ours (src\native)", (Under $ours)), @("OpenZFS module\zfs", (Under (Join-Path $openZfs "module\zfs"))), @("All of the engine", $files))) {
        $s = Sum $row[1]
        $md += "| $($row[0]) | $($s.lines) | $($s.functions) | $($s.regions) | $($s.branches) |"
    }
    $md += @("", "### Our files", "", "| File | Lines | Functions | Branches |", "| --- | ---: | ---: | ---: |")
    foreach ($f in Under $ours | Where-Object { $_.summary.lines.count -gt 0 } | Sort-Object filename) {
        $s = Sum @($f)
        $md += "| $(($f.filename -replace '/', '\').Substring($ours.Length + 1)) | $($s.lines) | $($s.functions) | $($s.branches) |"
    }
    Publish-Summary (($md + "") -join "`n") (Join-Path $coverageDir "summary.md")

    & "$llvm\llvm-cov.exe" show -format=html -show-line-counts-or-regions -output-dir (Join-Path $coverageDir "html") `
        -instr-profile $profdata $dll $ours (Join-Path $openZfs "module\zfs")
    if ($LASTEXITCODE -ne 0) { throw "llvm-cov show failed." }
}

if ($testExit -ne 0) { throw "User-mode unit tests failed ($label); results in $results." }
