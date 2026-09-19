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
using Windows.ApplicationModel.DataTransfer;
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
    private bool _navCollapsed;
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
        HideMissingPins();
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
        foreach (var panel in new[] { PinnedPanel, DrivesPanel, NetworkPanel })
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

            var label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                ? drive.Name.TrimEnd('\\')
                : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})";

            var btn = new ToggleButton
            {
                Style = (Style)Application.Current.Resources["NavItemStyle"],
                Margin = new Thickness(24, 0, 12, 0),
                Tag = drive.Name,
                Content = BuildNavContent("\uE977", label, null),
            };
            btn.Click += NavItem_Click;
            DrivesPanel.Children.Add(btn);
            _navButtons.Add(btn);
        }
    }

    private void HideMissingPins()
    {
        DriveDNav.Visibility = Directory.Exists(@"D:\") ? Visibility.Visible : Visibility.Collapsed;
        SoftwareNav.Visibility = Directory.Exists(@"D:\software") ? Visibility.Visible : Visibility.Collapsed;
    }

    private static object BuildNavContent(string glyph, string text, string? colorHex)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new FontIcon { Glyph = glyph, FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
        if (colorHex != null) icon.Foreground = new SolidColorBrush(ConvertHex(colorHex));
        Grid.SetColumn(icon, 0);

        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);

        grid.Children.Add(icon);
        grid.Children.Add(label);
        return grid;
    }

    private static Color ConvertHex(string hex)
        => ColorHelper.FromArgb(
            byte.Parse(hex.Substring(1, 2), System.Globalization.NumberStyles.HexNumber),
            byte.Parse(hex.Substring(3, 2), System.Globalization.NumberStyles.HexNumber),
            byte.Parse(hex.Substring(5, 2), System.Globalization.NumberStyles.HexNumber),
            byte.Parse(hex.Substring(7, 2), System.Globalization.NumberStyles.HexNumber));

    private void InitSectionChevrons()
    {
        foreach (var chevron in new[] { PinnedChevron, DrivesChevron, NetworkChevron })
        {
            chevron.RenderTransform = new RotateTransform { Angle = 0 };
            chevron.RenderTransformOrigin = new Point(0.5, 0.5);
        }
    }

    private void SectionHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string panelName }) return;
        FrameworkElement? panel = panelName switch
        {
            "PinnedPanel" => PinnedPanel,
            "DrivesPanel" => DrivesPanel,
            "NetworkPanel" => NetworkPanel,
            _ => null,
        };
        var chevron = panelName switch
        {
            "PinnedPanel" => PinnedChevron,
            "DrivesPanel" => DrivesChevron,
            "NetworkPanel" => NetworkChevron,
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

    private void HamburgerButton_Click(object sender, RoutedEventArgs e)
    {
        _navCollapsed = !_navCollapsed;
        NavColumn.Width = new GridLength(_navCollapsed ? 0 : 312);
        SidebarRoot.Visibility = _navCollapsed ? Visibility.Collapsed : Visibility.Visible;
        CommandBarGrid.Margin = new Thickness(_navCollapsed ? 12 : 312, 0, 12, 0);
    }

    private void UpdateNavSelection(string path)
    {
        var normalized = path.TrimEnd('\\').ToLowerInvariant();
        foreach (var btn in _navButtons)
        {
            if (btn.Tag is not string tag) continue;
            var target = ResolveTag(tag);
            btn.IsChecked = target != null &&
                            string.Equals(target.TrimEnd('\\'), normalized, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string? ResolveTag(string tag) => tag switch
    {
        "Home" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
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
        LayoutDetails.IsChecked = name == "详细信息";
        LayoutList.IsChecked = name == "列表";
        LayoutTiles.IsChecked = name == "大图标";
    }

    private void OnPageNavigationRequested(object? sender, string path) => NavigateTo(path);

    private void OnPageCountChanged(object? sender, int count)
        => CountText.Text = $"{count} 个项目";

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

    private void OpenButton_Click(object sender, RoutedEventArgs e) => GetPage()?.OpenSelection();

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

    private void CycleViewButton_Click(object sender, RoutedEventArgs e) => GetPage()?.CycleView();

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs e)
    {
        // 清空搜索框时恢复完整列表
        if (e.Reason == AutoSuggestionBoxTextChangeReason.UserInput &&
            string.IsNullOrWhiteSpace(sender.Text))
        {
            GetPage()?.ApplySearch(string.Empty);
        }
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
        var brush = new SolidColorBrush(
            (Color)Application.Current.Resources[_previewOpen ? "FsheAccentColor" : "FsheStrokeColor"]);
        PreviewPaneButton.BorderBrush = brush;
        PaneToggleButton.Foreground = new SolidColorBrush(
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

    // ================== 标签 ==================

    private void TagsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton btn) return;
        btn.IsChecked = false;

        // 注意：ToggleButton.Flyout 属性元素会触发 XamlCompiler Pass1 静默崩溃，
        // 因此标签菜单改为代码构建后 ShowAt 弹出。
        var menu = new MenuFlyout();
        foreach (var (label, color) in new[]
                 {
                     ("重要", "#FF6B6B"),
                     ("工作", "#4CC2FF"),
                     ("个人", "#54D18B"),
                     ("收藏", "#FFC83D"),
                 })
        {
            var item = new MenuFlyoutItem
            {
                Text = label,
                Tag = label,
                Icon = new FontIcon
                {
                    Glyph = "\u25CF",
                    Foreground = new SolidColorBrush(ColorHelper.FromArgb(255,
                        byte.Parse(color.Substring(1, 2), System.Globalization.NumberStyles.HexNumber),
                        byte.Parse(color.Substring(3, 2), System.Globalization.NumberStyles.HexNumber),
                        byte.Parse(color.Substring(5, 2), System.Globalization.NumberStyles.HexNumber))),
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

    // ================== 共享 ==================

    private async void ShareButton_Click(object sender, RoutedEventArgs e)
    {
        var page = GetPage();
        if (page == null) return;
        var items = page.GetSelected();
        if (items.Count == 0)
        {
            await ShowMessageDialogAsync("共享", "请先勾选要共享的项目。");
            return;
        }

        var paths = items.Select(i => i.FullPath).ToList();
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "共享 / 发送",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        TextWrapping = TextWrapping.WrapWholeWords,
                        Text = $"已选择 {paths.Count} 个项目，可复制完整路径，或在系统文件资源管理器中定位。",
                    },
                    new TextBlock
                    {
                        Opacity = 0.6,
                        FontSize = 12,
                        TextWrapping = TextWrapping.WrapWholeWords,
                        Text = string.Join("\n", paths),
                    },
                },
            },
            PrimaryButtonText = "复制路径",
            SecondaryButtonText = "在资源管理器中显示",
            CloseButtonText = "取消",
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var package = new DataPackage();
            package.SetText(string.Join("\n", paths));
            Clipboard.SetContent(package);
        }
        else if (result == ContentDialogResult.Secondary)
        {
            try
            {
                var target = paths[0];
                if (Directory.Exists(target))
                    Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
                else
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
            }
            catch { /* 忽略定位失败 */ }
        }
    }

    // ================== 属性 ==================

    private async void PropertiesButton_Click(object sender, RoutedEventArgs e)
    {
        var page = GetPage();
        if (page == null) return;
        var items = page.GetSelected();

        string title;
        string typeLine;
        string locationLine;
        string sizeLine;
        string timeLines;
        string attrLine;
        string tagLine;

        if (items.Count == 0)
        {
            // 无勾选时显示当前文件夹属性
            var dir = _currentPath;
            if (!Directory.Exists(dir)) return;
            var info = new DirectoryInfo(dir);
            title = info.Name;
            typeLine = "类型：文件夹";
            locationLine = $"位置：{info.Parent?.FullName}";
            sizeLine = "大小：计算中…";
            timeLines = $"创建时间：{info.CreationTime:yyyy/MM/dd HH:mm:ss}\n" +
                        $"修改时间：{info.LastWriteTime:yyyy/MM/dd HH:mm:ss}";
            attrLine = "属性：" + DescribeAttributes(FileAttributes.Normal);
            tagLine = "标签：（无）";
            await ShowPropertiesDialogAsync(title, typeLine, locationLine, sizeLine, timeLines, attrLine, tagLine,
                computeSize: () =>
                {
                    var (size, count) = CalcFolder(dir);
                    return $"大小：{FileSystemItem.FormatBytes(size)}（{count} 个文件）";
                });
            return;
        }

        if (items.Count > 1)
        {
            var total = items.Where(i => !i.IsDirectory).Sum(i => SafeFileLength(i.FullPath));
            title = $"{items.Count} 个项目";
            typeLine = "类型：多个项目";
            locationLine = $"位置：{_currentPath}";
            sizeLine = $"大小：{FileSystemItem.FormatBytes(total)}";
            timeLines = string.Empty;
            attrLine = string.Empty;
            tagLine = string.Empty;
            await ShowPropertiesDialogAsync(title, typeLine, locationLine, sizeLine, timeLines, attrLine, tagLine);
            return;
        }

        var item = items[0];
        title = item.Name;
        typeLine = $"类型：{item.TypeDisplay}";
        locationLine = $"位置：{Path.GetDirectoryName(item.FullPath)}";
        tagLine = $"标签：{(string.IsNullOrEmpty(item.FileTag) ? "（无）" : item.FileTag)}";

        if (item.IsDirectory)
        {
            var info = new DirectoryInfo(item.FullPath);
            sizeLine = "大小：计算中…";
            timeLines = $"创建时间：{info.CreationTime:yyyy/MM/dd HH:mm:ss}\n" +
                        $"修改时间：{info.LastWriteTime:yyyy/MM/dd HH:mm:ss}";
            attrLine = "属性：" + DescribeAttributes(info.Attributes);
            var folderPath = item.FullPath;
            await ShowPropertiesDialogAsync(title, typeLine, locationLine, sizeLine, timeLines, attrLine, tagLine,
                computeSize: () =>
                {
                    var (size, count) = CalcFolder(folderPath);
                    return $"大小：{FileSystemItem.FormatBytes(size)}（{count} 个文件）";
                });
        }
        else
        {
            var info = new FileInfo(item.FullPath);
            sizeLine = $"大小：{FileSystemItem.FormatBytes(info.Length)}（{info.Length:N0} 字节）";
            timeLines = $"创建时间：{info.CreationTime:yyyy/MM/dd HH:mm:ss}\n" +
                        $"修改时间：{info.LastWriteTime:yyyy/MM/dd HH:mm:ss}\n" +
                        $"访问时间：{info.LastAccessTime:yyyy/MM/dd HH:mm:ss}";
            attrLine = "属性：" + DescribeAttributes(info.Attributes);
            await ShowPropertiesDialogAsync(title, typeLine, locationLine, sizeLine, timeLines, attrLine, tagLine);
        }
    }

    private async Task ShowPropertiesDialogAsync(string title, string typeLine, string locationLine,
        string sizeLine, string timeLines, string attrLine, string tagLine, Func<string>? computeSize = null)
    {
        var sizeBlock = new TextBlock { Text = sizeLine, TextWrapping = TextWrapping.WrapWholeWords };
        var panel = new StackPanel { Spacing = 8 };
        void AddLine(string text)
        {
            if (!string.IsNullOrEmpty(text))
                panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.WrapWholeWords });
        }
        AddLine(typeLine);
        AddLine(locationLine);
        panel.Children.Add(sizeBlock);
        AddLine(timeLines);
        AddLine(attrLine);
        AddLine(tagLine);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = panel,
            CloseButtonText = "确定",
        };
        var shown = dialog.ShowAsync();

        if (computeSize != null)
        {
            var text = await Task.Run(computeSize);
            sizeBlock.Text = text;
        }
        await shown;
    }

    private static long SafeFileLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    /// <summary>递归统计文件夹总大小与文件数（实现见 FileSystemItem.CalcFolderSize）。</summary>
    private static (long Size, int Files) CalcFolder(string root) => FileSystemItem.CalcFolderSize(root);

    private static string DescribeAttributes(FileAttributes attr)
    {
        var parts = new List<string>();
        if (attr.HasFlag(FileAttributes.ReadOnly)) parts.Add("只读");
        if (attr.HasFlag(FileAttributes.Hidden)) parts.Add("隐藏");
        if (attr.HasFlag(FileAttributes.System)) parts.Add("系统");
        if (attr.HasFlag(FileAttributes.Archive)) parts.Add("存档");
        return parts.Count == 0 ? "无" : string.Join("、", parts);
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

    private async Task ShowMessageDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "确定",
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
