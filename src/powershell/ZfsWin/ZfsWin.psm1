# ZfsWin: PowerShell cmdlets for OpenZFS on Windows (plan step 14).
#
# Every cmdlet calls zfswin.exe --json, which talks to the ZfsWinBroker
# service, so the cmdlets and the CLI share one protocol, one set of checks
# and one audit trail.  Commands that change or destroy data support
# -WhatIf/-Confirm (ShouldProcess); destructive ones have ConfirmImpact High
# and so prompt unless -Confirm:$false or -Force is given.  Nothing selects a
# disk, a pool or a dataset implicitly.

Set-StrictMode -Version 3.0

$script:ZfsWinExe = $null

function Get-ZfsWinExecutable {
    if ($script:ZfsWinExe -and (Test-Path -LiteralPath $script:ZfsWinExe)) { return $script:ZfsWinExe }
    $candidates = @(
        (Join-Path $PSScriptRoot 'zfswin.exe'),
        $(if ($env:ZFSWIN_HOME) { Join-Path $env:ZFSWIN_HOME 'zfswin.exe' }),
        (Join-Path ${env:ProgramFiles} 'OpenZFS for Windows\bin\zfswin.exe')
    ) | Where-Object { $_ }
    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c) { $script:ZfsWinExe = $c; return $c }
    }
    $onPath = Get-Command zfswin.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath) { $script:ZfsWinExe = $onPath.Source; return $onPath.Source }
    throw [System.IO.FileNotFoundException]::new('zfswin.exe was not found; reinstall OpenZFS for Windows or set $env:ZFSWIN_HOME.')
}

# Maps zfswin error codes to PowerShell error categories.
$script:Categories = @{
    'command-failed'        = [System.Management.Automation.ErrorCategory]::InvalidOperation
    'usage'                 = [System.Management.Automation.ErrorCategory]::InvalidArgument
    'invalid-argument'      = [System.Management.Automation.ErrorCategory]::InvalidArgument
    'not-found'             = [System.Management.Automation.ErrorCategory]::ObjectNotFound
    'access-denied'         = [System.Management.Automation.ErrorCategory]::PermissionDenied
    'broker-unavailable'    = [System.Management.Automation.ErrorCategory]::ResourceUnavailable
    'confirmation-required' = [System.Management.Automation.ErrorCategory]::InvalidOperation
    'timeout'               = [System.Management.Automation.ErrorCategory]::OperationTimeout
    'cancelled'             = [System.Management.Automation.ErrorCategory]::OperationStopped
    'protocol-mismatch'     = [System.Management.Automation.ErrorCategory]::ProtocolError
    'bad-request'           = [System.Management.Automation.ErrorCategory]::ProtocolError
    'busy'                  = [System.Management.Automation.ErrorCategory]::ResourceBusy
    'internal'              = [System.Management.Automation.ErrorCategory]::NotSpecified
}

# Runs zfswin.exe --json <arguments> and returns its result object, or
# writes a terminating error carrying the zfswin error code.
function Invoke-ZfsWin {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [int]$TimeoutSeconds = 0,
        [Parameter(Mandatory)][System.Management.Automation.PSCmdlet]$Caller
    )
    $exe = Get-ZfsWinExecutable
    $all = @('--json')
    if ($TimeoutSeconds -gt 0) { $all += @('--timeout', "$TimeoutSeconds") }
    $all += $Arguments
    # zfswin prints exactly one JSON document on stdout.
    $text = (& $exe @all 2>$null) -join "`n"
    $exit = $LASTEXITCODE
    try {
        $reply = $text | ConvertFrom-Json -ErrorAction Stop
    } catch {
        $Caller.ThrowTerminatingError([System.Management.Automation.ErrorRecord]::new(
            [System.InvalidOperationException]::new("zfswin.exe exited with $exit and printed no JSON: $text"),
            'ZfsWin.BadOutput', [System.Management.Automation.ErrorCategory]::ProtocolError, $null))
    }
    if (-not $reply.ok) {
        $code = $reply.error.code
        $message = $reply.error.message
        if ($reply.error.PSObject.Properties['detail'] -and $reply.error.detail) { $message += "`n" + $reply.error.detail }
        $category = $script:Categories[$code]
        if ($null -eq $category) { $category = [System.Management.Automation.ErrorCategory]::NotSpecified }
        $exception = [System.InvalidOperationException]::new($message)
        $exception.Data['ZfsWinErrorCode'] = $code
        $exception.Data['ZfsWinExitCode'] = $exit
        $Caller.ThrowTerminatingError([System.Management.Automation.ErrorRecord]::new($exception, "ZfsWin.$code", $category, $Arguments))
    }
    return $reply.result
}

