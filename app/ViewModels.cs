using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DshSessionHud;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>状态点的固定配色：在两套主题下都可读，因此不做主题切换。</summary>
internal static class HudPalette
{
    public static readonly SolidColorBrush Running = Brush(0x2E, 0xA0, 0x43);
    public static readonly SolidColorBrush Pending = Brush(0xD2, 0x99, 0x22);
    public static readonly SolidColorBrush Idle = Brush(0x8B, 0x94, 0x9E);
    public static readonly SolidColorBrush Error = Brush(0xF8, 0x51, 0x49);

    private static SolidColorBrush Brush(byte r, byte g, byte b)
        => new(Color.FromArgb(255, r, g, b));
}

public static class Format
{
    public static string Elapsed(long ms)
    {
        if (ms < 0) ms = 0;
        if (ms < 1000) return $"{ms}ms";
        if (ms < 10_000) return $"{ms / 1000.0:0.0}s";
        if (ms < 60_000) return $"{ms / 1000.0:0.0}s";
        if (ms < 3_600_000)
        {
            long m = ms / 60_000;
            long s = (ms % 60_000) / 1000;
            return $"{m}m{s:00}s";
        }
        long h = ms / 3_600_000;
        long mm = (ms % 3_600_000) / 60_000;
        return $"{h}h{mm:00}m";
    }

    public static string Tokens(long n)
    {
        if (n < 1000) return n.ToString();
        if (n < 1_000_000) return $"{n / 1000.0:0.#}k";
        return $"{n / 1_000_000.0:0.##}M";
    }

    public static string Ago(long ms) => ms < 1500 ? "刚刚" : $"{Elapsed(ms)}前";
}

// ---------------------------------------------------------------------------

public sealed class ToolEntryVm
{
    /// <summary>共享的尺寸表：行内宽度直接从它绑定，窗口一变化全体跟着变。</summary>
    public HudMetrics Metrics { get; init; } = new();

    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
    public string TimeText { get; init; } = "";
    public string Glyph { get; init; } = "";
    public SolidColorBrush Brush { get; init; } = HudPalette.Idle;

    public static ToolEntryVm From(ToolEntry entry, HudMetrics metrics)
    {
        string state = entry.State ?? "";
        return new ToolEntryVm
        {
            Metrics = metrics,
            Name = entry.Name ?? "tool",
            Detail = entry.Detail ?? "",
            TimeText = state == "running" ? "…" : entry.Ms > 0 ? Format.Elapsed(entry.Ms) : "",
            Glyph = state switch
            {
                "running" => "\u25B6",
                "error" => "\u2715",
                _ => "\u2713",
            },
            Brush = state switch
            {
                "running" => HudPalette.Running,
                "error" => HudPalette.Error,
                _ => HudPalette.Idle,
            },
        };
    }
}

// ---------------------------------------------------------------------------

public sealed class ApprovalCardVm : ObservableObject
{
    /// <summary>共享尺寸表，供模板绑定字号与留白。</summary>
    public HudMetrics Metrics { get; set; } = new();

    private string _toolText = "";
    private string _reasonText = "";
    private Visibility _reasonVisibility = Visibility.Collapsed;
    private string _metaText = "";

    public string ToolText { get => _toolText; private set => Set(ref _toolText, value); }
    public string ReasonText { get => _reasonText; private set => Set(ref _reasonText, value); }
    public Visibility ReasonVisibility { get => _reasonVisibility; private set => Set(ref _reasonVisibility, value); }
    public string MetaText { get => _metaText; private set => Set(ref _metaText, value); }

    private long _askedAt;

    public void Update(ApprovalState approval, long now)
    {
        _askedAt = approval.AskedAt;
        ToolText = $"{approval.ToolName ?? "tool"} 需要批准";

        string reason = !string.IsNullOrWhiteSpace(approval.DisplayReason)
            ? approval.DisplayReason!
            : approval.Reason ?? "";
        if (!string.IsNullOrWhiteSpace(reason))
        {
            ReasonText = reason;
            ReasonVisibility = Visibility.Visible;
        }
        else
        {
            ReasonVisibility = Visibility.Collapsed;
        }

        RefreshLiveText(now);
    }

