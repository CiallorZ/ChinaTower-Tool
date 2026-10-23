function ConvertTo-InnoFileVersion {
    param([Parameter(Mandatory)][string]$ProductVersion)

    # Inno Setup's VersionInfoVersion accepts at most four numeric components.
    # Semantic prerelease labels are not valid here, so keep the numeric
    # product version and derive a stable numeric fourth component from the
    # first numeric prerelease component when present.
    if ($ProductVersion -notmatch '^(?<prefix>\d+\.\d+\.\d+)(?:-(?<suffix>.+))?$') {
        throw "Version '$ProductVersion' cannot be represented as an Inno file version."
    }

    $parts = @($Matches['prefix'].Split('.'))
    while ($parts.Count -lt 3) {
        $parts += '0'
    }

    $revision = 0
    if (-not [string]::IsNullOrWhiteSpace($Matches['suffix'])) {
        $numericSuffix = [regex]::Match($Matches['suffix'], '\d+')
        if ($numericSuffix.Success -and
            -not [int]::TryParse($numericSuffix.Value, [ref]$revision)) {
            $revision = 0
        }
    }

    if ($revision -gt 9999) {
        throw "Version '$ProductVersion' has a prerelease component too large for Inno file version metadata."
    }

    return (@($parts + [string]$revision) -join '.')
}
