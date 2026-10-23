<#
.SYNOPSIS
    Build the single LinkForge installer from x64/x86 self-contained payloads.

.DESCRIPTION
    The installer embeds both architectures and selects the matching payload at
    install time. It is per-user by default, so it does not require an
    administrator prompt and does not write application data under Program Files.
#>

[CmdletBinding()]
param(
    [string]$Version,

    [string]$PayloadRoot,

    [string]$OutputDirectory,

    [string]$IsccPath = 'C:\Users\0x00\AppData\Local\Programs\Inno Setup 6\ISCC.exe'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'release-helpers.ps1')

function Get-ConfiguredVersion {
    [xml]$props = Get-Content (Join-Path $repoRoot 'Directory.Build.props') -Raw
    $group = $props.Project.PropertyGroup | Select-Object -First 1
    $prefix = [string]$group.VersionPrefix
    $suffix = [string]$group.VersionSuffix

    if ([string]::IsNullOrWhiteSpace($prefix)) {
        throw 'Directory.Build.props does not define VersionPrefix.'
    }

    if ([string]::IsNullOrWhiteSpace($suffix)) {
        return $prefix
    }

    return "$prefix-$suffix"
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-ConfiguredVersion
}

$fileVersion = ConvertTo-InnoFileVersion -ProductVersion $Version

$commit = (& git -C $repoRoot rev-parse --short=12 HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($commit)) {
    throw 'Unable to resolve the current git commit.'
}

if ([string]::IsNullOrWhiteSpace($PayloadRoot)) {
    $PayloadRoot = Join-Path $repoRoot 'artifacts\release-payloads'
}

$PayloadRoot = [IO.Path]::GetFullPath($PayloadRoot)
$x64Payload = Join-Path $PayloadRoot 'win-x64'
$x86Payload = Join-Path $PayloadRoot 'win-x86'

foreach ($payload in @($x64Payload, $x86Payload)) {
    if (-not (Test-Path (Join-Path $payload 'LinkForge.App.exe'))) {
        throw "Missing published payload: $payload"
    }
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot 'artifacts'
}

$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

if (-not (Test-Path $IsccPath)) {
    throw "Inno Setup compiler was not found: $IsccPath"
}

$env:LINKFORGE_PAYLOAD_ROOT = $PayloadRoot
$env:LINKFORGE_VERSION = $Version
$env:LINKFORGE_FILE_VERSION = $fileVersion
$env:LINKFORGE_COMMIT = $commit
$env:LINKFORGE_OUTPUT_DIR = $OutputDirectory

try {
    Write-Host "Compiling LinkForge installer $Version (file version $fileVersion, $commit)..." -ForegroundColor Cyan
    & $IsccPath (Join-Path $repoRoot 'installer\LinkForge.iss') /Qp
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
    }
}
finally {
    Remove-Item Env:\LINKFORGE_PAYLOAD_ROOT -ErrorAction SilentlyContinue
    Remove-Item Env:\LINKFORGE_VERSION -ErrorAction SilentlyContinue
    Remove-Item Env:\LINKFORGE_FILE_VERSION -ErrorAction SilentlyContinue
    Remove-Item Env:\LINKFORGE_COMMIT -ErrorAction SilentlyContinue
    Remove-Item Env:\LINKFORGE_OUTPUT_DIR -ErrorAction SilentlyContinue
}

$installerPath = Join-Path $OutputDirectory "LinkForge-Setup-$Version-$commit.exe"
if (-not (Test-Path $installerPath)) {
    throw "Installer was not created: $installerPath"
}

$hash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(
    "$installerPath.sha256",
    "$hash *$([IO.Path]::GetFileName($installerPath))$([Environment]::NewLine)",
    [Text.UTF8Encoding]::new($false))

Write-Host "Installer: $installerPath" -ForegroundColor Green
Write-Host "SHA-256:  $hash"

$languageNoticePath = Join-Path $OutputDirectory 'LinkForge-Setup-THIRD-PARTY-NOTICES.txt'
Copy-Item -LiteralPath (Join-Path $repoRoot 'installer\Languages\LICENSE-ChineseSimplified.txt') `
    -Destination $languageNoticePath `
    -Force

Write-Host "Language notice: $languageNoticePath"
