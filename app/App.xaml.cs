using Microsoft.UI.Xaml;

namespace DshSessionHud;

public partial class App : Application
{
    /// <summary>在 Application.Start 之前由 Program 填好。</summary>
    public static AppOptions Options { get; set; } = AppOptions.Default;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new MainWindow();
        window.Activate();
    }
}
