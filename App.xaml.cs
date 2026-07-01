using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using J2MEGamepad.Windows;

namespace J2MEGamepad;

public partial class App : Application
{
    private static Mutex? _mutex;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "J2MEGamepad-SingleInstanceMutex", out bool createdNew);

        if (!createdNew)
        {
            MessageBox.Show("J2ME 360 Gamepad is already running.", "Already Running",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        using (var process = Process.GetCurrentProcess())
            process.PriorityClass = ProcessPriorityClass.BelowNormal;

        _mainWindow = new MainWindow();
        _mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
