using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using FSHE.Models;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.VisualBasic.FileIO;
using Windows.Foundation;
using Windows.System;

namespace FSHE.Views;

/// <summary>
/// 文件列表页面：复选框多选项、三种视图、排序、文件操作（新建/复制/剪切/粘贴/重命名/回收站删除）。
/// </summary>
public sealed partial class FileExplorerPage : Page
{
    public ObservableCollection<FileSystemItem> Items { get; } = new();

    public event EventHandler<string>? NavigationRequested;
    public event EventHandler<int>? CountChanged;

    /// <summary>排序变化：key = Name/Date/Type/Size。</summary>
    public event EventHandler<(string Key, bool Ascending)>? SortChanged;

    /// <summary>视图变化：名称 = 详细信息/列表/大图标。</summary>
    public event EventHandler<string>? ViewChanged;

    /// <summary>预览目标变化（复选框勾选或点击项目）；null 表示无选中。</summary>
    public event EventHandler<FileSystemItem?>? PreviewItemChanged;

    /// <summary>当前目录的项目总数（供宿主在订阅事件前主动读取）。</summary>
    public int CurrentCount => _allItems.Count;

    /// <summary>当前目录所有文件的总大小（文件夹不计入），供底部状态栏显示。</summary>
    public string TotalSizeDisplay
    {
        get
        {
            long total = 0;
            foreach (var item in _allItems)
            {
                if (!item.IsDirectory) total += FileSize(item.FullPath);
            }
            return total > 0 ? FileSystemItem.FormatBytes(total) : string.Empty;
        }
    }

    /// <summary>当前视图的中文名称（与菜单文本一致）。</summary>
    public string CurrentViewName => _mode switch
    {
        ViewMode.List => "列表",
        ViewMode.Tiles => "大图标",
        _ => "详细信息",
    };

    private static List<string>? _clipboardPaths;
    private static bool _clipboardIsCut;

    private readonly List<FileSystemItem> _allItems = new();
    private string _currentPath = string.Empty;
    private string _sortKey = "Name";
    private bool _sortAscending = true;
    private ViewMode _mode = ViewMode.Details;
    private readonly HashSet<string> _folderSizeRequested = new();
    private string _searchQuery = string.Empty;
    private string? _tagFilter;
    private bool _isRecycleBin;

    // 框选（左键在空白处拖拽出蓝色选框）
    private const double MarqueeDragThreshold = 4;
    private bool _marqueeArmed;
    private bool _marqueeDragging;
    private bool _marqueeAdditive;
    private bool _marqueeLogged;
    private uint _marqueePointerId;
    private Point _marqueeOrigin;
    private Point _marqueePointer;
    private Rect _marqueeBounds;
    private ScrollViewer? _marqueeScrollViewer;
    private DispatcherTimer? _marqueeScrollTimer;

    private enum ViewMode { Details, List, Tiles }

