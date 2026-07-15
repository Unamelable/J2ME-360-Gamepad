using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Timers;
using J2MEGamepad.Models;
using J2MEGamepad.NativeMethods;

namespace J2MEGamepad.Services;

public class GamepadPoller : IDisposable
{
    private enum ControllerType { None, XInput, DInput }
    private ControllerType _controllerType = ControllerType.None;

    private readonly Timer _timer;
    private DPadKey _lastSentDPadKey = DPadKey.None;
    private bool _aWasPressed;
    private bool _bWasPressed;
    private bool _xWasPressed;
    private bool _yWasPressed;
    private bool _backWasPressed;
    private bool _startWasPressed;
    private bool _leftThumbModeWasPressed;
    private bool _leftThumbWasPressed;
    private ushort _currentLeftThumbHeldKey;
    private bool _rightThumbWasPressed;
    private bool _lbWasPressed;
    private bool _rbWasPressed;
    private bool _ltWasPressed;
    private bool _rtWasPressed;
    private bool _connected;

    // Combo modifier state
    private enum ComboModifier { None, RbLb, RtLt, Lsb, Rsb }

    public bool LeftThumbIsComboModifier { get; set; }
    public bool RightThumbIsComboModifier { get; set; }
    private ComboModifier _activeComboModifier = ComboModifier.None;
    private readonly HashSet<ushort> _comboHeldKeys = new();
    private readonly Dictionary<string, bool> _comboWasPressed = new();

    // Combo confirmation hold state
    public bool ComboConfirmationHold { get; set; }
    private bool _confirmActive;
    private string _confirmComboName = "";
    private string _confirmOsdName = "";
    private long _confirmStartTickMs;
    private bool _confirmTriggerQueued;
    private const long ComboConfirmationDelayMs = 1000;


    private ushort _currentAHeldKey;
    private ushort _currentBHeldKey;
    private ushort _currentXHeldKey;
    private ushort _currentYHeldKey;
    private ushort _currentLBHeldKey;
    private ushort _currentRBHeldKey;
    private ushort _currentLTHeldKey;
    private ushort _currentRTHeldKey;
    private ushort _currentRightThumbHeldKey;

    private readonly KeyboardInjector _keyboard;
    private readonly ProfileManager _profiles;
    private readonly System.Diagnostics.Stopwatch _stopwatch = System.Diagnostics.Stopwatch.StartNew();
    private DirectInputReader _directInput = new();
    private IntPtr _directInputWindowHandle;
    private bool _tryXInputNext = true;

    private DPadMode _currentDPadMode = DPadMode.Pad;
    private bool _backCyclesYxab;
    private bool _skipDefault;
    private readonly HashSet<ushort> _heldPadKeys = new();
    private int _diagonalDelayMs;
    private bool _diagonalDelayHoldCardinals;
    private long _diagonalEntryTick;
    private DPadKey _pendingDiagonalKey = DPadKey.None;
    private bool _diagonalActivated;
    private DPadKey _pendingDiagonalConfirmKey = DPadKey.None;
    private int _diagonalConfirmCount;
    private DPadKey _confirmedDiagonalKey = DPadKey.None;
    private int _directionalDelayMs;
    private long _diagonalConfirmStartTick;
    private bool _diagonalConfirmTiming;

    private DPadKey _pendingCardinalConfirmKey = DPadKey.None;
    private int _cardinalConfirmCount;
    private bool _cardinalConfirmTiming;
    private long _cardinalConfirmStartTick;
    private DPadKey _confirmedCardinalKey = DPadKey.None;

    private DPadKey _pendingCardinalDelayKey = DPadKey.None;
    private long _cardinalDelayStartTick;

    private static class VK
    {
        public static readonly ushort NumPad1 = KeyMapProfile.KeyNumpad1;
        public static readonly ushort NumPad2 = KeyMapProfile.KeyNumpad2;
        public static readonly ushort NumPad3 = KeyMapProfile.KeyNumpad3;
        public static readonly ushort NumPad4 = KeyMapProfile.KeyNumpad4;
        public static readonly ushort NumPad5 = KeyMapProfile.KeyNumpad5;
        public static readonly ushort NumPad0 = KeyMapProfile.KeyNumpad0;
        public static readonly ushort NumPad6 = KeyMapProfile.KeyNumpad6;
        public static readonly ushort NumPad7 = KeyMapProfile.KeyNumpad7;
        public static readonly ushort NumPad8 = KeyMapProfile.KeyNumpad8;
        public static readonly ushort NumPad9 = KeyMapProfile.KeyNumpad9;
        public static readonly ushort Up = KeyMapProfile.KeyUp;
        public static readonly ushort Down = KeyMapProfile.KeyDown;
        public static readonly ushort Left = KeyMapProfile.KeyLeft;
        public static readonly ushort Right = KeyMapProfile.KeyRight;
        public static readonly ushort Multiply = KeyMapProfile.KeyMultiply;
        public static readonly ushort Divide = KeyMapProfile.KeyDivide;
        public static readonly ushort F1 = KeyMapProfile.KeyF1;
        public static readonly ushort F2 = KeyMapProfile.KeyF2;
        public static readonly ushort Return = KeyMapProfile.KeyEnter;
        public static readonly ushort F3 = KeyMapProfile.KeyF3;

        // Cached DPad key arrays to avoid per-call allocation
        public static readonly ushort[] Empty = new ushort[0];
        public static readonly ushort[] UpA = { KeyMapProfile.KeyUp };
        public static readonly ushort[] DownA = { KeyMapProfile.KeyDown };
        public static readonly ushort[] LeftA = { KeyMapProfile.KeyLeft };
        public static readonly ushort[] RightA = { KeyMapProfile.KeyRight };
        public static readonly ushort[] NumPad8A = { KeyMapProfile.KeyNumpad8 };
        public static readonly ushort[] NumPad2A = { KeyMapProfile.KeyNumpad2 };
        public static readonly ushort[] NumPad4A = { KeyMapProfile.KeyNumpad4 };
        public static readonly ushort[] NumPad6A = { KeyMapProfile.KeyNumpad6 };
        public static readonly ushort[] NumPad7A = { KeyMapProfile.KeyNumpad7 };
        public static readonly ushort[] NumPad9A = { KeyMapProfile.KeyNumpad9 };
        public static readonly ushort[] NumPad1A = { KeyMapProfile.KeyNumpad1 };
        public static readonly ushort[] NumPad3A = { KeyMapProfile.KeyNumpad3 };
    }

    private DInputMapping _dinputMapping = new();
    private DInputCalibration _dinputCalibration = new();
    private ComboSettings _comboSettings = new();
    private Dictionary<int, string> _dinputButtonToAction = new();
    private readonly Dictionary<int, bool> _dinputWasPressed = new();
    private readonly Dictionary<string, ushort> _dinputHeldKeys = new();
    private uint _lastDInputButtons;

    private static readonly int JoyInfoExSize = Marshal.SizeOf(typeof(JOYINFOEX));
    private int _dinputBackIdx = 8;
    private int _dinputStartIdx = 9;
    private int _dinputLeftThumbIdx = 10;
    private int _dinputRightThumbIdx = 11;
    private int _dinputLBIdx = 4;
    private int _dinputRBIdx = 5;
    private int _dinputLTIdx = 6;
    private int _dinputRTIdx = 7;

    public bool IsDInputMode => _controllerType == ControllerType.DInput;

