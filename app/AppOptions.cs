using System.IO;

namespace DshSessionHud;

/// <summary>命令行/环境变量选项。</summary>
public sealed class AppOptions
{
    /// <summary>状态文件路径（插件写入、本程序读取）。</summary>
    public string StatePath { get; init; } = DefaultStatePath();

    /// <summary>状态文件连续多久没有更新就认为宿主已停止（秒）。</summary>
    public int StaleSeconds { get; init; } = 15;

    /// <summary>宿主消失后多久自动关闭窗口（秒）；0 表示永不自动关闭。</summary>
    public int ExitAfterStaleSeconds { get; init; } = 0;

    public static AppOptions Default { get; } = new();

    public static AppOptions Parse(string[] args)
    {
        string statePath = DefaultStatePath();
        int stale = 15;
        int exitAfter = 0;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;

            switch (arg)
            {
                case "--state" or "-s":
                    statePath = Next() ?? statePath;
                    break;
                case "--stale-seconds":
                    if (int.TryParse(Next(), out int parsedStale) && parsedStale > 0) stale = parsedStale;
                    break;
                case "--exit-after-stale":
                    if (int.TryParse(Next(), out int parsedExit) && parsedExit >= 0) exitAfter = parsedExit;
                    break;
                default:
                    // 允许直接传一个路径：DshSessionHud.exe <state.json>
                    if (!arg.StartsWith('-') && arg.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        statePath = arg;
                    }
                    break;
            }
        }

        string? envState = Environment.GetEnvironmentVariable("DSH_HUD_STATE");
        if (!string.IsNullOrWhiteSpace(envState)) statePath = envState!;

        if (int.TryParse(Environment.GetEnvironmentVariable("DSH_HUD_STALE_SECONDS"), out int envStale) && envStale > 0)
        {
            stale = envStale;
        }

        return new AppOptions
        {
            StatePath = statePath,
            StaleSeconds = stale,
            ExitAfterStaleSeconds = exitAfter,
        };
    }

    private static string DefaultStatePath()
    {
        string dshHome = Environment.GetEnvironmentVariable("DSH_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        return Path.Combine(dshHome, "session-hud", "state.json");
    }
}