    public FileExplorerPage()
    {
        InitializeComponent();
        DetailsList.ItemsSource = Items;
        SimpleList.ItemsSource = Items;
        ItemsGrid.ItemsSource = Items;
        SetView(ViewMode.Details);

        // ListView / GridView 会把指针事件标记为已处理，必须用 handledEventsToo 才能收到
        ContentHost.AddHandler(PointerPressedEvent, new PointerEventHandler(ContentHost_PointerPressed), true);
        ContentHost.AddHandler(PointerMovedEvent, new PointerEventHandler(ContentHost_PointerMoved), true);
        ContentHost.AddHandler(PointerReleasedEvent, new PointerEventHandler(ContentHost_PointerReleased), true);
        ContentHost.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(ContentHost_PointerCaptureLost), true);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string path)
        {
            _currentPath = path;
            LoadDirectory(path);
        }
    }

    // ================== 加载 ==================

    private void LoadNetworkView()
    {
        // 1. 本地盘 (C/D/E...)
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady) continue;
                var label = string.IsNullOrEmpty(drive.VolumeLabel) ? drive.Name.TrimEnd('\\') : drive.VolumeLabel;
                Items.Add(new FileSystemItem
                {
                    Name = label,
                    FullPath = drive.Name,
                    IsDirectory = true,
                    DateModified = "",
                    SizeDisplay = drive.TotalSize > 0 ? FileSystemItem.FormatBytes(drive.TotalSize) : "",
                    TypeDisplay = drive.DriveType switch
                    {
                        DriveType.Fixed => "本地磁盘",
                        DriveType.Removable => "可移动磁盘",
                        DriveType.CDRom => "光盘",
                        DriveType.Network => "网络映射盘",
                        _ => "驱动器",
                    },
                    IconSource = ShellIconHelper.GetFolderIcon(),
                });
            }
        }
        catch { }

        // 2. 已连接网卡
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                var ip = ni.GetIPProperties().UnicastAddresses.FirstOrDefault()?.Address.ToString() ?? "";
                Items.Add(new FileSystemItem
                {
                    Name = ni.Name,
                    FullPath = $"::NIC::{ni.Id}",
                    IsDirectory = false,
                    DateModified = "",
                    SizeDisplay = ip,
                    TypeDisplay = ni.NetworkInterfaceType.ToString(),
                    IconSource = ShellIconHelper.GetFolderIcon(),
                });
            }
        }
        catch { }

        _allItems.AddRange(Items);
        CountChanged?.Invoke(this, Items.Count);
        _currentPath = "::NETWORK";
    }

    private int _loadVersion;

    private async void LoadRecycleBin()
    {
        var version = _loadVersion;
        // 回收站可能有上千条目，COM 枚举放后台线程，避免阻塞 UI
        var entries = await Task.Run(RecycleBinHelper.Enumerate);
        if (version != _loadVersion) return; // 加载期间已导航到别处

        foreach (var entry in entries)
        {
            try
            {
                var item = FileSystemItem.FromRecycleEntry(entry);
                item.PropertyChanged += Item_PropertyChanged;
                _allItems.Add(item);
            }
            catch { /* 跳过无法转换的条目 */ }
        }

        ApplySort();
        foreach (var item in _allItems) Items.Add(item);

        SelectAllBox.IsChecked = false;
        UpdateEmptyState(string.Empty);
        RaiseCount();
        _currentPath = "::RECYCLE";
        PreviewItemChanged?.Invoke(this, null);
    }

    private void LoadDirectory(string path)
    {
        _loadVersion++;
        Items.Clear();
        _allItems.Clear();
        _folderSizeRequested.Clear();
        _searchQuery = string.Empty;
        _tagFilter = null;
        _isRecycleBin = path == "::RECYCLE";
        RecycleToolBar.Visibility = _isRecycleBin ? Visibility.Visible : Visibility.Collapsed;
        DateHeaderText.Text = _isRecycleBin ? "删除日期" : "修改日期";
        EmptyStateTitle.Text = _isRecycleBin ? "回收站是空的" : "此文件夹是空的";

        // 虚拟路径：::NETWORK → 渲染已连接网络 + 映射盘 + 本地盘符
        if (path == "::NETWORK") { LoadNetworkView(); return; }
        // 虚拟路径：::RECYCLE → Shell 回收站内容
        if (path == "::RECYCLE") { LoadRecycleBin(); return; }

        try
        {
            foreach (var dir in Directory.GetDirectories(path))
            {
                try
                {
                    if (!AppSettings.ShowHiddenItems && IsHidden(dir)) continue;
                    var item = FileSystemItem.FromDirectory(dir);
                    item.FileTag = TagStore.Get(item.FullPath);
                    item.PropertyChanged += Item_PropertyChanged;
                    _allItems.Add(item);
                }
                catch { /* 无权限 */ }
            }
            foreach (var file in Directory.GetFiles(path))
            {
                try
                {
                    if (!AppSettings.ShowHiddenItems && IsHidden(file)) continue;
                    var item = FileSystemItem.FromFile(file);
                    item.FileTag = TagStore.Get(item.FullPath);
                    item.PropertyChanged += Item_PropertyChanged;
                    _allItems.Add(item);
                }
                catch { /* 无权限 */ }
            }

            ApplySort();
            foreach (var item in _allItems) Items.Add(item);
        }
        catch { /* 目录无法访问时保持空列表 */ }

        SelectAllBox.IsChecked = false;
        UpdateEmptyState(string.Empty);
        RaiseCount();
        PreviewItemChanged?.Invoke(this, null);
    }

    private static bool IsHidden(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.Hidden) != 0; }
        catch { return false; }
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileSystemItem.IsSelected))
            PreviewItemChanged?.Invoke(this, GetSelected().FirstOrDefault());
    }

    public void Refresh() => LoadDirectory(_currentPath);

    private void RaiseCount() => CountChanged?.Invoke(this, _allItems.Count);

    public void ApplySearch(string query)
    {
        _searchQuery = query ?? string.Empty;
        ReapplyFilter();
    }

    /// <summary>按标签过滤当前列表（null/空 = 显示全部）；与搜索条件叠加。</summary>
    public void ApplyTagFilter(string? tag)
    {
        _tagFilter = string.IsNullOrWhiteSpace(tag) ? null : tag;
        ReapplyFilter();
    }

    private void ReapplyFilter()
    {
        Items.Clear();
        foreach (var item in _allItems)
        {
            if (_tagFilter != null && item.FileTag != _tagFilter) continue;
            if (!string.IsNullOrWhiteSpace(_searchQuery) &&
                !item.Name.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase)) continue;
            Items.Add(item);
        }
        UpdateEmptyState(_searchQuery);
    }

    private void UpdateEmptyState(string query)
    {
        EmptyState.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateHint.Text = string.IsNullOrWhiteSpace(query) ? string.Empty : "没有与搜索匹配的项目";
    }

    // ================== 排序 / 视图 ==================

    public void SetSort(string key, bool ascending)
    {
        _sortKey = key;
        _sortAscending = ascending;
        ApplySort();
        ReapplyFilter();
        UpdateSortHeaders();
        SortChanged?.Invoke(this, (key, ascending));
    }

    /// <summary>表头点击：同列再次点击切换升降序，新列默认升序。</summary>
    private void SortHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;
        SetSort(key, key == _sortKey ? !_sortAscending : true);
    }

    private void UpdateSortHeaders()
    {
        TextBlock glyph = _sortKey switch
        {
            "Date" => DateSortGlyph,
            "Type" => TypeSortGlyph,
            "Size" => SizeSortGlyph,
            _ => NameSortGlyph,
        };
        foreach (var g in new[] { NameSortGlyph, DateSortGlyph, TypeSortGlyph, SizeSortGlyph })
            g.Visibility = Visibility.Collapsed;
        glyph.Text = _sortAscending ? "\u25B2" : "\u25BC";
        glyph.Visibility = Visibility.Visible;
    }

    private void ApplySort()
    {
        var dir = _sortAscending ? 1 : -1;
        _allItems.Sort((a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;

            int c;
            switch (_sortKey)
            {
                case "Date":
                    c = LastWriteTime(a.FullPath).CompareTo(LastWriteTime(b.FullPath));
                    break;
                case "Type":
                    c = string.Compare(a.TypeDisplay, b.TypeDisplay, StringComparison.OrdinalIgnoreCase);
                    break;
                case "Size":
                    c = FileSize(a.FullPath).CompareTo(FileSize(b.FullPath));
                    break;
                default:
                    c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                    break;
            }
            return c * dir;
        });
    }

    private static DateTime LastWriteTime(string path)
    {
        try
        {
            return Directory.Exists(path) ? new DirectoryInfo(path).LastWriteTime : new FileInfo(path).LastWriteTime;
        }
        catch { return default; }
    }

    private static long FileSize(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    public void SetView(string displayName)
    {
        var mode = displayName switch
        {
            "列表" => ViewMode.List,
            "大图标" => ViewMode.Tiles,
            _ => ViewMode.Details,
        };
        SetView(mode);
    }

    /// <summary>详细信息 → 列表 → 大图标 → 详细信息 循环切换。</summary>
    public void CycleView()
    {
        var next = _mode switch
        {
            ViewMode.Details => ViewMode.List,
            ViewMode.List => ViewMode.Tiles,
            _ => ViewMode.Details,
        };
        SetView(next);
    }

    private void SetView(ViewMode mode)
    {
        _mode = mode;
        DetailsList.Visibility = mode == ViewMode.Details ? Visibility.Visible : Visibility.Collapsed;
        SimpleList.Visibility = mode == ViewMode.List ? Visibility.Visible : Visibility.Collapsed;
        ItemsGrid.Visibility = mode == ViewMode.Tiles ? Visibility.Visible : Visibility.Collapsed;
        ViewChanged?.Invoke(this, CurrentViewName);
    }

    // ================== 标签 ==================

    /// <summary>给所有勾选项目打标签（tag 为 null/空 = 清除），并立即持久化。</summary>
    public void SetTagForSelected(string? tag)
    {
        if (_isRecycleBin) return;
        var selected = GetSelected();
        if (selected.Count == 0) return;
        foreach (var item in selected)
        {
            TagStore.Set(item.FullPath, tag);
            item.FileTag = string.IsNullOrWhiteSpace(tag) ? string.Empty : tag;
        }
        TagStore.Save();
    }

    /// <summary>点击文件夹大小列的 "View size"：后台递归计算并回填大小。</summary>
    private async void FolderSize_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not HyperlinkButton { DataContext: FileSystemItem item } || !item.IsDirectory) return;
        if (!_folderSizeRequested.Add(item.FullPath)) return;
        item.SizeDisplay = "…";
        try
        {
            var result = await Task.Run(() => FileSystemItem.CalcFolderSize(item.FullPath));
            item.SizeDisplay = FileSystemItem.FormatBytes(result.Size);
        }
        catch
        {
            item.SizeDisplay = "View size";
            _folderSizeRequested.Remove(item.FullPath);
        }
    }

    // ================== 选中 / 打开 ==================

    public List<FileSystemItem> GetSelected() => _allItems.Where(i => i.IsSelected).ToList();

    private void SelectAllBox_Changed(object sender, RoutedEventArgs e)
    {
        var check = SelectAllBox.IsChecked == true;
        foreach (var item in _allItems) item.IsSelected = check;
    }

    // === 交互：单击选中，双击打开 ===

    private void ItemList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView lv)
        {
            foreach (var old in e.RemovedItems.OfType<FileSystemItem>()) old.IsSelected = false;
            foreach (var added in e.AddedItems.OfType<FileSystemItem>()) added.IsSelected = true;
            if (lv.SelectedItem is FileSystemItem sel) PreviewItemChanged?.Invoke(this, sel);
            else PreviewItemChanged?.Invoke(this, null);
        }
        else if (sender is GridView gv)
        {
            foreach (var old in e.RemovedItems.OfType<FileSystemItem>()) old.IsSelected = false;
            foreach (var added in e.AddedItems.OfType<FileSystemItem>()) added.IsSelected = true;
            if (gv.SelectedItem is FileSystemItem sel) PreviewItemChanged?.Invoke(this, sel);
            else PreviewItemChanged?.Invoke(this, null);
        }
    }

    // ================== 框选（Lasso / Marquee） ==================

    private ListViewBase CurrentList => _mode switch
    {
        ViewMode.List => SimpleList,
        ViewMode.Tiles => ItemsGrid,
        _ => DetailsList,
    };

    private void ContentHost_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_marqueeArmed) return;

        var point = e.GetCurrentPoint(ContentHost);
        // 只处理鼠标左键；触摸/触控笔交给 ScrollViewer 自己滚动
        if (point.PointerDeviceType != PointerDeviceType.Mouse ||
            !point.Properties.IsLeftButtonPressed) return;

        // 从列表项、滚动条、按钮上按下时，交给控件自身处理
        if (IsInteractiveElement(e.OriginalSource as DependencyObject)) return;

        _marqueeArmed = true;
        _marqueeDragging = false;
        _marqueeAdditive = (e.KeyModifiers & VirtualKeyModifiers.Control) != 0;
        _marqueePointerId = e.Pointer.PointerId;
        _marqueeOrigin = point.Position;
        _marqueePointer = point.Position;
        ContentHost.CapturePointer(e.Pointer);
    }

    private void ContentHost_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_marqueeArmed || e.Pointer.PointerId != _marqueePointerId) return;

        var position = e.GetCurrentPoint(ContentHost).Position;
        if (!_marqueeDragging)
        {
            if (Math.Abs(position.X - _marqueeOrigin.X) < MarqueeDragThreshold &&
                Math.Abs(position.Y - _marqueeOrigin.Y) < MarqueeDragThreshold) return;

            _marqueeDragging = true;
            _marqueeLogged = false;
            MarqueeCanvas.Visibility = Visibility.Visible;
            // 未按 Ctrl 时，拖拽一开始就清空原有选择，和资源管理器一致
            if (!_marqueeAdditive) CurrentList.SelectedItems.Clear();
            StartMarqueeAutoScroll();
        }

        UpdateMarquee(position);
    }

    private void ContentHost_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_marqueeArmed || e.Pointer.PointerId != _marqueePointerId) return;

        var dragged = _marqueeDragging;
        EndMarquee();
        ContentHost.ReleasePointerCapture(e.Pointer);

        if (dragged)
        {
            LogMarquee($"done view={_mode} selected={CurrentList.SelectedItems.Count} " +
                       $"modelSelected={_allItems.Count(i => i.IsSelected)}");
        }

        // 空白处单击（没有拖出选框）：清空选择
        if (!dragged && CurrentList.SelectedItems.Count > 0)
        {
            CurrentList.SelectedItems.Clear();
            PreviewItemChanged?.Invoke(this, null);
        }
    }

    private void ContentHost_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_marqueeArmed) EndMarquee();
    }

    private void UpdateMarquee(Point pointer)
    {
        _marqueePointer = pointer;

        // 限制在内容区内，避免选框画到命令栏上
        var x1 = Math.Clamp(_marqueeOrigin.X, 0, ContentHost.ActualWidth);
        var y1 = Math.Clamp(_marqueeOrigin.Y, 0, ContentHost.ActualHeight);
        var x2 = Math.Clamp(pointer.X, 0, ContentHost.ActualWidth);
        var y2 = Math.Clamp(pointer.Y, 0, ContentHost.ActualHeight);

        _marqueeBounds = new Rect(
            Math.Min(x1, x2),
            Math.Min(y1, y2),
            Math.Abs(x1 - x2),
            Math.Abs(y1 - y2));

        Canvas.SetLeft(MarqueeRect, _marqueeBounds.X);
        Canvas.SetTop(MarqueeRect, _marqueeBounds.Y);
        MarqueeRect.Width = _marqueeBounds.Width;
        MarqueeRect.Height = _marqueeBounds.Height;

        ApplyMarqueeSelection();
    }

    /// <summary>把与选框相交的已实现容器加入选择（按住 Ctrl 时保留原有选择）。</summary>
    private void ApplyMarqueeSelection()
    {
        if (!_marqueeDragging) return;

        var list = CurrentList;
        var hitCount = 0;
        var containerCount = 0;

        foreach (var container in EnumerateItemContainers(list))
        {
            containerCount++;
            var item = ResolveItem(list, container);
            if (item is null) continue;

            var bounds = container
                .TransformToVisual(ContentHost)
                .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));

            var isSelected = list.SelectedItems.Contains(item);
            if (Intersects(_marqueeBounds, bounds))
            {
                hitCount++;
                if (!isSelected)
                {
                    list.SelectedItems.Add(item);
                    item.IsSelected = true;
                }
            }
            else if (!_marqueeAdditive && isSelected)
            {
                list.SelectedItems.Remove(item);
                item.IsSelected = false;
            }
        }

        // 诊断日志：每次拖拽只写一行，便于排查框选命中问题
        if (!_marqueeLogged)
        {
            _marqueeLogged = true;
            LogMarquee($"view={_mode} containers={containerCount} hits={hitCount} " +
                       $"rect={_marqueeBounds.X:F0},{_marqueeBounds.Y:F0} {_marqueeBounds.Width:F0}x{_marqueeBounds.Height:F0}");
        }
    }

    /// <summary>递归找出列表里已实现的每一项容器（ListViewItem / GridViewItem）。</summary>
    private static IEnumerable<FrameworkElement> EnumerateItemContainers(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ListViewItem or GridViewItem)
            {
                yield return (FrameworkElement)child;
                continue;
            }

            foreach (var nested in EnumerateItemContainers(child))
                yield return nested;
        }
    }

    private static FileSystemItem? ResolveItem(ListViewBase list, FrameworkElement container)
    {
        if (container.DataContext is FileSystemItem direct) return direct;
        try { return list.ItemFromContainer(container) as FileSystemItem; }
        catch { return null; }
    }

    private static void LogMarquee(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "fshe-marquee.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch { /* 日志失败不影响功能 */ }
    }

    private static bool Intersects(Rect a, Rect b)
        => a.X < b.X + b.Width && b.X < a.X + a.Width &&
           a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    private void StartMarqueeAutoScroll()
    {
        _marqueeScrollViewer = FindScrollViewer(CurrentList);
        if (_marqueeScrollViewer is null) return;

        _marqueeScrollTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _marqueeScrollTimer.Tick -= MarqueeAutoScroll_Tick;
        _marqueeScrollTimer.Tick += MarqueeAutoScroll_Tick;
        _marqueeScrollTimer.Start();
    }

    private void StopMarqueeAutoScroll()
    {
        _marqueeScrollTimer?.Stop();
        _marqueeScrollViewer = null;
    }

    /// <summary>拖到内容区上下边缘附近时自动滚动，边滚边重新计算命中。</summary>
    private void MarqueeAutoScroll_Tick(object? sender, object e)
    {
        var viewer = _marqueeScrollViewer;
        if (!_marqueeDragging || viewer is null) return;

        const double edge = 36;
        const double maxStep = 26;
        var y = _marqueePointer.Y;
        double delta = 0;
        if (y < edge)
            delta = -maxStep * Math.Clamp((edge - y) / edge, 0, 1);
        else if (y > viewer.ViewportHeight - edge)
            delta = maxStep * Math.Clamp((y - (viewer.ViewportHeight - edge)) / edge, 0, 1);

        if (Math.Abs(delta) < 0.5) return;

        var target = Math.Clamp(viewer.VerticalOffset + delta, 0, viewer.ScrollableHeight);
        if (Math.Abs(target - viewer.VerticalOffset) < 0.5) return;

        viewer.ChangeView(null, target, null, true);
        ApplyMarqueeSelection();
    }

    private void EndMarquee()
    {
        _marqueeArmed = false;
        _marqueeDragging = false;
        StopMarqueeAutoScroll();
        MarqueeCanvas.Visibility = Visibility.Collapsed;
        MarqueeRect.Width = 0;
        MarqueeRect.Height = 0;
    }

    /// <summary>判断按下点是否落在列表项、滚动条或命令控件上（这些位置不启动框选）。</summary>
    private static bool IsInteractiveElement(DependencyObject? source)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ListViewItem or GridViewItem or ScrollBar or Thumb
                or Button or CheckBox or HyperlinkButton or TextBox) return true;
        }
        return false;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        return null;
    }

    private void ItemContainer_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is ListView lv && lv.SelectedItem is FileSystemItem item) OpenItem(item);
        else if (sender is GridView gv && gv.SelectedItem is FileSystemItem itemG) OpenItem(itemG);
        else if (e.OriginalSource is FrameworkElement fe && fe.DataContext is FileSystemItem item2) OpenItem(item2);
    }

    private void OpenItem(FileSystemItem item)
    {
        PreviewItemChanged?.Invoke(this, item);
        // 回收站条目（文件或文件夹）都用 $R 物理路径交给 Shell 打开，不做应用内导航
        if (_isRecycleBin) { OpenPath(item.FullPath); return; }
        if (item.IsDirectory) NavigationRequested?.Invoke(this, item.FullPath);
        else OpenPath(item.FullPath);
    }

    public void OpenSelection()
    {
        var selected = GetSelected();
        if (selected.Count > 0)
        {
            foreach (var item in selected)
            {
                if (item.IsDirectory) NavigationRequested?.Invoke(this, item.FullPath);
                else OpenPath(item.FullPath);
            }
        }
    }

    private static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { /* 无关联程序 */ }
    }

    private void ItemsControl_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Delete:
                _ = DeleteSelectedAsync();
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.F2:
                _ = RenameSelectedAsync();
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.F5:
                Refresh();
                e.Handled = true;
                break;
        }
    }

    // ================== 文件操作 ==================

    public async Task NewFolderAsync()
    {
        if (_isRecycleBin) return;
        var name = await PromptAsync("新建文件夹", "输入文件夹名称：", "新建文件夹");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var path = GetUniquePath(Path.Combine(_currentPath, name.Trim()), isDirectory: true);
            Directory.CreateDirectory(path);
            Refresh();
        }
        catch { /* 忽略创建失败 */ }
    }

    public async Task NewFileAsync()
    {
        if (_isRecycleBin) return;
        var name = await PromptAsync("新建文件", "输入文件名称：", "新建文本文档.txt");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var path = GetUniquePath(Path.Combine(_currentPath, name.Trim()), isDirectory: false);
            File.Create(path).Dispose();
            Refresh();
        }
        catch { /* 忽略创建失败 */ }
    }

    private static string GetUniquePath(string path, bool isDirectory)
    {
        if (isDirectory ? !Directory.Exists(path) : !File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var nameNoExt = isDirectory ? path : Path.GetFileNameWithoutExtension(path);
        var ext = isDirectory ? string.Empty : Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = isDirectory
                ? Path.Combine(dir, $"{nameNoExt} ({i})")
                : Path.Combine(dir, $"{nameNoExt} ({i}){ext}");
            if (isDirectory ? !Directory.Exists(candidate) : !File.Exists(candidate)) return candidate;
        }
    }

    public void CutSelected()
    {
        var selected = GetSelected();
        if (selected.Count == 0) return;
        _clipboardPaths = selected.Select(i => i.FullPath).ToList();
        _clipboardIsCut = true;
    }

    public void CopySelected()
    {
        var selected = GetSelected();
        if (selected.Count == 0) return;
        _clipboardPaths = selected.Select(i => i.FullPath).ToList();
        _clipboardIsCut = false;
    }

    public Task PasteAsync()
    {
        if (_isRecycleBin) return Task.CompletedTask;
        if (_clipboardPaths == null || _clipboardPaths.Count == 0) return Task.CompletedTask;

        foreach (var src in _clipboardPaths.ToList())
        {
            try
            {
                var name = Path.GetFileName(src.TrimEnd(Path.DirectorySeparatorChar));
                var isDir = Directory.Exists(src);
                var dest = GetUniquePath(Path.Combine(_currentPath, name), isDir);

                if (_clipboardIsCut)
                {
                    if (isDir) FileSystem.MoveDirectory(src, dest);
                    else FileSystem.MoveFile(src, dest);
                }
                else
                {
                    if (isDir) FileSystem.CopyDirectory(src, dest);
                    else FileSystem.CopyFile(src, dest);
                }
            }
            catch { /* 跳过无法粘贴的项目 */ }
        }

        if (_clipboardIsCut) _clipboardPaths = null;
        Refresh();
        return Task.CompletedTask;
    }

    public async Task DeleteSelectedAsync()
    {
        var selected = GetSelected();
        if (selected.Count == 0) return;

        // 回收站里删除 = 永久删除（走 Shell 动词，由系统连带 $I 元数据一起清掉）
        if (_isRecycleBin)
        {
            var paths = selected.Select(i => i.FullPath).ToList();
            await Task.Run(() => RecycleBinHelper.InvokeVerb(paths, "delete", "删除"));
            Refresh();
            return;
        }

        foreach (var item in selected)
        {
            try
            {
                if (item.IsDirectory)
                    FileSystem.DeleteDirectory(item.FullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                else
                    FileSystem.DeleteFile(item.FullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            }
            catch { /* 跳过删除失败 */ }
        }
        Refresh();
        await Task.CompletedTask;
    }

    public async Task RenameSelectedAsync()
    {
        if (_isRecycleBin) return;
        var selected = GetSelected();
        if (selected.Count != 1) return;
        var item = selected[0];
        var newName = await PromptAsync("重命名", "输入新名称：", item.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName == item.Name) return;
        try
        {
            var dest = Path.Combine(_currentPath, newName.Trim());
            if (item.IsDirectory) Directory.Move(item.FullPath, dest);
            else File.Move(item.FullPath, dest);
            Refresh();
        }
        catch { /* 忽略重命名失败 */ }
    }

    // ================== 回收站操作 ==================

    private async void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelected();
        if (selected.Count == 0) return;
        var paths = selected.Select(i => i.FullPath).ToList();
        await Task.Run(() => RecycleBinHelper.InvokeVerb(paths, "restore", "还原"));
        Refresh();
    }

    private async void EmptyBinButton_Click(object sender, RoutedEventArgs e)
    {
        if (_allItems.Count == 0) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "清空回收站",
            Content = "确定要永久删除回收站中的所有项目吗？此操作无法撤销。",
            PrimaryButtonText = "清空回收站",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await Task.Run(() => RecycleBinHelper.Empty());
        Refresh();
    }

    private async Task<string?> PromptAsync(string title, string message, string defaultValue)
    {
        var box = new TextBox
        {
            Text = defaultValue,
            SelectionStart = 0,
            SelectionLength = defaultValue.Length,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = message }, box } },
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text : null;
    }
}