    public void RefreshLiveText(long now)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(_sessionTitle)) parts.Add(_sessionTitle!);
        if (!string.IsNullOrWhiteSpace(_workspace)) parts.Add(_workspace!);
        if (_askedAt > 0) parts.Add($"已等待 {Format.Elapsed(Math.Max(0, now - _askedAt))}");
        MetaText = string.Join(" · ", parts);
    }

    private string? _sessionTitle;
    private string? _workspace;

    public void SetContext(string? sessionTitle, string? workspace)
    {
        _sessionTitle = sessionTitle;
        _workspace = workspace;
    }

    /// <summary>窗口尺寸分层变化后重算一次派生文本。</summary>
    public void OnMetricsChanged()
        => RefreshLiveText(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}

// ---------------------------------------------------------------------------

public sealed class SessionCardVm : ObservableObject
{
    private SessionState? _state;

    private string _title = "";
    private string _statusText = "";
    private SolidColorBrush _statusBrush = HudPalette.Idle;
    private string _elapsedText = "";
    private string _currentLabel = "";
    private string _currentDetail = "";
    private Visibility _currentVisibility = Visibility.Collapsed;
    private string _metaText = "";
    private Visibility _toolsVisibility = Visibility.Collapsed;
    private string _toolsSignature = "";
    private string _lastText = "";
    private Visibility _lastTextVisibility = Visibility.Collapsed;

    public string Id { get; private set; } = "";
    public ObservableCollection<ToolEntryVm> Tools { get; } = new();

    /// <summary>共享尺寸表。工具行的列宽不是用 ColumnDefinition 做的
    /// ——ColumnDefinition 不是 FrameworkElement、拿不到 DataContext，
    /// 所以那一行改用固定宽度的 TextBlock + Auto 列，宽度从这里绑定。</summary>
    public HudMetrics Metrics { get; set; } = new();

    private bool _compact;

    /// <summary>精简模式：折叠「最近活动」，只保留状态与当前工作。</summary>
    public bool Compact
    {
        get => _compact;
        set
        {
            if (Set(ref _compact, value))
            {
                RefreshToolsVisibility();
                RefreshTileLastTextVisibility();
                Raise(nameof(TileDetailLines));
            }
        }
    }

    /// <summary>
    /// 格子模式下「当前工作」显示几行。格子模式本来就没有「最近活动」，
    /// 所以让精简开关改去控制这块的密度，两个模式下都有意义。
    /// </summary>
    public int TileDetailLines => _compact ? 1 : 2;

    private Visibility _tileLastTextVisibility = Visibility.Collapsed;

    /// <summary>
    /// 磁贴里那一行「上一条输出」。**这就是精简开关在格子模式下的可见效果**：
    /// 关掉精简 → 磁贴多一行摘要（磁贴也更高）；打开精简 → 只留标题与当前工作。
    /// 早先这里只让精简改「当前工作」是 1 行还是 2 行，短文本下根本看不出区别，
    /// 用起来就像「格子模式强制精简、而且关不掉」。
    /// </summary>
    public Visibility TileLastTextVisibility { get => _tileLastTextVisibility; private set => Set(ref _tileLastTextVisibility, value); }

