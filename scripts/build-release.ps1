<#
.SYNOPSIS
    Build the complete LinkForge V1 release set.

.DESCRIPTION
    Produces self-contained win-x64 and win-x86 portable ZIPs plus one Inno
    Setup installer that selects the matching payload. Formal mode requires a
    clean worktree, no version suffix, a successful test gate and matching
    installer/application versions.
#>

[CmdletBinding()]
param(
    [switch]$Formal,

    [switch]$SkipPortablePackages
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$powerShell = Join-Path $PSHOME $(if (Test-Path (Join-Path $PSHOME 'pwsh.exe')) {
    'pwsh.exe'
} else {
    'powershell.exe'
})

function Get-ConfiguredVersion {
    [xml]$props = Get-Content (Join-Path $repoRoot 'Directory.Build.props') -Raw
    $group = $props.Project.PropertyGroup | Select-Object -First 1
    $prefix = [string]$group.VersionPrefix
    $suffix = [string]$group.VersionSuffix

    if ([string]::IsNullOrWhiteSpace($prefix)) {
        throw 'Directory.Build.props does not define VersionPrefix.'
    }

    return [pscustomobject]@{
        Version = if ([string]::IsNullOrWhiteSpace($suffix)) { $prefix } else { "$prefix-$suffix" }
        Prefix = $prefix
        Suffix = $suffix
    }
}

function Invoke-Git {
    param([string[]]$Arguments)
    $output = & git -C $repoRoot @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
    return $output
}

$configured = Get-ConfiguredVersion
$commit = (& git -C $repoRoot rev-parse --short=12 HEAD).Trim()
$dirty = @(& git -C $repoRoot status --porcelain).Count -gt 0

if ($Formal) {
    if (-not [string]::IsNullOrWhiteSpace($configured.Suffix)) {
        throw "Formal release requires an empty VersionSuffix; configured version is $($configured.Version)."
    }

    if ($dirty) {
        throw 'Formal release requires a clean worktree. Commit all source, test and documentation changes first.'
    }
}

$artifacts = Join-Path $repoRoot 'artifacts'
$payloadRoot = Join-Path $artifacts 'release-payloads'
New-Item -ItemType Directory -Path $payloadRoot -Force | Out-Null

if (Test-Path $payloadRoot) {
    Remove-Item -LiteralPath $payloadRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $payloadRoot -Force | Out-Null

if (-not $SkipPortablePackages) {
    Write-Host 'Running the formal build/test gate before publishing...' -ForegroundColor Cyan
    & $powerShell -NoProfile -File (Join-Path $PSScriptRoot 'build.ps1') -Configuration Release
    if ($LASTEXITCODE -ne 0) {
        throw "Build/test gate failed with exit code $LASTEXITCODE."
    }

    foreach ($runtime in @('win-x64', 'win-x86')) {
        & $powerShell -NoProfile -File (Join-Path $PSScriptRoot 'publish.ps1') `
            -Configuration Release `
            -Version $configured.Version `
            -RuntimeIdentifier $runtime `
            -SkipTests
        if ($LASTEXITCODE -ne 0) {
            throw "Portable publication failed for $runtime."
        }
    }
}

$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) {
    $dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
}

foreach ($runtime in @('win-x64', 'win-x86')) {
    $destination = Join-Path $payloadRoot $runtime
    & $dotnet publish (Join-Path $repoRoot 'src\LinkForge.App\LinkForge.App.csproj') `
        -c Release `
        -r $runtime `
        --self-contained true `
        --nologo `
        -o $destination `
        "-p:Version=$($configured.Version)" `
        "-p:InformationalVersion=$($configured.Version)+$commit" `
        '-p:ContinuousIntegrationBuild=true'

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $runtime."
    }
}

& $powerShell -NoProfile -File (Join-Path $PSScriptRoot 'build-installer.ps1') `
    -Version $configured.Version `
    -PayloadRoot $payloadRoot `
    -OutputDirectory $artifacts

if ($LASTEXITCODE -ne 0) {
    throw 'Installer compilation failed.'
}

$installer = Join-Path $artifacts "LinkForge-Setup-$($configured.Version)-$commit.exe"
if (-not (Test-Path $installer)) {
    throw "Expected installer is missing: $installer"
}

$portableFiles = @(
    (Join-Path $artifacts "LinkForge-$($configured.Version)-$commit-win-x64-portable.zip"),
    (Join-Path $artifacts "LinkForge-$($configured.Version)-$commit-win-x86-portable.zip")
)

$installerNotice = Join-Path $artifacts 'LinkForge-Setup-THIRD-PARTY-NOTICES.txt'
if (-not (Test-Path $installerNotice)) {
    throw "Installer third-party notice is missing: $installerNotice"
}

$releaseFiles = @($installer, $installerNotice) + @($portableFiles | Where-Object { Test-Path $_ })
$releaseEntries = foreach ($file in $releaseFiles) {
    $item = Get-Item -LiteralPath $file
    [ordered]@{
        name = $item.Name
        size = $item.Length
        sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

$manifest = [ordered]@{
    product = 'LinkForge'
    version = $configured.Version
    commit = $commit
    channel = if ($Formal) { 'formal' } else { 'diagnostic' }
    dirty = $dirty
    builtAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    codeSigning = 'unsigned'
    architectures = @('x64', 'x86')
    installer = [ordered]@{
        name = [IO.Path]::GetFileName($installer)
        sha256 = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    artifacts = @($releaseEntries)
}

$manifestPath = Join-Path $artifacts "LinkForge-Release-$($configured.Version)-$commit.json"
[IO.File]::WriteAllText(
    $manifestPath,
    ($manifest | ConvertTo-Json -Depth 8),
    [Text.UTF8Encoding]::new($false))

Write-Host ''
Write-Host 'Release set created.' -ForegroundColor Green
Write-Host "  Version:   $($configured.Version)"
Write-Host "  Commit:    $commit"
Write-Host "  Manifest:  $manifestPath"
Get-ChildItem -LiteralPath $artifacts -Filter "*$commit*" | Select-Object Name, Length
