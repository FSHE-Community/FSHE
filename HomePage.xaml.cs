using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FSHE;

/// <summary>驱动器主页：展示本机所有就绪驱动器的容量卡片，单击进入。</summary>
public sealed partial class HomePage : Page
{
    /// <summary>请求导航到指定路径（驱动器根目录）。</summary>
    public event EventHandler<string>? NavigationRequested;

    public HomePage()
    {
        InitializeComponent();
        BuildDriveCards();
    }

    private void BuildDriveCards()
    {
        var cards = new List<DriveCard>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
            }
            catch { continue; }

            var letter = drive.Name.TrimEnd('\\');
            string label;
            if (string.IsNullOrWhiteSpace(drive.VolumeLabel))
            {
                label = drive.DriveType == DriveType.Fixed
                    ? (letter == "C:" ? $"系统 ({letter})" : $"本地磁盘 ({letter})")
                    : letter;
            }
            else
            {
                label = $"{drive.VolumeLabel} ({letter})";
            }

            long used = 0, total = 0, free = 0;
            try
            {
                total = drive.TotalSize;
                free = drive.AvailableFreeSpace;
                used = total - free;
            }
            catch { /* 容量读取失败时显示未知 */ }

            var fraction = total > 0 ? (double)used / total : 0;
            var pct = (int)Math.Round(fraction * 100);
            // Files 文案格式："27.39 GiB 可用，共 99.01 GiB"
            var spaceText = total > 0
                ? $"{FileSystemItem.FormatBytes(free)} 可用，共 {FileSystemItem.FormatBytes(total)}"
                : "容量未知";

            cards.Add(new DriveCard(
                drive.Name,
                label,
                ShellIconHelper.GetDriveIcon(drive.Name),
                fraction,
                spaceText,
                fraction >= 0.9 ? "#E5534B" : "#4CC2FF",
                $"{label}\n{spaceText}"));
        }

        DrivesList.ItemsSource = cards;
    }

    private void DriveCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string root })
            NavigationRequested?.Invoke(this, root);
    }

    /// <summary>驱动器卡片视图模型。</summary>
    public sealed class DriveCard
    {
        public DriveCard(string root, string label, ImageSource icon,
            double usedFraction, string spaceText, string barColorHex, string toolTipText)
        {
            Root = root;
            Label = label;
            Icon = icon;
            UsedFraction = usedFraction;
            SpaceText = spaceText;
            ToolTipText = toolTipText;
            byte a = 255,
                r = byte.Parse(barColorHex.Substring(1, 2), System.Globalization.NumberStyles.HexNumber),
                g = byte.Parse(barColorHex.Substring(3, 2), System.Globalization.NumberStyles.HexNumber),
                b = byte.Parse(barColorHex.Substring(5, 2), System.Globalization.NumberStyles.HexNumber);
            BarBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(a, r, g, b));
        }

        public string Root { get; }
        public string Label { get; }
        public ImageSource Icon { get; }
        public double UsedFraction { get; }
        public string SpaceText { get; }
        public string ToolTipText { get; }
        public Brush BarBrush { get; }
    }
}
