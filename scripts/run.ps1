<#
.SYNOPSIS
    启动 LinkForge 客户端（开发期）。

.DESCRIPTION
    本机 .NET 10 运行时安装在用户级目录（%USERPROFILE%\.dotnet），
    而 apphost 默认只查找 C:\Program Files\dotnet，会弹出
    "You must install or update .NET to run this application"。

    因此启动前显式设置 DOTNET_ROOT，仅作用于本进程，
    不修改系统或用户环境变量。

.PARAMETER Configuration
    Debug 或 Release。默认 Debug。

.PARAMETER FrameworkDependent
    以 dotnet LinkForge.App.dll 方式启动，而不是 apphost（.exe）。
    当 DOTNET_ROOT 方案不适用时可作为替代路径。

.EXAMPLE
    pwsh -File scripts/run.ps1
    pwsh -File scripts/run.ps1 -Configuration Release
#>

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

$outputDirectory = Join-Path $repoRoot "src\LinkForge.App\bin\$Configuration\net10.0-windows"
$appHost = Join-Path $outputDirectory 'LinkForge.App.exe'
$appDll = Join-Path $outputDirectory 'LinkForge.App.dll'

if (-not (Test-Path $appHost) -or -not (Test-Path $appDll)) {
    throw "未找到构建产物（$outputDirectory）。请先运行 scripts/build.ps1。"
}

$dotnetRoot = Join-Path $env:USERPROFILE '.dotnet'
if (-not (Test-Path (Join-Path $dotnetRoot 'dotnet.exe'))) {
    throw "未找到用户级 .NET 运行时（$dotnetRoot）。请先安装 .NET SDK 或设置 DOTNET_ROOT。"
}

# 仅影响本进程；不写注册表、不改用户/系统环境变量。
$env:DOTNET_ROOT = $dotnetRoot
$env:PATH = "$dotnetRoot;$env:PATH"

if ($FrameworkDependent) {
    Write-Host "以框架依赖方式启动：dotnet $appDll" -ForegroundColor Cyan
    & (Join-Path $dotnetRoot 'dotnet.exe') $appDll
}
else {
    Write-Host "启动：$appHost" -ForegroundColor Cyan
    Write-Host "DOTNET_ROOT=$dotnetRoot（仅本进程）" -ForegroundColor DarkGray
    & $appHost
}
