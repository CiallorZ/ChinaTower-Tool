<#
.SYNOPSIS
    按 LinkForge 规范执行构建与测试（DEV_STANDARDS.md §4）。

.DESCRIPTION
    统一入口，避免每次手写长命令、避免"声称测试通过但实际未运行"。
    默认执行：还原 -> 构建（x64，警告视为错误）-> 测试。
    可选执行 x86 构建以验证 32 位目标。

.PARAMETER Configuration
    构建配置，Debug 或 Release。默认 Debug。

.PARAMETER IncludeX86
    额外构建 x86 目标（PlatformTarget=x86）。

.PARAMETER SkipTests
    跳过测试（仅用于快速语法检查，不作为交付依据）。

.EXAMPLE
    pwsh -File scripts/build.ps1
    pwsh -File scripts/build.ps1 -Configuration Release -IncludeX86

.NOTES
    本机 .NET SDK 安装在用户级目录，未写入系统 PATH，
    因此脚本显式解析 dotnet 路径，而不是依赖 PATH。
#>

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [switch]$IncludeX86,

    [switch]$SkipTests
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
            # 必须能列出 SDK，只有运行时的安装无法构建。
            $sdks = & $candidate --list-sdks 2>$null
            if ($sdks) {
                return $candidate
            }
        }
    }

    throw '未找到可用的 .NET SDK。请确认已安装 SDK，或检查 %USERPROFILE%\.dotnet。'
}

function Invoke-Step {
    param(
        [string]$Name,
        [string[]]$Arguments
    )

    Write-Host ""
    Write-Host "==> $Name" -ForegroundColor Cyan
    Write-Host "    dotnet $($Arguments -join ' ')" -ForegroundColor DarkGray

    & $dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Name 失败（退出码 $LASTEXITCODE）。"
    }
}

$dotnet = Resolve-DotNet
$solution = Join-Path $repoRoot 'LinkForge.slnx'

Push-Location $repoRoot
try {
    Write-Host "LinkForge 构建验证" -ForegroundColor Green
    Write-Host "  SDK      : $dotnet"
    Write-Host "  配置     : $Configuration"
    Write-Host "  解决方案 : $solution"

    Invoke-Step '还原' @('restore', $solution, '--nologo')

    Invoke-Step "构建 ($Configuration)" @(
        'build', $solution,
        '-c', $Configuration,
        '--nologo',
        '--no-restore',
        '-warnaserror'
    )

    if ($IncludeX86) {
        Invoke-Step '构建 x86（仅 App 宿主）' @(
            'build', (Join-Path $repoRoot 'src\LinkForge.App\LinkForge.App.csproj'),
            '-c', $Configuration,
            '--nologo',
            '-p:PlatformTarget=x86',
            '-p:RuntimeIdentifier=win-x86',
            '-p:SelfContained=false'
        )
    }

    if ($SkipTests) {
        Write-Host ""
        Write-Warning '已跳过测试。跳过测试的结果不得作为阶段验收依据。'
    }
    else {
        Invoke-Step "测试 ($Configuration)" @(
            'test', $solution,
            '-c', $Configuration,
            '--nologo',
            '--no-build'
        )
    }

    Write-Host ""
    Write-Host '构建验证完成。' -ForegroundColor Green
}
finally {
    Pop-Location
}