function Add-TypeName($object, [string]$typeName) {
    if ($null -ne $object) { $object.PSObject.TypeNames.Insert(0, $typeName) }
    $object
}

function ConvertTo-PropertyArguments([hashtable]$Properties, [string]$Switch) {
    $out = @()
    if ($Properties) {
        foreach ($key in ($Properties.Keys | Sort-Object)) {
            if ($Switch) { $out += $Switch }
            $out += "$key=$($Properties[$key])"
        }
    }
    $out
}

# --- pools --------------------------------------------------------------------

function Get-ZfsPool {
    [CmdletBinding()]
    [OutputType('ZfsWin.Pool')]
    param([Parameter(Position = 0)][SupportsWildcards()][string]$Name = '*')
    $result = Invoke-ZfsWin -Caller $PSCmdlet -Arguments @('pool', 'list')
    foreach ($p in @($result.pools)) {
        if ($p.name -like $Name) { Add-TypeName $p 'ZfsWin.Pool' }
    }
}

function Get-ZfsPoolStatus {
    [CmdletBinding()]
    [OutputType('ZfsWin.PoolStatus')]
    param([Parameter(Position = 0, ValueFromPipelineByPropertyName)][string]$Name)
    process {
        $arguments = @('pool', 'status')
        if ($Name) { $arguments += $Name }
        foreach ($p in @((Invoke-ZfsWin -Caller $PSCmdlet -Arguments $arguments).pools)) { Add-TypeName $p 'ZfsWin.PoolStatus' }
    }
}

function Get-ZfsPoolProperty {
    [CmdletBinding()]
    [OutputType('ZfsWin.Property')]
    param(
        [Parameter(Mandatory, Position = 0, ValueFromPipelineByPropertyName)][string]$Name,
        [Parameter(Position = 1)][string[]]$Property
    )
    process {
        $result = Invoke-ZfsWin -Caller $PSCmdlet -Arguments (@('pool', 'get', $Name) + @($Property | Where-Object { $_ }))
        foreach ($p in @($result.properties)) {
            $p | Add-Member -NotePropertyName Target -NotePropertyValue $Name -PassThru | ForEach-Object { Add-TypeName $_ 'ZfsWin.Property' }
        }
    }
}

function Find-ZfsPool {
    [CmdletBinding()]
    [OutputType('ZfsWin.ImportablePool')]
    param([string[]]$SearchDirectory)
    $arguments = @('pool', 'discover')
    foreach ($d in $SearchDirectory) { $arguments += @('--dir', $d) }
    foreach ($p in @((Invoke-ZfsWin -Caller $PSCmdlet -Arguments $arguments).pools)) { Add-TypeName $p 'ZfsWin.ImportablePool' }
}

function Import-ZfsPool {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    param(
        [Parameter(Mandatory, Position = 0, ValueFromPipelineByPropertyName)][Alias('Guid')][string]$Name,
        [string[]]$SearchDirectory,
        [string]$NewName,
        [switch]$NoMount,
        [switch]$ReadOnly,
        [switch]$Force,
        [int]$TimeoutSeconds = 0
    )
    process {
        $arguments = @('pool', 'import', $Name)
        foreach ($d in $SearchDirectory) { $arguments += @('--dir', $d) }
        if ($NewName) { $arguments += @('--new-name', $NewName) }
        if ($NoMount) { $arguments += '--no-mount' }
        if ($ReadOnly) { $arguments += '--readonly' }
        if ($Force) { $arguments += '--force' }
        if ($PSCmdlet.ShouldProcess($Name, 'Import ZFS pool')) {
            Invoke-ZfsWin -Caller $PSCmdlet -Arguments $arguments -TimeoutSeconds $TimeoutSeconds | Out-Null
            Get-ZfsPool -Name $(if ($NewName) { $NewName } else { $Name }) -ErrorAction SilentlyContinue
        }
    }
}

function Export-ZfsPool {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    param(
        [Parameter(Mandatory, Position = 0, ValueFromPipelineByPropertyName)][string]$Name,
        [switch]$Force
    )
    process {
        $arguments = @('pool', 'export', $Name)
        if ($Force) { $arguments += '--force' }
        if ($PSCmdlet.ShouldProcess($Name, 'Export ZFS pool (unmount all of its datasets)')) {
            Invoke-ZfsWin -Caller $PSCmdlet -Arguments $arguments | Out-Null
        }
    }
}

