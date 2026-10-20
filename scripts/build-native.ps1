param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$out = Join-Path $repo 'out\native-nmake'
$source = Join-Path $repo 'src\native'
$vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw 'Visual Studio Installer (vswhere.exe) was not found.' }
$vsPath = (& $vswhere -latest -products * -property installationPath | Select-Object -First 1)
if (-not $vsPath) { throw 'No Visual Studio installation was found.' }
$devCmd = Join-Path $vsPath 'Common7\Tools\VsDevCmd.bat'
if (-not (Test-Path $devCmd)) { throw "Visual Studio developer command file was not found: $devCmd" }
$command = "call `"$devCmd`" -arch=x64 -host_arch=x64 >nul && cmake -S `"$source`" -B `"$out`" -G `"NMake Makefiles`" && cmake --build `"$out`" --config $Configuration"
cmd /c $command
if ($LASTEXITCODE -ne 0) { throw "Native build failed with exit code $LASTEXITCODE." }
