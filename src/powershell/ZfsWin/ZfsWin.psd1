@{
    RootModule           = 'ZfsWin.psm1'
    ModuleVersion        = '1.0.0'	# stamped with the build's version by scripts\build-installer.ps1
    GUID                 = '5d0c7b8e-6f1e-4a9b-9a51-2f3f0c6b7e14'
    Author               = 'zfsWindows contributors'
    CompanyName          = 'zfsWindows contributors'
    Copyright            = 'Copyright 2026 Rosen Balchev and contributors. MIT License.'
    Description          = 'Cmdlets for OpenZFS on Windows pools, datasets, snapshots and mirrors, over the ZfsWinBroker service.'
    PowerShellVersion    = '5.1'
    CompatiblePSEditions = @('Desktop', 'Core')
    FormatsToProcess     = @('ZfsWin.Format.ps1xml')
    FunctionsToExport    = @(
        'Get-ZfsPool', 'Get-ZfsPoolStatus', 'Get-ZfsPoolProperty', 'Find-ZfsPool', 'Import-ZfsPool', 'Export-ZfsPool', 'New-ZfsPool',
        'Get-ZfsScrub', 'Start-ZfsScrub', 'Suspend-ZfsScrub', 'Stop-ZfsScrub',
        'Get-ZfsDataset', 'Get-ZfsProperty', 'Set-ZfsProperty', 'New-ZfsDataset', 'Remove-ZfsDataset',
        'Get-ZfsMount', 'Mount-ZfsDataset', 'Dismount-ZfsDataset',
        'Get-ZfsSnapshot', 'New-ZfsSnapshot', 'Remove-ZfsSnapshot',
        'Add-ZfsMirrorDevice', 'Remove-ZfsMirrorDevice', 'Switch-ZfsPoolDevice',
        'Get-ZfsWinVersion')
    CmdletsToExport      = @()
    VariablesToExport    = @()
    AliasesToExport      = @()
    PrivateData          = @{
        PSData = @{
            Tags       = @('ZFS', 'OpenZFS', 'Storage', 'Windows')
            LicenseUri = 'https://opensource.org/license/mit'
        }
    }
}
