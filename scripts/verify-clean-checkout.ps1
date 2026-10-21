<#
.SYNOPSIS
    Clone the committed HEAD into a temporary directory and run its build gate.

.DESCRIPTION
    This validates that a clean checkout can restore, build and test without
    relying on ignored build outputs or untracked files in the working tree.
#>

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [switch]$SkipTests,

    [switch]$KeepTemporaryCheckout
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$tempBase = [IO.Path]::GetTempPath().TrimEnd('\')
$tempDirectory = Join-Path $tempBase ("linkforge-clean-" + [Guid]::NewGuid().ToString('N'))
$cloneDirectory = Join-Path $tempDirectory 'repo'

function Assert-PathUnder {
    param(
        [string]$Path,
        [string]$Root
    )

    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'

    if (-not $fullPath.StartsWith($fullRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a path outside the temporary root: $fullPath"
    }
}

New-Item -ItemType Directory -Path $tempDirectory -Force | Out-Null

try {
    $sourceHead = (& git -C $repoRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to read the source repository HEAD.'
    }

    Write-Host "Cloning committed HEAD $sourceHead..." -ForegroundColor Cyan
    & git clone --quiet --no-hardlinks $repoRoot $cloneDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "git clone failed with exit code $LASTEXITCODE."
    }

    $cloneHead = (& git -C $cloneDirectory rev-parse HEAD).Trim()
    if ($cloneHead -ne $sourceHead) {
        throw "Clean checkout HEAD mismatch: source=$sourceHead clone=$cloneHead"
    }

    $status = @(& git -C $cloneDirectory status --porcelain)
    if ($status.Count -gt 0) {
        throw "Clean checkout is unexpectedly dirty: $($status -join '; ')"
    }

    $arguments = @(
        '-NoProfile',
        '-File', (Join-Path $cloneDirectory 'scripts\build.ps1'),
        '-Configuration', $Configuration
    )

    if ($SkipTests) {
        $arguments += '-SkipTests'
    }

    & pwsh @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Clean checkout build gate failed with exit code $LASTEXITCODE."
    }

    Write-Host "Clean checkout verification passed: $cloneHead" -ForegroundColor Green
}
finally {
    if ($KeepTemporaryCheckout) {
        Write-Host "Temporary checkout kept at: $tempDirectory" -ForegroundColor Yellow
    }
    else {
        Assert-PathUnder -Path $tempDirectory -Root $tempBase

        if (Test-Path $tempDirectory) {
            Remove-Item -LiteralPath $tempDirectory -Recurse -Force
        }
    }
}