    public bool IsRemapping { get; set; }
    public DInputCalibration DInputCalibration => _dinputCalibration;
    public DirectInputReader DirectInputReader => _directInput;

    public IntPtr DirectInputWindowHandle
    {
        get => _directInputWindowHandle;
        set
        {
            _directInputWindowHandle = value;
            if (_directInput != null)
                _directInput.WindowHandle = value;
        }
    }

    public event Action<string>? ModeChanged;
    public event Action<bool>? ConnectionChanged;
    public event Action<bool>? BackCyclesToggled;
    public event Action<int>? DInputButtonPressed;
    public event Action<string>? DInputModeChanged;
    public event Action<string>? ComboTriggered;
    public event Action<string>? ComboModifierActive;
    public event Action? ComboModifierInactive;
    public event Action<string>? ComboConfirmationPending;
    public event Action? ComboConfirmationCancelled;

    public DPadMode CurrentDPadMode
    {
        get => _currentDPadMode;
        set
        {
            if (_currentDPadMode != value)
            {
                _currentDPadMode = value;
                ForceReleaseDPad();
                ModeChanged?.Invoke(GetModeName(value));
            }
        }
    }

    public bool BackCyclesYxab
    {
        get => _backCyclesYxab;
        set => _backCyclesYxab = value;
    }

    public bool SkipDefault
    {
        get => _skipDefault;
        set => _skipDefault = value;
    }

    public int DiagonalDelayMs
    {
        get => _diagonalDelayMs;
        set
        {
            _diagonalDelayMs = Math.Max(0, value);
            if (_diagonalDelayMs == 0)
            {
                _pendingDiagonalKey = DPadKey.None;
                _diagonalActivated = false;
            }
        }
    }

    public bool DiagonalDelayHoldCardinals
    {
        get => _diagonalDelayHoldCardinals;
        set => _diagonalDelayHoldCardinals = value;
    }

    public int DirectionalDelayMs
    {
        get => _directionalDelayMs;
        set => _directionalDelayMs = Math.Max(0, value);
    }

    public bool IsConnected => _connected;
    private volatile bool _disposed;

    public GamepadPoller(KeyboardInjector keyboard, ProfileManager profiles)
    {
        _keyboard = keyboard;
        _profiles = profiles;
        _dinputMapping = Models.DInputMapping.Load();
        _dinputButtonToAction = _dinputMapping.BuildReverseMap();
        _dinputCalibration = Models.DInputCalibration.Load();
        _comboSettings = Models.ComboSettings.Load();
        UpdateCachedDInputIndices();
        _timer = new Timer(8);
        _timer.Elapsed += Poll;
    }

    private void UpdateCachedDInputIndices()
    {
        _dinputBackIdx = _dinputMapping.ActionToButton.TryGetValue("Back", out int b) ? b : 8;
        _dinputStartIdx = _dinputMapping.ActionToButton.TryGetValue("Start", out int s) ? s : 9;
        _dinputLeftThumbIdx = _dinputMapping.ActionToButton.TryGetValue("LeftThumb", out int l) ? l : 10;
        _dinputRightThumbIdx = _dinputMapping.ActionToButton.TryGetValue("RightThumb", out int r) ? r : 11;
        _dinputLBIdx = _dinputMapping.ActionToButton.TryGetValue("LB", out int lb) ? lb : 4;
        _dinputRBIdx = _dinputMapping.ActionToButton.TryGetValue("RB", out int rb) ? rb : 5;
        _dinputLTIdx = _dinputMapping.ActionToButton.TryGetValue("LT", out int lt) ? lt : 6;
        _dinputRTIdx = _dinputMapping.ActionToButton.TryGetValue("RT", out int rt) ? rt : 7;
    }

    public void Start()
    {
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        _keyboard.ReleaseAll();
    }

    private static void LogError(string msg)
    {
        LogHelper.Info("Poller", msg);
    }

