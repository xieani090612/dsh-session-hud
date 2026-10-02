using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DshSessionHud;

/// <summary>
/// 显式入口点（csproj 里定义了 DISABLE_XAML_GENERATED_MAIN）。
/// 在这里做两件必须在窗口出现之前完成的事：
///   1. 单实例互斥体 —— DSH 插件可以放心地反复拉起本程序，重复实例会立刻退出；
///   2. 解析命令行（--state 指定状态文件路径）。
/// </summary>
public static class Program
{
    private static Mutex? _instanceMutex;

    [STAThread]
    public static void Main(string[] args)
    {
        _instanceMutex = new Mutex(initiallyOwned: true, @"Local\DshSessionHud.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            // 已经有一个 HUD 在跑了：把已有窗口叫到前台，然后退出本进程。
            SingleInstance.SignalExisting();
            return;
        }

        App.Options = AppOptions.Parse(args);

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });

        GC.KeepAlive(_instanceMutex);
    }
}
