using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace DshSessionHud;

public sealed partial class MainWindow : Window
{
    private readonly AppOptions _options = App.Options;
    private readonly HudViewModel _vm = new();
    private readonly WindowPlacement _placement;

    private readonly HudStateReader _reader;
    private readonly DispatcherQueueTimer _ticker;

    private AppWindow? _appWindow;
    private IntPtr _hwnd;
    private OverlappedPresenter? _presenter;
    private SystemTheme.Mode _themeMode;
    private long _lastSnapshotAt;
    private long _staleSince;

    // 视图模式（列表 / 格子）相关的布局与模板
    private readonly StackLayout _listLayout = new() { Spacing = 0 };
    private readonly UniformGridLayout _gridLayout = new();
    private DataTemplate? _listTemplate;
    private DataTemplate? _gridTemplate;
    private bool _gridMode;

    public MainWindow()
    {
        InitializeComponent();
        Title = "DSH 会话 HUD";

        _placement = WindowPlacement.Load();
        _themeMode = _placement.Theme switch
        {
            "Light" => SystemTheme.Mode.Light,
            "Dark" => SystemTheme.Mode.Dark,
            _ => SystemTheme.Mode.System,
        };

        RootGrid.DataContext = _vm;

        // ---- 随窗口大小自适应 ----
        // 由 RootGrid 的 SizeChanged 驱动尺寸分层：内容区一变，
        // 字号、留白、按键大小、列宽、显示哪些区块一起重算。
        RootGrid.SizeChanged += (_, e) =>
        {
            _vm.Metrics.Apply(e.NewSize.Width, e.NewSize.Height);
            UpdateTitleBarReserve();
            // 分层可能变了 → 磁贴的最小尺寸要跟着更新。
            ApplyViewMode();
        };
        // 先把当前尺寸喂一次：首次布局前 SizeChanged 还没触发，
        // 否则窗口会以 Normal 档画一帧再跳到正确档位。
        _vm.Metrics.Apply(_placement.Width, _placement.Height);

        // ---- 窗口与 Presenter ----
        var hwnd = WindowNative.GetWindowHandle(this);
        _hwnd = hwnd;
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        _presenter = _appWindow.Presenter as OverlappedPresenter;
        if (_presenter is not null)
        {
            _presenter.IsResizable = true;
            _presenter.IsMaximizable = true;
            _presenter.IsMinimizable = true;
            _presenter.IsAlwaysOnTop = _placement.Topmost;
            // PreferredMinimum* 也是 DIP，和分层阈值、持久化单位保持一致。
            _presenter.PreferredMinimumWidth = (int)MinWidthDip;
            _presenter.PreferredMinimumHeight = (int)MinHeightDip;
        }

        ApplyPlacement();
        ApplyWindowIcon();

        // 整条标题栏都能拖动窗口；窗口边缘照常拉伸缩放。
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);
        UpdateTitleBarReserve();

        // ---- 控件初始状态 ----
        TopmostToggle.IsChecked = _placement.Topmost;
        CompactToggle.IsChecked = _placement.Compact;
        ApplyCompact(_placement.Compact);

        // 视图模式：内联的详细卡片模板就是列表模板，磁贴模板从资源里取。
        _listTemplate = SessionsRepeater.ItemTemplate as DataTemplate;
        _gridTemplate = RootGrid.Resources["GridTileTemplate"] as DataTemplate;
        _gridMode = string.Equals(_placement.ViewMode, "Grid", StringComparison.OrdinalIgnoreCase);
        ApplyViewMode();

        ApplyTheme();

        TopmostToggle.Checked += (_, _) => SetTopmost(true);
        TopmostToggle.Unchecked += (_, _) => SetTopmost(false);
        CompactToggle.Checked += (_, _) => ApplyCompact(true);
        CompactToggle.Unchecked += (_, _) => ApplyCompact(false);
        ViewModeButton.Click += (_, _) => CycleViewMode();
        ApplyPersistedSettings();
        SettingsFlyout.Opening += (_, _) => SyncSettingsControls();

        SystemTheme.SystemThemeChanged += OnSystemThemeChanged;

