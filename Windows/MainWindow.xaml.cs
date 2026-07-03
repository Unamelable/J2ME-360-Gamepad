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
using System.Windows.Media.Animation;
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
    private bool _savedDelayHoldState = true;
    private bool _isDInputMode;
    private bool _isDInputRemapping;
    private string _dinputRemapAction = "";
    private Storyboard? _remapFadeAnimation;
    private bool _isDInputCapturingButton;
    private string _lastControllerName = "";
    private DInputMapping _dinputMapping = new();
    private readonly System.Windows.Threading.DispatcherTimer _disconnectTimer;

    public MainWindow()
    {
        InitializeComponent();

        _keyboard = new KeyboardInjector();
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
            Icon = System.Drawing.SystemIcons.Application,
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
        _profiles.ProfilesChanged += OnProfilesChanged;

        RefreshProfileList();

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void LogInfo(string msg)
    {
        LogHelper.Info("MainWindow", msg);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LogInfo("OnLoaded entered");

        try
        {
            // Signal to startup watchdog that the window loaded successfully
            if (Application.Current is App app)
            {
                app.SignalWindowReady();
                LogInfo("SignalWindowReady called");
            }
            else
                LogInfo("SignalWindowReady FAILED: Application.Current is not App");

            // Force window to be visible and focused
            Activate();

            // Register CTRL+R global hotkey for restart
            var hwnd = new WindowInteropHelper(this).Handle;
            RegisterHotKey(hwnd, HOTKEY_ID_RESTART, MOD_CONTROL | MOD_NOREPEAT, 0x52); // 0x52 = 'R'

            LogInfo("Showing overlay...");
            _overlay.Show();

            _lastProfileName = _profiles.CurrentProfile.Name;

            var settingsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "J2MEGamepad");
            Directory.CreateDirectory(settingsDir);
            var diagFile = Path.Combine(settingsDir, "diagdelay.txt");
            if (File.Exists(diagFile) && int.TryParse(File.ReadAllText(diagFile).Trim(), out int savedDiag))
            {
                savedDiag = Math.Clamp(savedDiag, 0, 300);
                DiagDelaySlider.Value = savedDiag;
                _poller.DiagonalDelayMs = savedDiag;
                DiagDelayValue.Text = savedDiag == 0 ? "Off" : savedDiag.ToString();
            }
            var holdFile = Path.Combine(settingsDir, "diaghold.txt");
            if (File.Exists(holdFile) && bool.TryParse(File.ReadAllText(holdFile).Trim(), out bool savedHold))
            {
                DiagDelayHoldCheckbox.IsChecked = savedHold;
                _poller.DiagonalDelayHoldCardinals = savedHold;
            }
            var perProfileFile = Path.Combine(settingsDir, "diagperprofile.txt");
            if (File.Exists(perProfileFile) && bool.TryParse(File.ReadAllText(perProfileFile).Trim(), out bool savedPerProfile))
                DiagDelayPerProfileCheckbox.IsChecked = savedPerProfile;

            var dirDelayFile = Path.Combine(settingsDir, "directional_delay.txt");
            if (File.Exists(dirDelayFile) && int.TryParse(File.ReadAllText(dirDelayFile).Trim(), out int savedDirDelay))
            {
                savedDirDelay = Math.Clamp(savedDirDelay, 0, 300);
                DirDelaySlider.Value = savedDirDelay;
                _poller.DirectionalDelayMs = savedDirDelay;
                DirDelayValue.Text = savedDirDelay == 0 ? "Off" : savedDirDelay.ToString();
                if (savedDirDelay > 0)
                {
                    _savedDelayHoldState = DiagDelayHoldCheckbox.IsChecked == true;
                    DiagDelayHoldCheckbox.IsChecked = false;
                    DiagDelayHoldCheckbox.IsEnabled = false;
                }
            }
            ApplyDiagonalDelayFromProfile();

            // Load DInput deadzone from calibration
            var cal = DInputCalibration.Load();
            DInputDeadzoneSlider.Value = cal.DeadzonePercent;
            DInputDeadzoneValue.Text = $"{cal.DeadzonePercent}%";
            _poller.ReloadCalibration();

            ShowFirstRunWarning();

            LogInfo("Starting watchdog and poller...");
            _watchdog.Start();
            _poller.Start();
            LogInfo("OnLoaded complete successfully");
        }
        catch (Exception ex)
        {
            LogInfo($"OnLoaded exception: {ex.Message}\n{ex.StackTrace}");
            MessageBox.Show($"Startup error:\n{ex.Message}\n\nDetails saved to crash.log.",
                "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowFirstRunWarning()
    {
        var settingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "J2MEGamepad");
        var sentinelFile = Path.Combine(settingsDir, "firstrun.txt");
        if (File.Exists(sentinelFile)) return;

        var dialog = new Window
        {
            Title = "Important Setup Notice",
            Width = 480,
            Height = 304,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            WindowStyle = WindowStyle.ToolWindow,
            ResizeMode = ResizeMode.NoResize,
            Background = Brushes.White,
            Topmost = true
        };

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
            File.WriteAllText(sentinelFile, "0");
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

        var settingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "J2MEGamepad");
        Directory.CreateDirectory(settingsDir);
        File.WriteAllText(Path.Combine(settingsDir, "diagdelay.txt"),
            ((int)DiagDelaySlider.Value).ToString());
        File.WriteAllText(Path.Combine(settingsDir, "diaghold.txt"),
            DiagDelayHoldCheckbox.IsChecked.ToString());
        File.WriteAllText(Path.Combine(settingsDir, "diagperprofile.txt"),
            DiagDelayPerProfileCheckbox.IsChecked.ToString());
        File.WriteAllText(Path.Combine(settingsDir, "directional_delay.txt"),
            ((int)DirDelaySlider.Value).ToString());

        if (DiagDelayPerProfileCheckbox.IsChecked == true)
        {
            _profiles.CurrentProfile.DiagonalDelayHoldCardinals = DiagDelayHoldCheckbox.IsChecked == true;
            _profiles.SaveProfile(_profiles.CurrentProfile);
        }

        StopDInputRemapAnimation();
        _disconnectTimer.Stop();
        _disconnectTimer.Tick -= DisconnectTimer_Tick;
        _poller.Dispose();
        _watchdog.Dispose();
        _keyboard.Dispose();
        _profiles.Dispose();
        _trayIcon.Dispose();
        _overlay.Close();
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
            StopDInputRemapAnimation();
            _disconnectTimer.Stop();
            _disconnectTimer.Tick -= DisconnectTimer_Tick;
            _poller.Dispose();
            _watchdog.Dispose();
            _keyboard.Dispose();
            _profiles.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _overlay.Close();

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
        int pct = Math.Clamp((int)DInputDeadzoneSlider.Value, 10, 80);
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

            string suffix = conflict ? $" (unmapped \"{conflictAction}\")" : "";
            DInputRemapButton.Content = $"Button {buttonIndex + 1}{suffix}";
            DInputRemapButton.Background = s_defaultButtonBrush;

            UpdateCurrentMappingDisplay();
            UpdateDInputListLabels();
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

    private void StartDInputRemapAnimation()
    {
        StopDInputRemapAnimation();
        _remapFadeAnimation = new Storyboard();

        var fadeIn = new ColorAnimation
        {
            From = Color.FromRgb(0xEE, 0xEE, 0xEE),
            To = Color.FromRgb(0xFF, 0xCC, 0x66),
            Duration = TimeSpan.FromMilliseconds(600),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };

        Storyboard.SetTarget(fadeIn, DInputRemapButton);
        Storyboard.SetTargetProperty(fadeIn, new PropertyPath("(Button.Background).(SolidColorBrush.Color)"));

        _remapFadeAnimation.Children.Add(fadeIn);
        _remapFadeAnimation.Begin(this);
    }

    private void StopDInputRemapAnimation()
    {
        if (_remapFadeAnimation != null)
        {
            _remapFadeAnimation.Stop();
            _remapFadeAnimation = null;
        }
        DInputRemapButton.Background = s_defaultButtonBrush;
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
            WindowStyle = WindowStyle.ToolWindow,
            ResizeMode = ResizeMode.NoResize,
            Background = Brushes.White,
            Topmost = true
        };

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
        var dirNames = new[] { "UpLeft", "Up", "UpRight", "Left", "Center", "Right", "DownLeft", "Down", "DownRight" };
        var dirAbbrev = new Dictionary<string, string>
        {
            ["UpLeft"] = "UL", ["Up"] = "Up", ["UpRight"] = "UR",
            ["Left"] = "L", ["Center"] = "C", ["Right"] = "R",
            ["DownLeft"] = "DL", ["Down"] = "Dn", ["DownRight"] = "DR"
        };

        var dirGrid = new (string name, int row, int col)[]
        {
            ("UpLeft", 0, 0), ("Up", 0, 1), ("UpRight", 0, 2),
            ("Left", 1, 0), ("Center", 1, 1), ("Right", 1, 2),
            ("DownLeft", 2, 0), ("Down", 2, 1), ("DownRight", 2, 2),
        };

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
        foreach (var (name, row, col) in dirGrid)
        {
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

        // Auto-record center from current rest position
        var calDiReader = _poller.DirectInputReader;
        {
            int cx = 32767, cy = 32767;
            if (calDiReader.Available && calDiReader.Poll())
            {
                cx = calDiReader.X;
                cy = calDiReader.Y;
            }
            else
            {
                var ji = new JOYINFOEX();
                ji.dwSize = Marshal.SizeOf<JOYINFOEX>();
                ji.dwFlags = JoyInput.JOY_RETURNALL;
                if (JoyInput.GetPosEx(0, ref ji) == JoyInput.JOYERR_NOERROR)
                {
                    cx = ji.dwXpos;
                    cy = ji.dwYpos;
                }
            }
            cal.CenterX = cx;
            cal.CenterY = cy;
        }

        // Clockwise order (center auto-recorded above)
        var steps = new[] { "Up", "UpRight", "Right", "DownRight", "Down", "DownLeft", "Left", "UpLeft" };
        // Map step names to their index in dirBoxes for highlighting
        var stepToBoxIndex = new Dictionary<string, int>();
        for (int i = 0; i < dirGrid.Length; i++)
            stepToBoxIndex[dirGrid[i].name] = i;

        int currentStep = 0;
        const double stepDuration = 1.0;
        double elapsed = 0;
        long sampleSumX = 0, sampleSumY = 0;
        int sampleCount = 0;
        bool complete = false;
        bool recording = false;
        DateTime stepStartTime = DateTime.UtcNow;
        bool failed = false;

        var calTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };

        calTimer.Tick += (_, _) =>
        {
            if (complete) return;

            int xPos = 32767, yPos = 32767;
            if (calDiReader.Available && calDiReader.Poll())
            {
                xPos = calDiReader.X;
                yPos = calDiReader.Y;
            }
            else
            {
                var ji = new JOYINFOEX();
                ji.dwSize = Marshal.SizeOf<JOYINFOEX>();
                ji.dwFlags = JoyInput.JOY_RETURNALL;
                if (JoyInput.GetPosEx(0, ref ji) == JoyInput.JOYERR_NOERROR)
                {
                    xPos = ji.dwXpos;
                    yPos = ji.dwYpos;
                }
            }

            int cx = cal.CenterX;
            int cy = cal.CenterY;
            int dx = xPos - cx;
            int dy = yPos - cy;
            int threshold = 13000;

            string dirName = steps[currentStep];
            stepText.Text = $"Step {currentStep + 1}/{steps.Length}";
            instructionText.Text = $"Press and hold {dirName} for 1 second...";

            if (stepToBoxIndex.TryGetValue(dirName, out int boxIdx))
            {
                for (int i = 0; i < dirBoxes.Count; i++)
                    dirBoxes[i].Background = i == boxIdx ? Brushes.LightSkyBlue : Brushes.LightGray;
            }

            bool detected = dirName switch
            {
                "Up" => dy < -threshold,
                "Down" => dy > threshold,
                "Left" => dx < -threshold,
                "Right" => dx > threshold,
                "UpLeft" => dx < -threshold && dy < -threshold,
                "UpRight" => dx > threshold && dy < -threshold,
                "DownLeft" => dx < -threshold && dy > threshold,
                "DownRight" => dx > threshold && dy > threshold,
                _ => false
            };

            // Failure state — shown until user re-presses the correct direction
            if (failed)
            {
                countdownText.Text = "FAIL";
                countdownText.Foreground = Brushes.Red;
                statusText.Text = "D-Pad released. Press the direction again to retry.";
                statusText.Foreground = Brushes.Red;
                if (detected)
                {
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

        var settingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "J2MEGamepad");
        Directory.CreateDirectory(settingsDir);
        var fontFile = Path.Combine(settingsDir, "keysfont.txt");
        int fontSize = 18;
        if (File.Exists(fontFile) && int.TryParse(File.ReadAllText(fontFile).Trim(), out int savedSize))
            fontSize = Math.Clamp(savedSize, 8, 36);

        var sizeFile = Path.Combine(settingsDir, "keyssize.txt");
        double winWidth = 593, winHeight = 682;
        if (File.Exists(sizeFile))
        {
            var parts = File.ReadAllText(sizeFile).Trim().Split('x');
            if (parts.Length == 2 && double.TryParse(parts[0], out double sw) && double.TryParse(parts[1], out double sh))
            {
                winWidth = Math.Max(sw, 300);
                winHeight = Math.Max(sh, 200);
            }
        }

        var hintWindow = new Window
        {
            Title = "Default Keys Reference",
            Width = winWidth,
            Height = winHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            WindowStyle = WindowStyle.ToolWindow,
            ResizeMode = ResizeMode.CanResize,
            Background = System.Windows.Media.Brushes.Black
        };

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
                int newSize = Math.Clamp(fontSize + delta, 8, 36);
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
            File.WriteAllText(fontFile, fontSize.ToString());
            File.WriteAllText(sizeFile, $"{hintWindow.Width}x{hintWindow.Height}");
        };
        hintWindow.ShowDialog();
    }

    private void MinimizeToTray_Click(object sender, RoutedEventArgs e)
    {
        HideToTray();
    }

    private void HideToTray()
    {
        _trayIcon.Visible = true;
        this.Hide();
    }

    private void TrayIcon_Click(object? sender, EventArgs e)
    {
        Show();
        Activate();
        _trayIcon.Visible = false;
    }

    private void TrayIcon_DoubleClick(object? sender, EventArgs e)
    {
        Show();
        Activate();
        _trayIcon.Visible = false;
    }

    private void Show_Click(object? sender, EventArgs e)
    {
        Show();
        Activate();
        _trayIcon.Visible = false;
    }

    private void Exit_Click(object? sender, EventArgs e)
    {
        _trayIcon.Visible = false;
        Application.Current.Shutdown();
    }

    private void BackCycles_Changed(object sender, RoutedEventArgs e)
    {
        _poller.BackCyclesYxab = BackCyclesCheckbox.IsChecked == true;
    }

    private void SkipDefault_Changed(object sender, RoutedEventArgs e)
    {
        _poller.SkipDefault = SkipDefaultCheckbox.IsChecked == true;
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
        RenameProfileButton.IsEnabled = !isDefault;
        SaveProfileButton.IsEnabled = !isDefault;
        DeleteProfileButton.IsEnabled = !isDefault;
        ProfileNameBox.IsEnabled = !isDefault;
        ExportProfileButton.IsEnabled = !isDefault || _profiles.UserProfileCount > 0;
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
                _profiles.CurrentProfileIndex = _profiles.Profiles.IndexOf(profile);
                ProfileNameBox.Text = profile.Name;
                ProfileText.Text = profile.Name;
                _lastProfileName = profile.Name;
                IsDefaultCheckbox.IsChecked = !_profiles.CurrentProfile.Mappings.ContainsKey(_selectedKeyName ?? "");
                UpdateCurrentMappingDisplay();
                UpdateProfileEditingState();
                UpdateSkipDefaultState();
                ApplyDiagonalDelayFromProfile();
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
                Mappings = new System.Collections.Generic.Dictionary<string, ushort>(_profiles.CurrentProfile.Mappings)
            };
            if (DiagDelayPerProfileCheckbox.IsChecked == true)
            {
                profile.DiagonalDelayMs = _profiles.CurrentProfile.DiagonalDelayMs;
                profile.DirectionalDelayMs = _profiles.CurrentProfile.DirectionalDelayMs;
                profile.DiagonalDelayHoldCardinals = _profiles.CurrentProfile.DiagonalDelayHoldCardinals;
            }
            _profiles.SaveProfile(profile);
            _profiles.CurrentProfileIndex = _profiles.Profiles.IndexOf(profile);
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

    private void RenameProfile_Click(object sender, RoutedEventArgs e)
    {
        SafeInvoke(() =>
        {
            if (_profiles.CurrentProfile.Name == "Default") return;
            var newName = ProfileNameBox.Text.Trim();
            if (string.IsNullOrEmpty(newName))
            {
                MessageBox.Show("Enter a new profile name in the text box first.", "Rename Profile",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!ValidateProfileName(newName)) return;
            if (newName == _profiles.CurrentProfile.Name) return;
            if (_profiles.Profiles.Any(p => p.Name == newName))
            {
                MessageBox.Show("A profile with this name already exists.", "Rename Profile",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _profiles.RenameProfile(_profiles.CurrentProfile.Name, newName);
            _lastProfileName = newName;
            RefreshProfileList();
        });
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        SafeInvoke(() =>
        {
            if (_profiles.CurrentProfile.Name == "Default") return;
            var name = ProfileNameBox.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show("Enter a profile name.", "Save Profile", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!ValidateProfileName(name)) return;
            var profile = _profiles.Profiles.FirstOrDefault(p => p.Name == name);
            if (profile == null)
            {
                profile = new KeyMapProfile { Name = name };
            }
            if (DiagDelayPerProfileCheckbox.IsChecked == true)
            {
                profile.DiagonalDelayMs = (int)DiagDelaySlider.Value;
                profile.DirectionalDelayMs = (int)DirDelaySlider.Value;
                profile.DiagonalDelayHoldCardinals = DiagDelayHoldCheckbox.IsChecked == true;
            }
            _profiles.SaveProfile(profile);
            _profiles.CurrentProfileIndex = _profiles.Profiles.IndexOf(profile);
            _lastProfileName = profile.Name;
            RefreshProfileList();
        });
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

    private void ImportProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import Profile",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".json"
        };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                var json = File.ReadAllText(dialog.FileName);
                var finalName = Path.GetFileNameWithoutExtension(dialog.FileName);
                if (finalName.Length > 18)
                {
                    finalName = finalName[..18];
                    MessageBox.Show("Filename was truncated to 18 characters for the profile name.",
                        "Name Truncated", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                int counter = 1;
                var baseName = finalName;
                while (_profiles.Profiles.Any(p => p.Name == finalName))
                {
                    var suffix = $" ({counter})";
                    var maxBase = 18 - suffix.Length;
                    finalName = (baseName.Length > maxBase ? baseName[..maxBase] : baseName) + suffix;
                    counter++;
                }
                _profiles.ImportProfile(json, finalName);
                RefreshProfileList();
                MessageBox.Show($"Profile \"{finalName}\" imported successfully.", "Import",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to import profile: {ex.Message}", "Import Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void ExportProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = _profiles.CurrentProfile.Name;
        if (name == "Default" && _profiles.UserProfileCount == 0)
        {
            MessageBox.Show("No user profile selected to export.", "Export",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = $"Export Profile - {name}",
            FileName = $"{name}.json",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".json"
        };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                var json = _profiles.ExportProfile(name);
                File.WriteAllText(dialog.FileName, json);
                MessageBox.Show($"Profile \"{name}\" exported successfully.", "Export",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to export profile: {ex.Message}", "Export Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void KeyListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (KeyListBox.SelectedItem is System.Windows.Controls.ListBoxItem item)
        {
            _selectedKeyName = item.Tag as string;
            IsDefaultCheckbox.IsChecked = !_profiles.CurrentProfile.Mappings.ContainsKey(_selectedKeyName ?? "");
            UpdateCurrentMappingDisplay();
        }
    }

    private void UpdateCurrentMappingDisplay()
    {
        KeyCaptureBox.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEE, 0xEE, 0xEE));
        if (_selectedKeyName == null)
        {
            CurrentMappingText.Text = "(none selected)";
            KeyCaptureBox.IsEnabled = false;
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

            // Show keyboard key in the capture box (same as XInput mode)
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
                _profiles.CurrentProfile.Mappings[_selectedKeyName] = GetDefaultKeyValue(_selectedKeyName);
                KeyCaptureBox.IsEnabled = _profiles.CurrentProfile.Name != "Default";
            }
        }
        UpdateCurrentMappingDisplay();
    }

    private void KeyCaptureBox_MouseDown(object sender, MouseButtonEventArgs e)
    {
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
        if (_selectedKeyName != null && IsDefaultCheckbox.IsChecked != true)
        {
            _profiles.CurrentProfile.Mappings[_selectedKeyName] = vk;
            CurrentMappingText.Text = keyName;
            if (!_customKeyWarningShown && _profiles.CurrentProfile.Name != "Default")
            {
                _customKeyWarningShown = true;
                MessageBox.Show(
                    "Custom keybinds should only be used if you have changed\nKEmulator's default keys.\n\nStick with the default key layout unless you know what you're doing.",
                    "Custom Keybind Warning",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        KeyCaptureText.Text = keyName;
        KeyCaptureBox.Background = System.Windows.Media.Brushes.LightGreen;
        _isCapturingKey = false;
        this.PreviewKeyDown -= OnCaptureKeyDown;
    }

    private string GetDInputDeviceName()
    {
        var di = _poller.DirectInputReader;
        if (di?.DebugInfo?.StartsWith("OK: ") == true)
            return di.DebugInfo[4..];
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
        catch
        {
            _dinputMapping = new DInputMapping();
        }
    }

    private static string GetKeyNameFromVK(ushort vk)
    {
        if (vk >= 0x30 && vk <= 0x39) return $"{(char)('0' + (vk - 0x30))}";
        if (vk >= 0x41 && vk <= 0x5A) return $"{(char)('A' + (vk - 0x41))}";
        if (vk >= 0x70 && vk <= 0x7A) return $"F{vk - 0x70 + 1}";
        if (vk >= 0x60 && vk <= 0x69) return $"Numpad {vk - 0x60}";
        return vk switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
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
            "Y" => "Num *",
            "X" => "F1",
            "A" => "Enter / Num 5",
            "B" => "F2",
            "LB" => "Num *",
            "RB" => "Num /",
            "LT" => "F1",
            "RT" => "F2",
            "RightThumb" => "Enter",
            _ => "Unknown"
        };
    }

    private static ushort GetDefaultKeyValue(string keyName)
    {
        return keyName switch
        {
            "Y" => 0x6A,
            "X" => 0x70,
            "A" => 0x0D,
            "B" => 0x71,
            "LB" => 0x6A,
            "RB" => 0x6F,
            "LT" => 0x70,
            "RT" => 0x71,
            "RightThumb" => 0x0D,
            _ => 0
        };
    }
}
