using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FSHE;

/// <summary>Shell32 SHGetFileInfo P/Invoke — 提取 Windows 系统真实文件图标。</summary>
internal static class ShellIconHelper
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_SMALLICON = 0x1;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

    /// <summary>需要跟随目标路径取图标的扩展名（快捷方式、可执行文件）。</summary>
    private static readonly HashSet<string> PerPathExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lnk", ".url", ".exe", ".bat", ".cmd", ".ps1", ".msi", ".scr", ".com",
    };

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static readonly Dictionary<string, BitmapImage> _cache = new(StringComparer.OrdinalIgnoreCase);

    public static BitmapImage GetFolderIcon()
    {
        const string key = "::FOLDER::";
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var img = Extract(null, isDirectory: true);
        if (img != null) _cache[key] = img;
        return img ?? DefaultFileIcon();
    }

    public static BitmapImage GetFileIcon(string path)
    {
        var ext = Path.GetExtension(path);

        // 普通文件：按扩展名缓存
        if (!PerPathExtensions.Contains(ext))
        {
            if (_cache.TryGetValue(ext, out var cached)) return cached;

            // 先尝试用真实路径（如果文件存在），不存在则按扩展名用 USEFILEATTRIBUTES 取关联图标
            var exists = File.Exists(path);
            var img = exists
                ? Extract(path, isDirectory: false)
                : ExtractByExtension(ext);

            if (img != null) _cache[ext] = img;
            return img ?? DefaultFileIcon();
        }

        // 需要跟随目标的文件（.lnk、.exe 等）：按完整路径缓存，因为每个 .lnk 指向不同目标
        if (_cache.TryGetValue(path, out var perPathCached)) return perPathCached;
        var real = File.Exists(path) ? Extract(path, isDirectory: false) : null;
        if (real != null) _cache[path] = real;
        return real ?? DefaultFileIcon();
    }

    /// <summary>从真实存在的路径取图标。</summary>
    private static BitmapImage? Extract(string? path, bool isDirectory)
    {
        SHFILEINFO info = default;
        uint flags = SHGFI_ICON | SHGFI_SMALLICON;

        IntPtr hIcon;
        if (isDirectory)
        {
            // 不能用 "C:\\" 这种驱动器根路径 —— SHGetFileInfo 会返回盘符图标而非文件夹图标
            // 用 SHGFI_USEFILEATTRIBUTES + FILE_ATTRIBUTE_DIRECTORY 让它按属性返回通用黄色文件夹图标
            flags |= SHGFI_USEFILEATTRIBUTES;
            hIcon = SHGetFileInfo("dummy", FILE_ATTRIBUTE_DIRECTORY, ref info, (uint)Marshal.SizeOf(info), flags);
        }
        else
        {
            hIcon = SHGetFileInfo(path ?? ".txt", 0, ref info, (uint)Marshal.SizeOf(info), flags);
        }

        if (hIcon == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
        return ConvertAndFree(info.hIcon);
    }

    /// <summary>用 SHGFI_USEFILEATTRIBUTES 按扩展名取关联图标（文件不存在时）。</summary>
    private static BitmapImage? ExtractByExtension(string ext)
    {
        SHFILEINFO info = default;
        uint flags = SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES;
        var hIcon = SHGetFileInfo("dummy" + (ext ?? ".txt"), FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf(info), flags);
        if (hIcon == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
        return ConvertAndFree(info.hIcon);
    }

    private static BitmapImage? ConvertAndFree(IntPtr hIcon)
    {
        try
        {
            using var icon = Icon.FromHandle(hIcon);
            using var bmp = icon.ToBitmap();
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            ms.Position = 0;
            var bi = new BitmapImage();
            bi.SetSource(ms.AsRandomAccessStream());
            return bi;
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static BitmapImage? _defaultFileIcon;
    private static BitmapImage DefaultFileIcon()
    {
        if (_defaultFileIcon != null) return _defaultFileIcon;
        return _defaultFileIcon = GetFolderIcon();
    }
}

/// <summary>
/// 文件系统中单个条目的视图模型（文件或文件夹）。
/// </summary>
public class FileSystemItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _fileTag = string.Empty;
    private string _sizeDisplay = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public string DateModified { get; set; } = string.Empty;

    /// <summary>大小列文本；文件夹点"View size"计算完成后会实时刷新。</summary>
    public string SizeDisplay
    {
        get => _sizeDisplay;
        set
        {
            if (_sizeDisplay != value)
            {
                _sizeDisplay = value;
                OnPropertyChanged();
            }
        }
    }
    public string TypeDisplay { get; set; } = string.Empty;

    /// <summary>Windows 系统真实图标（从 SHGetFileInfo 提取，已缓存）。</summary>
    public ImageSource IconSource { get; set; }

    /// <summary>用户打的标签（重要/工作/个人/收藏），变化时刷新标签列。</summary>
    public string FileTag
    {
        get => _fileTag;
        set
        {
            if (_fileTag != value)
            {
                _fileTag = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>复选框绑定的选中状态。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public static FileSystemItem FromDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        return new FileSystemItem
        {
            Name = info.Name,
            FullPath = info.FullName,
            IsDirectory = true,
            DateModified = FormatRelativeTime(info.LastWriteTime),
            TypeDisplay = "文件夹",
            SizeDisplay = "View size",
            IconSource = ShellIconHelper.GetFolderIcon(),
        };
    }

    public static FileSystemItem FromFile(string path)
    {
        var info = new FileInfo(path);
        return new FileSystemItem
        {
            Name = info.Name,
            FullPath = info.FullName,
            IsDirectory = false,
            DateModified = FormatRelativeTime(info.LastWriteTime),
            SizeDisplay = FormatSize(info.Length),
            TypeDisplay = DescribeFileType(info.Extension),
            IconSource = ShellIconHelper.GetFileIcon(path),
        };
    }

    /// <summary>相对时间：刚刚 / N 分钟前 / N 小时前 / N 天前 / 中文日期。</summary>
    private static string FormatRelativeTime(DateTime time)
    {
        var delta = DateTime.Now - time;
        if (delta.TotalSeconds < 60) return "刚刚";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes} 分钟前";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours} 小时前";
        if (delta.TotalDays < 7) return $"{(int)delta.TotalDays} 天前";
        return time.ToString("yyyy年M月d日");
    }

    private static string DescribeFileType(string ext) => ext.ToLowerInvariant() switch
    {
        ".txt" => "txtfile",
        ".md" => "Markdown 文档",
        ".manifest" => "MANIFEST 文件",
        ".xaml" => "Windows.XamlDocument",
        ".cs" => "CS 文件",
        ".csproj" => "VisualStudio.Launcher.csproj",
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" => "图片",
        ".svg" => "SVG 图像",
        ".mp3" or ".wav" or ".flac" or ".aac" or ".ogg" => "音频",
        ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" => "视频",
        ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "压缩文件",
        ".pdf" => "PDF 文档",
        ".doc" or ".docx" => "Word 文档",
        ".xls" or ".xlsx" => "Excel 工作表",
        ".csv" => "CSV 数据",
        ".exe" or ".msi" => "应用程序",
        ".lnk" => "快捷方式",
        ".url" => "Internet 快捷方式",
        "" => "文件",
        _ => $"{ext.TrimStart('.').ToUpperInvariant()} 文件",
    };

    private static string FormatSize(long bytes) => FormatBytes(bytes);

    /// <summary>字节数友好格式（属性对话框等外部复用）。</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024 * 1024) return $"{bytes / 1024.0 / 1024 / 1024 / 1024:F2} TiB";
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024 / 1024:F2} GiB";
        if (bytes >= 1024L * 1024) return $"{bytes / 1024.0 / 1024:F2} MiB";
        if (bytes >= 1024) return $"{bytes / 1024.0:F2} KiB";
        return $"{bytes} B";
    }

    /// <summary>迭代递归统计文件夹总大小与文件数（无权限目录自动跳过）。</summary>
    public static (long Size, int Files) CalcFolderSize(string root)
    {
        long size = 0;
        int files = 0;
        var dirs = new Stack<string>();
        dirs.Push(root);
        while (dirs.Count > 0)
        {
            var dir = dirs.Pop();
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    try { size += new FileInfo(f).Length; files++; } catch { /* 跳过 */ }
                }
                foreach (var d in Directory.EnumerateDirectories(dir)) dirs.Push(d);
            }
            catch { /* 无权限 */ }
        }
        return (size, files);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>应用级简单设置（非持久化的开关目前只有"显示隐藏项目"）。</summary>
public static class AppSettings
{
    public static bool ShowHiddenItems { get; set; } = false;
}

/// <summary>
/// 文件标签持久化：%LocalAppData%\FSHE\tags.json，键为完整路径。
/// </summary>
public static class TagStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FSHE", "tags.json");

    private static Dictionary<string, string>? _tags;

    /// <summary>内置标签及其颜色（ARGB 十六进制）。</summary>
    public static readonly IReadOnlyDictionary<string, string> PresetColors = new Dictionary<string, string>
    {
        ["重要"] = "#FF6B6B",
        ["工作"] = "#4CC2FF",
        ["个人"] = "#54D18B",
        ["收藏"] = "#FFC83D",
    };

    private static Dictionary<string, string> Tags
    {
        get
        {
            if (_tags != null) return _tags;
            try
            {
                if (File.Exists(FilePath))
                    _tags = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath))
                            ?? new Dictionary<string, string>();
                else
                    _tags = new Dictionary<string, string>();
            }
            catch { _tags = new Dictionary<string, string>(); }
            return _tags;
        }
    }

    public static string Get(string fullPath)
        => Tags.TryGetValue(fullPath, out var tag) ? tag : string.Empty;

    /// <summary>全部标签（预设 + tags.json 中出现过的），按预设顺序在前，供侧边栏标签分区与打标签菜单使用。</summary>
    public static IReadOnlyList<(string Name, string Color)> AllTags()
    {
        var list = new List<(string, string)>();
        var seen = new HashSet<string>();
        foreach (var kv in PresetColors)
        {
            if (seen.Add(kv.Key)) list.Add((kv.Key, kv.Value));
        }
        foreach (var tag in Tags.Values)
        {
            if (!string.IsNullOrWhiteSpace(tag) && seen.Add(tag))
                list.Add((tag, PresetColors.TryGetValue(tag, out var c) ? c : "#9B9B9B"));
        }
        return list;
    }

    public static void Set(string fullPath, string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) Tags.Remove(fullPath);
        else Tags[fullPath] = tag;
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Tags));
        }
        catch { /* 标签保存失败不阻断操作 */ }
    }
}
