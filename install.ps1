<#
.SYNOPSIS
    FSHE 一键安装脚本：从 GitHub Releases 下载最新安装包并静默安装。

.DESCRIPTION
    默认安装最新正式版；已安装同一版本时自动跳过（可用 -Force 重装）。
    安装为当前用户级别（无需管理员权限，不弹 UAC）。

.PARAMETER Version
    指定版本号，例如 1.1.0；默认 latest。

.PARAMETER Force
    即使已安装相同版本也重新安装。

.PARAMETER DownloadOnly
    只下载安装包，不执行安装（返回文件路径）。

.PARAMETER Uninstall
    静默卸载已安装的 FSHE。

.PARAMETER Mirror
    GitHub 加速镜像前缀，例如 https://ghfast.top 。下载后仍会用 Release 的
    SHA256 校验安装包，防止镜像返回错误内容。

.PARAMETER LocalInstaller
    直接安装本地已有的安装包，跳过下载（离线安装 / 调试用）。

.EXAMPLE
    irm https://raw.githubusercontent.com/YG-O/FSHE/main/install.ps1 | iex

.EXAMPLE
    .\install.ps1 -Version 1.1.0

.EXAMPLE
    .\install.ps1 -Mirror https://ghfast.top

.EXAMPLE
    .\install.ps1 -Uninstall
#>
param(
    [string]$Version = 'latest',
    [switch]$Force,
    [switch]$DownloadOnly,
    [switch]$Uninstall,
    [string]$Mirror,
    [string]$LocalInstaller
)

$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 的进度条会让 Invoke-WebRequest 慢好几倍
$ProgressPreference = 'SilentlyContinue'

$Repo = 'YG-O/FSHE'
$AppKey = '{8F4C3D2A-1B5E-4D8F-A6C1-9D0E7F1A2B3C}_is1'
$Headers = @{
    'User-Agent' = 'FSHE-Installer'
    'Accept'     = 'application/vnd.github+json'
}

function Write-Step([string]$text) {
    Write-Host ''
    Write-Host "==> $text" -ForegroundColor Cyan
}

function Get-FsheRegistration {
    $roots = @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    )
    foreach ($root in $roots) {
        $key = Join-Path $root $AppKey
        if (Test-Path $key) {
            $props = Get-ItemProperty $key
            return [pscustomobject]@{
                Version     = $props.DisplayVersion
                Location    = $props.InstallLocation
                Uninstaller = $props.UninstallString
            }
        }
    }
    return $null
}

function Get-Release([string]$version) {
    if ($version -eq 'latest') {
        $uri = "https://api.github.com/repos/$Repo/releases/latest"
    }
    else {
        $tag = if ($version.StartsWith('v')) { $version } else { "v$version" }
        $uri = "https://api.github.com/repos/$Repo/releases/tags/$tag"
    }
    try {
        return Invoke-RestMethod -Uri $uri -Headers $Headers
    }
    catch {
        throw "查询 GitHub Release 失败（$version）：$($_.Exception.Message)"
    }
}

function Invoke-Download([string]$url, [string]$dest) {
    $bits = Get-Command Start-BitsTransfer -ErrorAction SilentlyContinue
    if ($bits) {
        try {
            $job = Start-BitsTransfer -Source $url -Destination $dest -Asynchronous -Description 'FSHE installer'
            while ($job.JobState -in @('Connecting', 'Transferring', 'TransientError')) {
                $total = [double]$job.BytesTotal
                $done = [double]$job.BytesTransferred
                $pct = if ($total -gt 0) { [int](100 * $done / $total) } else { 0 }
                $status = '{0:N1} / {1:N1} MB' -f ($done / 1MB), ($total / 1MB)
                Write-Progress -Activity '下载 FSHE 安装包' -Status $status -PercentComplete $pct
                Start-Sleep -Milliseconds 500
            }
            Write-Progress -Activity '下载 FSHE 安装包' -Completed
            if ($job.JobState -eq 'Transferred') {
                Complete-BitsTransfer $job
                return
            }
            Remove-BitsTransfer $job -ErrorAction SilentlyContinue
            throw "BITS 状态 $($job.JobState)"
        }
        catch {
            Write-Verbose "BITS 下载失败，改用 Invoke-WebRequest：$($_.Exception.Message)"
            Remove-Item $dest -Force -ErrorAction SilentlyContinue
        }
    }

    Invoke-WebRequest -Uri $url -OutFile $dest -UseBasicParsing
}

if (-not [Environment]::Is64BitOperatingSystem) {
    throw 'FSHE 目前只提供 x64 版本，当前系统不是 64 位。'
}

