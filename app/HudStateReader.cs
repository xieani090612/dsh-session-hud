using System.IO;
using System.Text.Json;

namespace DshSessionHud;

/// <summary>
/// 轮询状态文件。插件以「写临时文件再 rename」的方式原子替换，
/// 所以这里永远读不到半截 JSON；同时用 FileShare.ReadWrite|Delete 打开，
/// 避免和 rename 抢锁。
/// </summary>
public sealed class HudStateReader : IDisposable
{
    private readonly string _path;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private long _lastWriteUtcTicks = -1;
    private long _lastLength = -1;
    private bool _disposed;

    /// <summary>在后台线程触发；订阅方负责切回 UI 线程。</summary>
    public event Action<StateSnapshot>? SnapshotRead;

    /// <summary>文件不存在或读不出内容时触发（用于显示「未连接」）。</summary>
    public event Action<string>? ReadFailed;

    public HudStateReader(string path, int intervalMs = 250)
    {
        _path = path;
        _timer = new Timer(_ => Poll(), null, 0, intervalMs);
    }

    private void Poll()
    {
        if (_disposed) return;
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists)
            {
                ReadFailed?.Invoke($"还没找到状态文件：{_path}（确认 dsh-session-hud 插件已在当前 profile 启用）");
                return;
            }

            // 文件没变就不重复反序列化。
            if (info.LastWriteTimeUtc.Ticks == _lastWriteUtcTicks && info.Length == _lastLength)
            {
                return;
            }

            StateSnapshot? snapshot = null;
            string? lastError = null;
            for (int attempt = 0; attempt < 3 && snapshot is null; attempt++)
            {
                try
                {
                    using var stream = new FileStream(
                        _path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    snapshot = JsonSerializer.Deserialize<StateSnapshot>(stream, JsonOptions);
                }
                catch (IOException ex)
                {
                    lastError = ex.Message;
                    Thread.Sleep(15);
                }
                catch (JsonException ex)
                {
                    // 可能是正在被原子替换，也可能是真的一份坏数据。重试几次再下结论。
                    lastError = ex.Message;
                    Thread.Sleep(15);
                }
            }

            if (snapshot is null)
            {
                // 关键：不要静默失败。早先这里直接 return，结果 schema 对不上时
                // 窗口会一直空白，完全看不出发生了什么。现在把原因报上去。
                if (lastError is not null) ReportOnce($"状态文件解析失败：{lastError}");
                return;
            }

            _lastParseError = null;
            _lastWriteUtcTicks = info.LastWriteTimeUtc.Ticks;
            _lastLength = info.Length;
            SnapshotRead?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            ReportOnce(ex.Message);
        }
    }

    private string? _lastParseError;

    /// <summary>同一个错误只上报一次，避免 250ms 轮询把它刷屏。</summary>
    private void ReportOnce(string error)
    {
        if (error == _lastParseError) return;
        _lastParseError = error;
        ReadFailed?.Invoke(error);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }
}
