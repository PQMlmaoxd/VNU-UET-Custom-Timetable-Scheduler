namespace Scheduler.Desktop;

public sealed partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        var window = new MainWindow(WebAssetLocator.Find(e.Args), new DesktopCommandDispatcher());
        MainWindow = window;
        window.Show();
    }
}