# ---------------------------------------------------------------- 卸载
if ($Uninstall) {
    $installed = Get-FsheRegistration
    if (-not $installed) {
        Write-Host '未检测到已安装的 FSHE，无需卸载。' -ForegroundColor Yellow
        return
    }

    $uninstaller = ($installed.Uninstaller -replace '"', '').Trim()
    if (-not (Test-Path $uninstaller)) {
        throw "找不到卸载程序：$uninstaller（可到「设置 → 应用」里手动卸载）"
    }

    Write-Step '卸载 FSHE'
    $proc = Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru
    if ($proc.ExitCode -ne 0) {
        throw "卸载程序退出码 $($proc.ExitCode)"
    }
    Write-Host "  已卸载（原版本 $($installed.Version)）" -ForegroundColor Green
    return
}

# -------------------------------------------------------- 本地安装包直装
if ($LocalInstaller) {
    if (-not (Test-Path $LocalInstaller)) {
        throw "找不到安装包：$LocalInstaller"
    }
    $tempFile = (Resolve-Path $LocalInstaller).Path
    $targetVersion = $null
    $asset = $null
    $release = $null
}
else {
    # ------------------------------------------------------------ 查询版本
    Write-Step '查询版本'
    $release = Get-Release $Version
    $asset = $release.assets | Where-Object { $_.name -like 'FSHE-Setup-*.exe' } | Select-Object -First 1
    if (-not $asset) {
        throw "发布 $($release.tag_name) 中没有找到 FSHE-Setup-*.exe 安装包。"
    }

    # 版本标签大小写不统一（有 v1.0.1 也有 V1.1.0），统一去掉前缀再比较
    $targetVersion = $release.tag_name -replace '^[vV]', ''
    $sizeMb = [math]::Round($asset.size / 1MB, 1)
    Write-Host "  版本：$($release.tag_name)"
    Write-Host "  文件：$($asset.name)  $sizeMb MB"

    $installed = Get-FsheRegistration
    if ($installed) {
        if (($installed.Version -eq $targetVersion) -and -not $Force -and -not $DownloadOnly) {
            Write-Host "  已安装 $($installed.Version)，无需更新（-Force 可强制重装）。" -ForegroundColor Green
            if ($installed.Location) {
                Write-Host "  安装位置：$($installed.Location)"
            }
            return
        }
        Write-Host "  检测到已安装：$($installed.Version) → 将更新到 $targetVersion"
    }

    # ------------------------------------------------------------ 下载
    $downloadUrl = $asset.browser_download_url
    if ($Mirror) {
        $downloadUrl = $Mirror.TrimEnd('/') + '/' + $asset.browser_download_url
        Write-Host "  镜像：$Mirror"
    }

    $tempFile = Join-Path $env:TEMP $asset.name
    Write-Step "下载 $($asset.name)（$sizeMb MB）"
    if (Test-Path $tempFile) { Remove-Item $tempFile -Force }

    $started = Get-Date
    Invoke-Download $downloadUrl $tempFile
    $seconds = [math]::Max(1, ((Get-Date) - $started).TotalSeconds)
    $actualSize = (Get-Item $tempFile).Length
    Write-Host ("  完成：{0:N1} MB / {1:N0} 秒（{2:N0} KB/s）" -f ($actualSize / 1MB), $seconds, ($actualSize / 1KB / $seconds))

    # 校验 SHA256（GitHub API 提供的 digest），镜像下载时尤其重要
    if ($asset.digest -and $asset.digest -like 'sha256:*') {
        Write-Host '  校验 SHA256…' -NoNewline
        $expected = $asset.digest.Substring(7).ToLowerInvariant()
        $actual = (Get-FileHash -Path $tempFile -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $expected) {
            Remove-Item $tempFile -Force -ErrorAction SilentlyContinue
            throw '安装包 SHA256 校验失败，下载内容不完整或被篡改，已删除。'
        }
        Write-Host ' 通过' -ForegroundColor Green
    }

    if (Get-Command Unblock-File -ErrorAction SilentlyContinue) {
        Unblock-File -Path $tempFile -ErrorAction SilentlyContinue
    }

    if ($DownloadOnly) {
        Write-Host ''
        Write-Host "  已按 -DownloadOnly 结束（未安装）：$tempFile" -ForegroundColor Yellow
        return $tempFile
    }
}

# ---------------------------------------------------------------- 安装
Write-Step '安装 FSHE'
$proc = Start-Process -FilePath $tempFile `
    -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOCANCEL' `
    -Wait -PassThru
if ($proc.ExitCode -ne 0) {
    throw "安装程序退出码 $($proc.ExitCode)（可以手动运行 $tempFile 查看原因）"
}

$installed = Get-FsheRegistration
if (-not $installed) {
    throw '安装程序已结束，但注册表里没有找到 FSHE，安装可能失败。'
}

Write-Step '完成'
Write-Host "  已安装：$($installed.Location)" -ForegroundColor Green
Write-Host "  版本：  $($installed.Version)"
$exe = Join-Path ($installed.Location.TrimEnd('\')) 'FSHE.exe'
if (Test-Path $exe) {
    Write-Host '  启动：' -NoNewline
    Write-Host $exe -ForegroundColor Yellow
}
