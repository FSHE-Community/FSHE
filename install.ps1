# FSHE installer bootstrap.
#
# This file is intentionally ASCII-only and has no UTF-8 BOM: when you run
#    irm https://raw.githubusercontent.com/YG-O/FSHE/main/install.ps1 | iex
# the text is fed straight into Invoke-Expression, and a BOM at the start
# would break parsing on Windows PowerShell 5.1.
#
# The real installer (Chinese messages, BOM, runnable directly) lives at
# installer/install.ps1. This bootstrap downloads it, strips the BOM and
# forwards the arguments.
#
# Usage:
#    irm https://raw.githubusercontent.com/YG-O/FSHE/main/install.ps1 | iex
#    .\install.ps1 -Version 1.1.0
#    .\install.ps1 -Mirror https://ghfast.top
#    .\install.ps1 -Uninstall

param(
    [string]$Version = 'latest',
    [switch]$Force,
    [switch]$DownloadOnly,
    [switch]$Uninstall,
    [string]$Mirror,
    [string]$LocalInstaller
)

$ErrorActionPreference = 'Stop'

$coreUrl = 'https://raw.githubusercontent.com/YG-O/FSHE/main/installer/install.ps1'
$source = Invoke-RestMethod -Uri $coreUrl
$source = $source.TrimStart([char]0xFEFF)

$core = [scriptblock]::Create($source)
& $core `
    -Version $Version `
    -Force:$Force `
    -DownloadOnly:$DownloadOnly `
    -Uninstall:$Uninstall `
    -Mirror $Mirror `
    -LocalInstaller $LocalInstaller
