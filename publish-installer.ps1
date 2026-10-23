<#
.SYNOPSIS
    一键构建 FSHE 发布包并编译 Inno Setup 安装程序。

.DESCRIPTION
    1. dotnet publish（Release / win-x64 / self-contained）到 artifacts\publish
    2. 校验 WinUI 运行时必需文件（*.xbf、FSHE.pri、Assets\AppIcon.ico）
    3. 镜像同步到 installer_src\（Inno Setup 脚本的源目录）
    4. 调用 ISCC 编译 FSHE-Setup.iss，产物在 installer_output\

.PARAMETER Configuration
    构建配置，默认 Release。

.PARAMETER Runtime
    目标运行时，默认 win-x64。

.PARAMETER IsccPath
    ISCC.exe 路径；不指定时自动查找。

.PARAMETER SkipInstaller
    只准备 installer_src，不编译安装包。

.EXAMPLE
    .\publish-installer.ps1

.EXAMPLE
    .\publish-installer.ps1 -SkipInstaller
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$IsccPath,
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$stagingDir = Join-Path $repoRoot 'artifacts\publish'
$sourceDir = Join-Path $repoRoot 'installer_src'
$issFile = Join-Path $repoRoot 'FSHE-Setup.iss'

function Write-Step([string]$text) {
    Write-Host ''
    Write-Host "==> $text" -ForegroundColor Cyan
}

function Assert-Files([string]$baseDir, [string[]]$relativePaths) {
    $missing = @()
    foreach ($rel in $relativePaths) {
        if (-not (Test-Path (Join-Path $baseDir $rel))) { $missing += $rel }
    }
    if ($missing.Count -gt 0) {
        throw "发布输出缺少运行时必需文件：`n  - $($missing -join "`n  - ")`n构建或 csproj 中的 CopyWinUIResourcesToPublish 目标可能失效。"
    }
}

# ---------------------------------------------------------------- 1. publish
Write-Step "发布 $Configuration / $Runtime（self-contained）"
if (Test-Path $stagingDir) { Remove-Item -Recurse -Force $stagingDir }
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

& dotnet publish (Join-Path $repoRoot 'FSHE.csproj') `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -o $stagingDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（退出码 $LASTEXITCODE）。" }

# ------------------------------------------------------------ 2. 校验必需文件
Write-Step '校验 WinUI 运行时资源'
$required = @(
    'FSHE.exe',
    'FSHE.dll',
    'FSHE.pri',
    'App.xbf',
    'MainWindow.xbf',
    'FileExplorerPage.xbf',
    'HomePage.xbf',
    'Styles\DesignTokens.xbf',
    'Assets\AppIcon.ico'
)
Assert-Files $stagingDir $required
$fileCount = (Get-ChildItem $stagingDir -Recurse -File | Measure-Object).Count
Write-Host "  通过（$fileCount 个文件）" -ForegroundColor Green

# --------------------------------------------------------- 3. 同步源目录
Write-Step '同步到 installer_src'
robocopy $stagingDir $sourceDir /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy 同步失败（退出码 $LASTEXITCODE）。" }
Write-Host '  完成' -ForegroundColor Green

if ($SkipInstaller) {
    Write-Host ''
    Write-Host '已按 -SkipInstaller 跳过安装包编译。' -ForegroundColor Yellow
    return
}

# ------------------------------------------------------- 4. 查找 ISCC
Write-Step '定位 Inno Setup 编译器'
if (-not $IsccPath) {
    $candidates = @(
        'E:\Inno\Inno Setup 6\ISCC.exe',
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )
    foreach ($key in @(
            'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
            'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1')) {
        try {
            $loc = (Get-ItemProperty -Path $key -ErrorAction Stop).InstallLocation
            if ($loc) { $candidates += (Join-Path $loc 'ISCC.exe') }
        } catch { }
    }
    $IsccPath = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $IsccPath) {
        $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($cmd) { $IsccPath = $cmd.Source }
    }
}
if (-not $IsccPath -or -not (Test-Path $IsccPath)) {
    throw '未找到 ISCC.exe，请用 -IsccPath 指定，例如：-IsccPath "E:\Inno\Inno Setup 6\ISCC.exe"'
}
Write-Host "  $IsccPath"

# ------------------------------------------------------ 5. 编译安装包
Write-Step '编译安装程序'
# 先捕获 ISCC 输出：它会把控制台代码页改成 UTF-8，直接 Out-Host 会让后续中文乱码
$isccOutput = & $IsccPath $issFile 2>&1 | ForEach-Object { $_.ToString() }
$isccExit = $LASTEXITCODE
if ($isccExit -ne 0) {
    $isccOutput | ForEach-Object { Write-Host $_ }
    throw "ISCC 编译失败（退出码 $isccExit）。"
}
$isccOutput | Where-Object { $_ -match 'Successful compile|Resulting Setup|警告|Warning' } |
    ForEach-Object { Write-Host "  $_" }

$setup = Get-ChildItem (Join-Path $repoRoot 'installer_output') -Filter '*.exe' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Step '完成'
if ($setup) {
    Write-Host ("  安装包：{0}" -f $setup.FullName) -ForegroundColor Green
    Write-Host ("  大小：  {0:N1} MB   时间：{1}" -f ($setup.Length / 1MB), $setup.LastWriteTime)
}
