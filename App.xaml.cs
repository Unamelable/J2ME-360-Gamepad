using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using J2MEGamepad.Services;
using J2MEGamepad.Windows;

namespace J2MEGamepad;

public partial class App : Application
{
    private static Mutex? _mutex;
    private MainWindow? _mainWindow;
    private readonly ManualResetEvent _windowReadyEvent = new(false);

    public void SignalWindowReady()
    {
        _windowReadyEvent.Set();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        LogHelper.Info("App", $"=== App starting (Runtime: {RuntimeInformation.FrameworkDescription}, OS: {RuntimeInformation.OSDescription}) ===");

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            LogHelper.Error("App", "AppDomain unhandled", (Exception)args.ExceptionObject);
            LogHelper.Info("App", "AppDomain unhandled -> process will exit");
        };
        DispatcherUnhandledException += (_, args) =>
        {
            LogHelper.Error("App", "Dispatcher unhandled", args.Exception);
            LogHelper.Info("App", "Dispatcher handled=true (app continues)");
            args.Handled = true;
        };

        try
        {
            const string mutexName = "J2MEGamepad-SingleInstanceMutex";
            bool createdNew;

            try
            {
                _mutex = new Mutex(true, mutexName, out createdNew);

                if (!createdNew)
                {
                    var result = MessageBox.Show(
                        "J2ME 360 Gamepad is already running.\nTerminate the bugged instance and start fresh?",
                        "Already Running",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        _mutex.Dispose();
                        _mutex = null;

                        // Kill all other instances of this process
                        var currentPid = Process.GetCurrentProcess().Id;
                        foreach (var proc in Process.GetProcessesByName(
                            Process.GetCurrentProcess().ProcessName))
                        {
                            if (proc.Id != currentPid)
                            {
                                try { proc.Kill(); proc.WaitForExit(3000); } catch { }
                            }
                        }

                        // Re-acquire mutex (may be abandoned after kill)
                        try
                        {
                            _mutex = new Mutex(true, mutexName, out createdNew);
                        }
                        catch (AbandonedMutexException)
                        {
                            // Mutex was abandoned after kill — ownership transferred
                            createdNew = true;
                        }

                        if (!createdNew)
                        {
                            MessageBox.Show("Could not acquire exclusive access.", "Error",
                                MessageBoxButton.OK, MessageBoxImage.Error);
                            Shutdown();
                            return;
                        }
                    }
                    else
                    {
                        Shutdown();
                        return;
                    }
                }
            }
            catch (AbandonedMutexException)
            {
                // Previous instance crashed — we can take ownership
                createdNew = true;
            }

            base.OnStartup(e);

            try
            {
                using (var p = Process.GetCurrentProcess())
                    p.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch { /* non-admin: ignore priority change failure */ }

            LogHelper.Info("App", "Creating MainWindow...");
            _mainWindow = new MainWindow();
            LogHelper.Info("App", "Showing MainWindow...");
            _mainWindow.Show();

            // Startup watchdog: if window doesn't signal ready within 6 seconds,
            // the UI thread may be hung. Show a notification and restart.
            var watchdogThread = new Thread(() =>
            {
                if (_windowReadyEvent.WaitOne(TimeSpan.FromSeconds(6)))
                    return;

                // Window never signaled ready — release mutex so new instance can start
                _mutex?.Dispose();
                _mutex = null;

                var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "J2ME 360 Gamepad.exe";
                try { Process.Start(exePath); } catch { }

                Dispatcher.Invoke(() =>
                {
                    MessageBox.Show(
                        "J2ME 360 Gamepad failed to start properly and will now restart.",
                        "Startup Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    Shutdown();
                });
            });
            watchdogThread.IsBackground = true;
            watchdogThread.Start();
        }
        catch (Exception ex)
        {
            LogHelper.Error("App", "OnStartup", ex);
            MessageBox.Show($"Startup error:\n{ex.Message}\n\nDetails saved to crash.log.",
                "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_mutex != null) { _mutex.Dispose(); _mutex = null; }
        _windowReadyEvent.Dispose();
        base.OnExit(e);
    }

    public void ReleaseMutexForRestart()
    {
        if (_mutex != null) { _mutex.Dispose(); _mutex = null; }
    }
}
