<#
.SYNOPSIS
    Build and package a traceable portable LinkForge release.

.DESCRIPTION
    Defaults to a self-contained win-x64 package under artifacts/.
    The package contains a CycloneDX SBOM, dependency/license inventory,
    build metadata and SHA-256 checksums.

    The script refuses a dirty worktree unless -AllowDirty is explicitly used.
    This prevents a package from claiming a commit that does not contain its code.
#>

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$Version,

    [ValidateSet('win-x64', 'win-x86')]
    [string]$RuntimeIdentifier = 'win-x64',

    [switch]$FrameworkDependent,

    [switch]$SkipTests,

    [switch]$AllowDirty
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Resolve-DotNet {
    $candidates = @(
        (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'),
        (Join-Path ${env:ProgramFiles} 'dotnet\dotnet.exe')
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            $sdks = & $candidate --list-sdks 2>$null
            if ($sdks) {
                return $candidate
            }
        }
    }

    throw 'No usable .NET SDK was found.'
}

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

function Assert-PathUnder {
    param(
        [string]$Path,
        [string]$Root
    )

    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'

    if (-not $fullPath.StartsWith($fullRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to operate outside the expected root: $fullPath"
    }
}

function Read-PackageInventory {
    param(
        [string]$ProjectAssetsPath
    )

    if (-not (Test-Path $ProjectAssetsPath)) {
        throw "project.assets.json not found after restore: $ProjectAssetsPath"
    }

    $assets = Get-Content $ProjectAssetsPath -Raw | ConvertFrom-Json
    $packageRoot = ($assets.packageFolders.PSObject.Properties | Select-Object -First 1).Name

    foreach ($entry in $assets.libraries.PSObject.Properties) {
        if ($entry.Value.type -ne 'package') {
            continue
        }

        $key = [string]$entry.Name
        $separator = $key.LastIndexOf('/')

        if ($separator -le 0) {
            continue
        }

        $id = $key.Substring(0, $separator)
        $packageVersion = $key.Substring($separator + 1)
        $license = 'not declared in nuspec'
        $nuspec = Join-Path $packageRoot "$($id.ToLowerInvariant())\$packageVersion\$($id.ToLowerInvariant()).nuspec"

        if (Test-Path $nuspec) {
            [xml]$packageSpec = Get-Content $nuspec -Raw
            $licenseNode = $packageSpec.package.metadata.license

            if ($licenseNode -is [System.Xml.XmlElement]) {
                $licenseValue = [string]$licenseNode.InnerText
                $licenseType = [string]$licenseNode.type

                if (-not [string]::IsNullOrWhiteSpace($licenseValue)) {
                    $license = if ($licenseType -eq 'file') {
                        "file: $licenseValue"
                    }
                    else {
                        $licenseValue
                    }
                }
            }
            elseif (-not [string]::IsNullOrWhiteSpace([string]$licenseNode)) {
                $license = [string]$licenseNode
            }
        }

        [pscustomobject]@{
            Id = $id
            Version = $packageVersion
            License = $license
            Sha512 = [string]$entry.Value.sha512
            Purl = "pkg:nuget/$id@$packageVersion"
        }
    }
}

function Convert-Base64ToHex {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    return -join ([Convert]::FromBase64String($Value) | ForEach-Object { $_.ToString('x2') })
}

function New-DeterministicUuid {
    param([string]$Value)

    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha256.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value))
    }
    finally {
        $sha256.Dispose()
    }
    $uuidBytes = [byte[]]$bytes[0..15]

    # RFC 9562 version 5 shape, derived from the release identity.
    $uuidBytes[6] = [byte](($uuidBytes[6] -band 0x0f) -bor 0x50)
    $uuidBytes[8] = [byte](($uuidBytes[8] -band 0x3f) -bor 0x80)

    return [Guid]::new($uuidBytes).ToString()
}