        _appWindow.Changed += OnAppWindowChanged;
        Closed += OnClosed;

        SingleInstance.StartListening(hwnd);

        // ---- 状态文件读取 + 秒表刷新 ----
        _lastSnapshotAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        _reader = new HudStateReader(_options.StatePath);
        _reader.SnapshotRead += OnSnapshotRead;
        _reader.ReadFailed += OnReadFailed;

        _ticker = DispatcherQueue.CreateTimer();
        _ticker.Interval = TimeSpan.FromMilliseconds(250);
        _ticker.Tick += (_, _) => OnTick();
        _ticker.Start();
    }

    // ------------------------------------------------------------------
    // 位置与大小
    // ------------------------------------------------------------------

    /// <summary>
    /// 实测系统标题栏按钮占用的宽度，让我们的三个按键正好贴在它左边。
    ///
    /// 不能直接信 AppWindow.TitleBar.RightInset：在 Windows 10 上
    /// AppWindowTitleBar.IsCustomizationSupported() 为 false，RightInset 实测返回
    /// 约 320 物理像素，而实际三个标题栏按钮只占约 174 物理像素（125% 缩放下），
    /// 照它留白会在按键和系统按钮之间留出上百像素的空洞。
    /// 所以 Win11 用 RightInset，Win10 用系统度量自己算：3 × SM_CXSIZE。
    /// </summary>
    private void UpdateTitleBarReserve()
    {
        double reserve = MeasureCaptionReserve();

        // 再兜一层：不管谁报了什么数，保留区都不该超过窗口的一半，
        // 否则窄窗口下按键会被挤出可视范围。
        double max = Math.Max(90, RootGrid.ActualWidth * 0.45);
        _vm.Metrics.TitleBarReserve = Math.Clamp(reserve, 90, max);
    }

    private double MeasureCaptionReserve()
    {
        const double Fallback = 138;
        try
        {
            if (_appWindow is null) return Fallback;

            // Windows 11：系统会准确告诉我们它占了多少。
            if (AppWindowTitleBar.IsCustomizationSupported())
            {
                double inset = _appWindow.TitleBar.RightInset;
                double scale = Scale;
                if (inset > 0 && scale > 0) return inset / scale;
            }

            // Windows 10：自己按系统度量算。
            uint dpi = GetDpiForWindow(_hwnd);
            if (dpi == 0) dpi = 96;
            int captionButtonWidth = GetSystemMetricsForDpi(SM_CXSIZE, dpi);
            if (captionButtonWidth <= 0) return Fallback;

            // GetSystemMetricsForDpi 给的是物理像素，XAML 布局用有效像素（DIP）。
            // +8 是安全余量：宁可留一点缝，也不要贴到系统按钮的可点区域上。
            double dipPerPixel = 96.0 / dpi;
            return 3.0 * captionButtonWidth * dipPerPixel + 8;
        }
        catch
        {
            // 老系统上 GetSystemMetricsForDpi 可能不存在，退回兜底值。
            return Fallback;
        }
    }

    private const int SM_CXSIZE = 30;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>
    /// 当前窗口的缩放比例（物理像素 / 有效像素）。
    ///
    /// 这里必须分清楚两套单位，之前混用导致了一个真实 bug：
    ///   * AppWindow.Position / Size / MoveAndResize / DisplayArea.WorkArea → **物理像素**
    ///   * RootGrid.SizeChanged / ActualWidth / 所有 XAML 尺寸 → **有效像素（DIP）**
    /// 125% 缩放下，一个 470 物理像素宽的窗口，内容区只有约 363 DIP，
    /// 按「物理像素」去判断尺寸分层就会误判成窄窗口。
    /// 所以持久化和分层判断统一用 DIP，只在真正调窗口 API 时换算成物理像素。
    /// </summary>
    private double Scale
    {
        get
        {
            // 优先问 Win32：GetDpiForWindow 在 HWND 一建好就能用，
            // 而构造函数里 RootGrid.XamlRoot 还是 null（元素尚未挂进可视树），
            // 那时去读 RasterizationScale 会拿到 1.0，DIP→物理像素就白换算。
            try
            {
                uint dpi = GetDpiForWindow(_hwnd);
                if (dpi > 0) return dpi / 96.0;
            }
            catch
            {
                // 退回到 XAML 侧的比例。
            }

            double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
            return scale > 0 ? scale : 1.0;
        }
    }

    private void ApplyPlacement()
    {
        if (_appWindow is null) return;

        double scale = Scale;
        var area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
        var work = area.WorkArea; // 物理像素

        // 存储值是 DIP，先换算成物理像素再参与工作区裁剪。
        int minW = (int)Math.Round(MinWidthDip * scale);
        int minH = (int)Math.Round(MinHeightDip * scale);
        int width = (int)Math.Round(_placement.Width * scale);
        int height = (int)Math.Round(_placement.Height * scale);
        width = Math.Clamp(width, minW, Math.Max(minW, work.Width));
        height = Math.Clamp(height, minH, Math.Max(minH, work.Height));

        int x, y;
        int px = (int)Math.Round(_placement.X * scale);
        int py = (int)Math.Round(_placement.Y * scale);
        if (_placement.X < 0 || _placement.Y < 0 || !IsOnSomeDisplay(px, py))
        {
            // 首次运行（或上次的位置已经不在任何显示器上）：贴在主屏右上角。
            x = work.X + work.Width - width - 24;
            y = work.Y + 24;
        }
        else
        {
            x = px;
            y = py;
        }

        // 保证标题栏一定在可见区域内，否则窗口就拖不回来了。
        x = Math.Clamp(x, work.X - width + 120, work.X + work.Width - 120);
        y = Math.Clamp(y, work.Y, work.Y + work.Height - 48);

        _appWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    /// <summary>窗口最小尺寸（DIP）。要允许用户真正走到 Narrow 档。</summary>
    private const double MinWidthDip = 300;
    private const double MinHeightDip = 200;

    /// <summary>
    /// 再显式设一次窗口图标。
    ///
    /// exe 里已经通过 csproj 的 &lt;ApplicationIcon&gt; 嵌了图标，但非打包的 WinUI 3 应用
    /// 在任务栏 / Alt+Tab 上有几率回落到默认图标；这里用 AppWindow.SetIcon 兜一层。
    /// ico 就在 exe 旁边（csproj 里 CopyToOutputDirectory 带出来的）。
    /// </summary>
    private void ApplyWindowIcon()
    {
        try
        {
            if (_appWindow is null) return;
            string iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (System.IO.File.Exists(iconPath)) _appWindow.SetIcon(iconPath);
        }
        catch
        {
            // 设不上图标不该影响窗口开出来。
        }
    }

    private static bool IsOnSomeDisplay(int x, int y)
    {
        try
        {
            var area = DisplayArea.GetFromPoint(new PointInt32(x + 40, y + 20), DisplayAreaFallback.None);
            return area is not null;
        }
        catch
        {
            return false;
        }
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPositionChange && !args.DidSizeChange) return;

        // 窗口 API 给的是物理像素，存成 DIP：这样换到不同 DPI 的显示器后，
        // 下次打开还是同样「看起来一样大」。
        double scale = Scale;
        var pos = sender.Position;
        var size = sender.Size;
        _placement.X = (int)Math.Round(pos.X / scale);
        _placement.Y = (int)Math.Round(pos.Y / scale);
        _placement.Width = (int)Math.Round(size.Width / scale);
        _placement.Height = (int)Math.Round(size.Height / scale);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        try
        {
            if (_presenter is not null) _placement.Topmost = _presenter.IsAlwaysOnTop;
            _placement.Compact = CompactToggle.IsChecked == true;
            _placement.Theme = _themeMode.ToString();
            _placement.ViewMode = _gridMode ? "Grid" : "List";
            _placement.Save();
        }
        catch
        {
            // ignore
        }

        SystemTheme.SystemThemeChanged -= OnSystemThemeChanged;
        _ticker.Stop();
        _reader.Dispose();
    }

    private void SetTopmost(bool topmost)
    {
        if (_presenter is not null) _presenter.IsAlwaysOnTop = topmost;
        _placement.Topmost = topmost;
    }

    private void ApplyCompact(bool compact)
    {
        // 精简模式：列表里把「最近活动」折叠掉；格子模式下改去控制磁贴高度与那行摘要。
        _vm.Compact = compact;
        // 磁贴最小高度随之变化，重新喂一次布局。
        if (_gridMode) ApplyViewMode();
    }

    // ------------------------------------------------------------------
    // 视图模式：列表 / 格子
    // ------------------------------------------------------------------

    private void CycleViewMode()
    {
        _gridMode = !_gridMode;
        _placement.ViewMode = _gridMode ? "Grid" : "List";
        ApplyViewMode();
    }

    /// <summary>
    /// 切换「列表」与「格子」两种排布。
    ///
    /// 用 ItemsRepeater 而不是自己算列数：Layout 和 ItemTemplate 都是普通属性
    /// （不像 ItemsPanelTemplate 那样藏在模板里），所以能在代码里直接换。
    /// 格子用 UniformGridLayout —— 它按可用宽度决定一行放几个（每个不小于 MinItemWidth），
    /// 再拉伸铺满，列数不用自己算；顺带也不会碰到「面板内部绑不到 DataContext」的问题。
    /// </summary>
    private void ApplyViewMode()
    {
        var metrics = _vm.Metrics;

        _gridLayout.MinItemWidth = metrics.TileMinWidth;
        // 磁贴高度跟着精简开关走：非精简多一行「上一条输出」，所以要更高。
        _gridLayout.MinItemHeight = _vm.Compact ? metrics.TileMinHeightCompact : metrics.TileMinHeight;
        _gridLayout.MinRowSpacing = metrics.TileSpacing;
        _gridLayout.MinColumnSpacing = metrics.TileSpacing;
        _gridLayout.ItemsStretch = UniformGridLayoutItemsStretch.Fill;
        _gridLayout.ItemsJustification = UniformGridLayoutItemsJustification.Start;

        // 赋同一个实例是 no-op（依赖属性会挡住），所以这里可以随便重复调用。
        SessionsRepeater.Layout = _gridMode ? _gridLayout : _listLayout;
        if (_gridMode && _gridTemplate is not null)
        {
            SessionsRepeater.ItemTemplate = _gridTemplate;
        }
        else if (_listTemplate is not null)
        {
            SessionsRepeater.ItemTemplate = _listTemplate;
        }

        // 图标显示「当前」模式，工具提示说明点击后会变成什么（和主题按钮一致）。
        ViewModeIcon.Glyph = _gridMode ? "\uF0E2" : "\uE8FD";
        ToolTipService.SetToolTip(ViewModeButton, _gridMode
            ? "视图：格子（点击切回列表）"
            : "视图：列表（点击切换为格子）");
    }

    // ------------------------------------------------------------------
    // 设置面板
    // ------------------------------------------------------------------

    /// <summary>
    /// 往设置控件里灌当前值时，挡住它们的 Changed 回调 ——
    /// 否则初始化会用默认值反过来把用户已经存下来的设置覆盖掉。
    /// </summary>
    private bool _loadingSettings;

    /// <summary>
    /// 往设置控件里灌当前值。**在浮出面板打开时调用**，而不是只在启动时调一次：
    /// 面板关着的时候那些控件还没进可视树，启动时赋值不保证反映得出来
    /// （实测 NumberBox 会一直显示 0）。每次打开都同步一遍也顺便保证面板永远是最新的。
    /// </summary>
    private void SyncSettingsControls()
    {
        _loadingSettings = true;
        try
        {
            MaxSessionsBox.Value = _vm.MaxSessions;
            HideIdleBox.Value = _vm.HideIdleMinutes;
            ThemeSelector.SelectedIndex = _themeMode switch
            {
                SystemTheme.Mode.Light => 1,
                SystemTheme.Mode.Dark => 2,
                _ => 0,
            };
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    /// <summary>把存下来的设置喂给 ViewModel（过滤逻辑跑在 ViewModel 里）。</summary>
    private void ApplyPersistedSettings()
    {
        _vm.MaxSessions = _placement.MaxSessions;
        _vm.HideIdleMinutes = _placement.HideIdleMinutes;
    }

    private void OnMaxSessionsChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loadingSettings) return;
        // 输入框被清空时 Value 是 NaN，这时保持原值不动。
        if (double.IsNaN(args.NewValue)) return;
        int value = (int)Math.Round(args.NewValue);
        if (value == _vm.MaxSessions) return;
        _vm.MaxSessions = value;
        _placement.MaxSessions = value;
    }

    private void OnHideIdleChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loadingSettings) return;
        if (double.IsNaN(args.NewValue)) return;
        int value = (int)Math.Round(args.NewValue);
        if (value == _vm.HideIdleMinutes) return;
        _vm.HideIdleMinutes = value;
        _placement.HideIdleMinutes = value;
    }

    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        _themeMode = ThemeSelector.SelectedIndex switch
        {
            1 => SystemTheme.Mode.Light,
            2 => SystemTheme.Mode.Dark,
            _ => SystemTheme.Mode.System,
        };
        _placement.Theme = _themeMode.ToString();
        ApplyTheme();
    }

    // ------------------------------------------------------------------
    // 明暗主题
    // ------------------------------------------------------------------

    private void OnSystemThemeChanged()
    {
        if (_themeMode != SystemTheme.Mode.System) return;
        // UISettings 的事件在后台线程，切回 UI 线程再改 XAML。
        DispatcherQueue.TryEnqueue(ApplyTheme);
    }

    private void ApplyTheme()
    {
        bool light = SystemTheme.ResolveIsLight(_themeMode);
        RootGrid.RequestedTheme = light ? ElementTheme.Light : ElementTheme.Dark;

        // 设置面板里的单选项要跟着走：跟随系统时系统切换也会走到这里。
        int index = _themeMode switch
        {
            SystemTheme.Mode.Light => 1,
            SystemTheme.Mode.Dark => 2,
            _ => 0,
        };
        if (!_loadingSettings && ThemeSelector.SelectedIndex != index)
        {
            _loadingSettings = true;
            try { ThemeSelector.SelectedIndex = index; }
            finally { _loadingSettings = false; }
        }

        // 让系统标题栏按钮的图标颜色跟着背景走（Win11 生效；Win10 上属无害调用）。
        try
        {
            if (_appWindow is not null && AppWindowTitleBar.IsCustomizationSupported())
            {
                var titleBar = _appWindow.TitleBar;
                titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
                titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
                titleBar.ButtonForegroundColor = light
                    ? Windows.UI.Color.FromArgb(255, 0x20, 0x20, 0x20)
                    : Windows.UI.Color.FromArgb(255, 0xF0, 0xF0, 0xF0);
                titleBar.ButtonHoverBackgroundColor = light
                    ? Windows.UI.Color.FromArgb(40, 0, 0, 0)
                    : Windows.UI.Color.FromArgb(40, 255, 255, 255);
                titleBar.ButtonHoverForegroundColor = titleBar.ButtonForegroundColor;
            }
        }
        catch
        {
            // 标题栏取色失败不影响内容区主题。
        }
    }

    // ------------------------------------------------------------------
    // 数据
    // ------------------------------------------------------------------

    private void OnSnapshotRead(StateSnapshot snapshot)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _vm.Apply(snapshot, now);
            _lastSnapshotAt = now;
            _staleSince = 0;
        });
    }

    private void OnReadFailed(string reason)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now - _lastSnapshotAt <= _options.StaleSeconds * 1000L) return;
            // 直接把原因显示出来（文件缺失 / 解析失败），不要笼统地都归到「插件没启用」。
            _vm.MarkDisconnected(reason);
        });
    }

    private void OnTick()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _vm.RefreshLiveText(now);

        bool stale = now - _lastSnapshotAt > _options.StaleSeconds * 1000L;
        if (stale)
        {
            _vm.MarkDisconnected("DSH 宿主没有心跳，可能已退出。");
            if (_staleSince == 0) _staleSince = now;

            if (_options.ExitAfterStaleSeconds > 0
                && now - _staleSince > _options.ExitAfterStaleSeconds * 1000L)
            {
                Close();
            }
        }
        else
        {
            _staleSince = 0;
        }
    }
}
