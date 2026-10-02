using System.IO;
using System.Text.Json;

namespace DshSessionHud;

/// <summary>
/// 窗口位置/大小与几个开关的持久化，让 HUD 下次开在同一个地方。
/// 除几何尺寸外也存显示过滤设置（会话上限 / 空闲隐藏分钟数）。
///
/// 单位统一是 **DIP（有效像素）**，不是物理像素：XAML 布局和尺寸分层判断都用 DIP，
/// 存成 DIP 才能在不同 DPI 的显示器之间保持「看起来一样大」。
/// 换算成物理像素只发生在调用 AppWindow 的那一处（见 MainWindow.Scale）。
/// </summary>
public sealed class WindowPlacement
{
    public int X { get; set; } = -1;
    public int Y { get; set; } = -1;
    public int Width { get; set; } = 380;
    public int Height { get; set; } = 540;
    public bool Topmost { get; set; } = true;
    public bool Compact { get; set; }
    /// <summary>"System" | "Light" | "Dark"</summary>
    public string Theme { get; set; } = "System";
    /// <summary>"List"（一行一张详细卡片）| "Grid"（磁贴平铺）。</summary>
    public string ViewMode { get; set; } = "List";
    /// <summary>最多显示几个会话；0 = 不限制。</summary>
    public int MaxSessions { get; set; }
    /// <summary>空闲超过这么多分钟就隐藏；0 = 从不隐藏。等待批准的会话永不隐藏。</summary>
    public int HideIdleMinutes { get; set; }

    private static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DshSessionHud",
        "window.json");

    public static WindowPlacement Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                string json = File.ReadAllText(Path);
                var loaded = JsonSerializer.Deserialize<WindowPlacement>(json);
                if (loaded is not null)
                {
                    loaded.Width = Math.Clamp(loaded.Width, 300, 3000);
                    loaded.Height = Math.Clamp(loaded.Height, 200, 3000);
                    return loaded;
                }
            }
        }
        catch
        {
            // 配置损坏就回到默认值，不要因此打不开窗口。
        }
        return new WindowPlacement();
    }

    public void Save()
    {
        try
        {
            string dir = System.IO.Path.GetDirectoryName(Path)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // ignore
        }
    }
}
