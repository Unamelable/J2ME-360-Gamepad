using System;
using System.Collections.Generic;
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
    private bool _leftThumbWasPressed;
    private bool _rightThumbWasPressed;
    private bool _lbWasPressed;
    private bool _rbWasPressed;
    private bool _ltWasPressed;
    private bool _rtWasPressed;
    private bool _connected;

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
    private DirectInputReader _directInput = new();

    private DPadMode _currentDPadMode = DPadMode.Pad;
    private bool _backCyclesYxab;
    private bool _skipDefault;
    private readonly HashSet<ushort> _heldPadKeys = new();
    private int _diagonalDelayMs;
    private bool _diagonalDelayHoldCardinals;
    private int _diagonalEntryTick;
    private DPadKey _pendingDiagonalKey = DPadKey.None;
    private bool _diagonalActivated;
    private DPadKey _pendingDiagonalConfirmKey = DPadKey.None;
    private int _diagonalConfirmCount;
    private DPadKey _confirmedDiagonalKey = DPadKey.None;
    private int _directionalDelayMs;
    private int _diagonalConfirmStartTick;
    private bool _diagonalConfirmTiming;

    private DPadKey _pendingCardinalConfirmKey = DPadKey.None;
    private int _cardinalConfirmCount;
    private bool _cardinalConfirmTiming;
    private int _cardinalConfirmStartTick;
    private DPadKey _confirmedCardinalKey = DPadKey.None;

    private DPadKey _pendingCardinalDelayKey = DPadKey.None;
    private int _cardinalDelayStartTick;

    private static class VK
    {
        public static readonly ushort NumPad1 = KeyMapProfile.KeyNumpad1;
        public static readonly ushort NumPad2 = KeyMapProfile.KeyNumpad2;
        public static readonly ushort NumPad3 = KeyMapProfile.KeyNumpad3;
        public static readonly ushort NumPad4 = KeyMapProfile.KeyNumpad4;
        public static readonly ushort NumPad5 = KeyMapProfile.KeyNumpad5;
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

        // Cached DPad key arrays to avoid per-call allocation
        public static readonly ushort[] Empty = Array.Empty<ushort>();
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
    private Dictionary<int, string> _dinputButtonToAction = new();
    private readonly Dictionary<int, bool> _dinputWasPressed = new();
    private readonly Dictionary<string, ushort> _dinputHeldKeys = new();
    private uint _lastDInputButtons;

    private static readonly int JoyInfoExSize = Marshal.SizeOf<JOYINFOEX>();
    private int _dinputBackIdx = 8;
    private int _dinputStartIdx = 9;
    private int _dinputLeftThumbIdx = 10;

    public bool IsDInputMode => _controllerType == ControllerType.DInput;

    public bool IsRemapping { get; set; }
    public DInputCalibration DInputCalibration => _dinputCalibration;
    public DirectInputReader DirectInputReader => _directInput;

    public event Action<string>? ModeChanged;
    public event Action<bool>? ConnectionChanged;
    public event Action<bool>? BackCyclesToggled;
    public event Action<int>? DInputButtonPressed;
    public event Action<string>? DInputModeChanged;

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
        UpdateCachedDInputIndices();
        _timer = new Timer(8);
        _timer.Elapsed += Poll;
    }

    private void UpdateCachedDInputIndices()
    {
        _dinputBackIdx = _dinputMapping.ActionToButton.TryGetValue("Back", out int b) ? b : 8;
        _dinputStartIdx = _dinputMapping.ActionToButton.TryGetValue("Start", out int s) ? s : 9;
        _dinputLeftThumbIdx = _dinputMapping.ActionToButton.TryGetValue("LeftThumb", out int l) ? l : 10;
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
            if (_controllerType == ControllerType.XInput || _controllerType == ControllerType.None)
            {
                var result = XInput.GetState(0, out var state);

                if (result == XInput.ERROR_SUCCESS)
                {
                    if (_controllerType == ControllerType.None)
                    {
                        _controllerType = ControllerType.XInput;
                        _connected = true;
                        ConnectionChanged?.Invoke(true);
                    }
                    else if (!_connected)
                    {
                        _connected = true;
                        ConnectionChanged?.Invoke(true);
                    }

                    var current = new GamepadState();
                    current.UpdateFromButtons(state.Gamepad.wButtons);
                    current.UpdateTriggers(state.Gamepad.bLeftTrigger, state.Gamepad.bRightTrigger);

                    ProcessDPad(current);
                    if (!IsRemapping)
                    {
                        bool isDefault = _profiles.IsDefaultProfile;
                        ProcessActionButtons(current, isDefault);
                        ProcessShoulderButtons(current, isDefault);
                        ProcessModeSwitches(current);
                    }
                    return;
                }

                if (_controllerType == ControllerType.XInput)
                {
                    _controllerType = ControllerType.None;
                    HandleDisconnect();
                }
            }

            if (_controllerType == ControllerType.None || _controllerType == ControllerType.DInput)
            {
                if (PollDInput())
                    return;

                if (_controllerType == ControllerType.DInput)
                {
                    _controllerType = ControllerType.None;
                    HandleDisconnect();
                }
            }
        }
        catch (Exception ex)
        {
            LogError($"Poll: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private bool PollDInput()
    {
        // Try DirectInput first (works with all DInput gamepads, reports axes + buttons + POV)
        bool diOk = _directInput.Available && _directInput.Poll();

        if (!diOk)
        {
            // Fallback: legacy joystick API
            var ji = new JOYINFOEX();
            ji.dwSize = JoyInfoExSize;
            ji.dwFlags = JoyInput.JOY_RETURNALL;

            if (JoyInput.GetPosEx(0, ref ji) != JoyInput.JOYERR_NOERROR)
                return false;

            if (_controllerType == ControllerType.None) InitDInputMode();

            var current = MakeStateFromJoyInfo(ji);
            ProcessDInput(current, ji);
            return true;
        }

        if (_controllerType == ControllerType.None) InitDInputMode();

        var cur = MakeStateFromDirectInput();
        ProcessDInput(cur, null);
        return true;
    }

    private void InitDInputMode()
    {
        _controllerType = ControllerType.DInput;
        _connected = true;
        _dinputMapping = Models.DInputMapping.Load();
        _dinputButtonToAction = _dinputMapping.BuildReverseMap();
        _dinputCalibration = Models.DInputCalibration.Load();
        UpdateCachedDInputIndices();
        _directInput.Dispose();
        _directInput = new DirectInputReader();
        _directInput.Initialize();
        ConnectionChanged?.Invoke(true);
        DInputModeChanged?.Invoke("DInput");
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

        // Button-based D-Pad from mapping
        if (current.GetDPadKey() == DPadKey.None)
        {
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

        ProcessDInputActions(buttons);
        ProcessDInputModeSwitches(buttons);
    }

    private static readonly HashSet<string> DInputSystemActions = new() { "Back", "Start", "LeftThumb" };

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

        ProcessButtonEdge(leftThumbPressed, ref _leftThumbWasPressed, () =>
        {
            _backCyclesYxab = !_backCyclesYxab;
            BackCyclesToggled?.Invoke(_backCyclesYxab);
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
                "A" => _currentDPadMode == DPadMode.Pad ? VK.Return : VK.NumPad5,
                "B" => VK.F2,
                "X" => VK.F1,
                "Y" => VK.Multiply,
                "LB" => VK.Multiply,
                "RB" => VK.Divide,
                "LT" => VK.F1,
                "RT" => VK.F2,
                "RightThumb" => VK.Return,
                _ => 0
            };
        }
        return _profiles.CurrentProfile.Mappings.TryGetValue(action, out var vk) ? vk : (ushort)0;
    }

    private void HandleDisconnect()
    {
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
        _lastDInputButtons = 0;
        _directInput.Dispose();
        _directInput = new DirectInputReader();
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
                _diagonalEntryTick = Environment.TickCount;
                _diagonalActivated = false;
                SwapPadKeys(_diagonalDelayHoldCardinals ? GetCardinalKeys(currentKey) : Array.Empty<ushort>());
                return;
            }
            if (!_diagonalActivated)
            {
                if (unchecked(Environment.TickCount - _diagonalEntryTick) >= _diagonalDelayMs)
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
            SwapPadKeys(Array.Empty<ushort>());
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
                        _diagonalConfirmStartTick = Environment.TickCount;
                        return;
                    }
                    if (unchecked(Environment.TickCount - _diagonalConfirmStartTick) >= _directionalDelayMs)
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
                        _cardinalConfirmStartTick = Environment.TickCount;
                        return;
                    }
                    if (unchecked(Environment.TickCount - _cardinalConfirmStartTick) >= _directionalDelayMs)
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
                _cardinalDelayStartTick = Environment.TickCount;
                return;
            }
            if (unchecked(Environment.TickCount - _cardinalDelayStartTick) >= _directionalDelayMs)
                _pendingCardinalDelayKey = DPadKey.None;
            else
                return;
        }
        else
        {
            _pendingCardinalDelayKey = DPadKey.None;
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

    private void SwapPadKeys(ReadOnlySpan<ushort> newKeys)
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

    private ReadOnlySpan<ushort> GetCardinalKeys(DPadKey diagonal)
    {
        bool up = diagonal == DPadKey.UpLeft || diagonal == DPadKey.UpRight;
        bool down = diagonal == DPadKey.DownLeft || diagonal == DPadKey.DownRight;
        bool left = diagonal == DPadKey.UpLeft || diagonal == DPadKey.DownLeft;
        bool right = diagonal == DPadKey.UpRight || diagonal == DPadKey.DownRight;
        bool isKeypad = _currentDPadMode == DPadMode.Keypad;
        int count = (up ? 1 : 0) + (down ? 1 : 0) + (left ? 1 : 0) + (right ? 1 : 0);
        var buf = new ushort[count];
        int i = 0;
        if (up) buf[i++] = isKeypad ? VK.NumPad8 : VK.Up;
        if (down) buf[i++] = isKeypad ? VK.NumPad2 : VK.Down;
        if (left) buf[i++] = isKeypad ? VK.NumPad4 : VK.Left;
        if (right) buf[i++] = isKeypad ? VK.NumPad6 : VK.Right;
        return buf;
    }

    private ReadOnlySpan<ushort> GetKeysForDPad(DPadKey key)
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
                ? _currentDPadMode == DPadMode.Pad ? VK.Return : VK.NumPad5
                : _profiles.CurrentProfile.Mappings.GetValueOrDefault("A", VK.Return));

        ProcessHoldButton(current.B, ref _bWasPressed, ref _currentBHeldKey, () =>
            isDefault ? VK.F2 : _profiles.CurrentProfile.Mappings.GetValueOrDefault("B", VK.F2));

        ProcessHoldButton(current.X, ref _xWasPressed, ref _currentXHeldKey, () =>
            isDefault ? VK.F1 : _profiles.CurrentProfile.Mappings.GetValueOrDefault("X", VK.F1));

        ProcessHoldButton(current.Y, ref _yWasPressed, ref _currentYHeldKey, () =>
            isDefault ? VK.Multiply : _profiles.CurrentProfile.Mappings.GetValueOrDefault("Y", VK.Multiply));
    }

    private void ProcessShoulderButtons(GamepadState current, bool isDefault)
    {
        ProcessHoldButton(current.LB, ref _lbWasPressed, ref _currentLBHeldKey, () =>
            isDefault ? VK.Multiply : _profiles.CurrentProfile.Mappings.GetValueOrDefault("LB", VK.Multiply));

        ProcessHoldButton(current.RB, ref _rbWasPressed, ref _currentRBHeldKey, () =>
            isDefault ? VK.Divide : _profiles.CurrentProfile.Mappings.GetValueOrDefault("RB", VK.Divide));

        ProcessHoldButton(current.LT, ref _ltWasPressed, ref _currentLTHeldKey, () =>
            isDefault ? VK.F1 : _profiles.CurrentProfile.Mappings.GetValueOrDefault("LT", VK.F1));

        ProcessHoldButton(current.RT, ref _rtWasPressed, ref _currentRTHeldKey, () =>
            isDefault ? VK.F2 : _profiles.CurrentProfile.Mappings.GetValueOrDefault("RT", VK.F2));

        ProcessHoldButton(current.RightThumb, ref _rightThumbWasPressed, ref _currentRightThumbHeldKey, () =>
            isDefault ? VK.Return : _profiles.CurrentProfile.Mappings.GetValueOrDefault("RightThumb", VK.Return));
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

        ProcessButtonEdge(current.LeftThumb, ref _leftThumbWasPressed, () =>
        {
            _backCyclesYxab = !_backCyclesYxab;
            BackCyclesToggled?.Invoke(_backCyclesYxab);
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
