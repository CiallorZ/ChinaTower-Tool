<#
.SYNOPSIS
    Install, upgrade, launch and uninstall a LinkForge installer.

.DESCRIPTION
    Uses a unique temporary install directory and a unique Start Menu group.
    The test verifies the selected payload starts, that an in-place upgrade
    failure preserves and restores the previous program directory, that a
    subsequent upgrade succeeds, and that uninstall removes program files
    without deleting the user's %LOCALAPPDATA%\LinkForge data directory.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InstallerPath,

    [string]$WorkingRoot
)

$ErrorActionPreference = 'Stop'

$InstallerPath = (Resolve-Path $InstallerPath).Path
if (-not (Test-Path $InstallerPath)) {
    throw "Installer not found: $InstallerPath"
}

if ([string]::IsNullOrWhiteSpace($WorkingRoot)) {
    $WorkingRoot = Join-Path $env:TEMP ('linkforge-installer-' + [Guid]::NewGuid().ToString('N'))
}

$WorkingRoot = [IO.Path]::GetFullPath($WorkingRoot)
$installDirectory = Join-Path $WorkingRoot 'app'
$groupName = 'LinkForge'
$installLog = Join-Path $WorkingRoot 'install.log'
$failedUpgradeLog = Join-Path $WorkingRoot 'upgrade-failure.log'
$upgradeLog = Join-Path $WorkingRoot 'upgrade.log'
$uninstallLog = Join-Path $WorkingRoot 'uninstall.log'
$markerPath = Join-Path $installDirectory 'upgrade-sentinel.txt'
$lockedDll = Join-Path $installDirectory 'LinkForge.App.dll'
$startMenuDirectory = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$groupName"
$dataDirectory = Join-Path $env:LOCALAPPDATA 'LinkForge'
$dataSentinel = Join-Path $dataDirectory 'installer-verification.sentinel'
$backupDirectory = Join-Path $dataDirectory 'upgrade-backup'
$createdDataDirectory = -not (Test-Path $dataDirectory)
$createdSentinel = $false
$lock = $null

New-Item -ItemType Directory -Path $WorkingRoot -Force | Out-Null

function Invoke-Installer {
    param(
        [string]$LogPath,
        [string[]]$ExtraArguments = @(),
        [switch]$AllowFailure,
        [switch]$NoCloseApplications
    )

    $arguments = @(
        '/VERYSILENT',
        '/SUPPRESSMSGBOXES',
        '/NORESTART',
        '/SP-',
        "/DIR=`"$installDirectory`"",
        "/LOG=`"$LogPath`""
    ) + $ExtraArguments
    if ($NoCloseApplications) {
        $arguments += '/NOCLOSEAPPLICATIONS'
    }

    $process = Start-Process -FilePath $InstallerPath `
        -ArgumentList $arguments `
        -Wait `
        -PassThru `
        -WindowStyle Hidden

    if (-not $AllowFailure -and $process.ExitCode -ne 0) {
        throw "Installer failed with exit code $($process.ExitCode). Log: $LogPath"
    }

    return $process.ExitCode
}

function Start-And-CloseApplication {
    param([string]$ExecutablePath)

    $process = Start-Process -FilePath $ExecutablePath `
        -PassThru `
        -WindowStyle Hidden
    $deadline = (Get-Date).AddSeconds(20)

    do {
        Start-Sleep -Milliseconds 500
    } while (-not $process.HasExited -and (Get-Date) -lt $deadline)

    if (-not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(10000)) {
            $process.Kill()
            throw 'Installed application did not close within the verification timeout.'
        }
    }

    if ($process.ExitCode -ne 0) {
        throw "Installed application exited with code $($process.ExitCode)."
    }

    return $process.ExitCode
}

