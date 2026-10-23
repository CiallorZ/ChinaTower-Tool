<#
.SYNOPSIS
    Verify that a failed in-place LinkForge upgrade leaves the previous
    installation runnable and preserves its files.

.DESCRIPTION
    Installs the base installer into an isolated directory, creates an
    install-local marker, locks LinkForge.App.dll with FileShare.None, and runs
    the candidate installer over it. The test requires the second installer
    invocation to fail. After releasing the lock it verifies the executable,
    locked DLL, marker, uninstaller, Start Menu shortcut, and startup/close
    behavior of the old installation.

    This is a controlled failure injection for an otherwise normal per-user
    install. It does not modify the source tree or the real user installation.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InstallerPath,

    [string]$BaseInstallerPath,

    [string]$WorkingRoot
)

$ErrorActionPreference = 'Stop'

$InstallerPath = (Resolve-Path $InstallerPath).Path
if (-not (Test-Path $InstallerPath)) {
    throw "Installer not found: $InstallerPath"
}

if ([string]::IsNullOrWhiteSpace($BaseInstallerPath)) {
    $BaseInstallerPath = $InstallerPath
}
else {
    $BaseInstallerPath = (Resolve-Path $BaseInstallerPath).Path
    if (-not (Test-Path $BaseInstallerPath)) {
        throw "Base installer not found: $BaseInstallerPath"
    }
}

if ([string]::IsNullOrWhiteSpace($WorkingRoot)) {
    $WorkingRoot = Join-Path $env:TEMP ('linkforge-upgrade-failure-' + [Guid]::NewGuid().ToString('N'))
}

$WorkingRoot = [IO.Path]::GetFullPath($WorkingRoot)
$installDirectory = Join-Path $WorkingRoot 'app'
$groupName = 'LinkForge'
$installLog = Join-Path $WorkingRoot 'install.log'
$failedUpgradeLog = Join-Path $WorkingRoot 'upgrade-failure.log'
$uninstallLog = Join-Path $WorkingRoot 'uninstall.log'
$markerPath = Join-Path $installDirectory 'upgrade-sentinel.txt'
$appExe = Join-Path $installDirectory 'LinkForge.App.exe'
$lockedDll = Join-Path $installDirectory 'LinkForge.App.dll'
$uninstaller = Join-Path $installDirectory 'unins000.exe'
$startMenuDirectory = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$groupName"
$startMenuLink = Join-Path $startMenuDirectory 'LinkForge.lnk'
$lock = $null

New-Item -ItemType Directory -Path $WorkingRoot -Force | Out-Null

function Invoke-Installer {
    param(
        [string]$Installer,
        [string]$LogPath,
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
    )
    if ($NoCloseApplications) {
        $arguments += '/NOCLOSEAPPLICATIONS'
    }

    $process = Start-Process -FilePath $Installer `
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

    Invoke-Installer -Installer $BaseInstallerPath -LogPath $installLog | Out-Null

    if (-not (Test-Path $appExe)) {
        throw 'Initial installation did not create LinkForge.App.exe.'
    }

    if (-not (Test-Path $lockedDll)) {
        throw 'Initial installation did not create LinkForge.App.dll.'
    }

    if (-not (Test-Path $uninstaller)) {
        throw 'Initial installation did not create the uninstaller.'
    }

    if (-not (Test-Path $startMenuLink)) {
        throw 'Initial installation did not create the Start Menu shortcut.'
    }

    $originalHash = (Get-FileHash -LiteralPath $appExe -Algorithm SHA256).Hash
    $originalDllHash = (Get-FileHash -LiteralPath $lockedDll -Algorithm SHA256).Hash
    [IO.File]::WriteAllText(
        $markerPath,
        'previous-installation-marker',
        [Text.UTF8Encoding]::new($false))

    # Hold a file that the upgrade must replace without sharing. Locking the
    # short application DLL proves partial-install rollback without preventing
    # the recovery copy from restoring the executable.
    $lock = [IO.File]::Open(
        $lockedDll,
        [IO.FileMode]::Open,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::None)

    $failedExitCode = Invoke-Installer `
        -Installer $InstallerPath `
        -LogPath $failedUpgradeLog `
        -AllowFailure `
        -NoCloseApplications
    $lock.Dispose()
    $lock = $null

    if ($failedExitCode -eq 0) {
        throw 'The failure-injected upgrade unexpectedly succeeded. The test cannot prove rollback behavior.'
    }

    if (-not (Test-Path $appExe)) {
        throw 'A failed upgrade removed the previous application executable.'
    }

    $afterHash = (Get-FileHash -LiteralPath $appExe -Algorithm SHA256).Hash
    if ($afterHash -ne $originalHash) {
        throw 'A failed upgrade replaced the previous application executable.'
    }

    if (-not (Test-Path $markerPath)) {
        throw 'A failed upgrade removed the install-local marker.'
    }

    $afterDllHash = (Get-FileHash -LiteralPath $lockedDll -Algorithm SHA256).Hash
    if ($afterDllHash -ne $originalDllHash) {
        throw 'A failed upgrade changed the locked application DLL.'
    }

    if (-not (Test-Path $uninstaller)) {
        throw 'A failed upgrade removed the previous uninstaller.'
    }

    if (-not (Test-Path $startMenuLink)) {
        throw 'A failed upgrade removed the previous Start Menu shortcut.'
    }

    $applicationExitCode = Start-And-CloseApplication -ExecutablePath $appExe

    [pscustomobject]@{
        Installer = $InstallerPath
        BaseInstaller = $BaseInstallerPath
        InstallDirectory = $installDirectory
        InitialInstallExitCode = 0
        FailedUpgradeExitCode = $failedExitCode
        PreviousExecutablePreserved = $true
        PreviousMarkerPreserved = $true
        LockedDllPreserved = $true
        PreviousUninstallerPreserved = $true
        PreviousStartMenuShortcutPreserved = $true
        PreviousApplicationExitCode = $applicationExitCode
        InstallLog = $installLog
        FailedUpgradeLog = $failedUpgradeLog
    } | Format-List
}
finally {
    if ($null -ne $lock) {
        $lock.Dispose()
    }

    if (Test-Path $uninstaller) {
        $cleanup = Start-Process -FilePath $uninstaller `
            -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$uninstallLog") `
            -Wait `
            -PassThru `
            -WindowStyle Hidden
        $null = $cleanup.ExitCode
    }
}
