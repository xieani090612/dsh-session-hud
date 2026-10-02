using Microsoft.UI.Xaml;

namespace DshSessionHud;

public enum LayoutTier
{
    /// <summary>很窄的窗口：缩字号、收紧留白、隐藏次要信息。</summary>
    Narrow,
    Normal,
    /// <summary>宽窗口：放大字号与行高，把内容铺开。</summary>
    Wide,
}

/// <summary>
/// 随窗口大小浮动的尺寸表。
///
/// 为什么做成一个共享对象而不是用 VisualStateManager：
/// 会话卡片在 DataTemplate 里，VSM 的 Setter 够不到模板内部的具名元素；
/// 而工具行又是嵌套 DataTemplate（DataContext 是 ToolEntryVm，
/// 连 ColumnDefinition 都没有 DataContext 可继承——ColumnDefinition 不是 FrameworkElement）。
/// 所以统一改成「所有尺寸都来自一个 INotifyPropertyChanged 对象」，
/// 模板里逐项绑定，窗口一变全体一起更新。
///
/// 只按**分层**重算（Narrow / Normal / Wide + 矮窗口），
/// 不做随像素连续插值——HUD 上连续变字号只会让文字模糊、行高抖动。
/// </summary>
public sealed class HudMetrics : ObservableObject
{
    private double _width;
    private double _height;
    private LayoutTier _tier = LayoutTier.Normal;
    private bool _short;

    /// <summary>
    /// 分层阈值，单位是**有效像素（DIP）**——和 RootGrid.SizeChanged 报的、以及
    /// 所有 XAML 尺寸是同一套单位。默认窗口 380 DIP 宽，内容区约 366 DIP，
    /// 落在标准档里。
    /// </summary>
    public const double NarrowThreshold = 340;
    public const double WideThreshold = 500;
    /// <summary>高度低于这个值算矮窗口：丢掉非必要区块，优先保住状态与当前工作。</summary>
    public const double ShortThreshold = 330;

    public LayoutTier Tier => _tier;
    public bool IsShort => _short;

    // ---- 标题栏 ----
    private double _titleBarHeight = 40;
    private double _buttonWidth = 30;
    private double _buttonHeight = 28;
    private double _buttonIconSize = 12;
    private double _buttonSpacing = 2;
    private Visibility _appTitleVisibility = Visibility.Visible;

    /// <summary>
    /// 系统的最小化/最大化/关闭按钮占掉的宽度（有效像素）。
    /// 由 MainWindow 从 AppWindow.TitleBar.RightInset 实测填入，不要写死：
    /// 不同 DPI、不同 Windows 版本、不同主题下这个值都不一样。
    /// 我们的三个按键要正好贴在它左边。
    /// </summary>
    public double TitleBarReserve { get => _titleBarReserve; set => Set(ref _titleBarReserve, value); }
    private double _titleBarReserve = 140;

    public double TitleBarHeight { get => _titleBarHeight; private set => Set(ref _titleBarHeight, value); }
    public double ButtonWidth { get => _buttonWidth; private set => Set(ref _buttonWidth, value); }
    public double ButtonHeight { get => _buttonHeight; private set => Set(ref _buttonHeight, value); }
    public double ButtonIconSize { get => _buttonIconSize; private set => Set(ref _buttonIconSize, value); }
    public double ButtonSpacing { get => _buttonSpacing; private set => Set(ref _buttonSpacing, value); }
    public Visibility AppTitleVisibility { get => _appTitleVisibility; private set => Set(ref _appTitleVisibility, value); }

    /// <summary>
    /// 标题栏文字。加了第四个按键之后，标准档下标题那一列只剩 60 来个 DIP，
    /// 放不下「DSH 会话 HUD」（会变成 "DSH 会话…"）。所以按档位给不同长度的文案，
    /// 宁可换个短名字，也不要留一串省略号。
    /// </summary>
    private string _titleText = "DSH HUD";
    public string TitleText { get => _titleText; private set => Set(ref _titleText, value); }

    // ---- 字号 ----
    private double _titleFontSize = 12.5;
    private double _bodyFontSize = 11;
    private double _smallFontSize = 10.5;
    private double _monoFontSize = 11.5;
    private double _elapsedFontSize = 11;

    public double TitleFontSize { get => _titleFontSize; private set => Set(ref _titleFontSize, value); }
    public double BodyFontSize { get => _bodyFontSize; private set => Set(ref _bodyFontSize, value); }
    public double SmallFontSize { get => _smallFontSize; private set => Set(ref _smallFontSize, value); }
    public double MonoFontSize { get => _monoFontSize; private set => Set(ref _monoFontSize, value); }
    public double ElapsedFontSize { get => _elapsedFontSize; private set => Set(ref _elapsedFontSize, value); }

