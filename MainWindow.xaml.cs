using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;
using Windows.Foundation;
using Windows.UI;
using WinRT;

namespace FSHE;

/// <summary>
/// FSHE 主窗口：标签页、工具栏、命令栏、分区侧边栏、内容 Frame、悬浮状态栏。
/// </summary>
public sealed partial class MainWindow : Window
{
    public ObservableCollection<string> PathSegments { get; } = new();
    public ObservableCollection<TabItem> Tabs { get; } = new();

    private readonly Stack<string> _backStack = new();
    private readonly Stack<string> _forwardStack = new();
    private string _currentPath = string.Empty;
    private TabItem? _activeTab;

    private readonly List<ToggleButton> _navButtons = new();
    private string _sortKey = "Name";
    private bool _sortAscending = true;
    private bool _previewOpen;
    private int _previewSizeToken;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ConfigureCaptionButtons();
        ConfigureWindowIcon();
        ConfigureDefaultWindowSize();
        RegisterKeyboardShortcuts();

        PathBreadcrumb.ItemsSource = PathSegments;
        TabsItemsControl.ItemsSource = Tabs;

        RegisterNavButtons();
        BuildDrivesSection();
        BuildTagsSection();
        InitSectionChevrons();

        // 启动时两个标签页：用户主目录 + 桌面
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        Tabs.Add(new TabItem("主页", home));
        Tabs.Add(new TabItem("桌面", Directory.Exists(desktop) ? desktop : home));
        SelectTab(Tabs[1]);
        Tabs.CollectionChanged += (_, __) => ScheduleUpdateTabVisuals();
        AppTitleBar.Loaded += (_, __) => ScheduleUpdateTabVisuals();
    }

    private void ScheduleUpdateTabVisuals()
    {
        var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        dq.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => UpdateTabVisuals());
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void ConfigureDefaultWindowSize()
    {
        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var scale = (double)GetDpiForWindow((IntPtr)AppWindow.Id.Value) / 96.0;
            var w = Math.Min((int)(1280 * scale), (int)(area.WorkArea.Width * 0.98));
            var h = Math.Min((int)(800 * scale), (int)(area.WorkArea.Height * 0.98));
            var x = area.WorkArea.X + (area.WorkArea.Width - w) / 2;
            var y = area.WorkArea.Y + (area.WorkArea.Height - h) / 2;
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, w, h));
        }
        catch { /* 忽略窗口定位失败 */ }
    }

    private void ConfigureCaptionButtons()
    {
        var tb = AppWindow.TitleBar;
        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.ButtonForegroundColor = (Color)Application.Current.Resources["FsheTextColor"];
        tb.ButtonInactiveForegroundColor = (Color)Application.Current.Resources["FsheTextSecondaryColor"];
        tb.ButtonHoverBackgroundColor = (Color)Application.Current.Resources["FsheHoverColor"];
        tb.ButtonPressedBackgroundColor = (Color)Application.Current.Resources["FsheStrokeColor"];
    }

    /// <summary>显式设置窗口/任务栏/Alt+Tab 图标（exe 资源之外的运行时保障）。</summary>
    private void ConfigureWindowIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        }
        catch { /* 图标缺失时保持默认 */ }
    }

    // ================== 标签页 ==================

    private static readonly Brush _transparentBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

    private void SelectTab(TabItem tab)
    {
        _activeTab = tab;
        UpdateTabVisuals();
        NavigateTo(tab.Path, addToHistory: false);
    }

    private void NewTabButton_Click(object sender, RoutedEventArgs e)
    {
        var path = _activeTab?.Path
                   ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var tab = new TabItem("FSHE", path);
        Tabs.Add(tab);
        SelectTab(tab);
    }

    // ================== 键盘快捷键 ==================

    private readonly Stack<TabItem> _recentlyClosed = new();

    private void RegisterKeyboardShortcuts()
    {
        // WinUI 3 的 Window 没有 KeyboardAccelerators 属性，注册到根 Grid（覆盖整个 Window）
        var host = RootGrid.KeyboardAccelerators;
        // Ctrl+T → 新建标签
        var kNew = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Key = VirtualKey.T,
            Modifiers = VirtualKeyModifiers.Control
        };
        kNew.Invoked += (_, __) => NewTabButton_Click(null, new RoutedEventArgs());
        host.Add(kNew);

        // Ctrl+W → 关闭当前
        var kClose = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Key = VirtualKey.W,
            Modifiers = VirtualKeyModifiers.Control
        };
        kClose.Invoked += (_, __) => CloseActiveTab();
        host.Add(kClose);

        // Ctrl+Tab / Ctrl+Shift+Tab → 切换
        var kNext = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Key = VirtualKey.Tab,
            Modifiers = VirtualKeyModifiers.Control
        };
        kNext.Invoked += (_, __) => SwitchTab(1);
        host.Add(kNext);

        var kPrev = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Key = VirtualKey.Tab,
            Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift
        };
        kPrev.Invoked += (_, __) => SwitchTab(-1);
        host.Add(kPrev);

        // F5 → 刷新
        var kF5 = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = VirtualKey.F5 };
        kF5.Invoked += (_, __) => RefreshButton_Click(null, new RoutedEventArgs());
        host.Add(kF5);

        // Ctrl+L / Alt+D → 聚焦地址栏
        var kAddr1 = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Key = VirtualKey.L,
            Modifiers = VirtualKeyModifiers.Control
        };
        kAddr1.Invoked += (_, __) => FocusAddressBar();
        host.Add(kAddr1);

        var kAddr2 = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Key = VirtualKey.D,
            Modifiers = VirtualKeyModifiers.Menu
        };
        kAddr2.Invoked += (_, __) => FocusAddressBar();
        host.Add(kAddr2);

        // Ctrl+F → 展开并聚焦搜索框
        var kSearch = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Key = VirtualKey.F,
            Modifiers = VirtualKeyModifiers.Control
        };
        kSearch.Invoked += (_, __) =>
        {
            SearchBox.Visibility = Visibility.Visible;
            SearchBox.Focus(FocusState.Programmatic);
        };
        host.Add(kSearch);
    }

    private void CloseActiveTab()
    {
        if (_activeTab == null) return;
        if (Tabs.Count > 1)
        {
            CloseTabButton_Click(this, new RoutedEventArgs());
        }
        else
        {
            Close();
        }
    }

    private void SwitchTab(int delta)
    {
        if (_activeTab == null || Tabs.Count <= 1) return;
        var idx = Tabs.IndexOf(_activeTab);
        var next = (idx + delta + Tabs.Count) % Tabs.Count;
        SelectTab(Tabs[next]);
    }

    private void FocusAddressBar()
    {
        EnterAddressEditMode();
    }

    private void EnterAddressEditMode()
    {
        if (_activeTab != null) AddressEdit.Text = _activeTab.Path;
        PathBreadcrumb.Visibility = Visibility.Collapsed;
        AddressEdit.Visibility = Visibility.Visible;
        AddressEdit.Focus(FocusState.Programmatic);
        AddressEdit.SelectAll();
    }

    private void ExitAddressEditMode(bool navigate)
    {
        AddressEdit.Visibility = Visibility.Collapsed;
        PathBreadcrumb.Visibility = Visibility.Visible;
        if (navigate)
        {
            var path = AddressEdit.Text?.Trim();
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                NavigateTo(path);
            }
        }
    }

    private void PathBreadcrumb_Tapped(object sender, TappedRoutedEventArgs e) => EnterAddressEditMode();
    private void PathBreadcrumb_RightTapped(object sender, RightTappedRoutedEventArgs e) => EnterAddressEditMode();

    private void AddressEdit_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            ExitAddressEditMode(navigate: true);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            ExitAddressEditMode(navigate: false);
        }
    }

    private void AddressEdit_LostFocus(object sender, RoutedEventArgs e)
    {
        if (AddressEdit.Visibility == Visibility.Visible)
            ExitAddressEditMode(navigate: false);
    }

    // ================== 标签右键菜单 ==================

    private void TabBorder_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TabItem tab }) return;
        SelectTab(tab);
        var menu = new MenuFlyout();
        var miClose = new MenuFlyoutItem { Text = "关闭标签页" };
        miClose.Click += (_, __) => CloseTabButton_Click(this, new RoutedEventArgs());
        menu.Items.Add(miClose);

        var miCloseOther = new MenuFlyoutItem { Text = "关闭其他标签页" };
        miCloseOther.Click += (_, __) =>
        {
            for (var i = Tabs.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(Tabs[i], tab))
                {
                    _recentlyClosed.Push(Tabs[i]);
                    Tabs.RemoveAt(i);
                }
            }
            SelectTab(tab);
        };
        menu.Items.Add(miCloseOther);

        var miCloseRight = new MenuFlyoutItem { Text = "关闭右侧标签页" };
        miCloseRight.Click += (_, __) =>
        {
            var idx = Tabs.IndexOf(tab);
            for (var i = Tabs.Count - 1; i > idx; i--)
            {
                _recentlyClosed.Push(Tabs[i]);
                Tabs.RemoveAt(i);
            }
        };
        menu.Items.Add(miCloseRight);

        menu.Items.Add(new MenuFlyoutSeparator());

        if (_recentlyClosed.Count > 0)
        {
            var last = _recentlyClosed.Peek();
            var miReopen = new MenuFlyoutItem { Text = $"重新打开: {last.Title}" };
            miReopen.Click += (_, __) =>
            {
                if (_recentlyClosed.Count == 0) return;
                var restored = _recentlyClosed.Pop();
                var newTab = new TabItem(restored.Title, restored.Path);
                // 插入到当前 tab 后面
                var curIdx = Tabs.IndexOf(_activeTab!);
                Tabs.Insert(curIdx + 1, newTab);
                SelectTab(newTab);
            };
            menu.Items.Add(miReopen);
        }

        e.Handled = true;
        menu.ShowAt((UIElement)sender, e.GetPosition((UIElement)sender));
    }

    // ================== 标签页核心 ==================

    private void CloseTabButton_Click(object sender, RoutedEventArgs e)
    {
        TabItem? tab = null;
        if (sender is FrameworkElement fe && fe.DataContext is TabItem t) tab = t;
        else tab = _activeTab;
        CloseTabCore(tab);
    }

    private void TabCloseX_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement self) return;
        // 向上找父级 TabBorder，取它的 DataContext
        var parent = VisualTreeHelper.GetParent(self);
        while (parent != null)
        {
            if (parent is FrameworkElement fe && fe.DataContext is TabItem ti)
            {
                CloseTabCore(ti);
                return;
            }
            parent = VisualTreeHelper.GetParent(parent);
        }
    }

    private void CloseTabCore(TabItem? tab)
    {
        if (tab == null) return;
        var idx = Tabs.IndexOf(tab);
        if (idx < 0) return;
        if (Tabs.Count <= 1) { Close(); return; }

        _recentlyClosed.Push(tab);

        // Play exit animation on the closing tab first
        FrameworkElement? container = null;
        var count = VisualTreeHelper.GetChildrenCount(TabsItemsControl);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(TabsItemsControl, i);
            if (child is FrameworkElement fe && fe.DataContext == tab) { container = fe; break; }
        }
        var border = container != null ? FindChild<Border>(container, "TabBorder") : null;

        Action doClose = () =>
        {
            Tabs.RemoveAt(idx);
            if (ReferenceEquals(_activeTab, tab))
            {
                SelectTab(Tabs[Math.Max(0, idx - 1)]);   // SelectTab 内会 ScheduleUpdateTabVisuals
            }
        };

        if (border != null) PlayTabExitAnimation(border, doClose);
        else doClose();
    }

    private void TabBorder_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border border) return;
        // Entry animation (only if still at initial state)
        if (border.Opacity < 0.95) PlayTabEntryAnimation(border);
        // Selected state visuals
        if (border.DataContext is TabItem tab)
        {
            var isActive = ReferenceEquals(tab, _activeTab);
            var res = Microsoft.UI.Xaml.Application.Current.Resources;
            border.Background = isActive ? (Brush)res["FsheSurfaceBrush"] : _transparentBrush;
            var indicator = FindChild<Border>(border, "TabIndicator");
            if (indicator != null) indicator.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void TabItemTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TabItem tab })
            SelectTab(tab);
    }

    private void TabCloseX_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not TextBlock tb) return;
        var res = Microsoft.UI.Xaml.Application.Current.Resources;
        tb.Foreground = (Brush)res["FsheTextBrush"];
    }

    private void TabCloseX_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not TextBlock tb) return;
        var res = Microsoft.UI.Xaml.Application.Current.Resources;
        tb.Foreground = (Brush)res["FsheTextSecondaryBrush"];
    }

    private void UpdateTabVisuals()
    {
        var res = Microsoft.UI.Xaml.Application.Current.Resources;
        var surfaceBrush = (Brush)res["FsheSurfaceBrush"];
        var count = VisualTreeHelper.GetChildrenCount(TabsItemsControl);
        foreach (var tab in Tabs)
        {
            FrameworkElement? container = null;
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(TabsItemsControl, i);
                if (child is FrameworkElement fe && fe.DataContext == tab) { container = fe; break; }
            }
            if (container == null) continue;
            var border = FindChild<Border>(container, "TabBorder");
            if (border == null) continue;
            var isActive = ReferenceEquals(tab, _activeTab);
            border.Background = isActive ? surfaceBrush : _transparentBrush;
            var indicator = FindChild<Border>(container, "TabIndicator");
            if (indicator != null) indicator.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static void PlayTabEntryAnimation(Border border)
    {
        var sb = new Storyboard();
        var opa = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(opa, border);
        Storyboard.SetTargetProperty(opa, "Opacity");
        sb.Children.Add(opa);
        var scaleX = new DoubleAnimation
        {
            From = 0.5,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(260),
            EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut }
        };
        var scaleY = new DoubleAnimation
        {
            From = 0.5,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(260),
            EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(scaleX, border);
        Storyboard.SetTarget(scaleY, border);
        Storyboard.SetTargetProperty(scaleX, "(Border.RenderTransform).(CompositeTransform.ScaleX)");
        Storyboard.SetTargetProperty(scaleY, "(Border.RenderTransform).(CompositeTransform.ScaleY)");
        sb.Children.Add(scaleX);
        sb.Children.Add(scaleY);
        sb.Begin();
    }

    private static void PlayTabExitAnimation(Border border, Action? onDone = null)
    {
        var sb = new Storyboard { FillBehavior = FillBehavior.Stop };
        var opa = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(opa, border);
        Storyboard.SetTargetProperty(opa, "Opacity");
        sb.Children.Add(opa);
        var scaleX = new DoubleAnimation
        {
            From = 1,
            To = 0.6,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        var scaleY = new DoubleAnimation
        {
            From = 1,
            To = 0.6,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(scaleX, border);
        Storyboard.SetTarget(scaleY, border);
        Storyboard.SetTargetProperty(scaleX, "(Border.RenderTransform).(CompositeTransform.ScaleX)");
        Storyboard.SetTargetProperty(scaleY, "(Border.RenderTransform).(CompositeTransform.ScaleY)");
        sb.Children.Add(scaleX);
        sb.Children.Add(scaleY);
        if (onDone != null) sb.Completed += (_, __) => onDone();
        sb.Begin();
    }

    // ================== 侧边栏 ==================

    private void RegisterNavButtons()
    {
        _navButtons.Clear();
        _navButtons.Add(HomeNav);
        _navButtons.Add(SettingsNav);
        foreach (var panel in new[] { PinnedPanel, DrivesPanel, NetworkPanel, TagsPanel })
        {
            foreach (var child in panel.Children.OfType<ToggleButton>())
                _navButtons.Add(child);
        }
    }

    private void BuildDrivesSection()
    {
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
                // 无卷标时用 Windows 中文习惯名（系统 C: / 本地磁盘）
                label = drive.DriveType == DriveType.Fixed
                    ? (letter == "C:" ? $"系统 ({letter})" : $"本地磁盘 ({letter})")
                    : letter;
            }
            else
            {
                label = $"{drive.VolumeLabel} ({letter})";
            }

            var btn = new ToggleButton
            {
                Style = (Style)Application.Current.Resources["NavItemStyle"],
                Margin = new Thickness(24, 0, 12, 0),
                Tag = drive.Name,
                Content = BuildDriveNavContent(label),
            };
            btn.Click += NavItem_Click;
            DrivesPanel.Children.Add(btn);
            _navButtons.Add(btn);
        }
    }

    private static object BuildDriveNavContent(string label)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var chevron = new FontIcon { Glyph = "\uE76C", FontSize = 8, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(chevron, 0);

        var icon = new FontIcon { Glyph = "\uE977", FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(icon, 1);

        var labelBlock = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(labelBlock, 2);

        grid.Children.Add(chevron);
        grid.Children.Add(icon);
        grid.Children.Add(labelBlock);
        return grid;
    }

    // ================== 侧边栏标签分区 ==================

    private readonly List<ToggleButton> _tagNavButtons = new();

    private void BuildTagsSection()
    {
        _tagNavButtons.Clear();
        TagsPanel.Children.Clear();
        foreach (var (name, color) in TagStore.AllTags())
        {
            var btn = new ToggleButton
            {
                Style = (Style)Application.Current.Resources["NavItemStyle"],
                Margin = new Thickness(24, 0, 12, 0),
                Tag = name,
                Content = BuildTagNavContent(name, color),
            };
            btn.Click += TagNav_Click;
            TagsPanel.Children.Add(btn);
            _navButtons.Add(btn);
            _tagNavButtons.Add(btn);
        }
    }

    private static object BuildTagNavContent(string name, string colorHex)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var diamond = new FontIcon
        {
            Glyph = "\uE8EC",
            FontSize = 13,
            Foreground = new SolidColorBrush(ConvertHex(colorHex)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(diamond, 0);

        var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);

        grid.Children.Add(diamond);
        grid.Children.Add(label);
        return grid;
    }

    private void TagNav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton btn) return;
        var checkedNow = btn.IsChecked == true;
        foreach (var nav in _navButtons)
        {
            if (!ReferenceEquals(nav, btn)) nav.IsChecked = false;
        }
        btn.IsChecked = checkedNow;
        GetPage()?.ApplyTagFilter(checkedNow ? btn.Tag as string : null);
    }

    private static Color ConvertHex(string hex)
    {
        // 兼容 "#RRGGBB"（TagStore 预设色）与 "#AARRGGBB"
        if (hex.Length == 7)
            return ColorHelper.FromArgb(255,
                byte.Parse(hex.Substring(1, 2), System.Globalization.NumberStyles.HexNumber),
                byte.Parse(hex.Substring(3, 2), System.Globalization.NumberStyles.HexNumber),
                byte.Parse(hex.Substring(5, 2), System.Globalization.NumberStyles.HexNumber));
        return ColorHelper.FromArgb(
            byte.Parse(hex.Substring(1, 2), System.Globalization.NumberStyles.HexNumber),
            byte.Parse(hex.Substring(3, 2), System.Globalization.NumberStyles.HexNumber),
            byte.Parse(hex.Substring(5, 2), System.Globalization.NumberStyles.HexNumber),
            byte.Parse(hex.Substring(7, 2), System.Globalization.NumberStyles.HexNumber));
    }

    private void InitSectionChevrons()
    {
        foreach (var chevron in new[] { PinnedChevron, DrivesChevron, NetworkChevron, TagsChevron })
        {
            chevron.RenderTransform = new RotateTransform { Angle = 0 };
            chevron.RenderTransformOrigin = new Point(0.5, 0.5);
        }
        // 网络分区默认收起
        NetworkPanel.Visibility = Visibility.Collapsed;
        if (NetworkChevron.RenderTransform is RotateTransform rt) rt.Angle = -90;
    }

    private void SectionHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string panelName }) return;
        FrameworkElement? panel = panelName switch
        {
            "PinnedPanel" => PinnedPanel,
            "DrivesPanel" => DrivesPanel,
            "NetworkPanel" => NetworkPanel,
            "TagsPanel" => TagsPanel,
            _ => null,
        };
        var chevron = panelName switch
        {
            "PinnedPanel" => PinnedChevron,
            "DrivesPanel" => DrivesChevron,
            "NetworkPanel" => NetworkChevron,
            "TagsPanel" => TagsChevron,
            _ => null,
        };
        if (panel == null || chevron == null) return;

        var collapse = panel.Visibility == Visibility.Visible;
        panel.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
        if (chevron.RenderTransform is RotateTransform rt)
            rt.Angle = collapse ? -90 : 0;
    }

    private void NavItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string tag } btn) return;
        if (tag == "Settings")
        {
            btn.IsChecked = false;
            _ = ShowSettingsAsync();
            return;
        }

        if (tag == "::RECYCLE")
        {
            // 回收站是 Shell 命名空间，不在文件系统内 —— 交给系统资源管理器打开
            btn.IsChecked = false;
            try
            {
                Process.Start(new ProcessStartInfo("shell:RecycleBinFolder") { UseShellExecute = true });
            }
            catch { /* 打开失败忽略 */ }
            return;
        }

        foreach (var nav in _navButtons)
        {
            if (!ReferenceEquals(nav, btn)) nav.IsChecked = false;
        }
        btn.IsChecked = true;

        var path = ResolveTag(tag);
        if (path != null) NavigateTo(path);
    }

    private void NetworkItem_Click(object sender, RoutedEventArgs e)
    {
        NavigateTo("::NETWORK", addToHistory: false);
    }

    private void UpdateNavSelection(string path)
    {
        var normalized = path.TrimEnd('\\').ToLowerInvariant();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var btn in _navButtons)
        {
            if (btn.Tag is not string tag) continue;
            var target = ResolveTag(tag);
            var match = target != null &&
                        string.Equals(target.TrimEnd('\\'), normalized, StringComparison.OrdinalIgnoreCase);
            // 用户目录同时命中"主页"和固定区"YU.GU"时，只点亮固定项，避免双高亮
            if (match && tag == "Home" && string.Equals(normalized, profile.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                match = false;
            btn.IsChecked = match;
        }
    }

    private static string? ResolveTag(string tag) => tag switch
    {
        "Home" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "::PROFILE" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Desktop" => Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        "Downloads" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        "Documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Pictures" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        "Music" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        "Videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        "Settings" => null,
        _ when Directory.Exists(tag) => Path.GetFullPath(tag),
        _ => null,
    };

    // ================== 导航 ==================

    private void NavigateTo(string path, bool addToHistory = true)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (path != "::NETWORK" && !Directory.Exists(path)) return;

        if (path != "::NETWORK") path = Path.GetFullPath(path);
        if (addToHistory && !string.IsNullOrEmpty(_currentPath) &&
            !string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase))
        {
            _backStack.Push(_currentPath);
            _forwardStack.Clear();
        }

        _currentPath = path;
        if (_activeTab != null)
        {
            _activeTab.Path = path;
            _activeTab.Title = new DirectoryInfo(path).Name;
            if (string.IsNullOrEmpty(_activeTab.Title)) _activeTab.Title = path;
        }

        UpdateBreadcrumb(path);
        UpdateNavigationButtons();
        UpdateNavSelection(path);

        ContentFrame.Navigate(typeof(FileExplorerPage), path);
        if (ContentFrame.Content is FileExplorerPage page)
        {
            page.NavigationRequested += OnPageNavigationRequested;
            page.CountChanged += OnPageCountChanged;
            page.SortChanged += OnPageSortChanged;
            page.ViewChanged += OnPageViewChanged;
            page.PreviewItemChanged += OnPagePreviewChanged;
            OnPageCountChanged(page, page.CurrentCount);
            UpdateSortUI(_sortKey, _sortAscending);
            SyncViewMenus(page.CurrentViewName);
            UpdatePreview(null);
        }
    }

    private void OnPageSortChanged(object? sender, (string Key, bool Ascending) a)
    {
        _sortKey = a.Key;
        _sortAscending = a.Ascending;
        UpdateSortUI(a.Key, a.Ascending);
    }

    private void OnPageViewChanged(object? sender, string name) => SyncViewMenus(name);

    private void OnPagePreviewChanged(object? sender, FileSystemItem? item) => UpdatePreview(item);

    private void UpdateSortUI(string key, bool ascending)
    {
        SortLetterText.Text = key switch
        {
            "Date" => "D",
            "Type" => "T",
            "Size" => "S",
            _ => "A",
        };
        SortArrowText.Text = ascending ? "\u2193" : "\u2191";
    }

    private void SyncViewMenus(string name)
    {
        ViewDetails.IsChecked = name == "详细信息";
        ViewList.IsChecked = name == "列表";
        ViewTiles.IsChecked = name == "大图标";
    }

    private void OnPageNavigationRequested(object? sender, string path) => NavigateTo(path);

    private void OnPageCountChanged(object? sender, int count)
    {
        CountText.Text = $"{count} 个项目";
        if (sender is FileExplorerPage page)
            TotalSizeText.Text = page.TotalSizeDisplay;
    }

    private void UpdateBreadcrumb(string path)
    {
        PathSegments.Clear();
        var root = Path.GetPathRoot(path) ?? string.Empty;

        // 根分区显示为 "Data (D:)" 形式
        if (root.Length > 0)
        {
            var letter = root.TrimEnd(Path.DirectorySeparatorChar);
            string label;
            try
            {
                var drive = new DriveInfo(root);
                label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? letter
                    : $"{drive.VolumeLabel} ({letter})";
            }
            catch { label = letter; }
            PathSegments.Add(label);
        }

        // 已知系统文件夹显示中文名
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [profile] = Path.GetFileName(profile.TrimEnd(Path.DirectorySeparatorChar)),
            [Environment.GetFolderPath(Environment.SpecialFolder.Desktop)] = "桌面",
            [Path.Combine(profile, "Downloads")] = "下载",
            [Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)] = "文档",
            [Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)] = "图片",
            [Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)] = "音乐",
            [Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)] = "视频",
        };

        var rest = path.Substring(root.Length);
        var acc = root;
        foreach (var part in rest.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            acc = Path.Combine(acc, part);
            PathSegments.Add(known.TryGetValue(acc, out var zh) ? zh : part);
        }
    }

    private string BuildPathFromSegments(int index)
    {
        var root = Path.GetPathRoot(_currentPath) ?? string.Empty;
        if (index <= 0) return root.Length > 0 ? root : _currentPath;
        var parts = _currentPath.Substring(root.Length)
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
        return Path.Combine(root, string.Join(Path.DirectorySeparatorChar, parts.Take(Math.Min(index, parts.Length))));
    }

    private void UpdateNavigationButtons()
    {
        BackButton.IsEnabled = _backStack.Count > 0;
        ForwardButton.IsEnabled = _forwardStack.Count > 0;
        UpButton.IsEnabled = !string.IsNullOrEmpty(_currentPath) &&
                             Path.GetDirectoryName(_currentPath.TrimEnd(Path.DirectorySeparatorChar)) != null;
    }

    private FileExplorerPage? GetPage() => ContentFrame.Content as FileExplorerPage;

    // ================== 工具栏事件 ==================

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_backStack.Count == 0) return;
        _forwardStack.Push(_currentPath);
        NavigateTo(_backStack.Pop(), addToHistory: false);
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (_forwardStack.Count == 0) return;
        _backStack.Push(_currentPath);
        NavigateTo(_forwardStack.Pop(), addToHistory: false);
    }

    private void UpButton_Click(object sender, RoutedEventArgs e)
    {
        var parent = Path.GetDirectoryName(_currentPath.TrimEnd(Path.DirectorySeparatorChar));
        if (parent != null) NavigateTo(parent);
    }

    private void HomeButton_Click(object sender, RoutedEventArgs e)
        => NavigateTo(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => GetPage()?.Refresh();

    private void PathBreadcrumb_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        var target = BuildPathFromSegments(args.Index);
        if (Directory.Exists(target)) NavigateTo(target);
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        => GetPage()?.ApplySearch(args.QueryText.Trim());

    // ================== 命令栏事件 ==================

    private void NewButton_Click(object sender, RoutedEventArgs e) { /* Flyout 自动弹出 */ }

    private void NewFolderItem_Click(object sender, RoutedEventArgs e) => _ = GetPage()?.NewFolderAsync();

    private void NewFileItem_Click(object sender, RoutedEventArgs e) => _ = GetPage()?.NewFileAsync();

    private void CutButton_Click(object sender, RoutedEventArgs e) => GetPage()?.CutSelected();

    private void CopyButton_Click(object sender, RoutedEventArgs e) => GetPage()?.CopySelected();

    private void PasteButton_Click(object sender, RoutedEventArgs e) => _ = GetPage()?.PasteAsync();

    private void RenameButton_Click(object sender, RoutedEventArgs e) => _ = GetPage()?.RenameSelectedAsync();

    private void DeleteButton_Click(object sender, RoutedEventArgs e) => _ = GetPage()?.DeleteSelectedAsync();

    private void SortButton_Click(object sender, RoutedEventArgs e) { /* Flyout 自动弹出 */ }

    private void SortOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem item) return;
        _sortKey = item.Text switch
        {
            "修改日期" => "Date",
            "类型" => "Type",
            "大小" => "Size",
            _ => "Name",
        };
        GetPage()?.SetSort(_sortKey, _sortAscending);
    }

    private void SortDirectionButton_Click(object sender, RoutedEventArgs e)
    {
        _sortAscending = !_sortAscending;
        GetPage()?.SetSort(_sortKey, _sortAscending);
    }

    private void ViewOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem item) return;
        GetPage()?.SetView(item.Text);
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs e)
    {
        // 清空搜索框时恢复完整列表
        if (e.Reason == AutoSuggestionBoxTextChangeReason.UserInput &&
            string.IsNullOrWhiteSpace(sender.Text))
        {
            GetPage()?.ApplySearch(string.Empty);
        }
    }

    private void SearchToggle_Click(object sender, RoutedEventArgs e)
    {
        if (SearchBox.Visibility == Visibility.Visible)
        {
            CollapseSearchBox();
        }
        else
        {
            SearchBox.Visibility = Visibility.Visible;
            SearchBox.Focus(FocusState.Programmatic);
        }
    }

    private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SearchBox.Text)) CollapseSearchBox();
    }

    private void CollapseSearchBox()
    {
        SearchBox.Visibility = Visibility.Collapsed;
        if (GetPage() != null) GetPage()!.ApplySearch(string.Empty);
    }

    private void ToggleHiddenItem_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.ShowHiddenItems = !AppSettings.ShowHiddenItems;
        HiddenItemIcon.Foreground = new SolidColorBrush(
            AppSettings.ShowHiddenItems
                ? (Color)Application.Current.Resources["FsheAccentColor"]
                : Colors.Transparent);
        GetPage()?.Refresh();
    }

    // ================== 预览窗格 ==================

    private void PreviewToggle_Click(object sender, RoutedEventArgs e)
    {
        _previewOpen = !_previewOpen;
        PreviewPane.Visibility = _previewOpen ? Visibility.Visible : Visibility.Collapsed;
        PreviewPaneButton.Foreground = new SolidColorBrush(
            (Color)Application.Current.Resources[_previewOpen ? "FsheAccentColor" : "FsheTextColor"]);
    }

    private void UpdatePreview(FileSystemItem? item)
    {
        if (item == null)
        {
            PreviewIcon.Source = null;
            PreviewName.Text = "未选择项目";
            PreviewTag.Text = string.Empty;
            PreviewType.Text = string.Empty;
            PreviewDate.Text = string.Empty;
            PreviewSize.Text = string.Empty;
            PreviewPath.Text = string.Empty;
            _previewSizeToken++;
            return;
        }

        PreviewIcon.Source = item.IconSource;
        PreviewName.Text = item.Name;
        PreviewTag.Text = item.FileTag;
        PreviewTag.Foreground = TagBrushForText(item.FileTag);
        PreviewType.Text = $"类型：{item.TypeDisplay}";
        PreviewDate.Text = $"修改日期：{item.DateModified}";
        PreviewPath.Text = $"位置：{Path.GetDirectoryName(item.FullPath.TrimEnd(Path.DirectorySeparatorChar))}";

        if (item.IsDirectory)
        {
            PreviewSize.Text = "大小：计算中…";
            var token = ++_previewSizeToken;
            var root = item.FullPath;
            _ = Task.Run(() => CalcFolder(root)).ContinueWith(t =>
            {
                if (token != _previewSizeToken) return;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (token == _previewSizeToken && !t.IsFaulted)
                        PreviewSize.Text = $"大小：{FileSystemItem.FormatBytes(t.Result.Size)}（{t.Result.Files} 个文件）";
                });
            });
        }
        else
        {
            PreviewSize.Text = $"大小：{item.SizeDisplay}";
        }
    }

    private static Brush TagBrushForText(string tag)
    {
        var hex = TagStore.PresetColors.TryGetValue(tag, out var h) ? h : "#9B9B9B";
        return new SolidColorBrush(ColorHelper.FromArgb(255,
            byte.Parse(hex.Substring(1, 2), System.Globalization.NumberStyles.HexNumber),
            byte.Parse(hex.Substring(3, 2), System.Globalization.NumberStyles.HexNumber),
            byte.Parse(hex.Substring(5, 2), System.Globalization.NumberStyles.HexNumber)));
    }

    /// <summary>递归统计文件夹总大小与文件数（实现见 FileSystemItem.CalcFolderSize）。</summary>
    private static (long Size, int Files) CalcFolder(string root) => FileSystemItem.CalcFolderSize(root);

    // ================== 标签 ==================

    private void TagButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement btn) return;

        // 注意：ToggleButton.Flyout 属性元素会触发 XamlCompiler Pass1 静默崩溃，
        // 因此标签菜单改为代码构建后 ShowAt 弹出。
        var menu = new MenuFlyout();
        foreach (var (label, color) in TagStore.AllTags())
        {
            var item = new MenuFlyoutItem
            {
                Text = label,
                Tag = label,
                Icon = new FontIcon
                {
                    Glyph = "\uE8EC",
                    Foreground = new SolidColorBrush(ConvertHex(color)),
                },
            };
            item.Click += TagMenuItem_Click;
            menu.Items.Add(item);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var clear = new MenuFlyoutItem { Text = "清除标签", Tag = string.Empty };
        clear.Click += TagMenuItem_Click;
        menu.Items.Add(clear);

        menu.ShowAt(btn, new Point(0, btn.ActualHeight));
    }

    private void TagMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string tag }) return;
        GetPage()?.SetTagForSelected(tag.Length == 0 ? null : tag);
        if (_previewOpen)
        {
            var selected = GetPage()?.GetSelected();
            if (selected is { Count: > 0 }) UpdatePreview(selected[0]);
        }
    }

    // ================== 设置 ==================

    private async Task ShowSettingsAsync()
    {
        var hiddenSwitch = new ToggleSwitch
        {
            Header = "显示隐藏的文件、文件夹和驱动器",
            IsOn = AppSettings.ShowHiddenItems,
        };
        hiddenSwitch.Toggled += (_, _) =>
        {
            AppSettings.ShowHiddenItems = hiddenSwitch.IsOn;
            HiddenItemIcon.Foreground = new SolidColorBrush(
                AppSettings.ShowHiddenItems
                    ? (Color)Application.Current.Resources["FsheAccentColor"]
                    : Colors.Transparent);
            GetPage()?.Refresh();
        };

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "设置",
            Content = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    hiddenSwitch,
                    new Microsoft.UI.Xaml.Shapes.Rectangle { Height = 1, Fill = (Brush)Application.Current.Resources["FsheStrokeBrush"] },
                    new TextBlock { Text = "关于", Opacity = 0.95 },
                    new TextBlock
                    {
                        Opacity = 0.7,
                        Text = "FSHE · WinUI 3 文件资源管理器\n标签数据保存在 %LocalAppData%\\FSHE\\tags.json",
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
            CloseButtonText = "关闭",
        };
        await dialog.ShowAsync();
    }

    // ================== 工具 ==================

    private static T? FindChild<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed && typed.Name == name) return typed;
            var result = FindChild<T>(child, name);
            if (result != null) return result;
        }
        return null;
    }

    private static T? FindFirstChild<T>(DependencyObject parent) where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;
            var result = FindFirstChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }
}