function New-ZfsPool {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    [OutputType('ZfsWin.Pool')]
    param(
        [Parameter(Mandatory, Position = 0)][string]$Name,
        # Every device is overwritten.  Files must exist; disks are \\?\PhysicalDriveN.
        [Parameter(Mandatory, Position = 1)][string[]]$Device,
        [switch]$Mirror,
        [hashtable]$PoolProperty,
        [hashtable]$DatasetProperty,
        [string]$MountPoint,
        [switch]$Force
    )
    $arguments = @('pool', 'create', $Name)
    if ($Mirror) { $arguments += '--mirror' }
    $arguments += $Device
    $arguments += ConvertTo-PropertyArguments $PoolProperty '-o'
    $arguments += ConvertTo-PropertyArguments $DatasetProperty '-O'
    if ($MountPoint) { $arguments += @('--mountpoint', $MountPoint) }
    $arguments += '--yes'
    $target = "$($Device -join ', ') (all data on these devices is destroyed)"
    if ($Force -and -not $PSBoundParameters.ContainsKey('Confirm')) { $ConfirmPreference = 'None' }
    if ($PSCmdlet.ShouldProcess($target, "Create ZFS pool '$Name'")) {
        Invoke-ZfsWin -Caller $PSCmdlet -Arguments $arguments | Out-Null
        Get-ZfsPool -Name $Name
    }
}

# --- scrub ----------------------------------------------------------------------

function Get-ZfsScrub {
    [CmdletBinding()]
    param([Parameter(Mandatory, Position = 0, ValueFromPipelineByPropertyName)][string]$Name)
    process { Add-TypeName (Invoke-ZfsWin -Caller $PSCmdlet -Arguments @('scrub', 'status', $Name)) 'ZfsWin.Scrub' }
}

function Start-ZfsScrub {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Low')]
    param([Parameter(Mandatory, Position = 0, ValueFromPipelineByPropertyName)][string]$Name)
    process {
        if ($PSCmdlet.ShouldProcess($Name, 'Start scrub')) { Invoke-ZfsWin -Caller $PSCmdlet -Arguments @('scrub', 'start', $Name) | Out-Null }
    }
}

function Suspend-ZfsScrub {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Low')]
    param([Parameter(Mandatory, Position = 0, ValueFromPipelineByPropertyName)][string]$Name)
    process {
        if ($PSCmdlet.ShouldProcess($Name, 'Pause scrub')) { Invoke-ZfsWin -Caller $PSCmdlet -Arguments @('scrub', 'pause', $Name) | Out-Null }
    }
}

function Stop-ZfsScrub {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Low')]
    param([Parameter(Mandatory, Position = 0, ValueFromPipelineByPropertyName)][string]$Name)
    process {
        if ($PSCmdlet.ShouldProcess($Name, 'Stop scrub')) { Invoke-ZfsWin -Caller $PSCmdlet -Arguments @('scrub', 'stop', $Name) | Out-Null }
    }
}

# --- datasets -------------------------------------------------------------------

function Get-ZfsDataset {
    [CmdletBinding()]
    [OutputType('ZfsWin.Dataset')]
    param(
        [Parameter(Position = 0, ValueFromPipelineByPropertyName)][string]$Name,
        [switch]$Recurse,
        [int]$Depth = -1
    )
    process {
        $arguments = @('dataset', 'list')
        if ($Name) { $arguments += $Name }
        if ($Recurse) { $arguments += '-r' }
        if ($Depth -ge 0) { $arguments += @('-d', "$Depth") }
        foreach ($d in @((Invoke-ZfsWin -Caller $PSCmdlet -Arguments $arguments).datasets)) { Add-TypeName $d 'ZfsWin.Dataset' }
    }
}

function Get-ZfsProperty {
    [CmdletBinding()]
    [OutputType('ZfsWin.Property')]
    param(
        [Parameter(Mandatory, Position = 0, ValueFromPipelineByPropertyName)][string]$Name,
        [Parameter(Position = 1)][string[]]$Property
    )
    process {
        $result = Invoke-ZfsWin -Caller $PSCmdlet -Arguments (@('dataset', 'get', $Name) + @($Property | Where-Object { $_ }))
        foreach ($p in @($result.properties)) {
            $p | Add-Member -NotePropertyName Target -NotePropertyValue $Name -PassThru | ForEach-Object { Add-TypeName $_ 'ZfsWin.Property' }
        }
    }
}