    // ---- 卡片与列表 ----
    private Thickness _cardPadding = new(10, 10, 10, 10);
    private Thickness _cardMargin = new(0, 0, 0, 8);
    private Thickness _innerPadding = new(9, 7, 9, 7);
    private Thickness _scrollPadding = new(10, 0, 10, 10);
    private double _cardSpacing = 9;
    private double _sectionSpacing = 5;

    public Thickness CardPadding { get => _cardPadding; private set => Set(ref _cardPadding, value); }
    public Thickness CardMargin { get => _cardMargin; private set => Set(ref _cardMargin, value); }
    /// <summary>卡片内部小块（「当前工作」「待批准」）的内边距，比卡片本身再小一档。</summary>
    public Thickness InnerPadding { get => _innerPadding; private set => Set(ref _innerPadding, value); }
    public Thickness ScrollPadding { get => _scrollPadding; private set => Set(ref _scrollPadding, value); }
    public double CardSpacing { get => _cardSpacing; private set => Set(ref _cardSpacing, value); }
    public double SectionSpacing { get => _sectionSpacing; private set => Set(ref _sectionSpacing, value); }

    // ---- 工具行 ----
    private double _glyphColumnWidth = 16;
    private double _nameColumnWidth = 84;
    private double _toolRowHeight = 20;

    public double GlyphColumnWidth { get => _glyphColumnWidth; private set => Set(ref _glyphColumnWidth, value); }
    public double NameColumnWidth { get => _nameColumnWidth; private set => Set(ref _nameColumnWidth, value); }
    public double ToolRowHeight { get => _toolRowHeight; private set => Set(ref _toolRowHeight, value); }

    // ---- 格子模式的磁贴 ----
    // 交给 UniformGridLayout：它按可用宽度决定一行放几个，
    // 每个磁贴不小于 MinItemWidth（放不下就换行），再拉伸铺满。
    // 这样列数不用自己算，也不会碰到「面板里绑不了 DataContext」的问题。
    //
    // 高度有两个值：非精简的磁贴多一行「上一条输出」，所以更高。
    // 由 MainWindow 按精简开关选一个喂给 UniformGridLayout.MinItemHeight。
    private double _tileMinWidth = 210;
    private double _tileMinHeight = 134;
    private double _tileMinHeightCompact = 88;
    private double _tileSpacing = 8;

    public double TileMinWidth { get => _tileMinWidth; private set => Set(ref _tileMinWidth, value); }
    /// <summary>非精简磁贴的最小高度（多一行「上一条输出」）。</summary>
    public double TileMinHeight { get => _tileMinHeight; private set => Set(ref _tileMinHeight, value); }
    /// <summary>精简磁贴的最小高度，只留标题与当前工作。</summary>
    public double TileMinHeightCompact { get => _tileMinHeightCompact; private set => Set(ref _tileMinHeightCompact, value); }
    public double TileSpacing { get => _tileSpacing; private set => Set(ref _tileSpacing, value); }

    // ---- 内容取舍 ----
    private int _timelineRows = 12;
    private int _lastTextLines = 2;
    private bool _showLastText = true;
    private Visibility _metaVisibility = Visibility.Visible;
    private Visibility _timelineVisibility = Visibility.Visible;

    /// <summary>最多渲染多少条工具调用（窗口矮的时候要往下砍）。</summary>
    public int TimelineRows { get => _timelineRows; private set => Set(ref _timelineRows, value); }
    /// <summary>上一条模型输出最多显示几行（至少 1，0 对 MaxLines 不友好）。</summary>
    public int LastTextLines { get => _lastTextLines; private set => Set(ref _lastTextLines, value); }
    /// <summary>矮窗口下整块「上一条输出」都藏掉。</summary>
    public bool ShowLastText { get => _showLastText; private set => Set(ref _showLastText, value); }
    public Visibility MetaVisibility { get => _metaVisibility; private set => Set(ref _metaVisibility, value); }
    /// <summary>窗口是否允许显示「最近活动」（还要叠加精简开关与「有没有工具」）。</summary>
    public Visibility TimelineVisibility { get => _timelineVisibility; private set => Set(ref _timelineVisibility, value); }
    public bool TimelineAllowed => _timelineVisibility == Visibility.Visible;

    /// <summary>由 MainWindow 的 SizeChanged 调用。窗口尺寸没跨过分层边界时什么都不做。</summary>
    public void Apply(double width, double height)
    {
        if (width <= 0 || height <= 0) return;
        _width = width;
        _height = height;

        var tier = width < NarrowThreshold ? LayoutTier.Narrow
                 : width > WideThreshold ? LayoutTier.Wide
                 : LayoutTier.Normal;
        bool isShort = height < ShortThreshold;

        if (_initialized && tier == _tier && isShort == _short) return;

        _tier = tier;
        _short = isShort;
        _initialized = true;
        Raise(nameof(Tier));
        Raise(nameof(IsShort));
        Recompute();
    }