    private void Poll(object? sender, ElapsedEventArgs e)
    {
        if (_disposed) return;
        try
        {
            if (_controllerType == ControllerType.XInput)
            {
                PollXInputActive();
                return;
            }

            if (_controllerType == ControllerType.DInput)
            {
                PollDInputActive();
                return;
            }

            // No controller — alternate detection each tick: XInput one tick, DInput the next
            if (_tryXInputNext)
                PollXInputDetection();
            else
                PollDInputDetection();

            _tryXInputNext = !_tryXInputNext;
        }
        catch (Exception ex)
        {
            LogError($"Poll: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private void PollXInputActive()
    {
        var result = XInput.GetState(0, out var state);
        if (result == XInput.ERROR_SUCCESS)
        {
            if (!_connected)
            {
                _connected = true;
                ConnectionChanged?.Invoke(true);
            }
            ProcessXInputState(state);
            return;
        }
        _controllerType = ControllerType.None;
        HandleDisconnect();
        _tryXInputNext = false; // try DInput next tick
    }

    private void PollXInputDetection()
    {
        var result = XInput.GetState(0, out var state);
        if (result != XInput.ERROR_SUCCESS)
            return;
        _controllerType = ControllerType.XInput;
        _connected = true;
        ConnectionChanged?.Invoke(true);
        ProcessXInputState(state);
    }

    private static bool IsXInputConnected()
    {
        try
        {
            return XInput.GetState(0, out _) == XInput.ERROR_SUCCESS;
        }
        catch
        {
            return false; // xinput1_3.dll not available
        }
    }

    private void ProcessXInputState(XInputState state)
    {
        var current = new GamepadState();
        current.UpdateFromButtons(state.Gamepad.wButtons);
        current.UpdateTriggers(state.Gamepad.bLeftTrigger, state.Gamepad.bRightTrigger);
        ProcessDPad(current);
        if (!IsRemapping)
        {
            bool isDefault = _profiles.IsDefaultProfile;
            bool rbLbNow = current.LB && current.RB;
            bool rtLtNow = current.LT && current.RT;
            bool lsbModNow = LeftThumbIsComboModifier && current.LeftThumb;
            bool rsbModNow = RightThumbIsComboModifier && current.RightThumb;

            if (rbLbNow || rtLtNow || lsbModNow || rsbModNow)
            {
                string prefix;
                if (lsbModNow) prefix = "LSB+";
                else if (rsbModNow) prefix = "RSB+";
                else if (rbLbNow) prefix = "RB+LB+";
                else prefix = "RT+LT+";
                ProcessComboModifierXInput(current, prefix, isDefault);
                ProcessButtonEdge(current.Back, ref _backWasPressed, () => { });
                ProcessButtonEdge(current.Start, ref _startWasPressed, () => { });
                ProcessButtonEdge(current.LeftThumb, ref _leftThumbModeWasPressed, () => { });
            }
            else
            {
                EndComboModifier();
                ProcessActionButtons(current, isDefault);
                ProcessShoulderButtons(current, isDefault);
                ProcessModeSwitches(current);
            }
        }
    }

    private void PollDInputActive()
    {
        if (PollDInput())
            return;
        _controllerType = ControllerType.None;
        HandleDisconnect();
        _tryXInputNext = true; // try XInput next tick
    }

    private void PollDInputDetection()
    {
        PollDInput();
    }

    private string _lastPollSummary = "";
    private long _lastDetectionTime;
    private const long DetectionCooldownMs = 500;
    private bool PollDInput()
    {
        // ── WinMM (winmm.dll / joyGetPosEx) ─────────────────────────────
        // This is the ONLY active detection path.  DirectInput8 is
        // permanently stripped out (see commit history) because on some
        // Windows 11 systems DirectInput8Create/CoCreateInstance both
        // fail with 0x80004002 (E_NOINTERFACE / COM class not registered).
        //
        // WinMM has been confirmed working on Windows XP through 11.
        // DO NOT REPLACE without adding a working HID API path first.
        //
        // Future HID API addition should be checked BEFORE this block
        // so WinMM can be disabled for debugging the new path.
        // ─────────────────────────────────────────────────────────────────

        // Cache: when no controller is detected, skip WinMM scan for 500ms
        if (_controllerType == ControllerType.None &&
            (_stopwatch.ElapsedMilliseconds - _lastDetectionTime) < DetectionCooldownMs)
            return false;
        _lastDetectionTime = _stopwatch.ElapsedMilliseconds;

        int winmmId = -1;
        var ji = new JOYINFOEX();
        for (int id = 0; id < 16; id++)
        {
            ji = new JOYINFOEX();
            ji.dwSize = JoyInfoExSize;
            ji.dwFlags = JoyInput.JOY_RETURNALL | JoyInput.JOY_RETURNPOVCTS;
            if (JoyInput.GetPosEx(id, ref ji) == JoyInput.JOYERR_NOERROR)
            {
                winmmId = id;
                break;
            }
        }

        if (winmmId >= 0)
        {
            // If XInput is connected, it takes priority — Xbox controllers
            // appear as WinMM stubs but should be read through XInput.
            if (IsXInputConnected() && _controllerType != ControllerType.XInput)
            {
                if (_controllerType == ControllerType.DInput)
                {
                    LogHelper.Info("Poller", $"WinMM(upgrade id={winmmId}, XInput available, switching)");
                    _controllerType = ControllerType.None;
                    HandleDisconnect();
                }
                return false; // let XInput detection path claim it
            }

            if (_controllerType == ControllerType.None)
            {
                string s = $"WinMM(init id={winmmId})";
                if (s != _lastPollSummary) { _lastPollSummary = s; LogHelper.Info("Poller", s); }
                LoadDInputConfig();
                _controllerType = ControllerType.DInput;
                _connected = true;
                ConnectionChanged?.Invoke(true);
                DInputModeChanged?.Invoke("DInput (WinMM)");
            }

            var current = MakeStateFromJoyInfo(ji);
            ProcessDInput(current, ji);
            return true;
        }

        if (_controllerType != ControllerType.None)
        {
            string s = "DISCONNECT";
            if (s != _lastPollSummary) { _lastPollSummary = s; LogHelper.Info("Poller", s); }
            return false;
        }

        return false;
    }

    // Loads DInput mapping/calibration files.  Does NOT touch the
    // DirectInput8 COM API — that path is dead (see PollDInput).
    private void LoadDInputConfig()
    {
        _dinputMapping = Models.DInputMapping.Load();
        _dinputButtonToAction = _dinputMapping.BuildReverseMap();
        _dinputCalibration = Models.DInputCalibration.Load();
        UpdateCachedDInputIndices();
    }

    private GamepadState MakeStateFromJoyInfo(JOYINFOEX ji)
    {
        var state = new GamepadState();
        state.UpdateFromDInput((uint)ji.dwButtons, ji.dwPOV, ji.dwXpos, ji.dwYpos, ji.dwZpos, ji.dwRpos, _dinputCalibration);
        return state;
    }

    private GamepadState MakeStateFromDirectInput()
    {
        var state = new GamepadState();
        int buttons = _directInput.GetButtonMask();
        state.UpdateFromDInput((uint)buttons, _directInput.Pov, _directInput.X, _directInput.Y, _directInput.Z, _directInput.Rx, _dinputCalibration);
        return state;
    }

    private void ProcessDInput(GamepadState current, JOYINFOEX? ji)
    {
        int rawButtons = ji.HasValue ? (int)(uint)ji.Value.dwButtons : _directInput.GetButtonMask();
        uint buttons = (uint)rawButtons;

        // Apply button-mapped DPad overrides FIRST (always, regardless of POV state)
        foreach (var kvp in _dinputButtonToAction)
        {
            if (((buttons >> kvp.Key) & 1) == 0) continue;
            switch (kvp.Value)
            {
                case "DPadUp": current.DPadUp = true; break;
                case "DPadDown": current.DPadDown = true; break;
                case "DPadLeft": current.DPadLeft = true; break;
                case "DPadRight": current.DPadRight = true; break;
            }
        }

        ProcessDPad(current);

        if (IsRemapping)
        {
            uint changed = buttons ^ _lastDInputButtons;
            uint pressed = buttons & changed;
            if (pressed != 0)
            {
                int buttonIndex = 0;
                uint temp = pressed;
                while ((temp & 1) == 0) { temp >>= 1; buttonIndex++; }
                _lastDInputButtons = buttons;
                IsRemapping = false;
                DInputButtonPressed?.Invoke(buttonIndex);
            }
            _lastDInputButtons = buttons;
            return;
        }

        // Check for DInput combo modifiers (cached bitmasks, O(1) per check)
        bool hasLb = _dinputLBIdx >= 0 && ((buttons >> _dinputLBIdx) & 1) != 0;
        bool hasRb = _dinputRBIdx >= 0 && ((buttons >> _dinputRBIdx) & 1) != 0;
        bool hasLt = _dinputLTIdx >= 0 && ((buttons >> _dinputLTIdx) & 1) != 0;
        bool hasRt = _dinputRTIdx >= 0 && ((buttons >> _dinputRTIdx) & 1) != 0;
        bool hasLsb = _dinputLeftThumbIdx >= 0 && ((buttons >> _dinputLeftThumbIdx) & 1) != 0;
        bool hasRsb = _dinputRightThumbIdx >= 0 && ((buttons >> _dinputRightThumbIdx) & 1) != 0;

        bool rbLbNow = hasLb && hasRb;
        bool rtLtNow = hasLt && hasRt;
        bool lsbModNow = LeftThumbIsComboModifier && hasLsb;
        bool rsbModNow = RightThumbIsComboModifier && hasRsb;

        if (rbLbNow || rtLtNow || lsbModNow || rsbModNow)
        {
            string prefix;
            if (lsbModNow) prefix = "LSB+";
            else if (rsbModNow) prefix = "RSB+";
            else if (rbLbNow) prefix = "RB+LB+";
            else prefix = "RT+LT+";
            ProcessDInputCombo(buttons, prefix);
            bool backPressed = ((buttons >> _dinputBackIdx) & 1) != 0;
            bool startPressed = ((buttons >> _dinputStartIdx) & 1) != 0;
            bool leftThumbPressed = ((buttons >> _dinputLeftThumbIdx) & 1) != 0;
            ProcessButtonEdge(backPressed, ref _backWasPressed, () => { });
            ProcessButtonEdge(startPressed, ref _startWasPressed, () => { });
            ProcessButtonEdge(leftThumbPressed, ref _leftThumbModeWasPressed, () => { });
            return;
        }

        EndComboModifier();
        ProcessDInputActions(buttons);
        ProcessDInputModeSwitches(buttons);
    }

    private void ProcessDInputCombo(uint buttons, string prefix)
    {
        ComboModifier mod = prefix switch
        {
            "RB+LB+" => ComboModifier.RbLb,
            "RT+LT+" => ComboModifier.RtLt,
            "LSB+" => ComboModifier.Lsb,
            "RSB+" => ComboModifier.Rsb,
            _ => ComboModifier.None
        };

        if (_activeComboModifier != mod)
        {
            EndComboModifier();
            _activeComboModifier = mod;
            // Release any held modifier keys
            foreach (var kvp in _dinputButtonToAction)
            {
                if (kvp.Value == "LB" || kvp.Value == "RB" || kvp.Value == "LT" || kvp.Value == "RT" ||
                    kvp.Value == "LeftThumb" || kvp.Value == "RightThumb")
                {
                    if (_dinputHeldKeys.TryGetValue(kvp.Value, out var vk) && vk != 0)
                    {
                        _keyboard.ReleaseKey(vk);
                        _dinputHeldKeys.Remove(kvp.Value);
                    }
                    _dinputWasPressed[kvp.Key] = false;
                }
            }
            ComboModifierActive?.Invoke(prefix.TrimEnd('+'));
        }

        bool isDefault = _profiles.IsDefaultProfile;

        var actionToButton = _dinputMapping.ActionToButton;
        foreach (string btn in ComboFaceButtons)
        {
            int btnIdx;
            if (!actionToButton.TryGetValue(btn, out btnIdx))
            {
                _comboWasPressed[prefix + btn] = false;
                continue;
            }
            bool pressed = ((buttons >> btnIdx) & 1) != 0;
            ProcessComboButton(prefix + btn, pressed, isDefault);
        }

        // Only RB+LB and RT+LT support Start/Back exec combos
        if (prefix == "RB+LB+" || prefix == "RT+LT+")
        {
            foreach (string btn in ComboExecButtons)
            {
                int btnIdx;
                if (!actionToButton.TryGetValue(btn, out btnIdx))
                {
                    _comboWasPressed[prefix + btn] = false;
                    continue;
                }
                bool pressed = ((buttons >> btnIdx) & 1) != 0;
                ProcessComboButton(prefix + btn, pressed, isDefault);
            }
        }
    }

    private static readonly HashSet<string> DInputSystemActions = new() { "Back", "Start" };

    private void ProcessDInputActions(uint buttons)
    {
        foreach (var kvp in _dinputButtonToAction)
        {
            string action = kvp.Value;
            // System buttons handled by ProcessDInputModeSwitches, D-Pad by ProcessDPad
            if (DInputSystemActions.Contains(action) || action.StartsWith("DPad")) continue;

            int btnIndex = kvp.Key;
            bool pressed = ((buttons >> btnIndex) & 1) != 0;

            _dinputWasPressed.TryGetValue(btnIndex, out bool wasPressed);

            if (pressed && !wasPressed)
            {
                _dinputWasPressed[btnIndex] = true;
                var vk = GetVKForDInputAction(action);
                _dinputHeldKeys[action] = vk;
                _keyboard.PressKey(vk);
            }
            else if (!pressed && wasPressed)
            {
                _dinputWasPressed[btnIndex] = false;
                if (_dinputHeldKeys.TryGetValue(action, out var vk) && vk != 0)
                {
                    _keyboard.ReleaseKey(vk);
                    _dinputHeldKeys.Remove(action);
                }
            }
        }
    }

    private void ProcessDInputModeSwitches(uint buttons)
    {
        bool backPressed = ((buttons >> _dinputBackIdx) & 1) != 0;
        bool startPressed = ((buttons >> _dinputStartIdx) & 1) != 0;
        bool leftThumbPressed = ((buttons >> _dinputLeftThumbIdx) & 1) != 0;

        ProcessButtonEdge(backPressed, ref _backWasPressed, () =>
        {
            if (_backCyclesYxab)
            {
                _profiles.CycleBackward(_skipDefault);
                ModeChanged?.Invoke($"YXAB: {_profiles.CurrentProfile.Name}");
            }
            else
            {
                CurrentDPadMode = _currentDPadMode switch
                {
                    DPadMode.Pad => DPadMode.Keypad,
                    DPadMode.Keypad => DPadMode.PadDiagonal,
                    DPadMode.PadDiagonal => DPadMode.Pad,
                    _ => DPadMode.Pad
                };
            }
        });

        ProcessButtonEdge(startPressed, ref _startWasPressed, () =>
        {
            _profiles.CycleForward(_skipDefault);
            ModeChanged?.Invoke($"YXAB: {_profiles.CurrentProfile.Name}");
        });

        ProcessButtonEdge(leftThumbPressed, ref _leftThumbModeWasPressed, () =>
        {
            bool hasMapping = !_profiles.IsDefaultProfile
                && _profiles.CurrentProfile.Mappings.TryGetValue("LeftThumb", out var vk)
                && vk != 0;
            if (!hasMapping)
            {
                _backCyclesYxab = !_backCyclesYxab;
                BackCyclesToggled?.Invoke(_backCyclesYxab);
            }
        });
    }

    private ushort GetVKForDInputAction(string action)
    {
        // D-Pad and system actions are handled elsewhere
        if (action.StartsWith("DPad") || DInputSystemActions.Contains(action))
            return 0;

        if (_profiles.IsDefaultProfile)
        {
            return action switch
            {
                "A" => _currentDPadMode == DPadMode.Pad ? VK.F3 : VK.NumPad5,
                "B" => VK.F2,
                "X" => VK.F1,
                "Y" => VK.NumPad0,
                "LB" => VK.Multiply,
                "RB" => VK.Divide,
                "LT" => VK.F1,
                "RT" => VK.F2,
                "RightThumb" => _currentDPadMode == DPadMode.Pad ? VK.F3 : VK.NumPad5,
                _ => 0
            };
        }
        return _profiles.CurrentProfile.Mappings.TryGetValue(action, out var vk) ? vk : (ushort)0;
    }

    private void HandleDisconnect()
    {
        LogHelper.Info("Poller", "HandleDisconnect");
        _connected = false;
        _keyboard.ReleaseAll();
        ConnectionChanged?.Invoke(false);
        _lastSentDPadKey = DPadKey.None;
        _pendingDiagonalKey = DPadKey.None;
        _diagonalActivated = false;
        _pendingDiagonalConfirmKey = DPadKey.None;
        _diagonalConfirmCount = 0;
        _diagonalConfirmTiming = false;
        _confirmedDiagonalKey = DPadKey.None;
        _dinputWasPressed.Clear();
        _dinputHeldKeys.Clear();
        _pendingCardinalDelayKey = DPadKey.None;
        _confirmActive = false;
        _confirmComboName = "";
        _confirmOsdName = "";
        _confirmTriggerQueued = false;
        EndComboModifier();
        _lastDInputButtons = 0;
        _directInput.Dispose();
        _directInput = new DirectInputReader();
        _directInput.WindowHandle = _directInputWindowHandle;
    }

    public void ReloadDInputMapping()
    {
        _dinputMapping = DInputMapping.Load();
        _dinputButtonToAction = _dinputMapping.BuildReverseMap();
        UpdateCachedDInputIndices();
    }

    public void ReloadCalibration()
    {
        _dinputCalibration = DInputCalibration.Load();
    }

    public void ReloadComboSettings()
    {
        _comboSettings = ComboSettings.Load();
    }

    public void ApplyComboSettings(Dictionary<string, List<ushort>> actions, Dictionary<string, string> osdNames, Dictionary<string, string> execPaths)
    {
        _comboSettings.Actions = actions;
        _comboSettings.OSDNames = osdNames;
        _comboSettings.ExecPaths = execPaths;
    }

    private void ProcessDPad(GamepadState current)
    {
        DPadKey currentKey = current.GetDPadKey();

        if (_currentDPadMode == DPadMode.PadDiagonal)
        {
            if (current.DPadUp && current.DPadLeft)
                currentKey = DPadKey.UpLeft;
            else if (current.DPadUp && current.DPadRight)
                currentKey = DPadKey.UpRight;
            else if (current.DPadDown && current.DPadLeft)
                currentKey = DPadKey.DownLeft;
            else if (current.DPadDown && current.DPadRight)
                currentKey = DPadKey.DownRight;
        }

        bool isDiagonal = currentKey is DPadKey.UpLeft or DPadKey.UpRight or DPadKey.DownLeft or DPadKey.DownRight;

        // --- Diagonal delay (PAD mode excluded) ---
        if (_diagonalDelayMs > 0 && _currentDPadMode != DPadMode.Pad && isDiagonal)
        {
            if (currentKey != _pendingDiagonalKey)
            {
                _pendingDiagonalKey = currentKey;
                _diagonalEntryTick = _stopwatch.ElapsedMilliseconds;
                _diagonalActivated = false;
                SwapPadKeys(_diagonalDelayHoldCardinals ? GetCardinalKeys(currentKey) : new ushort[0]);
                return;
            }
            if (!_diagonalActivated)
            {
                if ((_stopwatch.ElapsedMilliseconds - _diagonalEntryTick) >= _diagonalDelayMs)
                {
                    _diagonalActivated = true;
                    SwapPadKeys(GetKeysForDPad(currentKey));
                }
                return;
            }
            return;
        }

        if (_pendingDiagonalKey != DPadKey.None)
        {
            _pendingDiagonalKey = DPadKey.None;
            _diagonalActivated = false;
            SwapPadKeys(new ushort[0]);
        }

        // --- Diagonal crosstalk suppression ---
        // When releasing a cardinal, the stick can briefly pass through a diagonal
        // angle sector before reaching center (e.g. releasing Up → UpLeft flicker).
        // Require confirmation polls OR a directional delay timer to confirm a diagonal
        // from a cardinal, then hold it for one more poll.
        if (_lastSentDPadKey != DPadKey.None && _lastSentDPadKey != currentKey &&
            !IsDiagonal(_lastSentDPadKey) && IsDiagonal(currentKey))
        {
            if (_confirmedDiagonalKey == currentKey)
            {
                // Diagonal survived the hold poll — genuine transition
                _confirmedDiagonalKey = DPadKey.None;
                _diagonalConfirmTiming = false;
                // Falls through to state machine
            }
            else if (currentKey == _pendingDiagonalConfirmKey)
            {
                if (_directionalDelayMs > 0)
                {
                    // Time-based confirmation: wait for directional delay to elapse
                    if (!_diagonalConfirmTiming)
                    {
                        _diagonalConfirmTiming = true;
                        _diagonalConfirmStartTick = _stopwatch.ElapsedMilliseconds;
                        return;
                    }
                    if ((_stopwatch.ElapsedMilliseconds - _diagonalConfirmStartTick) >= _directionalDelayMs)
                    {
                        // Confirmed via directional delay — mark, hold for one more poll
                        _pendingDiagonalConfirmKey = DPadKey.None;
                        _diagonalConfirmTiming = false;
                        _confirmedDiagonalKey = currentKey;
                        return; // Keep old cardinal keys held
                    }
                    return;
                }
                else
                {
                    // Poll-count confirmation (original behavior): 2 polls
                    _diagonalConfirmCount++;
                    if (_diagonalConfirmCount >= 2)
                    {
                        _pendingDiagonalConfirmKey = DPadKey.None;
                        _diagonalConfirmCount = 0;
                        _confirmedDiagonalKey = currentKey;
                        return; // Keep old cardinal keys held
                    }
                    else return;
                }
            }
            else
            {
                _pendingDiagonalConfirmKey = currentKey;
                _diagonalConfirmCount = 1;
                _diagonalConfirmTiming = false;
                _confirmedDiagonalKey = DPadKey.None;
                return;
            }
        }
        else
        {
            _pendingDiagonalConfirmKey = DPadKey.None;
            _diagonalConfirmCount = 0;
            _diagonalConfirmTiming = false;
            _confirmedDiagonalKey = DPadKey.None;
        }

        // --- Diagonal-to-cardinal crosstalk suppression ---
        // When releasing a diagonal, one axis drops before the other, briefly
        // firing a subset cardinal (e.g. UpLeft → Up). Use directional delay or
        // poll-count confirmation before accepting the cardinal transition.
        if (IsDiagonal(_lastSentDPadKey) &&
            (currentKey == DPadKey.None || IsSubsetCardinal(currentKey, _lastSentDPadKey)))
        {
            // FIX: If stick is centered, release immediately — don't do confirmation logic
            if (currentKey == DPadKey.None)
            {
                if (_lastSentDPadKey != DPadKey.None)
                {
                    var oldKeys = GetKeysForDPad(_lastSentDPadKey);
                    foreach (var k in oldKeys)
                    {
                        _keyboard.ReleaseKey(k);
                        _heldPadKeys.Remove(k);
                    }
                    _lastSentDPadKey = DPadKey.None;
                }
                _pendingCardinalConfirmKey = DPadKey.None;
                _cardinalConfirmCount = 0;
                _cardinalConfirmTiming = false;
                _confirmedCardinalKey = DPadKey.None;
                return;
            }

            if (_confirmedCardinalKey == currentKey)
            {
                _confirmedCardinalKey = DPadKey.None;
                _cardinalConfirmTiming = false;
            }
            else if (currentKey == _pendingCardinalConfirmKey)
            {
                if (_directionalDelayMs > 0)
                {
                    if (!_cardinalConfirmTiming)
                    {
                        _cardinalConfirmTiming = true;
                        _cardinalConfirmStartTick = _stopwatch.ElapsedMilliseconds;
                        return;
                    }
                    if ((_stopwatch.ElapsedMilliseconds - _cardinalConfirmStartTick) >= _directionalDelayMs)
                    {
                        _pendingCardinalConfirmKey = DPadKey.None;
                        _cardinalConfirmTiming = false;
                        _confirmedCardinalKey = currentKey;
                        return;
                    }
                    return;
                }
                else
                {
                    _cardinalConfirmCount++;
                    if (_cardinalConfirmCount >= 2)
                    {
                        _pendingCardinalConfirmKey = DPadKey.None;
                        _cardinalConfirmCount = 0;
                        _confirmedCardinalKey = currentKey;
                        return;
                    }
                    else return;
                }
            }
            else
            {
                _pendingCardinalConfirmKey = currentKey;
                _cardinalConfirmCount = 1;
                _cardinalConfirmTiming = false;
                _confirmedCardinalKey = DPadKey.None;
                return;
            }
        }
        else
        {
            _pendingCardinalConfirmKey = DPadKey.None;
            _cardinalConfirmCount = 0;
            _cardinalConfirmTiming = false;
            _confirmedCardinalKey = DPadKey.None;
        }

        // --- Cardinal delay when directional delay active ---
        // Delay cardinal output by directional delay duration to prevent flicker when
        // the stick briefly passes through a cardinal (e.g. Up) before settling
        // on a diagonal (e.g. UpLeft). The cardinal only fires if held for the
        // full directional delay period.
        if (_directionalDelayMs > 0 && !IsDiagonal(currentKey) && currentKey != DPadKey.None)
        {
            if (currentKey != _pendingCardinalDelayKey)
            {
                _pendingCardinalDelayKey = currentKey;
                _cardinalDelayStartTick = _stopwatch.ElapsedMilliseconds;
                return;
            }
            if ((_stopwatch.ElapsedMilliseconds - _cardinalDelayStartTick) >= _directionalDelayMs)
                _pendingCardinalDelayKey = DPadKey.None;
            else
                return;
        }
        else
        {
            _pendingCardinalDelayKey = DPadKey.None;
            // FIX: If stick is centered, release immediately
            if (currentKey == DPadKey.None && _lastSentDPadKey != DPadKey.None)
            {
                var oldKeys = GetKeysForDPad(_lastSentDPadKey);
                foreach (var k in oldKeys)
                {
                    _keyboard.ReleaseKey(k);
                    _heldPadKeys.Remove(k);
                }
                _lastSentDPadKey = DPadKey.None;
            }
        }

        // --- Normal state machine ---
        if (currentKey == _lastSentDPadKey)
            return;

        if (_lastSentDPadKey != DPadKey.None)
        {
            var oldKeys = GetKeysForDPad(_lastSentDPadKey);
            foreach (var k in oldKeys)
            {
                _keyboard.ReleaseKey(k);
                _heldPadKeys.Remove(k);
            }
        }

        if (currentKey != DPadKey.None)
        {
            var newKeys = GetKeysForDPad(currentKey);
            foreach (var k in newKeys)
            {
                _keyboard.PressKey(k);
                _heldPadKeys.Add(k);
            }
        }

        _lastSentDPadKey = currentKey;
    }

    private void SwapPadKeys(ushort[] newKeys)
    {
        foreach (var k in _heldPadKeys)
            _keyboard.ReleaseKey(k);
        _heldPadKeys.Clear();
        foreach (var k in newKeys)
        {
            _keyboard.PressKey(k);
            _heldPadKeys.Add(k);
        }
    }

    private ushort[] GetCardinalKeys(DPadKey diagonal)
    {
        bool up = diagonal == DPadKey.UpLeft || diagonal == DPadKey.UpRight;
        bool down = diagonal == DPadKey.DownLeft || diagonal == DPadKey.DownRight;
        bool left = diagonal == DPadKey.UpLeft || diagonal == DPadKey.DownLeft;
        bool right = diagonal == DPadKey.UpRight || diagonal == DPadKey.DownRight;
        bool isKeypad = _currentDPadMode == DPadMode.Keypad;
        int count = (up ? 1 : 0) + (down ? 1 : 0) + (left ? 1 : 0) + (right ? 1 : 0);
        if (count == 0) return VK.Empty;
        if (isKeypad) return GetCachedCardinalKeysKeypad(up, down, left, right);
        return GetCachedCardinalKeysPad(up, down, left, right);
    }

    private static ushort[] GetCachedCardinalKeysPad(bool up, bool down, bool left, bool right)
    {
        if (up && !down && !left && !right) return VK.UpA;
        if (!up && down && !left && !right) return VK.DownA;
        if (!up && !down && left && !right) return VK.LeftA;
        if (!up && !down && !left && right) return VK.RightA;
        if (up && !down && left && !right) return Cached.PadUpLeft;
        if (up && !down && !left && right) return Cached.PadUpRight;
        if (!up && down && left && !right) return Cached.PadDownLeft;
        if (!up && down && !left && right) return Cached.PadDownRight;
        return VK.Empty;
    }

    private static ushort[] GetCachedCardinalKeysKeypad(bool up, bool down, bool left, bool right)
    {
        if (up && !down && !left && !right) return VK.NumPad8A;
        if (!up && down && !left && !right) return VK.NumPad2A;
        if (!up && !down && left && !right) return VK.NumPad4A;
        if (!up && !down && !left && right) return VK.NumPad6A;
        if (up && !down && left && !right) return Cached.KeypadUpLeft;
        if (up && !down && !left && right) return Cached.KeypadUpRight;
        if (!up && down && left && !right) return Cached.KeypadDownLeft;
        if (!up && down && !left && right) return Cached.KeypadDownRight;
        return VK.Empty;
    }

    private static class Cached
    {
        public static readonly ushort[] PadUpLeft = { VK.Up, VK.Left };
        public static readonly ushort[] PadUpRight = { VK.Up, VK.Right };
        public static readonly ushort[] PadDownLeft = { VK.Down, VK.Left };
        public static readonly ushort[] PadDownRight = { VK.Down, VK.Right };
        public static readonly ushort[] KeypadUpLeft = { VK.NumPad8, VK.NumPad4 };
        public static readonly ushort[] KeypadUpRight = { VK.NumPad8, VK.NumPad6 };
        public static readonly ushort[] KeypadDownLeft = { VK.NumPad2, VK.NumPad4 };
        public static readonly ushort[] KeypadDownRight = { VK.NumPad2, VK.NumPad6 };
    }

    private ushort[] GetKeysForDPad(DPadKey key)
    {
        return _currentDPadMode switch
        {
            DPadMode.Pad => key switch
            {
                DPadKey.Up => VK.UpA,
                DPadKey.Down => VK.DownA,
                DPadKey.Left => VK.LeftA,
                DPadKey.Right => VK.RightA,
                _ => VK.Empty
            },
            DPadMode.Keypad => key switch
            {
                DPadKey.Up => VK.NumPad8A,
                DPadKey.Down => VK.NumPad2A,
                DPadKey.Left => VK.NumPad4A,
                DPadKey.Right => VK.NumPad6A,
                DPadKey.UpLeft => VK.NumPad7A,
                DPadKey.UpRight => VK.NumPad9A,
                DPadKey.DownLeft => VK.NumPad1A,
                DPadKey.DownRight => VK.NumPad3A,
                _ => VK.Empty
            },
            DPadMode.PadDiagonal => key switch
            {
                DPadKey.Up => VK.UpA,
                DPadKey.Down => VK.DownA,
                DPadKey.Left => VK.LeftA,
                DPadKey.Right => VK.RightA,
                DPadKey.UpLeft => VK.NumPad7A,
                DPadKey.UpRight => VK.NumPad9A,
                DPadKey.DownLeft => VK.NumPad1A,
                DPadKey.DownRight => VK.NumPad3A,
                _ => VK.Empty
            },
            _ => VK.Empty
        };
    }

    private void ProcessActionButtons(GamepadState current, bool isDefault)
    {
        ProcessHoldButton(current.A, ref _aWasPressed, ref _currentAHeldKey, () =>
            isDefault
                ? _currentDPadMode == DPadMode.Pad ? VK.F3 : VK.NumPad5
                : _profiles.CurrentProfile.GetValueOrDefault("A", VK.F3));

        ProcessHoldButton(current.B, ref _bWasPressed, ref _currentBHeldKey, () =>
            isDefault ? VK.F2 : _profiles.CurrentProfile.GetValueOrDefault("B", VK.F2));

        ProcessHoldButton(current.X, ref _xWasPressed, ref _currentXHeldKey, () =>
            isDefault ? VK.F1 : _profiles.CurrentProfile.GetValueOrDefault("X", VK.F1));

        ProcessHoldButton(current.Y, ref _yWasPressed, ref _currentYHeldKey, () =>
            isDefault ? VK.NumPad0 : _profiles.CurrentProfile.GetValueOrDefault("Y", VK.NumPad0));
    }

    private void ProcessShoulderButtons(GamepadState current, bool isDefault)
    {
        ProcessHoldButton(current.LB, ref _lbWasPressed, ref _currentLBHeldKey, () =>
            isDefault ? VK.Multiply : _profiles.CurrentProfile.GetValueOrDefault("LB", VK.Multiply));

        ProcessHoldButton(current.RB, ref _rbWasPressed, ref _currentRBHeldKey, () =>
            isDefault ? VK.Divide : _profiles.CurrentProfile.GetValueOrDefault("RB", VK.Divide));

        ProcessHoldButton(current.LT, ref _ltWasPressed, ref _currentLTHeldKey, () =>
            isDefault ? VK.F1 : _profiles.CurrentProfile.GetValueOrDefault("LT", VK.F1));

        ProcessHoldButton(current.RT, ref _rtWasPressed, ref _currentRTHeldKey, () =>
            isDefault ? VK.F2 : _profiles.CurrentProfile.GetValueOrDefault("RT", VK.F2));

        ProcessHoldButton(current.RightThumb, ref _rightThumbWasPressed, ref _currentRightThumbHeldKey, () =>
            isDefault
                ? _currentDPadMode == DPadMode.Pad ? VK.F3 : VK.NumPad5
                : _profiles.CurrentProfile.GetValueOrDefault("RightThumb", VK.F3));

        ProcessHoldButton(current.LeftThumb, ref _leftThumbWasPressed, ref _currentLeftThumbHeldKey, () =>
            isDefault ? (ushort)0 : _profiles.CurrentProfile.GetValueOrDefault("LeftThumb", (ushort)0));
    }

    private static readonly string[] ComboFaceButtons = { "Y", "X", "A", "B" };
    private static readonly string[] ComboExecButtons = { "Start", "Back" };

    private string GetComboOsdName(string comboName)
    {
        if (_comboSettings.OSDNames.TryGetValue(comboName, out var n) && !string.IsNullOrEmpty(n))
            return n;
        if (_comboSettings.Actions.TryGetValue(comboName, out var keys) && keys.Count > 0)
            return string.Join("+", keys.Select(k => GetKeyDisplayName(k)));
        if (_comboSettings.ExecPaths.TryGetValue(comboName, out var execPath) && !string.IsNullOrEmpty(execPath))
            return System.IO.Path.GetFileName(execPath);
        return comboName;
    }

    private void ProcessComboButton(string comboName, bool pressed, bool isDefault)
    {
        _comboWasPressed.TryGetValue(comboName, out bool wasPressed);

        if (ComboConfirmationHold)
        {
            if (_confirmActive && _confirmComboName == comboName)
            {
                if (pressed && _activeComboModifier != ComboModifier.None)
                {
                    if (!_confirmTriggerQueued &&
                        (_stopwatch.ElapsedMilliseconds - _confirmStartTickMs) >= ComboConfirmationDelayMs)
                    {
                        _comboWasPressed[comboName] = true;
                        _confirmTriggerQueued = true;
                        TriggerCombo(comboName, isDefault);
                    }
                }
                else
                {
                    if (_confirmTriggerQueued)
                        EndComboKeys();
                    _confirmActive = false;
                    _confirmComboName = "";
                    _confirmOsdName = "";
                    _confirmTriggerQueued = false;
                    ComboConfirmationCancelled?.Invoke();
                }
                return;
            }

            if (pressed && !wasPressed)
            {
                bool hasActions = _comboSettings.Actions.TryGetValue(comboName, out var act) && act.Count > 0;
                bool hasExec = _comboSettings.ExecPaths.TryGetValue(comboName, out var exe) && !string.IsNullOrEmpty(exe);
                if (!hasActions && !hasExec)
                {
                    _comboWasPressed[comboName] = true;
                    return;
                }
                _confirmActive = true;
                _confirmComboName = comboName;
                _confirmOsdName = GetComboOsdName(comboName);
                _confirmStartTickMs = _stopwatch.ElapsedMilliseconds;
                _confirmTriggerQueued = false;
                ComboConfirmationPending?.Invoke(_confirmOsdName);
                return;
            }

            if (!pressed && wasPressed)
            {
                _comboWasPressed[comboName] = false;
                EndComboKeys();
            }
        }
        else
        {
            if (pressed && !wasPressed)
            {
                _comboWasPressed[comboName] = true;
                TriggerCombo(comboName, isDefault);
            }
            else if (!pressed && wasPressed)
            {
                _comboWasPressed[comboName] = false;
                EndComboKeys();
            }
        }
    }

    private void ProcessComboModifierXInput(GamepadState current, string prefix, bool isDefault)
    {
        ComboModifier mod = prefix switch
        {
            "RB+LB+" => ComboModifier.RbLb,
            "RT+LT+" => ComboModifier.RtLt,
            "LSB+" => ComboModifier.Lsb,
            "RSB+" => ComboModifier.Rsb,
            _ => ComboModifier.None
        };

        if (_activeComboModifier != mod)
        {
            EndComboModifier();
            _activeComboModifier = mod;
            if (prefix == "RB+LB+")
            {
                if (_lbWasPressed) { _keyboard.ReleaseKey(_currentLBHeldKey); _lbWasPressed = false; _currentLBHeldKey = 0; }
                if (_rbWasPressed) { _keyboard.ReleaseKey(_currentRBHeldKey); _rbWasPressed = false; _currentRBHeldKey = 0; }
            }
            else if (prefix == "RT+LT+")
            {
                if (_ltWasPressed) { _keyboard.ReleaseKey(_currentLTHeldKey); _ltWasPressed = false; _currentLTHeldKey = 0; }
                if (_rtWasPressed) { _keyboard.ReleaseKey(_currentRTHeldKey); _rtWasPressed = false; _currentRTHeldKey = 0; }
            }
            else if (prefix == "LSB+")
            {
                if (_leftThumbWasPressed) { _keyboard.ReleaseKey(_currentLeftThumbHeldKey); _leftThumbWasPressed = false; _currentLeftThumbHeldKey = 0; }
            }
            else if (prefix == "RSB+")
            {
                if (_rightThumbWasPressed) { _keyboard.ReleaseKey(_currentRightThumbHeldKey); _rightThumbWasPressed = false; _currentRightThumbHeldKey = 0; }
            }
            ComboModifierActive?.Invoke(prefix.TrimEnd('+'));
        }

        foreach (string btn in ComboFaceButtons)
        {
            bool pressed = btn switch
            {
                "Y" => current.Y, "X" => current.X,
                "A" => current.A, "B" => current.B,
                _ => false
            };
            ProcessComboButton(prefix + btn, pressed, isDefault);
        }

        // Only RB+LB and RT+LT support Start/Back exec combos
        if (prefix == "RB+LB+" || prefix == "RT+LT+")
        {
            foreach (string btn in ComboExecButtons)
            {
                bool pressed = btn switch
                {
                    "Start" => current.Start, "Back" => current.Back,
                    _ => false
                };
                ProcessComboButton(prefix + btn, pressed, isDefault);
            }
        }
    }

    internal static readonly HashSet<string> AllowedExecExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".bat", ".cmd", ".com", ".ps1"
    };

    private void TriggerCombo(string comboName, bool isDefault)
    {
        var settings = _comboSettings;

        // Check if this combo has an executable path to launch
        if (settings.ExecPaths.TryGetValue(comboName, out var execPath) && !string.IsNullOrEmpty(execPath))
        {
            string osdName;
            if (settings.OSDNames.TryGetValue(comboName, out var n) && !string.IsNullOrEmpty(n))
                osdName = n;
            else
                osdName = System.IO.Path.GetFileName(execPath);

            EndComboKeys();

            // Canonicalize path before validation
            execPath = System.IO.Path.GetFullPath(execPath);

            // Validate: file must exist and have a safe extension
            string ext = System.IO.Path.GetExtension(execPath);
            if (!System.IO.File.Exists(execPath) || !AllowedExecExtensions.Contains(ext))
            {
                LogHelper.Info("Poller", $"Combo exec blocked: invalid path or extension \"{execPath}\"");
                ComboTriggered?.Invoke($"[BLOCKED] {osdName}");
                return;
            }

            ComboTriggered?.Invoke(osdName);

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = execPath,
                    UseShellExecute = true
                });
            }
            catch { }
            return;
        }

