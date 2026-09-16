using System.Windows;

namespace EnragedON.Setup;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) =>
        {
            // Never vanish silently: show what happened, keep the window alive.
            MessageBox.Show(ex.Exception.Message, "EnragedON Setup", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        var win = new MainWindow(e.Args);
#if DEBUG
        if (e.Args.Length >= 2 && e.Args[0] == "--snapshot")
        {
            win.RenderSnapshots(e.Args[1]);
            Shutdown();
            return;
        }
#endif
        win.Show();
    }
}
