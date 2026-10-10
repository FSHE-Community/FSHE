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

## 项目结构

```
FSHE/
├── MainWindow.xaml / .cs       # 主窗口（标题栏、标签页、侧边栏、命令栏）
├── FileExplorerPage.xaml / .cs # 文件列表页面（搜索、排序、视图、选中）
├── FileSystemItem.cs           # 文件/文件夹模型 + ShellIconHelper (P/Invoke)
├── TabItem.cs                  # 标签页视图模型
├── Converters.cs               # 值转换器
├── Styles/DesignTokens.xaml   # 配色常量
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
