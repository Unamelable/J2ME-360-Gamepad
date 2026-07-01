using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using J2MEGamepad.Models;
using J2MEGamepad.Services;

namespace J2MEGamepad.Windows;

public partial class MainWindow : Window
{
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

    public MainWindow()
    {
        InitializeComponent();

        _keyboard = new KeyboardInjector();
        _profiles = new ProfileManager();
        _poller = new GamepadPoller(_keyboard, _profiles);
        _watchdog = new ControllerWatchdog();
        _overlay = new OverlayWindow();

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
        _profiles.ProfilesChanged += OnProfilesChanged;

        RefreshProfileList();

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
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

        ApplyDiagonalDelayFromProfile();
        ShowFirstRunWarning();

        _watchdog.Start();
        _poller.Start();
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

        _poller.Dispose();
        _watchdog.Dispose();
        _keyboard.Dispose();
        _profiles.Dispose();
        _trayIcon.Dispose();
        _overlay.Close();
    }

    private void DiagDelaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_poller == null) return;
        int val = (int)DiagDelaySlider.Value;
        _poller.DiagonalDelayMs = val;
        DiagDelayValue.Text = val == 0 ? "Off" : val.ToString();
    }

    private void DiagDelayHold_Changed(object sender, RoutedEventArgs e)
    {
        if (_poller == null) return;
        _poller.DiagonalDelayHoldCardinals = DiagDelayHoldCheckbox.IsChecked == true;
    }

    private void DiagDelayPerProfile_Changed(object sender, RoutedEventArgs e)
    {
        if (_poller == null) return;
        ApplyDiagonalDelayFromProfile();
    }

    private void ApplyDiagonalDelayFromProfile()
    {
        if (DiagDelayPerProfileCheckbox.IsChecked == true)
        {
            int delayMs = _profiles.CurrentProfile.DiagonalDelayMs;
            DiagDelaySlider.Value = delayMs;
            _poller.DiagonalDelayMs = delayMs;
            DiagDelayValue.Text = delayMs == 0 ? "Off" : delayMs.ToString();
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
            _overlay.HideDisconnected();
            ControllerStatus.Text = "Controller connected";
            ControllerStatus.Foreground = System.Windows.Media.Brushes.Green;
        });
    }

    private void OnControllerDisconnected()
    {
        Dispatcher.Invoke(() =>
        {
            ControllerStatus.Text = "Please connect Xbox 360 Controller";
            ControllerStatus.Foreground = System.Windows.Media.Brushes.Red;
            ShowDisconnectedWarning();
        });
    }

    private void OnConnectionChanged(bool connected)
    {
        Dispatcher.Invoke(() =>
        {
            if (connected)
            {
                _overlay.HideDisconnected();
                ControllerStatus.Text = "Controller connected";
                ControllerStatus.Foreground = System.Windows.Media.Brushes.Green;
            }
            else
            {
                ControllerStatus.Text = "Please connect Xbox 360 Controller";
                ControllerStatus.Foreground = System.Windows.Media.Brushes.Red;
                ShowDisconnectedWarning();
            }
        });
    }

    private void ShowDisconnectedWarning()
    {
        _overlay.Show();
        _overlay.ShowDisconnected();
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

    private void KeysHintButton_Click(object sender, RoutedEventArgs e)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("J2MEGamepad.KEys.txt");
        if (stream == null)
        {
            MessageBox.Show("KEys.txt not found.", "Keys Reference",
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
                profile.DiagonalDelayMs = _profiles.CurrentProfile.DiagonalDelayMs;
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
            try
            {
                var logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "J2MEGamepad");
                Directory.CreateDirectory(logDir);
                var logPath = Path.Combine(logDir, "error.log");
                File.AppendAllText(logPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex.Message}\n{ex.StackTrace}\n\n");
            }
            catch { }
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
                profile.DiagonalDelayMs = (int)DiagDelaySlider.Value;
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
        if (IsDefaultCheckbox.IsChecked == true)
        {
            CurrentMappingText.Text = GetDefaultKeyName(_selectedKeyName);
            KeyCaptureText.Text = "Default";
            return;
        }
        if (_profiles.CurrentProfile.Mappings.TryGetValue(_selectedKeyName, out var vk))
            CurrentMappingText.Text = GetKeyNameFromVK(vk);
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
