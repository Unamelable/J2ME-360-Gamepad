using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using J2MEGamepad.Services;
using J2MEGamepad.Windows;

namespace J2MEGamepad;

public partial class App : Application
{
    private static Mutex? s_mutex;
    private MainWindow? _mainWindow;
    private readonly ManualResetEvent _windowReadyEvent = new(false);

    private static bool s_shutdownPerformed;

    internal static KeyboardInjector? GlobalKeyboard { get; set; }

    public void SignalWindowReady()
    {
        _windowReadyEvent.Set();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        DllSafety.HardenNativeLoading();

        LogHelper.Info("App", $"=== App starting (Runtime: .NET Framework {Environment.Version}, OS: {Environment.OSVersion}) ===");

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            LogHelper.Error("App", "AppDomain unhandled", (Exception)args.ExceptionObject);
            LogHelper.Info("App", "AppDomain unhandled -> releasing keys before exit");
            PerformShutdown();
        };
        DispatcherUnhandledException += (_, args) =>
        {
            LogHelper.Error("App", "Dispatcher unhandled", args.Exception);
            LogHelper.Info("App", "Dispatcher handled=true (app continues)");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            PerformShutdown();
        };

        try
        {
            const string mutexName = @"Local\J2MEGamepad-D4E7A1F0-9B3C-4F6E-8A2B-1C5D7E9F0A3B";
            bool createdNew;

            try
            {
                s_mutex = new Mutex(true, mutexName, out createdNew);

                if (!createdNew)
                {
                    var result = MessageBox.Show(
                        "J2ME 360 Gamepad is already running.\nTerminate the bugged instance and start fresh?",
                        "Already Running",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        s_mutex.Dispose();
                        s_mutex = null;

                        var currentPid = Process.GetCurrentProcess().Id;
                        using var currentProcess = Process.GetCurrentProcess();
                        string? myPath = currentProcess.MainModule?.FileName;
                        foreach (var proc in Process.GetProcessesByName(
                            currentProcess.ProcessName))
                        {
                            if (proc.Id == currentPid) continue;
                            try
                            {
                                string? otherPath = proc.MainModule?.FileName;
                                if (!string.IsNullOrEmpty(myPath) &&
                                    string.Equals(otherPath, myPath, StringComparison.OrdinalIgnoreCase))
                                {
                                    proc.Kill();
                                    proc.WaitForExit(3000);
                                }
                            }
                            catch { }
                        }

                        try
                        {
                            s_mutex = new Mutex(true, mutexName, out createdNew);
                        }
                        catch (AbandonedMutexException)
                        {
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
                createdNew = true;
            }

            base.OnStartup(e);

            try
            {
                using (var p = Process.GetCurrentProcess())
                    p.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch { }

            LogHelper.Info("App", "Creating MainWindow...");
            _mainWindow = new MainWindow();

            var appSettings = J2MEGamepad.Models.AppSettings.Load();
            bool startMinimized = appSettings.StartMinimized;

            if (startMinimized)
            {
                try
                {
                    new WindowInteropHelper(_mainWindow).EnsureHandle();
                    _mainWindow.Initialize();
                    if (!_mainWindow.StartMinimized())
                    {
                        LogHelper.Info("App", "Tray icon failed, showing window instead");
                        _mainWindow.Show();
                    }
                    else
                    {
                        LogHelper.Info("App", "Started minimized to tray");
                    }
                }
                catch (Exception ex)
                {
                    LogHelper.Error("App", "Minimized startup failed, showing window", ex);
                    _mainWindow.Show();
                }
            }
            else
            {
                LogHelper.Info("App", "Showing MainWindow...");
                _mainWindow.Show();
            }

            var watchdogThread = new Thread(() =>
            {
                if (_windowReadyEvent.WaitOne(TimeSpan.FromSeconds(6)))
                    return;

                s_mutex?.Dispose();
                s_mutex = null;

                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                {
                    try { Process.Start(exePath); } catch { }
                }

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
        PerformShutdown();
        base.OnExit(e);
    }

    public void ReleaseMutexForRestart()
    {
        if (s_mutex != null) { s_mutex.Dispose(); s_mutex = null; }
    }

    public static void PerformShutdown()
    {
        if (s_shutdownPerformed) return;
        s_shutdownPerformed = true;

        LogHelper.Info("App", "PerformShutdown: releasing keyboard and cleaning up");

        try
        {
            GlobalKeyboard?.Dispose();
        }
        catch (Exception ex)
        {
            LogHelper.Error("App", "PerformShutdown keyboard release", ex);
        }

        s_mutex?.Dispose();
        s_mutex = null;
    }
}