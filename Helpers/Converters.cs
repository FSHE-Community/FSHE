using FSHE.Models;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace FSHE.Helpers;

/// <summary>按文件扩展名返回图标前景色；文件夹→黄色，图片→绿，音频→紫，视频→红，压缩→橙，可执行→蓝，文档→白，其余→次级灰。</summary>
public sealed class FolderIconBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, (byte R, byte G, byte B)> ExtColors = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"]  = (84, 209, 139), [".jpg"]  = (84, 209, 139), [".jpeg"] = (84, 209, 139), [".gif"] = (84, 209, 139),
        [".bmp"]  = (84, 209, 139), [".webp"] = (84, 209, 139), [".svg"]  = (84, 209, 139), [".ico"] = (84, 209, 139),
        [".mp3"]  = (199, 146, 234), [".wav"]  = (199, 146, 234), [".flac"] = (199, 146, 234), [".m4a"] = (199, 146, 234),
        [".aac"]  = (199, 146, 234), [".ogg"]  = (199, 146, 234),
        [".mp4"]  = (255, 107, 107), [".mkv"]  = (255, 107, 107), [".mov"]  = (255, 107, 107), [".avi"] = (255, 107, 107),
        [".wmv"]  = (255, 107, 107), [".m4v"]  = (255, 107, 107),
        [".zip"]  = (255, 159, 67), [".rar"]  = (255, 159, 67), [".7z"]   = (255, 159, 67), [".tar"] = (255, 159, 67),
        [".gz"]   = (255, 159, 67),
        [".exe"]  = (76, 194, 255), [".msi"]  = (76, 194, 255), [".dll"]  = (76, 194, 255),
        [".pdf"]  = (232, 76, 61),
        [".ps1"]  = (31, 180, 160), [".bat"]  = (31, 180, 160), [".cmd"]  = (31, 180, 160),
        [".sln"]  = (0, 120, 212), [".csproj"] = (0, 120, 212), [".vbproj"] = (0, 120, 212),
        [".txt"]  = (230, 230, 230), [".md"]   = (230, 230, 230), [".log"]  = (230, 230, 230),
        [".doc"]  = (76, 194, 255), [".docx"] = (76, 194, 255),
        [".xls"]  = (84, 209, 139), [".xlsx"] = (84, 209, 139), [".csv"]  = (84, 209, 139),
        [".ppt"]  = (255, 159, 67), [".pptx"] = (255, 159, 67),
    };

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is FileSystemItem item)
        {
            if (item.IsDirectory) return FolderYellow;
            var ext = System.IO.Path.GetExtension(item.FullPath);
            if (ExtColors.TryGetValue(ext, out var c)) return Brushed(c.R, c.G, c.B);
            return FileGray;
        }
        // 兼容旧的 bool 绑定（IsDirectory）
        if (value is bool b && b) return FolderYellow;
        return FileGray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();

    private static readonly SolidColorBrush FolderYellow = Brushed(255, 200, 61);
    private static readonly SolidColorBrush FileGray     = Brushed(230, 230, 230);

    private static SolidColorBrush Brushed(byte r, byte g, byte b)
        => new(Microsoft.UI.ColorHelper.FromArgb(255, r, g, b));
}

/// <summary>文件夹大小列 → 强调色 "View size" 链接；文件 → 次级文本色。</summary>
public sealed class FolderAccentBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isDir = value is bool b && b;
        return new SolidColorBrush(isDir
            ? Microsoft.UI.ColorHelper.FromArgb(255, 76, 194, 255)
            : Microsoft.UI.ColorHelper.FromArgb(190, 155, 155, 155));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>bool → Visibility；parameter="invert" 时取反。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var flag = value is bool b && b;
        if (parameter as string == "invert") flag = !flag;
        return flag ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>标签名 → 预设颜色；无标签时回退次级灰。</summary>
public sealed class TagBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var tag = value as string ?? string.Empty;
        var hex = TagStore.PresetColors.TryGetValue(tag, out var h) ? h : "#9B9B9B";
        return new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(
            255,
            System.Convert.ToByte(hex.Substring(1, 2), 16),
            System.Convert.ToByte(hex.Substring(3, 2), 16),
            System.Convert.ToByte(hex.Substring(5, 2), 16)));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
