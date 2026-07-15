using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using J2MEGamepad.Models;
using J2MEGamepad.NativeMethods;
using J2MEGamepad.Services;

namespace J2MEGamepad.Windows;

public partial class MainWindow : Window
{
    private static readonly Brush s_defaultButtonBrush = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE));

    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID_RESTART = 1;
    private const int MOD_CONTROL = 0x0002;
    private const int MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly GamepadPoller _poller;
    private readonly KeyboardInjector _keyboard;
    private readonly ProfileManager _profiles;
    private readonly ControllerWatchdog _watchdog;
    private readonly OverlayWindow _overlay;
    private readonly System.Windows.Forms.NotifyIcon _trayIcon;
    private bool _isCapturingKey;
    private string? _selectedKeyName;
    private string _lastProfileName = "Default";
    private bool _customKeyWarningShown;
    private bool _terminateWarningShown;
    private bool _javaSeenOnce;
    private System.Timers.Timer? _terminateTimer;
    private bool _savedDelayHoldState = true;
    private bool _isDInputMode;
    private bool _isDInputRemapping;
    private bool _initialized;
    private string _dinputRemapAction = "";
    private bool _isDInputCapturingButton;
    private string _lastControllerName = "";
    private DInputMapping _dinputMapping = new();
    private ComboSettings _comboSettings = new();
    private readonly System.Windows.Threading.DispatcherTimer _disconnectTimer;

    public MainWindow()
    {
        InitializeComponent();

        _keyboard = new KeyboardInjector();
        App.GlobalKeyboard = _keyboard;
        _profiles = new ProfileManager();
        _poller = new GamepadPoller(_keyboard, _profiles);
        _watchdog = new ControllerWatchdog();
        _overlay = new OverlayWindow();

        _disconnectTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(60)
        };
        _disconnectTimer.Tick += DisconnectTimer_Tick;

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "J2ME 360 Gamepad",
            Visible = false
        };
        _trayIcon.Click += TrayIcon_Click;
        _trayIcon.DoubleClick += TrayIcon_DoubleClick;

        var exitItem = new System.Windows.Forms.ToolStripMenuItem("Exit");
        exitItem.Click += Exit_Click;
        var showItem = new System.Windows.Forms.ToolStripMenuItem("Show");
        showItem.Click += Show_Click;
        _trayIcon.ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip();
        _trayIcon.ContextMenuStrip.Items.Add(showItem);
        _trayIcon.ContextMenuStrip.Items.Add(exitItem);

        _watchdog.Connected += OnControllerConnected;
        _watchdog.Disconnected += OnControllerDisconnected;
        _poller.ModeChanged += OnModeChanged;
        _poller.ConnectionChanged += OnConnectionChanged;
        _poller.BackCyclesToggled += OnBackCyclesToggled;
        _poller.DInputModeChanged += OnDInputModeChanged;
        _poller.DInputButtonPressed += OnDInputButtonPressed;
        _poller.ComboTriggered += OnComboTriggered;
        _poller.ComboModifierActive += OnComboModifierActive;
        _poller.ComboModifierInactive += OnComboModifierInactive;
        _poller.ComboConfirmationPending += OnComboConfirmationPending;
        _poller.ComboConfirmationCancelled += OnComboConfirmationCancelled;
        _profiles.ProfilesChanged += OnProfilesChanged;

        RefreshProfileList();

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
        PreviewKeyDown += OnWindowPreviewKeyDown;
        Deactivated += (_, _) => CancelDInputRemapping();
        PreviewMouseDown += OnMainWindowPreviewMouseDown;
    }

    private static void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is Window w)
            StripMinMax(w);
    }

    private const int GWL_STYLE = -16;
    private const int WS_MINIMIZEBOX = 0x20000;
    private const int WS_MAXIMIZEBOX = 0x10000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    internal static void StripMinMax(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int style = GetWindowLong(hwnd, GWL_STYLE);
        style &= ~WS_MINIMIZEBOX;
        style &= ~WS_MAXIMIZEBOX;
        SetWindowLong(hwnd, GWL_STYLE, style);
    }

    private void LogInfo(string msg)
    {
        LogHelper.Info("MainWindow", msg);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LogInfo("OnLoaded entered");
        Initialize();
        // Show window and overlay
        _overlay.Show();
        Activate();
        LogInfo("OnLoaded complete successfully");
    }

    /// <summary>
    /// Sets up all subsystems without making the window visible.
    /// Call from OnLoaded when showing normally, or directly from App when starting minimized.
    /// </summary>
    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            // Signal to startup watchdog that initialization succeeded
            if (Application.Current is App app)
            {
                app.SignalWindowReady();
                LogInfo("SignalWindowReady called");
            }
            else
                LogInfo("SignalWindowReady FAILED: Application.Current is not App");

            // Register CTRL+R global hotkey for restart
            var hwnd = new WindowInteropHelper(this).Handle;
            RegisterHotKey(hwnd, HOTKEY_ID_RESTART, MOD_CONTROL | MOD_NOREPEAT, 0x52); // 0x52 = 'R'

            // Forward window handle to DInput for SetCooperativeLevel (required on XP)
            _poller.DirectInputWindowHandle = hwnd;

            _lastProfileName = _profiles.CurrentProfile.Name;

            var appSettings = Models.AppSettings.Load();

            int diagDelay = appSettings.DiagonalDelayMs;
            DiagDelaySlider.Value = diagDelay;
            _poller.DiagonalDelayMs = diagDelay;
            DiagDelayValue.Text = diagDelay == 0 ? "Off" : diagDelay.ToString();

            DiagDelayHoldCheckbox.IsChecked = appSettings.DiagonalDelayHold;
            _poller.DiagonalDelayHoldCardinals = appSettings.DiagonalDelayHold;
            DiagDelayPerProfileCheckbox.IsChecked = appSettings.DiagonalDelayPerProfile;

            int dirDelay = appSettings.DirectionalDelayMs;
            DirDelaySlider.Value = dirDelay;
            _poller.DirectionalDelayMs = dirDelay;
            DirDelayValue.Text = dirDelay == 0 ? "Off" : dirDelay.ToString();
            if (dirDelay > 0)
            {
                _savedDelayHoldState = appSettings.DiagonalDelayHold;
                DiagDelayHoldCheckbox.IsChecked = false;
                DiagDelayHoldCheckbox.IsEnabled = false;
            }

            int comboDelay = appSettings.ComboActivationDelayMs;
            ComboActDelaySlider.Value = comboDelay;
            _poller.ComboActivationDelayMs = comboDelay;
            ComboActDelayValue.Text = comboDelay == 0 ? "Off" : comboDelay.ToString();

            ApplyDiagonalDelayFromProfile();

            StartMinimizedCheckbox.IsChecked = appSettings.StartMinimized;
            TerminateIfKemulatorClosedCheckbox.IsChecked = appSettings.TerminateIfKemulatorClosed;
            if (appSettings.TerminateIfKemulatorClosed)
            {
                bool javaAlive = System.Diagnostics.Process.GetProcessesByName("java").Length > 0;
                if (!javaAlive && !appSettings.TerminateWarningHidden)
                {
                    var warn = new Window
                    {
                        Title = "KEmulator Not Detected",
                        Width = 440,
                        Height = 203,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        Owner = this,
                        ResizeMode = ResizeMode.NoResize,
                        Background = Brushes.White,
                        Topmost = true
                    };
                    warn.SourceInitialized += (_, _) => StripMinMax(warn);
                    var stack = new StackPanel { Margin = new Thickness(20) };
                    stack.Children.Add(new TextBlock
                    {
                        Text = "KEmulator not detected",
                        FontWeight = FontWeights.Bold,
                        FontSize = 16,
                        Margin = new Thickness(0, 0, 0, 12)
                    });
                    stack.Children.Add(new TextBlock
                    {
                        Text = "The application will open as usual, but it will then latch onto java.exe. If java.exe is terminated, the program will close.",
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 13,
                        Margin = new Thickness(0, 0, 0, 12)
                    });
                    var dontRemind = new CheckBox
                    {
                        Content = "Don't show this warning again",
                        Margin = new Thickness(0, 0, 0, 10)
                    };
                    stack.Children.Add(dontRemind);
                    var okBtn = new Button { Content = "OK", Width = 80, Height = 28, HorizontalAlignment = HorizontalAlignment.Right };
                    okBtn.Click += (_, _) => warn.Close();
                    stack.Children.Add(okBtn);
                    warn.Content = stack;
                    warn.ShowDialog();
                    if (dontRemind.IsChecked == true)
                    {
                        var termS = Models.AppSettings.Load();
                        termS.TerminateWarningHidden = true;
                        termS.Save();
                    }
                }
                StartTerminateMonitoring();
            }

            // Load DInput deadzone from calibration
            var cal = DInputCalibration.Load();
            DInputDeadzoneSlider.Value = cal.DeadzonePercent;
            DInputDeadzoneValue.Text = $"{cal.DeadzonePercent}%";
            _poller.ReloadCalibration();

            _comboSettings = ComboSettings.Load();

            BackCyclesCheckbox.IsChecked = appSettings.BackCycles;
            SkipDefaultCheckbox.IsChecked = appSettings.SkipDefault;
            DisableComboModifierOSDCheckbox.IsChecked = appSettings.DisableComboModifierOSD;
            ComboConfirmationHoldCheckbox.IsChecked = appSettings.ComboConfirmationHold;
            _poller.ComboConfirmationHold = appSettings.ComboConfirmationHold;
            _poller.LeftThumbIsComboModifier = appSettings.LeftThumbIsComboModifier;
            _poller.RightThumbIsComboModifier = appSettings.RightThumbIsComboModifier;
            ComboPerProfileCheckbox.IsChecked = appSettings.ComboPerProfile;

            ApplyComboSettingsFromProfile();

            ShowFirstRunWarning();

            LogInfo("Starting watchdog and poller...");
            _watchdog.Start();
            _poller.Start();
        }
        catch (Exception ex)
        {
            LogInfo($"Initialize exception: {ex.Message}\n{ex.StackTrace}");
            MessageBox.Show($"Startup error:\n{ex.Message}\n\nDetails saved to crash.log.",
                "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowFirstRunWarning()
    {
        var a = Models.AppSettings.Load();
        if (a.FirstRunCompleted) return;

        var dialog = new Window
        {
            Title = "Important Setup Notice",
            Width = 480,
            Height = 295,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = Brushes.White,
            Topmost = true
        };
        dialog.SourceInitialized += (_, _) => StripMinMax(dialog);

        var stack = new StackPanel { Margin = new Thickness(20) };

        stack.Children.Add(new TextBlock
        {
            Text = "KEmulator KeyMap Cleanup Required",
            FontWeight = FontWeights.Bold,
            FontSize = 16,
            Margin = new Thickness(0, 0, 0, 15)
        });

        stack.Children.Add(new TextBlock
        {
            Text = "To prevent duplicate key output, please open KEmulator and:\n\n" +
                   "  1. Go to View → Global Settings → KeyMap\n" +
                   "  2. Find your Xbox 360 Gamepad entry\n" +
                   "  3. Clear ALL entries (they will conflict with this program)\n\n" +
                   "This program handles all gamepad-to-keyboard translation directly.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 15)
        });

        var dontRemind = new CheckBox
        {
            Content = "Don't remind me again",
            Margin = new Thickness(0, 0, 0, 10)
        };
        stack.Children.Add(dontRemind);

        var okButton = new Button
        {
            Content = "OK",
            Width = 80,
            Height = 28,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        okButton.Click += (_, _) => dialog.Close();
        stack.Children.Add(okButton);

        dialog.Content = stack;
        dialog.ShowDialog();

        if (dontRemind.IsChecked == true)
        {
            var s = Models.AppSettings.Load();
            s.FirstRunCompleted = true;
            s.Save();
        }
    }

    /// <summary>
    /// Releases all held keys and disposes services.
    /// Safe to call from any context (WPF shutdown, ProcessExit, taskkill).
    /// </summary>
    public void ShutdownCleanup()
    {
        StopTerminateMonitoring();
        StopDInputRemapAnimation();
        _disconnectTimer.Stop();
        _disconnectTimer.Tick -= DisconnectTimer_Tick;
        _poller.Dispose();
        _watchdog.Dispose();
        _keyboard.Dispose();
        App.GlobalKeyboard = null;
        _profiles.Dispose();
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        _overlay.Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // Unregister CTRL+R hotkey
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(hwnd, HOTKEY_ID_RESTART);
        }
        catch { }

        var s = Models.AppSettings.Load();
        s.DiagonalDelayMs = (int)DiagDelaySlider.Value;
        s.DiagonalDelayHold = DiagDelayHoldCheckbox.IsChecked == true;
        s.DiagonalDelayPerProfile = DiagDelayPerProfileCheckbox.IsChecked == true;
        s.DirectionalDelayMs = (int)DirDelaySlider.Value;
        s.ComboActivationDelayMs = (int)ComboActDelaySlider.Value;
        s.StartMinimized = StartMinimizedCheckbox.IsChecked == true;
        s.TerminateIfKemulatorClosed = TerminateIfKemulatorClosedCheckbox.IsChecked == true;
        s.BackCycles = BackCyclesCheckbox.IsChecked == true;
        s.SkipDefault = SkipDefaultCheckbox.IsChecked == true;
        s.DisableComboModifierOSD = DisableComboModifierOSDCheckbox.IsChecked == true;
        s.ComboConfirmationHold = ComboConfirmationHoldCheckbox.IsChecked == true;
        s.ComboPerProfile = ComboPerProfileCheckbox.IsChecked == true;
        if (ComboPerProfileCheckbox.IsChecked != true)
        {
            s.LeftThumbIsComboModifier = _poller.LeftThumbIsComboModifier;
            s.RightThumbIsComboModifier = _poller.RightThumbIsComboModifier;
        }
        s.Save();

        if (DiagDelayPerProfileCheckbox.IsChecked == true)
        {
            _profiles.CurrentProfile.DiagonalDelayHoldCardinals = DiagDelayHoldCheckbox.IsChecked == true;
            _profiles.SaveProfile(_profiles.CurrentProfile);
        }

        ShutdownCleanup();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID_RESTART)
        {
            RestartApplication();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void RestartApplication()
    {
        LogInfo("CTRL+R restart requested");
        try
        {
            ShutdownCleanup();

            // Release mutex so the new instance can acquire it
            if (Application.Current is App app)
                app.ReleaseMutexForRestart();

            // Start new instance
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exePath))
                Process.Start(exePath);

            // Use Environment.Exit to bypass OnClosed and avoid duplicate dispose
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            LogHelper.Error("MainWindow", "RestartApplication", ex);
            MessageBox.Show($"Restart failed: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DiagDelaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_poller == null) return;
        int val = (int)DiagDelaySlider.Value;
        _poller.DiagonalDelayMs = val;
        DiagDelayValue.Text = val == 0 ? "Off" : val.ToString();
    }

    private void DInputDeadzoneSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int pct = Math.Max(10, Math.Min(80, (int)DInputDeadzoneSlider.Value));
        if (DInputDeadzoneValue == null) return;
        DInputDeadzoneValue.Text = $"{pct}%";
        var cal = DInputCalibration.Load();
        cal.DeadzonePercent = pct;
        cal.Save();
        _poller?.ReloadCalibration();
    }

    private void DiagDelayHold_Changed(object sender, RoutedEventArgs e)
    {
        if (_poller == null) return;
        bool hold = DiagDelayHoldCheckbox.IsChecked == true;
        _poller.DiagonalDelayHoldCardinals = hold;
        if (DiagDelayHoldCheckbox.IsEnabled)
            _savedDelayHoldState = hold;
        if (DiagDelayPerProfileCheckbox.IsChecked == true)
            _profiles.CurrentProfile.DiagonalDelayHoldCardinals = hold;
    }

    private void DiagDelayPerProfile_Changed(object sender, RoutedEventArgs e)
    {
        if (_poller == null) return;
        ApplyDiagonalDelayFromProfile();
    }

    private void DirDelaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_poller == null) return;
        int val = (int)DirDelaySlider.Value;
        _poller.DirectionalDelayMs = val;
        DirDelayValue.Text = val == 0 ? "Off" : val.ToString();

        if (DiagDelayPerProfileCheckbox.IsChecked == true)
        {
            _profiles.CurrentProfile.DirectionalDelayMs = val;
            DiagDelayHoldCheckbox.IsEnabled = val == 0;
            if (val > 0) DiagDelayHoldCheckbox.IsChecked = false;
            return;
        }

        if (val > 0 && DiagDelayHoldCheckbox.IsEnabled)
        {
            _savedDelayHoldState = DiagDelayHoldCheckbox.IsChecked == true;
            DiagDelayHoldCheckbox.IsChecked = false;
            DiagDelayHoldCheckbox.IsEnabled = false;
        }
        else if (val == 0)
        {
            DiagDelayHoldCheckbox.IsEnabled = true;
            DiagDelayHoldCheckbox.IsChecked = _savedDelayHoldState;
        }
    }

    private void ComboActDelaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_poller == null) return;
        int val = (int)ComboActDelaySlider.Value;
        _poller.ComboActivationDelayMs = val;
        ComboActDelayValue.Text = val == 0 ? "Off" : val.ToString();
    }

    private void ApplyDiagonalDelayFromProfile()
    {
        if (DiagDelayPerProfileCheckbox.IsChecked != true)
            return;

        var profile = _profiles.CurrentProfile;

        int delayMs = profile.DiagonalDelayMs;
        DiagDelaySlider.Value = delayMs;
        _poller.DiagonalDelayMs = delayMs;
        DiagDelayValue.Text = delayMs == 0 ? "Off" : delayMs.ToString();

        int dirDelayMs = profile.DirectionalDelayMs;
        DirDelaySlider.Value = dirDelayMs;
        _poller.DirectionalDelayMs = dirDelayMs;
        DirDelayValue.Text = dirDelayMs == 0 ? "Off" : dirDelayMs.ToString();

        // Restore hold cardinal state from profile (overrides DirDelaySlider_ValueChanged side-effects)
        bool hold = profile.DiagonalDelayHoldCardinals;
        _poller.DiagonalDelayHoldCardinals = hold;
        DiagDelayHoldCheckbox.IsChecked = hold;
        DiagDelayHoldCheckbox.IsEnabled = dirDelayMs == 0;
    }

    private void ComboConfirmationHold_Changed(object sender, RoutedEventArgs e)
    {
        bool hold = ComboConfirmationHoldCheckbox.IsChecked == true;
        _poller.ComboConfirmationHold = hold;
        var s = Models.AppSettings.Load();
        s.ComboConfirmationHold = hold;
        s.Save();
    }

    private void OnComboConfirmationPending(string osdText)
    {
        Dispatcher.Invoke(() =>
        {
            _overlay.HideComboModifier();
            _overlay.Show();
            _overlay.ShowComboConfirmation(osdText);
        });
    }

    private void OnComboConfirmationCancelled()
    {
        Dispatcher.Invoke(() =>
        {
            _overlay.HideComboConfirmation();
            if (DisableComboModifierOSDCheckbox.IsChecked != true)
            {
                _overlay.HideComboModifier();
                _overlay.ShowComboModifier();
            }
        });
    }

    private void ComboPerProfile_Changed(object sender, RoutedEventArgs e)
    {
        var comboSet = Models.AppSettings.Load();
        comboSet.ComboPerProfile = ComboPerProfileCheckbox.IsChecked == true;
        comboSet.Save();
        ApplyComboSettingsFromProfile();
    }

    private bool GetCurrentThumbIsComboModifier(string thumbName)
    {
        return thumbName == "LeftThumb" ? _poller.LeftThumbIsComboModifier : _poller.RightThumbIsComboModifier;
    }

    private void ThumbComboModifierCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        if (_selectedKeyName == null || (!ComboActionNames.Contains(_selectedKeyName) && _selectedKeyName != "LeftThumb" && _selectedKeyName != "RightThumb"))
            return;

        bool isChecked = ThumbComboModifierCheckbox.IsChecked == true;

        if (ComboPerProfileCheckbox.IsChecked == true)
        {
            var profile = _profiles.CurrentProfile;
            if (_selectedKeyName == "LeftThumb")
                profile.LeftThumbIsComboModifier = isChecked;
            else
                profile.RightThumbIsComboModifier = isChecked;
            _profiles.SaveProfile(profile);
            _poller.LeftThumbIsComboModifier = profile.LeftThumbIsComboModifier;
            _poller.RightThumbIsComboModifier = profile.RightThumbIsComboModifier;
        }
        else
        {
            var s = Models.AppSettings.Load();
            if (_selectedKeyName == "LeftThumb")
                s.LeftThumbIsComboModifier = isChecked;
            else
                s.RightThumbIsComboModifier = isChecked;
            s.Save();
            _poller.LeftThumbIsComboModifier = s.LeftThumbIsComboModifier;
            _poller.RightThumbIsComboModifier = s.RightThumbIsComboModifier;
        }
    }

    private void ApplyComboSettingsFromProfile()
    {
        if (ComboPerProfileCheckbox.IsChecked == true)
        {
            var profile = _profiles.CurrentProfile;
            _comboSettings.Actions = new Dictionary<string, List<ushort>>(profile.ComboActions);
            _comboSettings.OSDNames = new Dictionary<string, string>(profile.ComboOSDNames);
            _comboSettings.ExecPaths = new Dictionary<string, string>(profile.ComboExecPaths);
            _poller.ApplyComboSettings(profile.ComboActions, profile.ComboOSDNames, profile.ComboExecPaths);
            _poller.LeftThumbIsComboModifier = profile.LeftThumbIsComboModifier;
            _poller.RightThumbIsComboModifier = profile.RightThumbIsComboModifier;
        }
        else
        {
            _comboSettings = ComboSettings.Load();
            _poller.ReloadComboSettings();
            var s = Models.AppSettings.Load();
            _poller.LeftThumbIsComboModifier = s.LeftThumbIsComboModifier;
            _poller.RightThumbIsComboModifier = s.RightThumbIsComboModifier;
        }

        if (_selectedKeyName != null)
        {
            if (ComboActionNames.Contains(_selectedKeyName) || ComboExecActionNames.Contains(_selectedKeyName))
            {
                if (_comboSettings.OSDNames.TryGetValue(_selectedKeyName, out var osdName))
                    ComboOSDNameBox.Text = osdName;
                else
                    ComboOSDNameBox.Text = "";
                if (ComboExecActionNames.Contains(_selectedKeyName) && _comboSettings.ExecPaths.TryGetValue(_selectedKeyName, out var execPath))
                    ExecPathBox.Text = execPath;
                else
                    ExecPathBox.Text = "";
            }
        }
        UpdateCurrentMappingDisplay();
    }

    private void PersistComboSettings()
    {
        if (ComboPerProfileCheckbox.IsChecked == true)
        {
            var profile = _profiles.CurrentProfile;
            profile.ComboActions = new Dictionary<string, List<ushort>>(_comboSettings.Actions);
            profile.ComboOSDNames = new Dictionary<string, string>(_comboSettings.OSDNames);
            profile.ComboExecPaths = new Dictionary<string, string>(_comboSettings.ExecPaths);
            profile.LeftThumbIsComboModifier = _poller.LeftThumbIsComboModifier;
            profile.RightThumbIsComboModifier = _poller.RightThumbIsComboModifier;
            _profiles.SaveProfile(profile);
            _poller.ApplyComboSettings(profile.ComboActions, profile.ComboOSDNames, profile.ComboExecPaths);
        }
        else
        {
            _comboSettings.Save();
            _poller.ReloadComboSettings();
        }
    }

    private void OnProfilesChanged()
    {
        Dispatcher.Invoke(() =>
        {
            try { RefreshProfileList(); } catch { }
        });
    }

    private void OnBackCyclesToggled(bool enabled)
    {
        Dispatcher.Invoke(() =>
        {
            BackCyclesCheckbox.IsChecked = enabled;
            _overlay.Show();
            _overlay.ShowOsd(enabled ? "BackCycles: ON" : "BackCycles: OFF");
        });
    }

    private void OnControllerConnected()
    {
        Dispatcher.Invoke(() =>
        {
            _disconnectTimer.Stop();
            _overlay.HideDisconnected();
            if (_isDInputMode)
            {
                _lastControllerName = GetDInputDeviceName();
                ControllerStatus.Text = $"{_lastControllerName} connected";
                ControllerStatus.Foreground = System.Windows.Media.Brushes.Orange;
            }
            else
            {
                _lastControllerName = "Xbox 360 Controller";
                ControllerStatus.Text = $"{_lastControllerName} connected";
                ControllerStatus.Foreground = System.Windows.Media.Brushes.Green;
            }
        });
    }

    private void OnControllerDisconnected()
    {
        Dispatcher.Invoke(() =>
        {
            if (_isDInputMode) return;
            _isDInputMode = false;
            DInputActionPanel.Visibility = Visibility.Collapsed;
            DInputDeadzonePanel.Visibility = Visibility.Collapsed;
            StopDInputRemapAnimation();
            _isDInputCapturingButton = false;
            _isDInputRemapping = false;
            _dinputRemapAction = "";
            _poller.IsRemapping = false;
            DInputRemapButton.Content = "Remap button";
            ControllerStatus.Text = "Please connect Xbox/DInput controller";
            ControllerStatus.Foreground = System.Windows.Media.Brushes.Red;
        });
    }

    private void OnConnectionChanged(bool connected)
    {
        Dispatcher.Invoke(() =>
        {
            if (connected)
            {
                _disconnectTimer.Stop();
                _overlay.HideDisconnected();
                if (_isDInputMode)
                {
                    _lastControllerName = GetDInputDeviceName();
                    ControllerStatus.Text = $"{_lastControllerName} connected";
                    ControllerStatus.Foreground = System.Windows.Media.Brushes.Orange;
                    DInputActionPanel.Visibility = Visibility.Visible;
                    DInputDeadzonePanel.Visibility = Visibility.Visible;
                    ShowDInputListItems(true);
                    UpdateDInputListLabels();
                }
                else
                {
                    _lastControllerName = "Xbox 360 Controller";
                    ControllerStatus.Text = $"{_lastControllerName} connected";
                    ControllerStatus.Foreground = System.Windows.Media.Brushes.Green;
                    DInputActionPanel.Visibility = Visibility.Collapsed;
                    DInputDeadzonePanel.Visibility = Visibility.Collapsed;
                    ShowDInputListItems(false);
                }
            }
            else
            {
                _isDInputMode = false;
                DInputActionPanel.Visibility = Visibility.Collapsed;
                DInputDeadzonePanel.Visibility = Visibility.Collapsed;
                ShowDInputListItems(false);
                StopDInputRemapAnimation();
                _isDInputCapturingButton = false;
                _isDInputRemapping = false;
                _dinputRemapAction = "";
                _poller.IsRemapping = false;
                DInputRemapButton.Content = "Remap button";
                ControllerStatus.Text = "Please connect Xbox/DInput controller";
                ControllerStatus.Foreground = System.Windows.Media.Brushes.Red;
                ShowDisconnectedWarning();
            }
        });
    }

    private void OnDInputModeChanged(string mode)
    {
        Dispatcher.Invoke(() =>
        {
            _isDInputMode = true;
            DInputActionPanel.Visibility = Visibility.Visible;
            DInputDeadzonePanel.Visibility = Visibility.Visible;
            _lastControllerName = GetDInputDeviceName();
            ControllerStatus.Text = $"{_lastControllerName} connected";
            ControllerStatus.Foreground = System.Windows.Media.Brushes.Orange;
            _overlay.HideDisconnected();
            ShowDInputListItems(true);
            UpdateDInputListLabels();
        });
    }

    private void ShowDInputListItems(bool show)
    {
        var vis = show ? Visibility.Visible : Visibility.Collapsed;
        DInputBackItem.Visibility = vis;
        DInputStartItem.Visibility = vis;
        DInputDpadUpItem.Visibility = vis;
        DInputDpadDownItem.Visibility = vis;
        DInputDpadLeftItem.Visibility = vis;
        DInputDpadRightItem.Visibility = vis;
        if (!show)
        {
            // Restore XInput labels when switching away from DInput
            RestoreXInputListLabels();
            if (KeyListBox.SelectedItem is ListBoxItem item &&
                (item.Tag as string) is string tag &&
                (tag == "Back" || tag == "Start" || tag.StartsWith("DPad")))
            {
                KeyListBox.SelectedItem = null;
                _selectedKeyName = null;
                UpdateCurrentMappingDisplay();
            }
        }
    }

    private static readonly Dictionary<string, int> DInputButtonIndices = new()
    {
        ["X"] = 0, ["A"] = 1, ["B"] = 2, ["Y"] = 3,
        ["LB"] = 4, ["RB"] = 5, ["LT"] = 6, ["RT"] = 7,
        ["RightThumb"] = 11,
        ["Back"] = 8, ["Start"] = 9, ["LeftThumb"] = 10,
        ["DPadUp"] = 12, ["DPadDown"] = 13, ["DPadLeft"] = 14, ["DPadRight"] = 15,
    };

    private void UpdateDInputListLabels()
    {
        ReloadDInputMapping();
        var mapping = _dinputMapping;

        foreach (ListBoxItem item in KeyListBox.Items)
        {
            if (item.Tag is string tag && DInputButtonIndices.TryGetValue(tag, out int _))
            {
                if (mapping.ActionToButton.TryGetValue(tag, out int actualIdx))
                    item.Content = $"{tag} ({actualIdx + 1})";
                else
                    item.Content = $"{tag} (N/A)";
            }
        }
    }

    private void RestoreXInputListLabels()
    {
        foreach (ListBoxItem item in KeyListBox.Items)
        {
            if (item.Tag is string tag)
            {
                item.Content = tag switch
                {
                    "Y" => "Y Button",
                    "X" => "X Button",
                    "A" => "A Button",
                    "B" => "B Button",
                    "LB" => "LB",
                    "RB" => "RB",
                    "LT" => "LT",
                    "RT" => "RT",
                    "RightThumb" => "Right Stick Button",
                    "LeftThumb" => "Left Stick Button",
                    "Back" => "SELECT (Back)",
                    "Start" => "START",
                    "DPadUp" => "D-Pad Up",
                    "DPadDown" => "D-Pad Down",
                    "DPadLeft" => "D-Pad Left",
                    "DPadRight" => "D-Pad Right",
                    _ => (string)item.Content
                };
            }
        }
    }

    private void ShowDisconnectedWarning()
    {
        _disconnectTimer.Stop();
        _overlay.Show();
        string msg = string.IsNullOrEmpty(_lastControllerName)
            ? "CONTROLLER NOT DETECTED"
            : $"{_lastControllerName.ToUpperInvariant()}\nNOT DETECTED";
        _overlay.SetDisconnectedText(msg);
        _overlay.ShowDisconnected();
        _lastControllerName = "";
        _disconnectTimer.Start();
    }

    private void DisconnectTimer_Tick(object? sender, EventArgs e)
    {
        _disconnectTimer.Stop();
        _overlay.SetDisconnectedText("WAITING FOR CONTROLLER...");
    }

    private void OnModeChanged(string modeName)
    {
        Dispatcher.Invoke(() =>
        {
            DpadModeText.Text = _poller.CurrentDPadMode switch
            {
                DPadMode.Pad => "PAD Mode",
                DPadMode.Keypad => "KEYPAD Mode",
                DPadMode.PadDiagonal => "PAD+DIAGONAL Mode",
                _ => "Unknown"
            };
            ProfileText.Text = _profiles.CurrentProfile.Name;
            ApplyDiagonalDelayFromProfile();
            ApplyComboSettingsFromProfile();

            _overlay.Show();

            if (modeName.StartsWith("YXAB:"))
            {
                var newName = _profiles.CurrentProfile.Name;
                _overlay.ShowOsdSwap(_lastProfileName, newName);
                _lastProfileName = newName;
            }
            else
            {
                _overlay.ShowOsd(modeName);
            }
        });
    }

    private void OnComboModifierActive(string modName)
    {
        Dispatcher.Invoke(() =>
        {
            if (DisableComboModifierOSDCheckbox.IsChecked == true) return;
            _overlay.Show();
            _overlay.ShowComboModifier();
        });
    }

    private void OnComboModifierInactive()
    {
        Dispatcher.Invoke(() =>
        {
            _overlay.HideComboModifier();
        });
    }

    private void DisableComboModifierOSD_Changed(object sender, RoutedEventArgs e)
    {
        var s = Models.AppSettings.Load();
        s.DisableComboModifierOSD = DisableComboModifierOSDCheckbox.IsChecked == true;
        s.Save();
        if (DisableComboModifierOSDCheckbox.IsChecked == true)
            _overlay.HideComboModifier();
    }

    private void OnComboTriggered(string osdText)
    {
        Dispatcher.Invoke(() =>
        {
            if (DisableComboModifierOSDCheckbox.IsChecked == true) return;
            _overlay.HideComboConfirmation();
            _overlay.HideComboModifier();
            _overlay.Show();
            _overlay.ShowOsd(osdText);
        });
    }

    private void OnDInputButtonPressed(int buttonIndex)
    {
        string capturedAction = _dinputRemapAction;
        Dispatcher.Invoke(() =>
        {
            if (!_isDInputRemapping || string.IsNullOrEmpty(capturedAction))
                return;

            StopDInputRemapAnimation();
            _isDInputRemapping = false;
            _isDInputCapturingButton = false;
            _dinputRemapAction = "";

            ReloadDInputMapping();
            var mapping = _dinputMapping;

            bool conflict = false;
            string? conflictAction = null;
            foreach (var kvp in mapping.ActionToButton)
            {
                if (kvp.Value == buttonIndex && kvp.Key != capturedAction)
                {
                    conflict = true;
                    conflictAction = kvp.Key;
                    break;
                }
            }

            if (conflict && conflictAction != null)
                mapping.ActionToButton.Remove(conflictAction);

            mapping.ActionToButton[capturedAction] = buttonIndex;
            mapping.Save();

            _poller.ReloadDInputMapping();

            DInputRemapButton.Content = "Remap button";
            DInputRemapButton.Background = s_defaultButtonBrush;

            UpdateCurrentMappingDisplay();
            UpdateDInputListLabels();

            // Auto-advance to next visible item in list
            int currentIdx = KeyListBox.SelectedIndex;
            for (int i = currentIdx + 1; i < KeyListBox.Items.Count; i++)
            {
                if (KeyListBox.Items[i] is ListBoxItem nextItem && nextItem.Visibility == Visibility.Visible)
                {
                    KeyListBox.SelectedItem = nextItem;
                    _dinputRemapAction = nextItem.Tag as string ?? "";
                    _isDInputRemapping = true;
                    _isDInputCapturingButton = true;
                    _poller.IsRemapping = true;
                    StartDInputRemapAnimation();
                    DInputRemapButton.Content = $"Remapping: \"{_dinputRemapAction}\"...";
                    break;
                }
            }
        });
    }

    private void DInputRemapButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isDInputMode)
            return;

        if (KeyListBox.SelectedItem is ListBoxItem item)
        {
            var action = item.Tag as string;
            if (string.IsNullOrEmpty(action))
                return;

            if (_isDInputCapturingButton)
            {
                StopDInputRemapAnimation();
                _isDInputRemapping = false;
                _isDInputCapturingButton = false;
                _dinputRemapAction = "";
                _poller.IsRemapping = false;
                DInputRemapButton.Content = "Remap button";
                DInputRemapButton.Background = s_defaultButtonBrush;
                return;
            }

            _dinputRemapAction = action;
            _isDInputRemapping = true;
            _isDInputCapturingButton = true;
            _poller.IsRemapping = true;

            StartDInputRemapAnimation();
            DInputRemapButton.Content = $"Remapping: \"{action}\"...";
        }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_isDInputCapturingButton && e.Key == Key.Escape)
        {
            CancelDInputRemapping();
            e.Handled = true;
        }
    }

    private void StartDInputRemapAnimation()
    {
        DInputRemapButton.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xCC, 0x66));
    }

    private void StopDInputRemapAnimation()
    {
        DInputRemapButton.Background = s_defaultButtonBrush;
    }

    private void CancelDInputRemapping()
    {
        if (!_isDInputRemapping && !_isDInputCapturingButton) return;
        StopDInputRemapAnimation();
        _isDInputRemapping = false;
        _isDInputCapturingButton = false;
        _dinputRemapAction = "";
        _poller.IsRemapping = false;
        DInputRemapButton.Content = "Remap button";
        DInputRemapButton.Background = s_defaultButtonBrush;
    }

    private void OnMainWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        bool anyCapture = _isDInputRemapping || _isDInputCapturingButton || _isCapturingCombo;
        if (!anyCapture) return;

        var source = e.OriginalSource as DependencyObject;
        if (source != null)
        {
            for (var el = source; el != null; el = VisualTreeHelper.GetParent(el))
            {
                if (el == DInputRemapButton) return;
            }
        }

        if (_isDInputRemapping || _isDInputCapturingButton)
            CancelDInputRemapping();
        if (_isCapturingCombo)
            CancelComboCapture();
    }

    private void CalibrateDpadButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isDInputMode) return;

        _poller.Stop();
        _poller.IsRemapping = true;

        var dialog = new Window
        {
            Title = "D-Pad Calibration Wizard",
            Width = 350,
            Height = 317,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = Brushes.White,
            Topmost = true
        };
        dialog.SourceInitialized += (_, _) => StripMinMax(dialog);

        var stack = new StackPanel { Margin = new Thickness(16) };

        stack.Children.Add(new TextBlock
        {
            Text = "D-Pad Calibration Wizard",
            FontWeight = FontWeights.Bold,
            FontSize = 18,
            Margin = new Thickness(0, 0, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Center
        });

        var stepText = new TextBlock
        {
            Text = "Step 1/9: Up",
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            Foreground = Brushes.DodgerBlue,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 2)
        };
        stack.Children.Add(stepText);

        var instructionText = new TextBlock
        {
            Text = "Press and hold Up for 2 seconds...",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 6)
        };
        stack.Children.Add(instructionText);

        // 3x3 D-Pad grid
        var dirAbbrev = new Dictionary<string, string>
        {
            ["UpLeft"] = "UL", ["Up"] = "Up", ["UpRight"] = "UR",
            ["Left"] = "L", ["Center"] = "C", ["Right"] = "R",
            ["DownLeft"] = "DL", ["Down"] = "Dn", ["DownRight"] = "DR"
        };

        var dirNames = new[] { "UpLeft", "Up", "UpRight", "Left", "Center", "Right", "DownLeft", "Down", "DownRight" };
        var dirRows = new[] { 0, 0, 0, 1, 1, 1, 2, 2, 2 };
        var dirCols = new[] { 0, 1, 2, 0, 1, 2, 0, 1, 2 };

        var dpadGrid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 6)
        };
        for (int i = 0; i < 3; i++)
        {
            dpadGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            dpadGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var dirBoxes = new List<Border>();
        for (int idx = 0; idx < dirNames.Length; idx++)
        {
            var name = dirNames[idx];
            var row = dirRows[idx];
            var col = dirCols[idx];
            var box = new Border
            {
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(2),
                Background = Brushes.LightGray,
                Tag = name,
                Child = new TextBlock
                {
                    Text = dirAbbrev[name],
                    FontWeight = FontWeights.Bold,
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    MinWidth = 24
                }
            };
            Grid.SetRow(box, row);
            Grid.SetColumn(box, col);
            dirBoxes.Add(box);
            dpadGrid.Children.Add(box);
        }
        stack.Children.Add(dpadGrid);

        var progressBar = new System.Windows.Controls.ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Height = 14,
            Margin = new Thickness(0, 0, 0, 3)
        };
        stack.Children.Add(progressBar);

        var countdownText = new TextBlock
        {
            Text = "---",
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.DodgerBlue,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 3)
        };
        stack.Children.Add(countdownText);

        var statusText = new TextBlock
        {
            Text = "Press Up on the D-Pad...",
            FontSize = 12,
            Foreground = Brushes.DodgerBlue,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4)
        };
        stack.Children.Add(statusText);

        var closeButton = new Button
        {
            Content = "Finish",
            Width = 80,
            Height = 26,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsEnabled = false
        };
        closeButton.Click += (_, _) => dialog.Close();
        stack.Children.Add(closeButton);

        dialog.Content = stack;

        var cal = DInputCalibration.Load();

        // Auto-record center from current rest position (WinMM only — reliable across controller switches)
        {
            int cx = 32767, cy = 32767;
            var ji = new JOYINFOEX();
            ji.dwSize = Marshal.SizeOf(typeof(JOYINFOEX));
            ji.dwFlags = (int)JoyInput.JOY_RETURNALL;
            if (JoyInput.GetPosEx(0, ref ji) == JoyInput.JOYERR_NOERROR)
            {
                cx = ji.dwXpos;
                cy = ji.dwYpos;
            }
            cal.CenterX = cx;
            cal.CenterY = cy;
        }

        // Clockwise order (center auto-recorded above)
        var steps = new[] { "Up", "UpRight", "Right", "DownRight", "Down", "DownLeft", "Left", "UpLeft" };
        // Map step names to their index in dirBoxes for highlighting
        var stepToBoxIndex = new Dictionary<string, int>();
        for (int i = 0; i < dirNames.Length; i++)
            stepToBoxIndex[dirNames[i]] = i;

        int currentStep = 0;
        const double stepDuration = 1.0;
        double elapsed = 0;
        long sampleSumX = 0, sampleSumY = 0;
        int sampleCount = 0;
        bool complete = false;
        bool recording = false;
        DateTime stepStartTime = DateTime.UtcNow;
        bool failed = false;
        int disconnectCount = 0;
        var recordedSteps = new System.Collections.Generic.HashSet<string>();

        var calTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };

        calTimer.Tick += (_, _) =>
        {
            if (complete) return;

            // Always use WinMM during calibration — DirectInput can lag/stale when
            // the controller type switched (DInput→XInput→DInput) while poller is stopped.
            int xPos = 32767, yPos = 32767;
            var ji = new JOYINFOEX();
            ji.dwSize = Marshal.SizeOf(typeof(JOYINFOEX));
            ji.dwFlags = (int)JoyInput.JOY_RETURNALL;
            bool deviceOk = JoyInput.GetPosEx(0, ref ji) == JoyInput.JOYERR_NOERROR;
            if (deviceOk)
            {
                xPos = ji.dwXpos;
                yPos = ji.dwYpos;
                disconnectCount = 0;
            }
            else if (++disconnectCount >= 10)
            {
                dialog.Close();
                return;
            }

            int cx = cal.CenterX;
            int cy = cal.CenterY;
            int dx = xPos - cx;
            int dy = yPos - cy;

            string dirName = steps[currentStep];
            stepText.Text = $"Step {currentStep + 1}/{steps.Length}";
            instructionText.Text = $"Press and hold {dirName} for 1 second...";

            if (stepToBoxIndex.TryGetValue(dirName, out int boxIdx))
            {
                for (int i = 0; i < dirBoxes.Count; i++)
                    dirBoxes[i].Background = i == boxIdx ? Brushes.LightSkyBlue : Brushes.LightGray;
            }

            // Accept any strong deflection (non-standard gamepads may have inverted/swapped axes)
            bool detected = Math.Abs(dx) > 13000 || Math.Abs(dy) > 13000;

            // Failure state — shown until user re-presses the correct direction
            if (failed)
            {
                countdownText.Text = "FAIL";
                countdownText.Foreground = Brushes.Red;
                statusText.Text = "D-Pad released. Press the direction again to retry.";
                statusText.Foreground = Brushes.Red;
                if (detected)
                {
                    if (IsDuplicateCalibrationInput(xPos, yPos, cx, cy, recordedSteps, cal))
                    {
                        countdownText.Text = "SAME INPUT";
                        countdownText.Foreground = Brushes.Red;
                        statusText.Text = $"Same as prior step — press DIFFERENT for \"{dirName}\".";
                        statusText.Foreground = Brushes.Red;
                        progressBar.Value = 0;
                        return;
                    }
                    failed = false;
                    recording = true;
                    stepStartTime = DateTime.UtcNow;
                    sampleSumX = 0;
                    sampleSumY = 0;
                    sampleCount = 0;
                    elapsed = 0;
                    progressBar.Value = 0;
                    countdownText.Foreground = Brushes.DodgerBlue;
                    statusText.Text = $"Recording {dirName}...";
                    statusText.Foreground = Brushes.Green;
                }
                return;
            }

            if (recording)
            {
                if (!detected)
                {
                    failed = true;
                    recording = false;
                    elapsed = 0;
                    return;
                }

                if (IsDuplicateCalibrationInput(xPos, yPos, cx, cy, recordedSteps, cal))
                {
                    countdownText.Text = "SAME INPUT";
                    countdownText.Foreground = Brushes.Red;
                    statusText.Text = $"Same as prior step — press DIFFERENT for \"{dirName}\".";
                    statusText.Foreground = Brushes.Red;
                    progressBar.Value = 0;
                    recording = false;
                    elapsed = 0;
                    return;
                }

                sampleSumX += xPos;
                sampleSumY += yPos;
                sampleCount++;

                elapsed = (DateTime.UtcNow - stepStartTime).TotalSeconds;
                double pct = Math.Min(elapsed / stepDuration * 100.0, 100.0);
                progressBar.Value = pct;
                countdownText.Text = Math.Max(0, stepDuration - elapsed).ToString("F1");

                if (elapsed < stepDuration)
                    return;

                progressBar.Foreground = SystemColors.HighlightBrush;
                int avgX = (int)(sampleSumX / sampleCount);
                int avgY = (int)(sampleSumY / sampleCount);

                recordedSteps.Add(dirName);

                switch (dirName)
                {
                    case "Up": cal.UpX = avgX; cal.UpY = avgY; break;
                    case "Down": cal.DownX = avgX; cal.DownY = avgY; break;
                    case "Left": cal.LeftX = avgX; cal.LeftY = avgY; break;
                    case "Right": cal.RightX = avgX; cal.RightY = avgY; break;
                    case "UpLeft": cal.UpLeftX = avgX; cal.UpLeftY = avgY; break;
                    case "UpRight": cal.UpRightX = avgX; cal.UpRightY = avgY; break;
                    case "DownLeft": cal.DownLeftX = avgX; cal.DownLeftY = avgY; break;
                    case "DownRight": cal.DownRightX = avgX; cal.DownRightY = avgY; break;
                }

                statusText.Text = $"{dirName} done";
                statusText.Foreground = Brushes.Green;
                progressBar.Value = 100;
                recording = false;
                currentStep++;
                elapsed = 0;

                if (currentStep >= steps.Length)
                {
                    complete = true;
                    calTimer.Stop();
                    cal.Save();
                    _poller.ReloadCalibration();

                    stepText.Text = "Calibration Complete!";
                    instructionText.Text = "D-Pad calibration saved and applied.";
                    countdownText.Text = "Done";
                    statusText.Text = "All 8 directions + center calibrated.";
                    statusText.Foreground = Brushes.Green;
                    closeButton.IsEnabled = true;
                    closeButton.Content = "Close";

                    for (int i = 0; i < dirBoxes.Count; i++)
                        dirBoxes[i].Background = Brushes.LightGreen;
                }
                else
                {
                    statusText.Text = $"Press {steps[currentStep]}...";
                    statusText.Foreground = Brushes.DodgerBlue;
                    progressBar.Foreground = SystemColors.HighlightBrush;
                    progressBar.Value = 0;
                    countdownText.Text = "---";
                }
                return;
            }

            // Wait for D-Pad input — immediate start (no debounce)
            if (detected)
            {
                if (IsDuplicateCalibrationInput(xPos, yPos, cx, cy, recordedSteps, cal))
                {
                    countdownText.Text = "SAME INPUT";
                    countdownText.Foreground = Brushes.Red;
                    statusText.Text = $"Same as prior step — press DIFFERENT for \"{dirName}\".";
                    statusText.Foreground = Brushes.Red;
                    progressBar.Value = 0;
                    return;
                }
                recording = true;
                stepStartTime = DateTime.UtcNow;
                sampleSumX = 0;
                sampleSumY = 0;
                sampleCount = 0;
                elapsed = 0;
                progressBar.Foreground = SystemColors.HighlightBrush;
                statusText.Text = $"Recording {dirName}...";
                statusText.Foreground = Brushes.Green;
            }
            else
            {
                statusText.Text = $"Press {dirName}...";
                statusText.Foreground = Brushes.DodgerBlue;
                progressBar.Foreground = SystemColors.HighlightBrush;
                progressBar.Value = 0;
                countdownText.Text = "---";
            }
        };

        dialog.Closed += (_, _) => { calTimer.Stop(); };
        calTimer.Start();
        dialog.ShowDialog();
        calTimer.Stop();
        _poller.IsRemapping = false;
        _poller.Start();
    }

    private static bool IsDuplicateCalibrationInput(int xPos, int yPos, int cx, int cy, HashSet<string> recordedSteps, DInputCalibration cal)
    {
        if (recordedSteps.Count == 0) return false;

        double newAngle = Math.Atan2(-(yPos - cy), (xPos - cx)) * 180.0 / Math.PI;
        if (newAngle < 0) newAngle += 360;

        foreach (var prevStep in recordedSteps)
        {
            int px = 0, py = 0;
            switch (prevStep)
            {
                case "Up": px = cal.UpX; py = cal.UpY; break;
                case "Down": px = cal.DownX; py = cal.DownY; break;
                case "Left": px = cal.LeftX; py = cal.LeftY; break;
                case "Right": px = cal.RightX; py = cal.RightY; break;
                case "UpLeft": px = cal.UpLeftX; py = cal.UpLeftY; break;
                case "UpRight": px = cal.UpRightX; py = cal.UpRightY; break;
                case "DownLeft": px = cal.DownLeftX; py = cal.DownLeftY; break;
                case "DownRight": px = cal.DownRightX; py = cal.DownRightY; break;
            }
            double prevAngle = Math.Atan2(-(py - cy), (px - cx)) * 180.0 / Math.PI;
            if (prevAngle < 0) prevAngle += 360;

            double diff = Math.Abs(newAngle - prevAngle);
            if (diff > 180) diff = 360 - diff;
            if (diff < 20.0) return true;
        }
        return false;
    }

    private void KeysHintButton_Click(object sender, RoutedEventArgs e)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("J2MEGamepad.Keys.txt");
        if (stream == null)
        {
            MessageBox.Show("Keys.txt not found.", "Keys Reference",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();

        var ks = Models.AppSettings.Load();
        int fontSize = ks.KeysFontSize;
        double winWidth = ks.KeysWindowWidth, winHeight = ks.KeysWindowHeight;

        var hintWindow = new Window
        {
            Title = "Default Keys Reference",
            Width = winWidth,
            Height = winHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.CanResize,
            Background = System.Windows.Media.Brushes.Black
        };
        hintWindow.SourceInitialized += (_, _) => StripMinMax(hintWindow);

        var scroll = new System.Windows.Controls.ScrollViewer
        {
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled
        };

        var textBlock = new System.Windows.Controls.TextBlock
        {
            Text = content,
            Foreground = System.Windows.Media.Brushes.LightGreen,
            Background = System.Windows.Media.Brushes.Black,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = fontSize,
            Padding = new Thickness(20),
            TextWrapping = System.Windows.TextWrapping.Wrap
        };

        scroll.Content = textBlock;
        hintWindow.Content = scroll;

        scroll.PreviewMouseWheel += (_, args) =>
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                int delta = args.Delta > 0 ? 1 : -1;
                int newSize = Math.Max(8, Math.Min(36, fontSize + delta));
                if (newSize != fontSize)
                {
                    fontSize = newSize;
                    textBlock.FontSize = fontSize;
                }
                args.Handled = true;
            }
        };

        hintWindow.Closed += (_, _) =>
        {
            var s = Models.AppSettings.Load();
            s.KeysFontSize = fontSize;
            s.KeysWindowWidth = hintWindow.Width;
            s.KeysWindowHeight = hintWindow.Height;
            s.Save();
        };
        hintWindow.ShowDialog();
    }

    private void MinimizeToTray_Click(object sender, RoutedEventArgs e)
    {
        HideToTray();
    }

    private void StartMinimizedCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        var s = Models.AppSettings.Load();
        s.StartMinimized = StartMinimizedCheckbox.IsChecked == true;
        s.Save();
    }

    private void TerminateIfKemulatorClosed_Changed(object sender, RoutedEventArgs e)
    {
        bool isChecked = TerminateIfKemulatorClosedCheckbox.IsChecked == true;
        var s = Models.AppSettings.Load();
        s.TerminateIfKemulatorClosed = isChecked;
        s.Save();
        if (isChecked)
            StartTerminateMonitoring();
        else
            StopTerminateMonitoring();
    }

    private void StartTerminateMonitoring()
    {
        StopTerminateMonitoring();
        _javaSeenOnce = false;
        _terminateTimer = new System.Timers.Timer(2000);
        _terminateTimer.Elapsed += (_, _) =>
        {
            try
            {
                bool running = System.Diagnostics.Process.GetProcessesByName("java").Length > 0;
                if (running)
                    _javaSeenOnce = true;
                else if (_javaSeenOnce)
                    Dispatcher.Invoke(() => {
                        bool stillGone = System.Diagnostics.Process.GetProcessesByName("java").Length == 0;
                        if (stillGone) { ShutdownCleanup(); Close(); }
                    });
            }
            catch { }
        };
        _terminateTimer.Start();
    }

    private void StopTerminateMonitoring()
    {
        if (_terminateTimer != null)
        {
            _terminateTimer.Stop();
            _terminateTimer.Dispose();
            _terminateTimer = null;
        }
    }

    private static string SettingsDir()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "J2MEGamepad");
        Directory.CreateDirectory(dir);
        return dir;
    }

    internal bool StartMinimized()
    {
        try
        {
            if (_trayIcon == null) return false;
            _trayIcon.Visible = true;
            // Verify tray icon actually became visible
            if (!_trayIcon.Visible) return false;
            ShowInTaskbar = false;
            WindowState = WindowState.Minimized;
            Hide();
            return true;
        }
        catch (Exception ex)
        {
            LogInfo($"StartMinimized exception: {ex.Message}");
            return false;
        }
    }

    private void HideToTray()
    {
        _trayIcon.Visible = true;
        ShowInTaskbar = false;
        WindowState = WindowState.Minimized;
        Hide();
    }

    private void ShowFromTray()
    {
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Show();
        Activate();
        _trayIcon.Visible = false;
        if (!_overlay.IsVisible)
            _overlay.Show();
    }

    private void TrayIcon_Click(object? sender, EventArgs e)
    {
        ShowFromTray();
    }

    private void TrayIcon_DoubleClick(object? sender, EventArgs e)
    {
        ShowFromTray();
    }

    private void Show_Click(object? sender, EventArgs e)
    {
        ShowFromTray();
    }

    private void Exit_Click(object? sender, EventArgs e)
    {
        _trayIcon.Visible = false;
        Application.Current.Shutdown();
    }

    private void BackCycles_Changed(object sender, RoutedEventArgs e)
    {
        _poller.BackCyclesYxab = BackCyclesCheckbox.IsChecked == true;
        var s = Models.AppSettings.Load();
        s.BackCycles = BackCyclesCheckbox.IsChecked == true;
        s.Save();
    }

    private void SkipDefault_Changed(object sender, RoutedEventArgs e)
    {
        _poller.SkipDefault = SkipDefaultCheckbox.IsChecked == true;
        var s = Models.AppSettings.Load();
        s.SkipDefault = SkipDefaultCheckbox.IsChecked == true;
        s.Save();
    }

    private void UpdateSkipDefaultState()
    {
        SkipDefaultCheckbox.IsEnabled = _profiles.UserProfileCount >= 2;
        if (_profiles.UserProfileCount < 2)
        {
            SkipDefaultCheckbox.IsChecked = false;
            _poller.SkipDefault = false;
        }
    }

    private void UpdateProfileEditingState()
    {
        bool isDefault = _profiles.CurrentProfile.Name == "Default";
        KeyListBox.IsEnabled = !isDefault;
        KeyCaptureBox.IsEnabled = !isDefault && _selectedKeyName != null;
        IsDefaultCheckbox.IsEnabled = !isDefault;
        DeleteProfileButton.IsEnabled = !isDefault;
        ProfileNameBox.IsEnabled = !isDefault;
    }

    private void RefreshProfileList()
    {
        ProfileListBox.Items.Clear();
        foreach (var p in _profiles.Profiles)
        {
            ProfileListBox.Items.Add(p.Name);
        }
        if (ProfileListBox.Items.Count > 0)
        {
            var current = _profiles.CurrentProfile.Name;
            ProfileListBox.SelectedItem = ProfileListBox.Items.OfType<string>()
                .FirstOrDefault(n => n == current) ?? ProfileListBox.Items[0];
        }
        ProfileNameBox.Text = _profiles.CurrentProfile.Name;
        ProfileText.Text = _profiles.CurrentProfile.Name;
        UpdateProfileEditingState();
        UpdateSkipDefaultState();
    }

    private void ProfileListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ProfileListBox.SelectedItem is string name)
        {
            var profile = _profiles.Profiles.FirstOrDefault(p => p.Name == name);
            if (profile != null)
            {
                _profiles.SetCurrentProfileByName(profile.Name);
                ProfileNameBox.Text = profile.Name;
                ProfileText.Text = profile.Name;
                _lastProfileName = profile.Name;
                IsDefaultCheckbox.IsChecked = !_profiles.CurrentProfile.Mappings.ContainsKey(_selectedKeyName ?? "");
                UpdateCurrentMappingDisplay();
                UpdateProfileEditingState();
                UpdateSkipDefaultState();
            ApplyDiagonalDelayFromProfile();
            ApplyComboSettingsFromProfile();
            }
        }
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        SafeInvoke(() =>
        {
            var name = _profiles.GetNextAvailableName("Profile");
            var profile = new KeyMapProfile
            {
                Name = name,
                Mappings = new System.Collections.Generic.Dictionary<string, ushort>(_profiles.CurrentProfile.Mappings),
                LeftThumbIsComboModifier = _profiles.CurrentProfile.LeftThumbIsComboModifier,
                RightThumbIsComboModifier = _profiles.CurrentProfile.RightThumbIsComboModifier,
            };
            if (DiagDelayPerProfileCheckbox.IsChecked == true)
            {
                profile.DiagonalDelayMs = _profiles.CurrentProfile.DiagonalDelayMs;
                profile.DirectionalDelayMs = _profiles.CurrentProfile.DirectionalDelayMs;
                profile.DiagonalDelayHoldCardinals = _profiles.CurrentProfile.DiagonalDelayHoldCardinals;
            }
            if (ComboPerProfileCheckbox.IsChecked == true)
            {
                profile.ComboActions = new Dictionary<string, List<ushort>>(_profiles.CurrentProfile.ComboActions);
                profile.ComboOSDNames = new Dictionary<string, string>(_profiles.CurrentProfile.ComboOSDNames);
                profile.ComboExecPaths = new Dictionary<string, string>(_profiles.CurrentProfile.ComboExecPaths);
            }
            _profiles.SaveProfile(profile);
            _profiles.SetCurrentProfileByName(profile.Name);
            _lastProfileName = profile.Name;
            RefreshProfileList();
        });
    }

    private static bool ValidateProfileName(string name)
    {
        if (name.Length > 18)
        {
            MessageBox.Show("Profile name must be 18 characters or less.",
                "Name Too Long", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return true;
    }

    private void SafeInvoke(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            LogHelper.Error("MainWindow", "SafeInvoke", ex);
            MessageBox.Show($"An error occurred. Details saved to error.log.\n\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        SafeInvoke(() =>
        {
            if (_profiles.CurrentProfile.Name == "Default") return;
            var name = ProfileNameBox.Text.Trim();
            _profiles.DeleteProfile(name);
            if (_profiles.CurrentProfileIndex >= _profiles.Profiles.Count)
                _profiles.CurrentProfileIndex = 0;
            _lastProfileName = _profiles.CurrentProfile.Name;
            RefreshProfileList();
        });
    }

    private void ProfileNameBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
            PerformRename();
    }

    private void ProfileNameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        PerformRename();
    }

    private void PerformRename()
    {
        SafeInvoke(() =>
        {
            if (_profiles.CurrentProfile.Name == "Default") return;
            var newName = ProfileNameBox.Text.Trim();
            if (string.IsNullOrEmpty(newName) || newName == _profiles.CurrentProfile.Name) return;
            if (!ValidateProfileName(newName)) return;
            if (_profiles.Profiles.Any(p => p.Name == newName))
            {
                ProfileNameBox.Text = _profiles.CurrentProfile.Name;
                return;
            }
            var oldName = _profiles.CurrentProfile.Name;
            _profiles.RenameProfile(oldName, newName);
            _lastProfileName = newName;
            _profiles.SetCurrentProfileByName(newName);
            RefreshProfileList();
        });
    }

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "J2MEGamepad", "profiles");
        System.IO.Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start("explorer.exe", dir);
    }

    private static readonly HashSet<string> ComboActionNames = new()
    {
        "RB+LB+Y", "RB+LB+X", "RB+LB+A", "RB+LB+B",
        "RT+LT+Y", "RT+LT+X", "RT+LT+A", "RT+LT+B",
        "RSB+Y", "RSB+X", "RSB+A", "RSB+B",
        "LSB+Y", "LSB+X", "LSB+A", "LSB+B",
    };

    private static readonly HashSet<string> ComboExecActionNames = new()
    {
        "RB+LB+Start", "RB+LB+Back",
        "RT+LT+Start", "RT+LT+Back",
    };

    private void KeyListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        CancelComboCapture();
        if (KeyListBox.SelectedItem is System.Windows.Controls.ListBoxItem item)
        {
            _selectedKeyName = item.Tag as string;
            bool isCombo = _selectedKeyName != null && ComboActionNames.Contains(_selectedKeyName);
            bool isExec = _selectedKeyName != null && ComboExecActionNames.Contains(_selectedKeyName);

            KeyCaptureBox.Visibility = isExec ? Visibility.Collapsed : Visibility.Visible;
            IsDefaultCheckbox.Visibility = isCombo || isExec ? Visibility.Collapsed : Visibility.Visible;
            ComboControlPanel.Visibility = isCombo || isExec ? Visibility.Visible : Visibility.Collapsed;
            ExecPathPanel.Visibility = isExec ? Visibility.Visible : Visibility.Collapsed;
            DInputActionPanel.Visibility = isCombo || isExec ? Visibility.Collapsed : (_isDInputMode ? Visibility.Visible : Visibility.Collapsed);
            DInputDeadzonePanel.Visibility = isCombo || isExec ? Visibility.Collapsed : (_isDInputMode ? Visibility.Visible : Visibility.Collapsed);

            if (isCombo || isExec)
            {
                IsDefaultCheckbox.IsChecked = false;
                if (_comboSettings.OSDNames.TryGetValue(_selectedKeyName, out var osdName))
                    ComboOSDNameBox.Text = osdName;
                else
                    ComboOSDNameBox.Text = "";
                if (isExec && _comboSettings.ExecPaths.TryGetValue(_selectedKeyName, out var execPath))
                    ExecPathBox.Text = execPath;
                else if (isExec)
                    ExecPathBox.Text = "";
            }
            else
            {
                IsDefaultCheckbox.IsChecked = !_profiles.CurrentProfile.Mappings.ContainsKey(_selectedKeyName ?? "");
            }

            bool isLeftThumb = _selectedKeyName == "LeftThumb";
            bool isRightThumb = _selectedKeyName == "RightThumb";
            bool isThumb = isLeftThumb || isRightThumb;
            ThumbComboModifierCheckbox.Visibility = isThumb ? Visibility.Visible : Visibility.Collapsed;
            ThumbWarningPanel.Visibility = isThumb ? Visibility.Visible : Visibility.Collapsed;
            LeftThumbWarningText.Visibility = isLeftThumb ? Visibility.Visible : Visibility.Collapsed;
            RightThumbWarningText.Visibility = isRightThumb ? Visibility.Visible : Visibility.Collapsed;

            if (isThumb)
            {
                bool isComboMod = GetCurrentThumbIsComboModifier(_selectedKeyName!);
                ThumbComboModifierCheckbox.IsChecked = isComboMod;
            }

            UpdateCurrentMappingDisplay();
        }
        else
        {
            ComboControlPanel.Visibility = Visibility.Collapsed;
            IsDefaultCheckbox.Visibility = Visibility.Visible;
            KeyCaptureBox.Visibility = Visibility.Visible;
            DInputActionPanel.Visibility = _isDInputMode ? Visibility.Visible : Visibility.Collapsed;
            DInputDeadzonePanel.Visibility = _isDInputMode ? Visibility.Visible : Visibility.Collapsed;
            ThumbComboModifierCheckbox.Visibility = Visibility.Collapsed;
            ThumbWarningPanel.Visibility = Visibility.Collapsed;
            LeftThumbWarningText.Visibility = Visibility.Collapsed;
            RightThumbWarningText.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateCurrentMappingDisplay()
    {
        KeyCaptureBox.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEE, 0xEE, 0xEE));
        KeyCaptureText.Text = "Click to capture";
        ExecIconImage.Source = null;
        ExecIconImage.Visibility = Visibility.Collapsed;
        if (_selectedKeyName == null)
        {
            CurrentMappingText.Text = "(none selected)";
            KeyCaptureBox.IsEnabled = false;
            return;
        }

        if (ComboExecActionNames.Contains(_selectedKeyName))
        {
            KeyCaptureBox.IsEnabled = false;
            if (_comboSettings.ExecPaths.TryGetValue(_selectedKeyName, out var exePath) && !string.IsNullOrEmpty(exePath))
            {
                CurrentMappingText.Text = System.IO.Path.GetFileName(exePath);
                var icon = ExtractAssociatedIcon(exePath);
                if (icon != null)
                {
                    ExecIconImage.Source = icon;
                    ExecIconImage.Visibility = Visibility.Visible;
                }
            }
            else
                CurrentMappingText.Text = "(no executable assigned)";
            return;
        }

        if (ComboActionNames.Contains(_selectedKeyName))
        {
            KeyCaptureBox.IsEnabled = true;
            if (_comboSettings.Actions.TryGetValue(_selectedKeyName, out var keys) && keys.Count > 0)
                CurrentMappingText.Text = string.Join(" + ", keys.Select(k => GetKeyNameFromVK(k)));
            else
                CurrentMappingText.Text = "(no combo assigned)";
            return;
        }

        KeyCaptureBox.IsEnabled = _profiles.CurrentProfile.Name != "Default";

        if (_isDInputMode)
        {
            ReloadDInputMapping();
            var mapping = _dinputMapping;

            if (mapping.ActionToButton.TryGetValue(_selectedKeyName, out int btnIdx))
                CurrentMappingText.Text = $"Button {btnIdx + 1}";
            else
                CurrentMappingText.Text = "(not mapped)";

            if (IsDefaultCheckbox.IsChecked == true)
            {
                KeyCaptureText.Text = GetDefaultKeyName(_selectedKeyName);
            }
            else if (_profiles.CurrentProfile.Mappings.TryGetValue(_selectedKeyName, out var vk))
            {
                KeyCaptureText.Text = GetKeyNameFromVK(vk);
            }
            else
            {
                KeyCaptureText.Text = "Default";
            }
            return;
        }

        if (IsDefaultCheckbox.IsChecked == true)
        {
            CurrentMappingText.Text = GetDefaultKeyName(_selectedKeyName);
            KeyCaptureText.Text = "Default";
            return;
        }
        if (_profiles.CurrentProfile.Mappings.TryGetValue(_selectedKeyName, out var vk2))
            CurrentMappingText.Text = GetKeyNameFromVK(vk2);
        else
            CurrentMappingText.Text = "(not set)";
    }

    private void IsDefaultCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        if (_selectedKeyName == null) return;
        KeyCaptureBox.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEE, 0xEE, 0xEE));
        KeyCaptureText.Text = "Click to capture";
        if (IsDefaultCheckbox.IsChecked == true)
            _profiles.CurrentProfile.Mappings.Remove(_selectedKeyName);
        else
        {
            if (!_profiles.CurrentProfile.Mappings.ContainsKey(_selectedKeyName))
            {
                ushort defaultVal = GetDefaultKeyValue(_selectedKeyName);
                if (defaultVal != 0)
                    _profiles.CurrentProfile.Mappings[_selectedKeyName] = defaultVal;
                KeyCaptureBox.IsEnabled = _profiles.CurrentProfile.Name != "Default";
            }
        }
        _profiles.SaveProfile(_profiles.CurrentProfile);
        UpdateCurrentMappingDisplay();
    }

    private void KeyCaptureBox_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_selectedKeyName != null && ComboActionNames.Contains(_selectedKeyName))
        {
            StartComboCapture();
            return;
        }
        if (_profiles.CurrentProfile.Name == "Default") return;
        if (!_isCapturingKey)
        {
            _isCapturingKey = true;
            KeyCaptureText.Text = "Press a key...";
            KeyCaptureBox.Background = System.Windows.Media.Brushes.LightYellow;
            this.PreviewKeyDown += OnCaptureKeyDown;
        }
    }

    private void OnCaptureKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isCapturingKey) return;
        var key = e.Key;
        e.Handled = true;
        ushort vk = (ushort)KeyInterop.VirtualKeyFromKey(key);
        var keyName = GetKeyNameFromVK(vk);
        if (_selectedKeyName != null)
        {
            if (IsDefaultCheckbox.IsChecked == true)
                IsDefaultCheckbox.IsChecked = false;
            if (IsDefaultCheckbox.IsChecked != true)
            {
                _profiles.CurrentProfile.Mappings[_selectedKeyName] = vk;
                CurrentMappingText.Text = keyName;
                _profiles.SaveProfile(_profiles.CurrentProfile);
                if (!_customKeyWarningShown && _profiles.CurrentProfile.Name != "Default")
                {
                    _customKeyWarningShown = true;
                    var warnSettings = Models.AppSettings.Load();
                    if (warnSettings.CustomWarningHidden)
                        goto afterWarning;
                    var warn = new Window
                    {
                        Title = "Custom Keybind Warning",
                        Width = 440,
                        Height = 189,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        Owner = this,
                        ResizeMode = ResizeMode.NoResize,
                        Background = Brushes.White,
                        Topmost = true
                    };
                    warn.SourceInitialized += (_, _) => StripMinMax(warn);
                    var stack = new StackPanel { Margin = new Thickness(20) };
                    stack.Children.Add(new TextBlock
                    {
                        Text = "Custom keybinds should only be used if you have changed KEmulator's default keys. Stick with the default key layout unless you know what you're doing.",
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 13,
                        Margin = new Thickness(0, 0, 0, 12)
                    });
                    var dontRemind = new CheckBox
                    {
                        Content = "Don't show this warning again",
                        Margin = new Thickness(0, 0, 0, 10)
                    };
                    stack.Children.Add(dontRemind);
                    var okBtn = new Button { Content = "OK", Width = 80, Height = 28, HorizontalAlignment = HorizontalAlignment.Right };
                    okBtn.Click += (_, _) => warn.Close();
                    stack.Children.Add(okBtn);
                    warn.Content = stack;
                    warn.ShowDialog();
                    if (dontRemind.IsChecked == true)
                    {
                        warnSettings.CustomWarningHidden = true;
                        warnSettings.Save();
                    }
                }
                afterWarning: ;
            }
        }
        KeyCaptureText.Text = keyName;
        KeyCaptureBox.Background = System.Windows.Media.Brushes.LightGreen;
        _isCapturingKey = false;
        this.PreviewKeyDown -= OnCaptureKeyDown;
    }

    private bool _isCapturingCombo;

    private void StartComboCapture()
    {
        if (_isCapturingCombo) return;

        _isCapturingCombo = true;
        KeyCaptureText.Text = "Press a key combo...";
        KeyCaptureBox.Background = System.Windows.Media.Brushes.LightYellow;
        CurrentMappingText.Text = "Press key combo...";
        this.PreviewKeyDown += OnCaptureComboKeyDown;
    }

    private void CancelComboCapture()
    {
        _isCapturingCombo = false;
        this.PreviewKeyDown -= OnCaptureComboKeyDown;
        UpdateCurrentMappingDisplay();
    }

    private void OnCaptureComboKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isCapturingCombo || _selectedKeyName == null) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Ignore pure modifier keys (Ctrl, Shift, Alt, Win) as standalone captures
        if (key == Key.LeftCtrl || key == Key.RightCtrl ||
            key == Key.LeftShift || key == Key.RightShift ||
            key == Key.LeftAlt || key == Key.RightAlt ||
            key == Key.LWin || key == Key.RWin)
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;

        var keys = new List<ushort>();

        // Detect modifiers from Keyboard.Modifiers
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            keys.Add(0x11); // VK_CONTROL
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            keys.Add(0x10); // VK_SHIFT
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0)
            keys.Add(0x12); // VK_MENU

        ushort mainVk = (ushort)KeyInterop.VirtualKeyFromKey(key);
        keys.Add(mainVk);

        // Store the combo globally
        _comboSettings.Actions[_selectedKeyName] = keys;

        // Auto-generate OSD name if not set
        if (!_comboSettings.OSDNames.TryGetValue(_selectedKeyName, out var existing) || string.IsNullOrEmpty(existing))
        {
            _comboSettings.OSDNames[_selectedKeyName] = string.Join("+", keys.Select(k => GetKeyNameFromVK(k)));
        }
        PersistComboSettings();

        ComboOSDNameBox.Text = _comboSettings.OSDNames[_selectedKeyName];

        CancelComboCapture();
    }

    private void ClearComboButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedKeyName == null || (!ComboActionNames.Contains(_selectedKeyName) && !ComboExecActionNames.Contains(_selectedKeyName)))
            return;

        bool isExec = ComboExecActionNames.Contains(_selectedKeyName);
        _comboSettings.Actions.Remove(_selectedKeyName);
        if (isExec)
            _comboSettings.ExecPaths.Remove(_selectedKeyName);
        _comboSettings.OSDNames.Remove(_selectedKeyName);
        PersistComboSettings();
        ComboOSDNameBox.Text = "";
        if (isExec)
            ExecPathBox.Text = "";
        else
            KeyCaptureText.Text = "Click to capture";
        UpdateCurrentMappingDisplay();
    }

    private void ComboOSDNameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_selectedKeyName == null || (!ComboActionNames.Contains(_selectedKeyName) && !ComboExecActionNames.Contains(_selectedKeyName)))
            return;

        var name = ComboOSDNameBox.Text.Trim();
        if (!string.IsNullOrEmpty(name))
        {
            _comboSettings.OSDNames[_selectedKeyName] = name;
        }
        else
        {
            _comboSettings.OSDNames.Remove(_selectedKeyName);
        }
        PersistComboSettings();
    }

    private void ComboOSDNameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_selectedKeyName == null || (!ComboActionNames.Contains(_selectedKeyName) && !ComboExecActionNames.Contains(_selectedKeyName)))
            return;

        var name = ComboOSDNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            var defaultName = GetDefaultComboOSDName(_selectedKeyName);
            if (defaultName != null)
            {
                _comboSettings.OSDNames[_selectedKeyName] = defaultName;
                ComboOSDNameBox.Text = defaultName;
            }
            else
            {
                _comboSettings.OSDNames.Remove(_selectedKeyName);
            }
            PersistComboSettings();
        }
    }

    private string? GetDefaultComboOSDName(string comboName)
    {
        if (ComboExecActionNames.Contains(comboName))
        {
            if (_comboSettings.ExecPaths.TryGetValue(comboName, out var execPath) && !string.IsNullOrEmpty(execPath))
                return comboName.Replace("RB+LB+", "").Replace("RT+LT+", "");
            return null;
        }
        if (_comboSettings.Actions.TryGetValue(comboName, out var keys) && keys.Count > 0)
        {
            return string.Join("+", keys.Select(k => GetKeyNameFromVK(k)));
        }
        return null;
    }

    private void BrowseExecButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedKeyName == null || !ComboExecActionNames.Contains(_selectedKeyName))
            return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select executable to launch",
            Filter = "Executables (*.exe;*.bat;*.ps1;*.cmd)|*.exe;*.bat;*.ps1;*.cmd|All files (*.*)|*.*",
            DefaultExt = ".exe",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() == true)
        {
            ExecPathBox.Text = dialog.FileName;
        }
    }

    private void OpenExecButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedKeyName == null || !ComboExecActionNames.Contains(_selectedKeyName))
            return;

        var path = ExecPathBox.Text.Trim();
        if (string.IsNullOrEmpty(path)) return;
        path = System.IO.Path.GetFullPath(path);
        if (!File.Exists(path)) return;

        string ext = System.IO.Path.GetExtension(path);
        if (!GamepadPoller.AllowedExecExtensions.Contains(ext))
        {
            MessageBox.Show($"Cannot launch \"{path}\": file type \"{ext}\" is not in the allowed list.",
                "Launch Blocked", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to launch: {ex.Message}", "Launch Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExecPathBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_selectedKeyName == null || !ComboExecActionNames.Contains(_selectedKeyName))
            return;

        var path = ExecPathBox.Text.Trim();
        if (!string.IsNullOrEmpty(path))
        {
            _comboSettings.ExecPaths[_selectedKeyName] = path;
            // Clear any stale key binding when exec path is set
            _comboSettings.Actions.Remove(_selectedKeyName);
        }
        else
        {
            _comboSettings.ExecPaths.Remove(_selectedKeyName);
        }
        PersistComboSettings();
        UpdateCurrentMappingDisplay();
    }

    private string GetDInputDeviceName()
    {
        return "DInput controller";
    }

    private void ReloadDInputMapping()
    {
        try
        {
            var path = DInputMapping.GetFilePath();
            if (File.Exists(path))
                _dinputMapping = DInputMapping.FromJson(File.ReadAllText(path)) ?? new DInputMapping();
            else
                _dinputMapping = new DInputMapping();
        }
        catch (Exception ex)
        {
            LogHelper.Error("MainWindow", "ReloadDInputMapping", ex);
            _dinputMapping = new DInputMapping();
        }
    }

    private static string GetKeyNameFromVK(ushort vk)
    {
        if (vk == 0) return "Unassigned";
        if (vk >= 0x30 && vk <= 0x39) return $"{(char)('0' + (vk - 0x30))}";
        if (vk >= 0x41 && vk <= 0x5A) return $"{(char)('A' + (vk - 0x41))}";
        if (vk >= 0x70 && vk <= 0x7A) return $"F{vk - 0x70 + 1}";
        if (vk >= 0x60 && vk <= 0x69) return $"Numpad {vk - 0x60}";
        return vk switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x10 => "Shift",
            0x11 => "Ctrl",
            0x12 => "Alt",
            0x14 => "CapsLock",
            0x1B => "Escape",
            0x20 => "Space",
            0x25 => "Left Arrow",
            0x26 => "Up Arrow",
            0x27 => "Right Arrow",
            0x28 => "Down Arrow",
            0x2E => "Delete",
            0x5B => "Left Win",
            0x5C => "Right Win",
            0x6A => "Numpad *",
            0x6B => "Numpad +",
            0x6C => "Numpad Separator",
            0x6D => "Numpad -",
            0x6E => "Numpad .",
            0x6F => "Numpad /",
            0x90 => "NumLock",
            0xA0 => "Left Shift",
            0xA1 => "Right Shift",
            0xA2 => "Left Ctrl",
            0xA3 => "Right Ctrl",
            0xA4 => "Left Alt",
            0xA5 => "Right Alt",
            0xBA => "; (semicolon)",
            0xBB => "= (equals)",
            0xBC => ", (comma)",
            0xBD => "- (minus)",
            0xBE => ". (period)",
            0xBF => "/ (slash)",
            0xC0 => "` (backtick)",
            0xDB => "[ (left bracket)",
            0xDC => "\\ (backslash)",
            0xDD => "] (right bracket)",
            0xDE => "' (apostrophe)",
            0xE2 => "\\ (ISO)",
            _ => $"0x{vk:X4}"
        };
    }

    private static string GetDefaultKeyName(string keyName)
    {
        return keyName switch
        {
            "Y" => "Num 0",
            "X" => "F1",
            "A" => "F3 / Num 5",
            "B" => "F2",
            "LB" => "Num *",
            "RB" => "Num /",
            "LT" => "F1",
            "RT" => "F2",
            "RightThumb" => "F3 / Num 5",
            "LeftThumb" => "Unassigned",
            _ => "Unknown"
        };
    }

    private static ushort GetDefaultKeyValue(string keyName)
    {
        return keyName switch
        {
            "Y" => 0x60,
            "X" => 0x70,
            "A" => 0x72,
            "B" => 0x71,
            "LB" => 0x6A,
            "RB" => 0x6F,
            "LT" => 0x70,
            "RT" => 0x71,
            "RightThumb" => 0x72,
            "LeftThumb" => 0,
            _ => 0
        };
    }

    private static System.Windows.Media.ImageSource? ExtractAssociatedIcon(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return null;
            path = System.IO.Path.GetFullPath(path);
            if (!System.IO.File.Exists(path)) return null;
            string ext = System.IO.Path.GetExtension(path);
            if (!GamepadPoller.AllowedExecExtensions.Contains(ext)) return null;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;
            return System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                System.Windows.Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
        }
        catch
        {
            return null;
        }
    }

    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/icon.ico", UriKind.RelativeOrAbsolute);
            var info = System.Windows.Application.GetResourceStream(uri);
            if (info == null) return System.Drawing.SystemIcons.Application;
            using var stream = info.Stream;
            return new System.Drawing.Icon(stream);
        }
        catch
        {
            return System.Drawing.SystemIcons.Application;
        }
    }
}