    private bool _initialized;

    private void Recompute()
    {
        switch (_tier)
        {
            case LayoutTier.Narrow:
                TitleBarHeight = 34;
                ButtonWidth = 27;
                ButtonHeight = 25;
                ButtonIconSize = 10;
                ButtonSpacing = 1;
                // 窄窗口先把标题字去掉：系统那三个标题栏按钮固定占掉约 140px，
                // 这点宽度必须让给按键，否则按键会被挤出去。
                AppTitleVisibility = Visibility.Collapsed;
                TitleText = "DSH HUD";

                TitleFontSize = 11.5;
                BodyFontSize = 10.5;
                SmallFontSize = 9.5;
                MonoFontSize = 10;
                ElapsedFontSize = 10;

                CardPadding = new Thickness(8, 7, 8, 7);
                CardMargin = new Thickness(0, 0, 0, 6);
                InnerPadding = new Thickness(7, 6, 7, 6);
                ScrollPadding = new Thickness(8, 0, 8, 8);
                CardSpacing = 6;
                SectionSpacing = 4;

                GlyphColumnWidth = 13;
                NameColumnWidth = 66;
                ToolRowHeight = 16;

                TileMinWidth = 190;
                TileMinHeight = 118;
                TileMinHeightCompact = 78;
                TileSpacing = 6;

                TimelineRows = 6;
                LastTextLines = 1;
                ShowLastText = true;
                MetaVisibility = Visibility.Collapsed;
                TimelineVisibility = Visibility.Visible;
                break;

            case LayoutTier.Wide:
                TitleBarHeight = 44;
                ButtonWidth = 34;
                ButtonHeight = 31;
                ButtonIconSize = 13;
                ButtonSpacing = 3;
                AppTitleVisibility = Visibility.Visible;
                TitleText = "DSH 会话 HUD";

                TitleFontSize = 13.5;
                BodyFontSize = 12;
                SmallFontSize = 11;
                MonoFontSize = 12.5;
                ElapsedFontSize = 11.5;

                CardPadding = new Thickness(12, 12, 12, 12);
                CardMargin = new Thickness(0, 0, 0, 10);
                InnerPadding = new Thickness(11, 9, 11, 9);
                ScrollPadding = new Thickness(12, 0, 12, 12);
                CardSpacing = 11;
                SectionSpacing = 6;

                GlyphColumnWidth = 18;
                NameColumnWidth = 104;
                ToolRowHeight = 24;

                TileMinWidth = 244;
                TileMinHeight = 158;
                TileMinHeightCompact = 100;
                TileSpacing = 10;

                TimelineRows = 16;
                LastTextLines = 3;
                ShowLastText = true;
                MetaVisibility = Visibility.Visible;
                TimelineVisibility = Visibility.Visible;
                break;

            default: // Normal
                TitleBarHeight = 40;
                ButtonWidth = 30;
                ButtonHeight = 28;
                ButtonIconSize = 12;
                ButtonSpacing = 2;
                AppTitleVisibility = Visibility.Visible;
                // 标准档只有 60 来个 DIP 给标题，用短名字免得被截成省略号。
                TitleText = "DSH HUD";

                TitleFontSize = 12.5;
                BodyFontSize = 11;
                SmallFontSize = 10.5;
                MonoFontSize = 11.5;
                ElapsedFontSize = 11;

                CardPadding = new Thickness(10, 10, 10, 10);
                CardMargin = new Thickness(0, 0, 0, 8);
                InnerPadding = new Thickness(9, 7, 9, 7);
                ScrollPadding = new Thickness(10, 0, 10, 10);
                CardSpacing = 9;
                SectionSpacing = 5;

                GlyphColumnWidth = 16;
                NameColumnWidth = 84;
                ToolRowHeight = 20;

                TileMinWidth = 212;
                TileMinHeight = 134;
                TileMinHeightCompact = 88;
                TileSpacing = 8;

                TimelineRows = 12;
                LastTextLines = 2;
                ShowLastText = true;
                MetaVisibility = Visibility.Visible;
                TimelineVisibility = Visibility.Visible;
                break;
        }

        // 矮窗口再往下压一层：先丢「最近活动」，再丢上一条输出和元信息。
        // 这样窗口被压扁时，用户仍能看到「哪个会话在干什么」这个最核心的信息。
        if (_short)
        {
            TimelineRows = Math.Min(TimelineRows, 3);
            LastTextLines = 1;
            ShowLastText = false;
            MetaVisibility = Visibility.Collapsed;
            if (_height < 260) TimelineVisibility = Visibility.Collapsed;
        }
    }
}