        List<ushort> keys;
        if (!settings.Actions.TryGetValue(comboName, out keys) || keys.Count == 0)
            return;

        EndComboKeys();

        foreach (var k in keys)
        {
            _keyboard.PressKey(k);
            _comboHeldKeys.Add(k);
        }

        string osdName2;
        if (settings.OSDNames.TryGetValue(comboName, out var n2) && !string.IsNullOrEmpty(n2))
            osdName2 = n2;
        else
            osdName2 = string.Join("+", keys.Select(k => GetKeyDisplayName(k)));

        ComboTriggered?.Invoke(osdName2);
    }

    private void EndComboKeys()
    {
        foreach (var k in _comboHeldKeys)
            _keyboard.ReleaseKey(k);
        _comboHeldKeys.Clear();
    }

    private void EndComboModifier()
    {
        if (_activeComboModifier == ComboModifier.None)
            return;
        CancelComboConfirmation();
        _activeComboModifier = ComboModifier.None;
        EndComboKeys();
        _comboWasPressed.Clear();
        ComboModifierInactive?.Invoke();
    }

    private void CancelComboConfirmation()
    {
        if (!_confirmActive) return;
        _confirmActive = false;
        _confirmComboName = "";
        _confirmOsdName = "";
        _confirmTriggerQueued = false;
        ComboConfirmationCancelled?.Invoke();
    }

    private static string GetKeyDisplayName(ushort vk)
    {
        if (vk >= 0x30 && vk <= 0x39) return $"{(char)('0' + (vk - 0x30))}";
        if (vk >= 0x41 && vk <= 0x5A) return $"{(char)('A' + (vk - 0x41))}";
        if (vk >= 0x70 && vk <= 0x7A) return $"F{vk - 0x70 + 1}";
        if (vk >= 0x60 && vk <= 0x69) return $"Num{vk - 0x60}";
        return vk switch
        {
            0x08 => "Bksp", 0x09 => "Tab", 0x0D => "Enter",
            0x1B => "Esc", 0x20 => "Space",
            0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down",
            0x10 => "Shift", 0x11 => "Ctrl", 0x12 => "Alt",
            0x5B => "Win", 0x6A => "Num*", 0x6B => "Num+", 0x6D => "Num-",
            0x6E => "Num.", 0x6F => "Num/", 0x90 => "NumLk",
            0xA0 => "LShift", 0xA1 => "RShift",
            0xA2 => "LCtrl", 0xA3 => "RCtrl",
            0xA4 => "LAlt", 0xA5 => "RAlt",
            _ => $"0x{vk:X2}"
        };
    }

    private void ProcessHoldButton(bool current, ref bool wasPressed, ref ushort heldKey, Func<ushort> getKey)
    {
        if (current && !wasPressed)
        {
            wasPressed = true;
            heldKey = getKey();
            _keyboard.PressKey(heldKey);
        }
        else if (!current && wasPressed)
        {
            wasPressed = false;
            if (heldKey != 0)
            {
                _keyboard.ReleaseKey(heldKey);
                heldKey = 0;
            }
        }
    }

    private void ProcessModeSwitches(GamepadState current)
    {
        ProcessButtonEdge(current.Back, ref _backWasPressed, () =>
        {
            if (_backCyclesYxab)
            {
                _profiles.CycleBackward(_skipDefault);
                ModeChanged?.Invoke($"YXAB: {_profiles.CurrentProfile.Name}");
            }
            else
            {
                CurrentDPadMode = _currentDPadMode switch
                {
                    DPadMode.Pad => DPadMode.Keypad,
                    DPadMode.Keypad => DPadMode.PadDiagonal,
                    DPadMode.PadDiagonal => DPadMode.Pad,
                    _ => DPadMode.Pad
                };
            }
        });

        ProcessButtonEdge(current.Start, ref _startWasPressed, () =>
        {
            _profiles.CycleForward(_skipDefault);
            ModeChanged?.Invoke($"YXAB: {_profiles.CurrentProfile.Name}");
        });

        ProcessButtonEdge(current.LeftThumb, ref _leftThumbModeWasPressed, () =>
        {
            bool hasMapping = !_profiles.IsDefaultProfile
                && _profiles.CurrentProfile.Mappings.TryGetValue("LeftThumb", out var vk)
                && vk != 0;
            if (!hasMapping)
            {
                _backCyclesYxab = !_backCyclesYxab;
                BackCyclesToggled?.Invoke(_backCyclesYxab);
            }
        });
    }

    private static void ProcessButtonEdge(bool current, ref bool wasPressed, Action onPress)
    {
        if (current && !wasPressed)
        {
            wasPressed = true;
            onPress();
        }
        else if (!current && wasPressed)
        {
            wasPressed = false;
        }
    }

    private void ForceReleaseDPad()
    {
        foreach (var k in _heldPadKeys)
            _keyboard.ReleaseKey(k);
        _heldPadKeys.Clear();
        _lastSentDPadKey = DPadKey.None;
        _pendingDiagonalKey = DPadKey.None;
        _diagonalActivated = false;
        _pendingDiagonalConfirmKey = DPadKey.None;
        _diagonalConfirmCount = 0;
        _diagonalConfirmTiming = false;
        _pendingCardinalConfirmKey = DPadKey.None;
        _cardinalConfirmCount = 0;
        _cardinalConfirmTiming = false;
        _confirmedCardinalKey = DPadKey.None;
        _pendingCardinalDelayKey = DPadKey.None;
    }

    private static bool IsDiagonal(DPadKey key) =>
        key is DPadKey.UpLeft or DPadKey.UpRight or DPadKey.DownLeft or DPadKey.DownRight;

    private static bool IsSubsetCardinal(DPadKey cardinal, DPadKey diagonal)
    {
        if (cardinal == DPadKey.None || cardinal == diagonal) return false;
        return diagonal switch
        {
            DPadKey.UpLeft => cardinal == DPadKey.Up || cardinal == DPadKey.Left,
            DPadKey.UpRight => cardinal == DPadKey.Up || cardinal == DPadKey.Right,
            DPadKey.DownLeft => cardinal == DPadKey.Down || cardinal == DPadKey.Left,
            DPadKey.DownRight => cardinal == DPadKey.Down || cardinal == DPadKey.Right,
            _ => false
        };
    }

    private static string GetModeName(DPadMode mode) => mode switch
    {
        DPadMode.Pad => "PAD Mode",
        DPadMode.Keypad => "KEYPAD Mode",
        DPadMode.PadDiagonal => "PAD+DIAGONAL Mode",
        _ => "Unknown"
    };

    public void Dispose()
    {
        _disposed = true;
        Stop();
        _timer.Dispose();
    }
}