function Get-PortableRelativePath {
    param(
        [string]$Root,
        [string]$Path
    )

    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $fullPath = [IO.Path]::GetFullPath($Path)

    if (-not $fullPath.StartsWith($fullRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is not under the portable package root: $fullPath"
    }

    return $fullPath.Substring($fullRoot.Length).Replace('\', '/')
}

function Write-Utf8NoBom {
    param(
        [string]$Path,
        [string]$Content
    )

    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

$dotnet = Resolve-DotNet
$appProject = Join-Path $repoRoot 'src\LinkForge.App\LinkForge.App.csproj'
$artifactRoot = Join-Path $repoRoot 'artifacts'

$commit = (& git -C $repoRoot rev-parse --short=12 HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($commit)) {
    throw 'Unable to resolve the current git commit.'
}

$dirtyLines = @(& git -C $repoRoot status --porcelain)
$isDirty = $dirtyLines.Count -gt 0

if ($isDirty -and -not $AllowDirty) {
    throw 'The worktree is dirty. Commit the changes or pass -AllowDirty for a non-release diagnostic package.'
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-ConfiguredVersion
}

$dirtySuffix = if ($isDirty) { '.dirty' } else { '' }
$packageLabel = "$Version-$commit$dirtySuffix"
$packageName = "LinkForge-$packageLabel-$RuntimeIdentifier-portable"
$stagingDirectory = Join-Path $artifactRoot $packageName
$zipPath = Join-Path $artifactRoot "$packageName.zip"
$selfContained = -not $FrameworkDependent

New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
Assert-PathUnder -Path $stagingDirectory -Root $artifactRoot

if (Test-Path $stagingDirectory) {
    Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
}

if (Test-Path $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

if (-not $SkipTests) {
    Write-Host 'Running the full build and test gate...' -ForegroundColor Cyan
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration

    if ($LASTEXITCODE -ne 0) {
        throw "Build/test gate failed with exit code $LASTEXITCODE."
    }
}

Write-Host "Publishing $packageName..." -ForegroundColor Cyan
New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null

$informationalVersion = "$Version+$commit$dirtySuffix"
$publishArguments = @(
    'publish', $appProject,
    '-c', $Configuration,
    '-r', $RuntimeIdentifier,
    '--self-contained', $selfContained.ToString().ToLowerInvariant(),
    '--nologo',
    '-o', $stagingDirectory,
    "-p:Version=$Version",
    "-p:InformationalVersion=$informationalVersion",
    '-p:ContinuousIntegrationBuild=true'
)

& $dotnet @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$appExe = Join-Path $stagingDirectory 'LinkForge.App.exe'
if (-not (Test-Path $appExe)) {
    throw "Published application executable is missing: $appExe"
}

$assetsPath = Join-Path $repoRoot 'src\LinkForge.App\obj\project.assets.json'
$packages = @(Read-PackageInventory -ProjectAssetsPath $assetsPath | Sort-Object Id, Version)

$inventoryLines = @(
    '# LinkForge third-party dependency inventory',
    '',
    "Version: $Version",
    "Commit: $commit$dirtySuffix",
    "Runtime: $RuntimeIdentifier",
    '',
    '| Package | Version | Declared license |',
    '| --- | --- | --- |'
)

foreach ($package in $packages) {
    $inventoryLines += "| $($package.Id) | $($package.Version) | $($package.License) |"
}

$inventoryLines += ''
$inventoryLines += 'License values are read from the installed NuGet package nuspec files.'
$inventoryLines += 'This inventory is not a legal approval or a substitute for reviewing the full license texts.'

Write-Utf8NoBom -Path (Join-Path $stagingDirectory 'THIRD-PARTY-NOTICES.md') -Content ($inventoryLines -join [Environment]::NewLine)

$dependencyLines = $packages | ForEach-Object { "$($_.Id) $($_.Version) [$($_.License)]" }
Write-Utf8NoBom -Path (Join-Path $stagingDirectory 'DEPENDENCIES.txt') -Content (($dependencyLines -join [Environment]::NewLine) + [Environment]::NewLine)

$appHash = (Get-FileHash -LiteralPath $appExe -Algorithm SHA256).Hash.ToLowerInvariant()
$sbomComponents = foreach ($package in $packages) {
    $component = [ordered]@{
        type = 'library'
        'bom-ref' = $package.Purl
        name = $package.Id
        version = $package.Version
        purl = $package.Purl
    }

    $hex = Convert-Base64ToHex -Value $package.Sha512
    if ($hex) {
        $component.hashes = @(
            [ordered]@{
                alg = 'SHA-512'
                content = $hex
            }
        )
    }

    $component
}

$sbom = [ordered]@{
    bomFormat = 'CycloneDX'
    specVersion = '1.5'
    serialNumber = "urn:uuid:$(New-DeterministicUuid -Value $informationalVersion)"
    version = 1
    metadata = [ordered]@{
        timestamp = (Get-Date).ToUniversalTime().ToString('o')
        tools = @(
            [ordered]@{
                vendor = 'LinkForge'
                name = 'scripts/publish.ps1'
                version = $Version
            }
        )
        component = [ordered]@{
            type = 'application'
            'bom-ref' = "pkg:generic/LinkForge@$Version"
            name = 'LinkForge'
            version = $Version
            hashes = @(
                [ordered]@{
                    alg = 'SHA-256'
                    content = $appHash
                }
            )
        }
    }
    components = @($sbomComponents)
}

Write-Utf8NoBom -Path (Join-Path $stagingDirectory 'sbom.cdx.json') -Content ($sbom | ConvertTo-Json -Depth 12)

$metadata = [ordered]@{
    product = 'LinkForge'
    version = $Version
    informationalVersion = $informationalVersion
    commit = $commit
    dirty = $isDirty
    configuration = $Configuration
    runtimeIdentifier = $RuntimeIdentifier
    selfContained = $selfContained
    builtAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    operatingSystem = [Environment]::OSVersion.VersionString
    dotnetVersion = (& $dotnet --version).Trim()
}

Write-Utf8NoBom -Path (Join-Path $stagingDirectory 'release.json') -Content ($metadata | ConvertTo-Json -Depth 5)
$versionLines = @(
    "Product: LinkForge"
    "Version: $Version"
    "Commit: $commit$dirtySuffix"
    "Runtime: $RuntimeIdentifier"
    "Self-contained: $selfContained"
    "Built at (UTC): $($metadata.builtAtUtc)"
    "Code signing: not signed"
)
Write-Utf8NoBom -Path (Join-Path $stagingDirectory 'VERSION.txt') -Content ($versionLines -join [Environment]::NewLine)

$checksumLines = Get-ChildItem -LiteralPath $stagingDirectory -File -Recurse |
    Sort-Object FullName |
    ForEach-Object {
        $relative = Get-PortableRelativePath -Root $stagingDirectory -Path $_.FullName
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash *$relative"
    }

Write-Utf8NoBom -Path (Join-Path $stagingDirectory 'SHA256SUMS.txt') -Content (($checksumLines -join [Environment]::NewLine) + [Environment]::NewLine)

Compress-Archive -Path (Join-Path $stagingDirectory '*') -DestinationPath $zipPath -CompressionLevel Optimal
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Utf8NoBom -Path "$zipPath.sha256" -Content "$zipHash *$([IO.Path]::GetFileName($zipPath))$([Environment]::NewLine)"

Write-Host ''
Write-Host 'Portable package created.' -ForegroundColor Green
Write-Host "  Directory: $stagingDirectory"
Write-Host "  Archive:   $zipPath"
Write-Host "  SHA-256:   $zipHash"
