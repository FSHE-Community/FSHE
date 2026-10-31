# FSHE

一个基于 WinUI 3 的现代化 Windows 文件资源管理器，参考了 [Files](https://github.com/files-community/Files) 的设计风格。

> **F**ile **S**ystem **H**elper **E**xplorer

## 功能特性

- 🎨 **深色现代化 UI** — 药丸面包屑、圆角标签、系统真实 Shell 图标
- 📑 **多标签页** — 标签切换、关闭、右键菜单（关闭其他/右侧）、Ctrl+T/W/Tab 快捷键
- 🗂️ **侧边栏导航** — 主页、固定项目、驱动器分区（运行时枚举）、网络
- 📂 **Shell 真实图标** — 通过 `SHGetFileInfo` P/Invoke 提取系统原生图标（.lnk 跟随目标、.exe 显示自身图标等）
- 🔍 **内置搜索** — 按文件名实时过滤当前目录
- 🖼️ **预览窗格** — 右侧面板显示选中文件的图标、标签、大小
- 🏷️ **文件标签** — 重要/工作/个人/收藏四色标签，持久化到 `%LocalAppData%\FSHE\tags.json`
- 🔄 **剪贴板操作** — 剪切/复制/粘贴（VB FileIO）、回收站删除
- 🗑️ **回收站** — Delete 键或命令栏删除按钮
- 📐 **多视图切换** — 详情/大图标/小图标
- ↕️ **排序** — 按名称/日期/类型/大小，升降序

## 技术栈

| 技术 | 版本 |
|------|------|
| C# | .NET 10 |
| WinUI 3 / Windows App SDK | 1.8.250916003 |
| 目标平台 | net10.0-windows10.0.26100.0 (Windows 11) |
| 模式 | 非打包（Unpackaged），无需 Developer Mode |

## 安装

### 命令行一键安装（推荐）

```powershell
irm https://raw.githubusercontent.com/FSHE-Community/FSHE/main/install.ps1 | iex
```

一条命令完成「查询最新正式版 → 下载 → 静默安装」：装到当前用户目录，**不弹 UAC、不需要管理员权限**，已安装同版本会自动跳过。走管道执行，不受 PowerShell 执行策略限制。

需要更多控制时，先把脚本落到本地再运行：

```powershell
iwr https://raw.githubusercontent.com/FSHE-Community/FSHE/main/install.ps1 -OutFile install.ps1

.\install.ps1                  # 安装最新正式版
.\install.ps1 -Version 1.1.1   # 安装指定版本
.\install.ps1 -Force           # 已安装同版本时强制重装
.\install.ps1 -DownloadOnly    # 只下载安装包，不安装
.\install.ps1 -Uninstall       # 静默卸载
.\install.ps1 -LocalInstaller .\FSHE-Setup-1.1.1.exe   # 装本地已有的安装包（离线）
```

> 从 GitHub 直连下载慢（国内常见）时可加镜像，脚本会用 Release 的 SHA256 校验安装包：
>
> ```powershell
> .\install.ps1 -Mirror https://ghfast.top
> ```

### 图形安装包

在 [Releases](https://github.com/FSHE-Community/FSHE/releases) 下载 `FSHE-Setup-<版本>.exe` 双击安装：

- 静默安装参数：`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`
- 默认安装到 `%LocalAppData%\Programs\FSHE`，可自行改路径

## 构建与运行

### 环境要求

- Windows 11（或 Windows 10 22H2+ 带 Windows App SDK 运行时）
- .NET 10 SDK（`dotnet --version` 确认）

### 步骤

```powershell
# 1. 克隆仓库
git clone <repo-url>
cd FSHE

# 2. 还原依赖 + 构建
dotnet build

# 3. 运行（非打包模式，直接运行 exe）
dotnet run
# 或者手动：
.\bin\Debug\net10.0-windows10.0.26100.0\win-x64\FSHE.exe
```

### 打包安装程序

仓库内 `installer\` 目录提供一键脚本（自动 publish、补齐 WinUI 运行时资源、同步到 `installer_src`、调用 Inno Setup 编译）：

```powershell
.\installer\publish-installer.ps1
```

- 需安装 [Inno Setup 6](https://jrsoftware.org/isinfo.php)。脚本会依次在 `E:\Inno\Inno Setup 6\`、Program Files、注册表中查找 `ISCC.exe`，也可用 `-IsccPath` 手动指定
- 产物：`installer_output\FSHE-Setup-<版本>.exe`
- 只准备发布文件、不编译安装包：`.\installer\publish-installer.ps1 -SkipInstaller`

> 注意：非打包模式下，`dotnet publish` 默认不会带上 `*.xbf` 与 `FSHE.pri`，缺了会导致应用启动即崩溃。`FSHE.csproj` 里的 `CopyWinUIResourcesToPublish` 目标已自动处理，无需手动拷贝。

## 项目结构

```
FSHE/
├── App.xaml / .cs              # 应用入口、全局资源
├── Views/
│   ├── MainWindow.xaml / .cs       # 主窗口（标题栏、标签页、侧边栏、命令栏）
│   ├── FileExplorerPage.xaml / .cs # 文件列表页面（搜索、排序、视图、选中、框选）
│   └── HomePage.xaml / .cs         # 主页（驱动器概览）
├── Models/
│   ├── FileSystemItem.cs           # 文件/文件夹模型 + ShellIconHelper (P/Invoke)
│   └── TabItem.cs                  # 标签页视图模型
├── Helpers/Converters.cs       # 值转换器
├── Styles/DesignTokens.xaml    # 配色常量
├── installer/                  # FSHE-Setup.iss + publish-installer.ps1
├── docs/                       # 设计与开发文档
└── Assets/                     # 应用图标资源
```

## 快捷键

| 快捷键 | 功能 |
|--------|------|
| `Ctrl+T` | 新建标签页 |
| `Ctrl+W` | 关闭当前标签页 |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | 切换前后标签 |
| `Ctrl+L` / `Alt+D` | 聚焦地址栏 |
| `Ctrl+F` | 展开并聚焦搜索框 |
| `F5` | 刷新当前目录 |
| `F2` | 重命名（选中文件时） |
| `Delete` | 移入回收站 |
| `Ctrl+A/D/C/V/X` | 全选/剪切/复制/粘贴 |

## 许可证

本项目基于 [GNU General Public License v3.0](LICENSE) 开源。

**简而言之**：你可以自由使用、修改、分发本项目，但修改后的衍生项目也必须以 GPL-3.0 开源。