try {
    if (Test-Path $startMenuDirectory) {
        throw "An existing Start Menu folder prevents isolated verification: $startMenuDirectory"
    }

    New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
    [IO.File]::WriteAllText($dataSentinel, 'preserve-me', [Text.UTF8Encoding]::new($false))
    $createdSentinel = $true

    $installExitCode = Invoke-Installer -LogPath $installLog

    $appExe = Join-Path $installDirectory 'LinkForge.App.exe'
    $uninstaller = Join-Path $installDirectory 'unins000.exe'
    if (-not (Test-Path $appExe)) {
        throw 'Installed application executable is missing.'
    }

    if (-not (Test-Path $uninstaller)) {
        throw 'Uninstall executable is missing.'
    }

    $startMenuLink = Join-Path $startMenuDirectory 'LinkForge.lnk'
    if (-not (Test-Path $startMenuLink)) {
        throw 'Start Menu shortcut is missing.'
    }

    $firstExitCode = Start-And-CloseApplication -ExecutablePath $appExe

    # Force a file-replacement failure after the installer has started. The
    # installer must preserve the previous program directory and restore it.
    $originalHash = (Get-FileHash -LiteralPath $appExe -Algorithm SHA256).Hash
    $originalDllHash = (Get-FileHash -LiteralPath $lockedDll -Algorithm SHA256).Hash
    [IO.File]::WriteAllText(
        $markerPath,
        'preserve-me-during-failed-upgrade',
        [Text.UTF8Encoding]::new($false))

    $lock = [IO.File]::Open(
        $lockedDll,
        [IO.FileMode]::Open,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::None)

    $failedUpgradeExitCode = Invoke-Installer `
        -LogPath $failedUpgradeLog `
        -AllowFailure `
        -NoCloseApplications

    $lock.Dispose()
    $lock = $null

    if ($failedUpgradeExitCode -eq 0) {
        throw 'The failure-injected upgrade unexpectedly succeeded.'
    }

    if ((Get-FileHash -LiteralPath $appExe -Algorithm SHA256).Hash -ne $originalHash) {
        throw 'The failed upgrade replaced the previous application executable.'
    }

    if ((Get-FileHash -LiteralPath $lockedDll -Algorithm SHA256).Hash -ne $originalDllHash) {
        throw 'The failed upgrade changed the locked application DLL.'
    }

    if (-not (Test-Path $markerPath)) {
        throw 'The failed upgrade removed the install-local marker.'
    }

    if (-not (Test-Path $uninstaller)) {
        throw 'The failed upgrade removed the previous uninstaller.'
    }

    if (-not (Test-Path $startMenuLink)) {
        throw 'The failed upgrade removed the previous Start Menu shortcut.'
    }

    $recoveredExitCode = Start-And-CloseApplication -ExecutablePath $appExe

    # Running the same installer over the existing install checks the per-user
    # upgrade path without needing a previous release artifact.
    $upgradeExitCode = Invoke-Installer -LogPath $upgradeLog
    $secondExitCode = Start-And-CloseApplication -ExecutablePath $appExe

    if (Test-Path $backupDirectory) {
        throw 'The successful upgrade left the temporary program backup behind.'
    }

    $uninstallProcess = Start-Process -FilePath $uninstaller `
        -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$uninstallLog") `
        -Wait `
        -PassThru `
        -WindowStyle Hidden
    if ($uninstallProcess.ExitCode -ne 0) {
        throw "Uninstaller failed with exit code $($uninstallProcess.ExitCode). Log: $uninstallLog"
    }

    Start-Sleep -Milliseconds 500
    if (Test-Path $appExe) {
        throw 'Uninstall did not remove the application executable.'
    }

    if (Test-Path $startMenuLink) {
        throw 'Uninstall did not remove the Start Menu shortcut.'
    }

    if (-not (Test-Path $dataSentinel)) {
        throw 'Uninstall unexpectedly removed user data.'
    }

    [pscustomobject]@{
        Installer = $InstallerPath
        InstallDirectory = $installDirectory
        InstallExitCode = $installExitCode
        FailedUpgradeExitCode = $failedUpgradeExitCode
        UpgradeExitCode = $upgradeExitCode
        FirstApplicationExitCode = $firstExitCode
        RecoveredApplicationExitCode = $recoveredExitCode
        SecondApplicationExitCode = $secondExitCode
        UninstallExitCode = 0
        PreviousInstallationPreserved = $true
        UpgradeBackupRemoved = $true
        UserDataPreserved = $true
        InstallLog = $installLog
        FailedUpgradeLog = $failedUpgradeLog
        UpgradeLog = $upgradeLog
        UninstallLog = $uninstallLog
    } | Format-List
}
finally {
    if ($null -ne $lock) {
        $lock.Dispose()
    }

    if ($createdSentinel -and (Test-Path $dataSentinel)) {
        Remove-Item -LiteralPath $dataSentinel -Force
    }

    if ($createdDataDirectory -and (Test-Path $dataDirectory)) {
        $remaining = Get-ChildItem -LiteralPath $dataDirectory -Force -ErrorAction SilentlyContinue
        if (-not $remaining) {
            Remove-Item -LiteralPath $dataDirectory -Force
        }
    }

    if (Test-Path $installDirectory) {
        $uninstaller = Join-Path $installDirectory 'unins000.exe'
        if (Test-Path $uninstaller) {
            $cleanup = Start-Process -FilePath $uninstaller `
                -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') `
                -Wait `
                -PassThru `
                -WindowStyle Hidden
            $null = $cleanup.ExitCode
        }
    }
}
