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

    /// <summary>提取驱动器根目录的真实图标（硬盘/USB 盘各自的原生图标）。根路径不能走 Extract 的目录分支。</summary>
    public static BitmapImage GetDriveIcon(string rootPath)
    {
        var key = "::DRIVE:" + rootPath.ToUpperInvariant();
        if (_cache.TryGetValue(key, out var cached)) return cached;

        SHFILEINFO info = default;
        var hIcon = SHGetFileInfo(rootPath, 0, ref info, (uint)Marshal.SizeOf(info), SHGFI_ICON);
        BitmapImage? img = null;
        if (hIcon != IntPtr.Zero && info.hIcon != IntPtr.Zero) img = ConvertAndFree(info.hIcon);
        img ??= GetFolderIcon();
        _cache[key] = img;
        return img;
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

/// <summary>回收站（Shell 命名空间 ssfBITBUCKET=10）枚举与操作：晚绑定 Shell.Application COM。</summary>
internal static class RecycleBinHelper
{
    public sealed record RecycleEntry(
        string Name,
        string PhysicalPath,
        bool IsFolder,
        long Size,
        string Type,
        DateTime? DeletedDate,
        string DeletedFrom);

    private const int SsfBitBucket = 10;
    private const uint SHERB_NOCONFIRMATION = 0x1;
    private const uint SHERB_NOPROGRESSUI = 0x2;
    private const uint SHERB_NOSOUND = 0x4;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    /// <summary>枚举回收站全部条目（PhysicalPath 指向 $R... 真实数据，可供取图标/打开/计算大小）。</summary>
    public static List<RecycleEntry> Enumerate()
    {
        var list = new List<RecycleEntry>();
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType == null) return list;
        try
        {
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic folder = shell.NameSpace(SsfBitBucket);
            if (folder == null) return list;
            foreach (dynamic item in folder.Items())
            {
                try
                {
                    string path = (string)item.Path;
                    if (string.IsNullOrEmpty(path)) continue;
                    long size = 0;
                    try { size = Convert.ToInt64(item.Size); } catch { /* 文件夹可能无大小 */ }
                    list.Add(new RecycleEntry(
                        (string)item.Name,
                        path,
                        (bool)item.IsFolder,
                        size,
                        item.Type as string ?? string.Empty,
                        ReadDate(item, "System.Recycle.DateDeleted"),
                        ReadString(item, "System.Recycle.DeletedFrom")));
                }
                catch { /* 跳过无法读取的条目 */ }
            }
        }
        catch { /* Shell 不可用时返回空列表 */ }
        return list;
    }

    /// <summary>清空所有驱动器的回收站（无确认、无进度条、无声）。</summary>
    public static void Empty() => SHEmptyRecycleBin(IntPtr.Zero, null,
        SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);

    /// <summary>对回收站条目执行 Shell 动词（永久删除/还原），按物理 $R 路径匹配。</summary>
    public static void InvokeVerb(IEnumerable<string> physicalPaths, string canonicalVerb, params string[] localizedVerbs)
    {
        var targets = new HashSet<string>(physicalPaths, StringComparer.OrdinalIgnoreCase);
        if (targets.Count == 0) return;

        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType == null) return;
        try
        {
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic folder = shell.NameSpace(SsfBitBucket);
            if (folder == null) return;

            // 先收集 COM 对象，不能边枚举边删除（集合会变化）
            var matched = new List<dynamic>();
            foreach (dynamic item in folder.Items())
            {
                try { if (targets.Contains((string)item.Path)) matched.Add(item); }
                catch { /* 忽略 */ }
            }
            foreach (var item in matched) DoVerb(item, canonicalVerb, localizedVerbs);
        }
        catch { /* Shell 操作失败忽略 */ }
    }

    private static void DoVerb(dynamic item, string canonicalVerb, string[] localizedVerbs)
    {
        // 规范动词（delete 等）与语言无关；restore 不一定支持，失败再按本地化名称匹配
        try { item.InvokeVerb(canonicalVerb); return; }
        catch { /* 继续尝试本地化动词 */ }

        try
        {
            foreach (dynamic verb in item.Verbs())
            {
                var raw = ((string)verb.Name).Replace("&", string.Empty);
                var paren = raw.IndexOf('(');
                var name = (paren >= 0 ? raw[..paren] : raw).Trim();
                if (string.Equals(name, canonicalVerb, StringComparison.OrdinalIgnoreCase) ||
                    localizedVerbs.Any(v => string.Equals(name, v, StringComparison.OrdinalIgnoreCase)))
                {
                    verb.DoIt();
                    return;
                }
            }
        }
        catch { /* 忽略动词枚举失败 */ }
    }

    private static string ReadString(dynamic item, string propName)
    {
        try { return item.ExtendedProperty(propName) as string ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static DateTime? ReadDate(dynamic item, string propName)
    {
        try
        {
            var value = item.ExtendedProperty(propName);
            return value switch
            {
                DateTime dt => dt,
                string s when DateTime.TryParse(s, out var parsed) => parsed,
                _ => null,
            };
        }
        catch { return null; }
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

    /// <summary>是否为回收站中的条目（FullPath 此时是 $R 物理路径，而非原始位置）。</summary>
    public bool IsRecycleItem { get; set; }

    /// <summary>回收站条目的原始所在目录（System.Recycle.DeletedFrom）。</summary>
    public string OriginalLocation { get; set; } = string.Empty;

    /// <summary>Windows 系统真实图标（从 SHGetFileInfo 提取，已缓存）。</summary>
    public ImageSource IconSource { get; set; }

    /// <summary>用户打的标签（Home/Work/Photos/Important/Software 等），变化时刷新标签列。</summary>
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

    /// <summary>回收站条目：名称为原始文件名，路径为 $R 物理数据路径，日期取删除时间。</summary>
    internal static FileSystemItem FromRecycleEntry(RecycleBinHelper.RecycleEntry entry)
    {
        var item = new FileSystemItem
        {
            Name = entry.Name,
            FullPath = entry.PhysicalPath,
            IsDirectory = entry.IsFolder,
            DateModified = entry.DeletedDate is { } d ? FormatRelativeTime(d) : string.Empty,
            TypeDisplay = string.IsNullOrWhiteSpace(entry.Type)
                ? (entry.IsFolder ? "文件夹" : "文件")
                : entry.Type,
            IconSource = entry.IsFolder
                ? ShellIconHelper.GetFolderIcon()
                : ShellIconHelper.GetFileIcon(entry.PhysicalPath),
            IsRecycleItem = true,
            OriginalLocation = entry.DeletedFrom ?? string.Empty,
        };
        if (entry.IsFolder)
            item.SizeDisplay = entry.Size > 0 ? FormatBytes(entry.Size) : "View size";
        else
            item.SizeDisplay = FormatSize(entry.Size);
        return item;
    }

    /// <summary>相对时间：刚刚 / N 分钟前 / N 小时前 / N 天前 / 中文日期。</summary>
    public static string FormatRelativeTime(DateTime time)
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
        ["Home"] = "#2E7BE0",
        ["Work"] = "#E5534B",
        ["Photos"] = "#4CC2FF",
        ["Important"] = "#54D18B",
        ["Software"] = "#1B5FD0",
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
