using System.Runtime.InteropServices;
using System.Threading;

namespace DshSessionHud;

/// <summary>
/// 单实例协调。DSH 插件会按需拉起 HUD；用户也可能自己双击 exe。
/// 第二个实例不弹第二个窗口，而是把已有窗口叫到前台。
/// </summary>
public static class SingleInstance
{
    private const string EventName = @"Local\DshSessionHud.Activate";
    private static EventWaitHandle? _activateSignal;

    /// <summary>由「后启动的实例」调用：唤醒已经在跑的窗口。</summary>
    public static void SignalExisting()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(EventName, out var handle))
            {
                using (handle)
                {
                    handle.Set();
                }
            }
        }
        catch
        {
            // 拿不到就算了，安静退出即可。
        }
    }

    /// <summary>由「首个实例」调用：开始监听唤醒信号。</summary>
    public static void StartListening(IntPtr windowHandle)
    {
        try
        {
            _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
            var thread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        _activateSignal.WaitOne();
                        BringToFront(windowHandle);
                    }
                    catch
                    {
                        return;
                    }
                }
            })
            {
                IsBackground = true,
                Name = "DshSessionHud.Activate",
            };
            thread.Start();
        }
        catch
        {
            // 监听失败不影响主功能。
        }
    }

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private static void BringToFront(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
        }
        catch
        {
            // ignore
        }
    }
}