function Set-ZfsProperty {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    param(
        [Parameter(Mandatory, Position = 0, ValueFromPipelineByPropertyName)][string]$Name,
        [Parameter(Mandatory, Position = 1)][hashtable]$Property
    )
    process {
        $assignments = ConvertTo-PropertyArguments $Property ''
        if ($PSCmdlet.ShouldProcess($Name, "Set $($assignments -join ', ')")) {
            Invoke-ZfsWin -Caller $PSCmdlet -Arguments (@('dataset', 'set', $Name) + $assignments) | Out-Null
        }
    }
}

function New-ZfsDataset {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Low')]
    [OutputType('ZfsWin.Dataset')]
    param(
        [Parameter(Mandatory, Position = 0)][string]$Name,
        [hashtable]$Property,
        [switch]$CreateParents
    )
    $arguments = @('dataset', 'create', $Name) + (ConvertTo-PropertyArguments $Property '-o')
    if ($CreateParents) { $arguments += '-p' }
    if ($PSCmdlet.ShouldProcess($Name, 'Create ZFS filesystem')) {
        Invoke-ZfsWin -Caller $PSCmdlet -Arguments $arguments | Out-Null
        Get-ZfsDataset -Name $Name
    }
}

function Remove-ZfsDataset {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    param(
        [Parameter(Mandatory, Position = 0, ValueFromPipelineByPropertyName)][string]$Name,
        [switch]$Recurse,
        [switch]$Force
    )
    process {
        $arguments = @('dataset', 'destroy', $Name, '--yes')
        if ($Recurse) { $arguments += '-r' }
        if ($Force -and -not $PSBoundParameters.ContainsKey('Confirm')) { $ConfirmPreference = 'None' }
        $what = if ($Recurse) { 'Destroy ZFS filesystem, its children and snapshots, and all their data' } else { 'Destroy ZFS filesystem and all its data' }
        if ($PSCmdlet.ShouldProcess($Name, $what)) { Invoke-ZfsWin -Caller $PSCmdlet -Arguments $arguments | Out-Null }
    }
}

function Get-ZfsMount {
    [CmdletBinding()]
    param()
    foreach ($m in @((Invoke-ZfsWin -Caller $PSCmdlet -Arguments @('mount', 'list')).mounts)) { Add-TypeName $m 'ZfsWin.Mount' }
}

function Mount-ZfsDataset {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Low')]
    param([Parameter(Mandatory, Position = 0, ValueFromPipelineByPropertyName)][string]$Name)
    process {
        if ($PSCmdlet.ShouldProcess($Name, 'Mount')) { Invoke-ZfsWin -Caller $PSCmdlet -Arguments @('dataset', 'mount', $Name) | Out-Null }
    }
}

function Dismount-ZfsDataset {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    param(
        [Parameter(Mandatory, Position = 0, ValueFromPipelineByPropertyName)][string]$Name,
        [switch]$Force
    )
    process {
        $arguments = @('dataset', 'unmount', $Name)
        if ($Force) { $arguments += '--force' }
        if ($PSCmdlet.ShouldProcess($Name, 'Unmount')) { Invoke-ZfsWin -Caller $PSCmdlet -Arguments $arguments | Out-Null }
    }
}

# --- snapshots ------------------------------------------------------------------

function Get-ZfsSnapshot {
    [CmdletBinding()]
    [OutputType('ZfsWin.Snapshot')]
    param(
        [Parameter(Position = 0, ValueFromPipelineByPropertyName)][Alias('Dataset')][string]$Name,
        [switch]$Recurse
    )
    process {
        $arguments = @('snapshot', 'list')
        if ($Name) { $arguments += $Name }
        if ($Recurse) { $arguments += '-r' }
        foreach ($s in @((Invoke-ZfsWin -Caller $PSCmdlet -Arguments $arguments).snapshots)) { Add-TypeName $s 'ZfsWin.Snapshot' }
    }
}

function New-ZfsSnapshot {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Low')]
    [OutputType('ZfsWin.Snapshot')]
    param(
        # dataset@snapshot
        [Parameter(Mandatory, Position = 0)][string]$Name,
        [switch]$Recurse
    )
    $arguments = @('snapshot', 'create', $Name)
    if ($Recurse) { $arguments += '-r' }
    if ($PSCmdlet.ShouldProcess($Name, 'Create snapshot')) {
        Invoke-ZfsWin -Caller $PSCmdlet -Arguments $arguments | Out-Null
        Get-ZfsSnapshot -Name ($Name -split '@')[0] | Where-Object name -eq $Name
    }
}

