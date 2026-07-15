using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace J2MEGamepad.Windows;

public partial class OverlayWindow : Window
{
    private Storyboard? _currentAnimation;
    private Storyboard? _disconnectedAnimation;
    private DispatcherTimer? _swapTimer;
    private string _lastOsdMessage = "";

    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int GWL_EXSTYLE = -20;

    [DllImport("user32.dll")]
    private static extern nint GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern nint SetWindowLong(IntPtr hWnd, int nIndex, nint dwNewLong);

    public OverlayWindow()
    {
        InitializeComponent();
        // Apply WS_EX_NOACTIVATE before the window is shown (SourceInitialized fires
        // when the HWND is created, before Show() returns), not in Loaded which fires
        // after Show() has already activated the window.
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT);
        };
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ClearAnimations();
    }

    private void ClearAnimations()
    {
        _currentAnimation?.Stop();
        _currentAnimation = null;
        if (_swapTimer != null)
        {
            _swapTimer.Stop();
            _swapTimer = null;
        }
        _disconnectedAnimation?.Stop();
        _disconnectedAnimation = null;
        DisconnectedBorder.BeginAnimation(OpacityProperty, null);
        ComboModifierBorder.BeginAnimation(OpacityProperty, null);
        ConfirmationOsdBorder.BeginAnimation(OpacityProperty, null);
        OsdBorder.BeginAnimation(OpacityProperty, null);
        OsdText.BeginAnimation(OpacityProperty, null);
    }

    public void SetDisconnectedText(string text)
    {
        DisconnectedText.Text = text;
    }

    public void ShowDisconnected()
    {
        ClearAnimations();
        DisconnectedBorder.Opacity = 0;
        OsdBorder.Opacity = 0;

        var fadeIn = new DoubleAnimation(0, 0.8, TimeSpan.FromMilliseconds(500));
        var fadeOut = new DoubleAnimation(0.8, 0, TimeSpan.FromMilliseconds(500));
        fadeOut.BeginTime = TimeSpan.FromMilliseconds(1500);

        var storyboard = new Storyboard();
        storyboard.Children.Add(fadeIn);
        storyboard.Children.Add(fadeOut);

        Storyboard.SetTarget(fadeIn, DisconnectedBorder);
        Storyboard.SetTargetProperty(fadeIn, new PropertyPath(OpacityProperty));
        Storyboard.SetTarget(fadeOut, DisconnectedBorder);
        Storyboard.SetTargetProperty(fadeOut, new PropertyPath(OpacityProperty));

        storyboard.RepeatBehavior = RepeatBehavior.Forever;
        _disconnectedAnimation = storyboard;
        storyboard.Begin(this);
    }

    public void HideDisconnected()
    {
        ClearAnimations();
        DisconnectedBorder.Opacity = 0;
    }

    public void ShowComboModifier()
    {
        ClearAnimations();
        DisconnectedBorder.Opacity = 0;
        OsdBorder.Opacity = 0;
        ConfirmationOsdBorder.Opacity = 0;
        ConfirmationOsdText.Text = "";
        ComboModifierText.Text = "Press combo key...";
        ComboModifierBorder.Opacity = 0;

        var fadeIn = new DoubleAnimation(0, 0.85, TimeSpan.FromMilliseconds(750));
        var fadeOut = new DoubleAnimation(0.85, 0, TimeSpan.FromMilliseconds(750));
        fadeIn.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
        fadeOut.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
        fadeOut.BeginTime = TimeSpan.FromMilliseconds(750);

        var storyboard = new Storyboard();
        storyboard.Children.Add(fadeIn);
        storyboard.Children.Add(fadeOut);

        Storyboard.SetTarget(fadeIn, ComboModifierBorder);
        Storyboard.SetTargetProperty(fadeIn, new PropertyPath(OpacityProperty));
        Storyboard.SetTarget(fadeOut, ComboModifierBorder);
        Storyboard.SetTargetProperty(fadeOut, new PropertyPath(OpacityProperty));

        storyboard.RepeatBehavior = RepeatBehavior.Forever;
        _disconnectedAnimation = storyboard;
        storyboard.Begin(this);
    }

    public void HideComboModifier()
    {
        ClearAnimations();
        ComboModifierBorder.Opacity = 0;
        ComboModifierText.Text = "";
    }

    public void ShowComboConfirmation(string text)
    {
        ClearAnimations();
        DisconnectedBorder.Opacity = 0;
        ComboModifierBorder.Opacity = 0;
        ComboModifierText.Text = "";
        OsdBorder.Opacity = 0;
        OsdText.Text = "";
        _lastOsdMessage = text;
        ConfirmationOsdText.Text = text;
        ConfirmationOsdBorder.Opacity = 0.7;
    }

    public void HideComboConfirmation()
    {
        ClearAnimations();
        ConfirmationOsdBorder.Opacity = 0;
        ConfirmationOsdText.Text = "";
    }

    public void ShowOsd(string text)
    {
        ClearAnimations();
        DisconnectedBorder.Opacity = 0;
        ComboModifierBorder.Opacity = 0;
        ComboModifierText.Text = "";
        ConfirmationOsdBorder.Opacity = 0;
        ConfirmationOsdText.Text = "";
        _lastOsdMessage = text;
        OsdText.Text = text;
        OsdText.Opacity = 1;
        OsdBorder.Opacity = 0;

        var fadeIn = new DoubleAnimation(0, 0.5, TimeSpan.FromMilliseconds(80));
        var hold = new DoubleAnimation(0.5, 0.5, TimeSpan.FromMilliseconds(1000));
        var fadeOut = new DoubleAnimation(0.5, 0, TimeSpan.FromMilliseconds(420));

        var storyboard = new Storyboard();
        storyboard.Children.Add(fadeIn);
        storyboard.Children.Add(hold);
        storyboard.Children.Add(fadeOut);

        Storyboard.SetTarget(fadeIn, OsdBorder);
        Storyboard.SetTargetProperty(fadeIn, new PropertyPath(OpacityProperty));
        Storyboard.SetTarget(hold, OsdBorder);
        Storyboard.SetTargetProperty(hold, new PropertyPath(OpacityProperty));
        Storyboard.SetTarget(fadeOut, OsdBorder);
        Storyboard.SetTargetProperty(fadeOut, new PropertyPath(OpacityProperty));

        fadeIn.BeginTime = TimeSpan.Zero;
        hold.BeginTime = TimeSpan.FromMilliseconds(80);
        fadeOut.BeginTime = TimeSpan.FromMilliseconds(1080);

        _currentAnimation = storyboard;
        storyboard.Begin(this);
    }

    public void ShowOsdSwap(string oldText, string newText)
    {
        ClearAnimations();
        DisconnectedBorder.Opacity = 0;
        ConfirmationOsdBorder.Opacity = 0;
        ConfirmationOsdText.Text = "";

        OsdText.Text = oldText;
        OsdText.Opacity = 1;
        OsdBorder.Opacity = 0.5;

        var fadeOutOld = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150));
        var fadeInNew = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
        fadeInNew.BeginTime = TimeSpan.FromMilliseconds(150);
        var hold = new DoubleAnimation(0.5, 0.5, TimeSpan.FromMilliseconds(700));
        hold.BeginTime = TimeSpan.FromMilliseconds(300);
        var fadeOut = new DoubleAnimation(0.5, 0, TimeSpan.FromMilliseconds(500));
        fadeOut.BeginTime = TimeSpan.FromMilliseconds(1000);

        var storyboard = new Storyboard();
        storyboard.Children.Add(fadeOutOld);
        storyboard.Children.Add(fadeInNew);
        storyboard.Children.Add(hold);
        storyboard.Children.Add(fadeOut);

        Storyboard.SetTarget(fadeOutOld, OsdText);
        Storyboard.SetTargetProperty(fadeOutOld, new PropertyPath(OpacityProperty));
        Storyboard.SetTarget(fadeInNew, OsdText);
        Storyboard.SetTargetProperty(fadeInNew, new PropertyPath(OpacityProperty));
        Storyboard.SetTarget(hold, OsdBorder);
        Storyboard.SetTargetProperty(hold, new PropertyPath(OpacityProperty));
        Storyboard.SetTarget(fadeOut, OsdBorder);
        Storyboard.SetTargetProperty(fadeOut, new PropertyPath(OpacityProperty));

        _swapTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _swapTimer.Tick += (_, _) =>
        {
            _swapTimer.Stop();
            _lastOsdMessage = newText;
            OsdText.Text = newText;
            OsdText.Opacity = 0;
        };
        _swapTimer.Start();

        _currentAnimation = storyboard;
        storyboard.Begin(this);
    }

    public void ShowLastOsd()
    {
        if (!string.IsNullOrEmpty(_lastOsdMessage))
            ShowOsd(_lastOsdMessage);
    }
}