    private void RefreshTileLastTextVisibility()
        => TileLastTextVisibility = !_compact && _lastText.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void RefreshToolsVisibility()
        => ToolsVisibility = Tools.Count > 0 && !_compact && Metrics.TimelineAllowed
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>尺寸分层变了：重算可见性，并作废工具列表签名让它按新的行数上限重建。</summary>
    public void OnMetricsChanged()
    {
        _toolsSignature = "";
        RefreshToolsVisibility();
        RefreshMetaVisibility();
        RefreshLiveText(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private void RefreshMetaVisibility()
        => MetaVisibility = Metrics.MetaVisibility == Visibility.Visible && _metaText.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    public string Title { get => _title; private set => Set(ref _title, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public SolidColorBrush StatusBrush { get => _statusBrush; private set => Set(ref _statusBrush, value); }
    public string ElapsedText { get => _elapsedText; private set => Set(ref _elapsedText, value); }
    public string CurrentLabel { get => _currentLabel; private set => Set(ref _currentLabel, value); }
    public string CurrentDetail { get => _currentDetail; private set => Set(ref _currentDetail, value); }
    public Visibility CurrentVisibility { get => _currentVisibility; private set => Set(ref _currentVisibility, value); }
    public string MetaText { get => _metaText; private set => Set(ref _metaText, value); }
    public Visibility ToolsVisibility { get => _toolsVisibility; private set => Set(ref _toolsVisibility, value); }
    public string LastText { get => _lastText; private set => Set(ref _lastText, value); }
    public Visibility LastTextVisibility { get => _lastTextVisibility; private set => Set(ref _lastTextVisibility, value); }

    private Visibility _metaVisibility = Visibility.Collapsed;
    public Visibility MetaVisibility { get => _metaVisibility; private set => Set(ref _metaVisibility, value); }

    private Visibility _noCurrentVisibility = Visibility.Visible;
    /// <summary>CurrentVisibility 的反相：磁贴里用它决定要不要显示补位信息。</summary>
    public Visibility NoCurrentVisibility { get => _noCurrentVisibility; private set => Set(ref _noCurrentVisibility, value); }

    private string _tileSubtitle = "";
    /// <summary>磁贴底部的次要信息（工作区 · 模型 · 批准策略）。</summary>
    public string TileSubtitle { get => _tileSubtitle; private set => Set(ref _tileSubtitle, value); }

    public void Update(SessionState state, long now)
    {
        _state = state;
        Id = state.Id ?? "";

        string title = state.Title ?? "";
        if (string.IsNullOrWhiteSpace(title)) title = state.Workspace ?? state.ShortId ?? "会话";
        Title = (state.Kind == "subagent" ? "↳ " : "") + title;

        bool pending = state.PendingApproval is not null;
        if (pending)
        {
            StatusText = "待批准";
            StatusBrush = HudPalette.Pending;
        }
        else if (state.Running)
        {
            StatusText = "运行中";
            StatusBrush = HudPalette.Running;
        }
        else
        {
            StatusText = "空闲";
            StatusBrush = HudPalette.Idle;
        }

        // 当前工作
        var current = state.Current;
        if (pending)
        {
            CurrentLabel = "等待批准";
            CurrentDetail = $"{state.PendingApproval!.ToolName} 已挂起，等待人工决定";
            CurrentVisibility = Visibility.Visible;
        }
        else if (current is not null)
        {
            bool isTool = string.Equals(current.Kind, "tool", StringComparison.OrdinalIgnoreCase);
            CurrentLabel = isTool ? $"工具 · {current.Name}" : "模型生成中";
            CurrentDetail = string.IsNullOrWhiteSpace(current.Detail)
                ? (isTool ? current.Name ?? "" : $"{current.Name} 正在推理")
                : current.Detail!;
            CurrentVisibility = Visibility.Visible;
        }
        else
        {
            CurrentVisibility = Visibility.Collapsed;
        }
        // 格子模式的磁贴里，没有「当前工作」时用一行次要信息补位，
        // 否则空闲的磁贴会只剩标题，看起来很空。
        NoCurrentVisibility = CurrentVisibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

        var tile = new List<string>();
        if (!string.IsNullOrWhiteSpace(state.Workspace)) tile.Add(state.Workspace!);
        if (!string.IsNullOrWhiteSpace(state.Model)) tile.Add(state.Model!);
        if (!string.IsNullOrWhiteSpace(state.ApprovalPolicy))
        {
            tile.Add(state.ApprovalPolicy == "ask" ? "批准:询问" : "批准:从不");
        }
        TileSubtitle = string.Join(" · ", tile);

        // 最近活动（只有内容变了才重建，避免无谓的 UI 抖动）。
        // 签名里带上分层与行数上限：窗口变矮时上限变小，必须重建才能少渲染几行。
        string signature = $"{Metrics.Tier}/{Metrics.TimelineRows}#"
            + string.Join("|", state.Tools.Select(t => $"{t.CallId}:{t.State}:{t.Ms}"));
        if (signature != _toolsSignature)
        {
            _toolsSignature = signature;
            Tools.Clear();
            // state.Tools 是「最新在前」，所以 Take 拿到的就是最近的 N 条。
            foreach (var tool in state.Tools.Take(Math.Max(1, Metrics.TimelineRows)))
            {
                Tools.Add(ToolEntryVm.From(tool, Metrics));
            }
        }
        RefreshToolsVisibility();

        // 最近一段模型输出
        string lastText = state.LastText ?? "";
        LastText = lastText;
        LastTextVisibility = string.IsNullOrWhiteSpace(lastText) || !Metrics.ShowLastText
            ? Visibility.Collapsed
            : Visibility.Visible;
        RefreshTileLastTextVisibility();

        // 元信息
        var meta = new List<string>();
        if (!string.IsNullOrWhiteSpace(state.Workspace)) meta.Add(state.Workspace!);
        if (!string.IsNullOrWhiteSpace(state.Model)) meta.Add(state.Model!);
        if (state.Turn > 0) meta.Add($"turn {state.Turn}");
        if (state.Step > 0) meta.Add($"step {state.Step}");
        if (state.Usage is { } usage && (usage.Input > 0 || usage.Output > 0))
        {
            meta.Add($"↑{Format.Tokens(usage.Input)} ↓{Format.Tokens(usage.Output)}");
        }
        if (!string.IsNullOrWhiteSpace(state.ApprovalPolicy))
        {
            meta.Add(state.ApprovalPolicy == "ask" ? "批准:询问" : "批准:从不");
        }
        MetaText = string.Join(" · ", meta);
        RefreshMetaVisibility();

        RefreshLiveText(now);
    }

    /// <summary>每 250ms 重算一次「已经跑了多久」，这样即使插件不再写文件，秒表也不会停。</summary>
    public void RefreshLiveText(long now)
    {
        var state = _state;
        if (state is null) return;

        string elapsed;
        if (state.PendingApproval is not null)
        {
            long waited = Math.Max(0, now - state.PendingApproval.AskedAt);
            elapsed = $"等待 {Format.Elapsed(waited)}";
        }
        else if (state.Running)
        {
            long since = state.Current?.Since is > 0 ? state.Current!.Since : state.UpdatedAt;
            elapsed = Format.Elapsed(Math.Max(0, now - since));
        }
        else
        {
            // 「空闲」后面直接跟时长，比 "空闲 38.8s前" 更顺。
            elapsed = $"空闲 {Format.Elapsed(Math.Max(0, now - state.UpdatedAt))}";
        }
        ElapsedText = elapsed;
    }
}

// ---------------------------------------------------------------------------

public sealed class HudViewModel : ObservableObject
{
    private readonly Dictionary<string, SessionCardVm> _sessionCards = new();
    private readonly Dictionary<string, ApprovalCardVm> _approvalCards = new();

    /// <summary>全窗口共享的尺寸表；由 MainWindow 的 SizeChanged 驱动。</summary>
    public HudMetrics Metrics { get; } = new();

    public HudViewModel()
    {
        // 尺寸分层一变，卡片要重算可见性并按新行数上限重建工具列表。
        Metrics.PropertyChanged += (_, _) =>
        {
            foreach (var card in _sessionCards.Values) card.OnMetricsChanged();
            foreach (var card in _approvalCards.Values) card.OnMetricsChanged();
        };
    }

    private string _connectionText = "连接中…";
    private string _statusBarText = "等待 DSH 宿主…";
    private string _approvalHeaderText = "";
    private Visibility _approvalsVisibility = Visibility.Collapsed;
    private Visibility _emptyVisibility = Visibility.Collapsed;
    private string _emptyText = "等待 DSH 会话…";

    public ObservableCollection<SessionCardVm> Sessions { get; } = new();
    public ObservableCollection<ApprovalCardVm> Approvals { get; } = new();

    public string ConnectionText { get => _connectionText; private set => Set(ref _connectionText, value); }
    public string StatusBarText { get => _statusBarText; private set => Set(ref _statusBarText, value); }
    public string ApprovalHeaderText { get => _approvalHeaderText; private set => Set(ref _approvalHeaderText, value); }
    public Visibility ApprovalsVisibility { get => _approvalsVisibility; private set => Set(ref _approvalsVisibility, value); }
    public Visibility EmptyVisibility { get => _emptyVisibility; private set => Set(ref _emptyVisibility, value); }
    public string EmptyText { get => _emptyText; private set => Set(ref _emptyText, value); }

    private long _generatedAt;
    private HostInfo? _host;
    private int _runningCount;
    private int _sessionCount;
    private int _pendingCount;

    private bool _compact;

    /// <summary>精简模式开关，会传播到每张会话卡片。</summary>
    public bool Compact
    {
        get => _compact;
        set
        {
            if (!Set(ref _compact, value)) return;
            foreach (var card in _sessionCards.Values) card.Compact = value;
        }
    }

    // ---- 显示过滤（设置里可调）----

    private int _maxSessions;
    private int _hideIdleMinutes;

    /// <summary>最多显示几个会话；0 = 不限制。改动立刻重新过滤，不用等下一帧快照。</summary>
    public int MaxSessions
    {
        get => _maxSessions;
        set
        {
            if (Set(ref _maxSessions, Math.Clamp(value, 0, 200))) Rerender();
        }
    }

    /// <summary>空闲超过这么多分钟就隐藏；0 = 从不隐藏。等待批准的会话永不隐藏。</summary>
    public int HideIdleMinutes
    {
        get => _hideIdleMinutes;
        set
        {
            if (Set(ref _hideIdleMinutes, Math.Clamp(value, 0, 24 * 60))) Rerender();
        }
    }

    /// <summary>过滤后实际显示的会话数，以及被过滤掉的数量（状态栏用）。</summary>
    private int _hiddenCount;
    public int HiddenCount { get => _hiddenCount; private set => Set(ref _hiddenCount, value); }

    /// <summary>
    /// 判断一个会话现在该不该显示。
    ///
    /// 两条规则：
    ///   1. 空闲超过 N 分钟就隐藏 —— 但**等待批准的和正在跑的永远显示**，
    ///      否则「谁在等我批准」会被静默藏掉，那正是最不该藏的东西；
    ///   2. 会话总数上限 —— 因为快照里已经按「待批准 → 运行中 → 最近活动」排好序，
    ///      直接 Take 就等于优先保留最重要的那几个。
    /// </summary>
    private List<SessionState> FilterSessions(StateSnapshot snapshot, long now)
    {
        IEnumerable<SessionState> result = snapshot.Sessions;

        if (_hideIdleMinutes > 0)
        {
            long limitMs = _hideIdleMinutes * 60_000L;
            result = result.Where(s =>
            {
                if (s.Running || s.PendingApproval is not null) return true;
                long idle = now - s.UpdatedAt;
                return idle <= limitMs;
            });
        }

        // 自己再排一次序，不信快照的顺序：上限必须优先保住「待批准 → 运行中 → 最近活动」。
        // 插件写快照时本来就按这个顺序排，所以生产环境下这次排序是空操作；
        // 但自己排过之后，「上限 3」就不会因为来源顺序不同而把等批准的会话切掉。
        result = result
            .OrderByDescending(s => s.PendingApproval is not null)
            .ThenByDescending(s => s.Running)
            .ThenByDescending(s => s.UpdatedAt);

        if (_maxSessions > 0) result = result.Take(_maxSessions);
        return result.ToList();
    }

    /// <summary>设置变了但还没有新快照时，用上一份快照重新渲染一遍。</summary>
    private void Rerender()
    {
        if (_lastSnapshot is null) return;
        Render(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private StateSnapshot? _lastSnapshot;

    /// <summary>把一份新快照合并进现有卡片（增删改，尽量不动已有对象）。</summary>
    public void Apply(StateSnapshot snapshot, long now)
    {
        _lastSnapshot = snapshot;
        Render(now);
    }

    private void Render(long now)
    {
        var snapshot = _lastSnapshot;
        if (snapshot is null) return;

        _generatedAt = snapshot.GeneratedAt;
        _host = snapshot.Host;
        _runningCount = snapshot.Totals?.Running ?? snapshot.Sessions.Count(s => s.Running);
        _sessionCount = snapshot.Totals?.Sessions ?? snapshot.Sessions.Count;
        _pendingCount = snapshot.Totals?.PendingApprovals ?? snapshot.Approvals.Count;

        var visible = FilterSessions(snapshot, now);
        HiddenCount = Math.Max(0, snapshot.Sessions.Count - visible.Count);

        // 会话卡片。每个条目单独兜异常：一条脏数据不应该让整份快照更新中断，
        // 否则后面的批准提示就永远刷不出来。
        var seen = new HashSet<string>();
        foreach (var state in visible)
        {
            try
            {
                string id = state.Id ?? Guid.NewGuid().ToString();
                seen.Add(id);
                if (!_sessionCards.TryGetValue(id, out var card))
                {
                    card = new SessionCardVm { Compact = _compact, Metrics = Metrics };
                    _sessionCards[id] = card;
                }
                card.Update(state, now);
            }
            catch
            {
                // 跳过这条，继续更新其余会话。
            }
        }
        // 被过滤掉（或已销毁）的卡片一并清掉：卡片是按 id 复用的，
        // 留着会让「上限 3」这种设置看起来没生效。
        foreach (string id in _sessionCards.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _sessionCards.Remove(id);
        }

        // 保持插件给出的顺序，但复用卡片对象，避免滚动位置与动画被打断。
        SyncOrder(Sessions, visible.Select(s => s.Id ?? ""), id => _sessionCards.TryGetValue(id, out var c) ? c : null);

        // 批准提示卡片
        var seenApprovals = new HashSet<string>();
        foreach (var approval in snapshot.Approvals)
        {
            try
            {
                string id = approval.Id ?? $"{approval.SessionId}:{approval.ToolName}";
                seenApprovals.Add(id);
                if (!_approvalCards.TryGetValue(id, out var card))
                {
                    card = new ApprovalCardVm { Metrics = Metrics };
                    _approvalCards[id] = card;
                }
                card.SetContext(approval.SessionTitle, approval.Workspace);
                card.Update(approval, now);
            }
            catch
            {
                // 同上：单条失败不影响整体。
            }
        }
        foreach (string id in _approvalCards.Keys.Where(k => !seenApprovals.Contains(k)).ToList())
        {
            _approvalCards.Remove(id);
        }
        SyncOrder(Approvals, snapshot.Approvals.Select(a => a.Id ?? $"{a.SessionId}:{a.ToolName}"),
            id => _approvalCards.TryGetValue(id, out var c) ? c : null);

        ApprovalsVisibility = Approvals.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ApprovalHeaderText = $"待批准 · {Approvals.Count}";

        EmptyVisibility = Sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText = snapshot.Host?.Stopped == true ? "DSH 宿主已停止" : "当前没有活动会话";

        RefreshLiveText(now);
    }

    public void MarkDisconnected(string reason)
    {
        ConnectionText = "未连接";
        StatusBarText = reason;
        ApprovalsVisibility = Visibility.Collapsed;

        // 一条会话都还没收到过时，用空状态文案说明原因；
        // 已经有卡片了就保留卡片（内容灰着也比消失有用）。
        if (Sessions.Count == 0)
        {
            EmptyVisibility = Visibility.Visible;
            EmptyText = "等待 DSH 宿主…";
        }
    }

    public void RefreshLiveText(long now)
    {
        foreach (var card in _sessionCards.Values) card.RefreshLiveText(now);
        foreach (var card in _approvalCards.Values) card.RefreshLiveText(now);

        // 连接状态：宿主心跳 + 进程是否还在
        bool stopped = _host?.Stopped == true;
        long age = _generatedAt > 0 ? Math.Max(0, now - _generatedAt) : long.MaxValue;

        if (stopped)
        {
            ConnectionText = "宿主已退出";
        }
        else if (age < 3000)
        {
            ConnectionText = "已连接";
        }
        else
        {
            ConnectionText = "等待心跳…";
        }

        var parts = new List<string>();
        // 连接状态放在状态栏，而不是标题栏：标题栏被系统按钮占掉一大块，
        // 塞不下「标题 + 连接状态」两段文字 —— 塞了就会溢出到按键底下。
        if (!string.IsNullOrEmpty(ConnectionText)) parts.Add(ConnectionText);
        parts.Add($"{_sessionCount} 个会话");
        parts.Add($"{_runningCount} 个运行中");
        if (_pendingCount > 0) parts.Add($"{_pendingCount} 个待批准");
        // 有会话被设置过滤掉时要说清楚，否则「上限 3」看起来像丢数据。
        if (HiddenCount > 0) parts.Add($"已隐藏 {HiddenCount}");
        if (_host?.Pid > 0) parts.Add($"dsh pid {_host.Pid}");
        if (_generatedAt > 0) parts.Add($"更新于 {Format.Ago(age)}");
        StatusBarText = string.Join(" · ", parts);
    }

    /// <summary>按 desiredIds 的顺序重排集合，但保持同一个卡片实例。</summary>
    private static void SyncOrder<T>(
        ObservableCollection<T> target,
        IEnumerable<string> desiredIds,
        Func<string, T?> resolve) where T : class
    {
        var desired = new List<T>();
        foreach (string id in desiredIds)
        {
            var item = resolve(id);
            if (item is not null && !desired.Contains(item)) desired.Add(item);
        }

        // 现有集合里已经不在目标列表中的，直接移除。
        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(target[i])) target.RemoveAt(i);
        }

        // 再逐个位置对齐。
        for (int i = 0; i < desired.Count; i++)
        {
            if (i >= target.Count)
            {
                target.Add(desired[i]);
            }
            else if (!ReferenceEquals(target[i], desired[i]))
            {
                int existing = target.IndexOf(desired[i]);
                if (existing >= 0) target.Move(existing, i);
                else target.Insert(i, desired[i]);
            }
        }
    }
}