function Remove-ZfsSnapshot {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    param(
        [Parameter(Mandatory, Position = 0, ValueFromPipelineByPropertyName)][string]$Name,
        [switch]$Recurse,
        [switch]$Force
    )
    process {
        $arguments = @('snapshot', 'destroy', $Name, '--yes')
        if ($Recurse) { $arguments += '-r' }
        if ($Force -and -not $PSBoundParameters.ContainsKey('Confirm')) { $ConfirmPreference = 'None' }
        if ($PSCmdlet.ShouldProcess($Name, 'Destroy snapshot')) { Invoke-ZfsWin -Caller $PSCmdlet -Arguments $arguments | Out-Null }
    }
}

# --- mirror devices -------------------------------------------------------------

function Add-ZfsMirrorDevice {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    param(
        [Parameter(Mandatory, Position = 0)][string]$Pool,
        # An existing device of the pool; NewDevice becomes its mirror.
        [Parameter(Mandatory, Position = 1)][string]$Device,
        [Parameter(Mandatory, Position = 2)][string]$NewDevice,
        [switch]$Force
    )
    if ($Force -and -not $PSBoundParameters.ContainsKey('Confirm')) { $ConfirmPreference = 'None' }
    if ($PSCmdlet.ShouldProcess("$NewDevice (overwritten)", "Attach as a mirror of $Device in pool '$Pool'")) {
        Invoke-ZfsWin -Caller $PSCmdlet -Arguments @('vdev', 'attach', $Pool, $Device, $NewDevice, '--yes') | Out-Null
    }
}

function Remove-ZfsMirrorDevice {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    param(
        [Parameter(Mandatory, Position = 0)][string]$Pool,
        [Parameter(Mandatory, Position = 1)][string]$Device,
        [switch]$Force
    )
    if ($Force -and -not $PSBoundParameters.ContainsKey('Confirm')) { $ConfirmPreference = 'None' }
    if ($PSCmdlet.ShouldProcess($Device, "Detach from pool '$Pool'")) {
        Invoke-ZfsWin -Caller $PSCmdlet -Arguments @('vdev', 'detach', $Pool, $Device, '--yes') | Out-Null
    }
}

function Switch-ZfsPoolDevice {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    param(
        [Parameter(Mandatory, Position = 0)][string]$Pool,
        [Parameter(Mandatory, Position = 1)][string]$Device,
        # Omit to rebuild onto the same (replaced) device.
        [Parameter(Position = 2)][string]$NewDevice,
        [switch]$Force
    )
    $arguments = @('vdev', 'replace', $Pool, $Device)
    if ($NewDevice) { $arguments += $NewDevice }
    $arguments += '--yes'
    if ($Force -and -not $PSBoundParameters.ContainsKey('Confirm')) { $ConfirmPreference = 'None' }
    $target = if ($NewDevice) { "$NewDevice (overwritten)" } else { $Device }
    if ($PSCmdlet.ShouldProcess($target, "Replace $Device in pool '$Pool'")) {
        Invoke-ZfsWin -Caller $PSCmdlet -Arguments $arguments | Out-Null
    }
}

function Get-ZfsWinVersion {
    [CmdletBinding()]
    param()
    Invoke-ZfsWin -Caller $PSCmdlet -Arguments @('version')
}

Export-ModuleMember -Function @(
    'Get-ZfsPool', 'Get-ZfsPoolStatus', 'Get-ZfsPoolProperty', 'Find-ZfsPool', 'Import-ZfsPool', 'Export-ZfsPool', 'New-ZfsPool',
    'Get-ZfsScrub', 'Start-ZfsScrub', 'Suspend-ZfsScrub', 'Stop-ZfsScrub',
    'Get-ZfsDataset', 'Get-ZfsProperty', 'Set-ZfsProperty', 'New-ZfsDataset', 'Remove-ZfsDataset',
    'Get-ZfsMount', 'Mount-ZfsDataset', 'Dismount-ZfsDataset',
    'Get-ZfsSnapshot', 'New-ZfsSnapshot', 'Remove-ZfsSnapshot',
    'Add-ZfsMirrorDevice', 'Remove-ZfsMirrorDevice', 'Switch-ZfsPoolDevice',
    'Get-ZfsWinVersion')
