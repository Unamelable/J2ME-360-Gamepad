using System;
using System.Collections.Generic;
using System.Timers;
using J2MEGamepad.Models;
using J2MEGamepad.NativeMethods;

namespace J2MEGamepad.Services;

public class GamepadPoller : IDisposable
{
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

    private DPadMode _currentDPadMode = DPadMode.Pad;
    private bool _backCyclesYxab;
    private bool _skipDefault;
    private readonly HashSet<ushort> _heldPadKeys = new();
    private int _diagonalDelayMs;
    private bool _diagonalDelayHoldCardinals;
    private DateTime _diagonalEntryTime;
    private DPadKey _pendingDiagonalKey = DPadKey.None;
    private bool _diagonalActivated;

    private const ushort VK_NUMPAD2 = 0x62;
    private const ushort VK_NUMPAD4 = 0x64;
    private const ushort VK_NUMPAD6 = 0x66;
    private const ushort VK_NUMPAD8 = 0x68;
    private const ushort VK_NUMPAD7 = 0x67;
    private const ushort VK_NUMPAD9 = 0x69;
    private const ushort VK_NUMPAD1 = 0x61;
    private const ushort VK_NUMPAD3 = 0x63;
    private const ushort VK_UP = 0x26;
    private const ushort VK_DOWN = 0x28;
    private const ushort VK_LEFT = 0x25;
    private const ushort VK_RIGHT = 0x27;
    private const ushort VK_MULTIPLY = 0x6A;
    private const ushort VK_DIVIDE = 0x6F;
    private const ushort VK_F1 = 0x70;
    private const ushort VK_F2 = 0x71;
    private const ushort VK_RETURN = 0x0D;
    private const ushort VK_NUMPAD5 = 0x65;

    public event Action<string>? ModeChanged;
    public event Action<bool>? ConnectionChanged;
    public event Action<bool>? BackCyclesToggled;

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

    public bool IsConnected => _connected;

    public GamepadPoller(KeyboardInjector keyboard, ProfileManager profiles)
    {
        _keyboard = keyboard;
        _profiles = profiles;
        _timer = new Timer(8);
        _timer.Elapsed += Poll;
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

    private void Poll(object? sender, ElapsedEventArgs e)
    {
        var result = XInput.GetState(0, out var state);

        if (result != XInput.ERROR_SUCCESS)
        {
            if (_connected)
            {
                _connected = false;
                _keyboard.ReleaseAll();
                ConnectionChanged?.Invoke(false);
                _lastSentDPadKey = DPadKey.None;
            }
            return;
        }

        if (!_connected)
        {
            _connected = true;
            ConnectionChanged?.Invoke(true);
        }

        var current = new GamepadState();
        current.UpdateFromButtons(state.Gamepad.wButtons);
        current.UpdateTriggers(state.Gamepad.bLeftTrigger, state.Gamepad.bRightTrigger);

        ProcessDPad(current);
        ProcessActionButtons(current);
        ProcessShoulderButtons(current);
        ProcessModeSwitches(current);
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
                _diagonalEntryTime = DateTime.UtcNow;
                _diagonalActivated = false;
                SwapPadKeys(_diagonalDelayHoldCardinals ? GetCardinalKeys(currentKey) : Array.Empty<ushort>());
                return;
            }
            if (!_diagonalActivated)
            {
                if ((DateTime.UtcNow - _diagonalEntryTime).TotalMilliseconds >= _diagonalDelayMs)
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
        var keys = new System.Collections.Generic.List<ushort>(4);
        if (up) keys.Add(isKeypad ? VK_NUMPAD8 : VK_UP);
        if (down) keys.Add(isKeypad ? VK_NUMPAD2 : VK_DOWN);
        if (left) keys.Add(isKeypad ? VK_NUMPAD4 : VK_LEFT);
        if (right) keys.Add(isKeypad ? VK_NUMPAD6 : VK_RIGHT);
        return keys.ToArray();
    }

    private ushort[] GetKeysForDPad(DPadKey key)
    {
        return _currentDPadMode switch
        {
            DPadMode.Pad => key switch
            {
                DPadKey.Up => new[] { VK_UP },
                DPadKey.Down => new[] { VK_DOWN },
                DPadKey.Left => new[] { VK_LEFT },
                DPadKey.Right => new[] { VK_RIGHT },
                _ => Array.Empty<ushort>()
            },
            DPadMode.Keypad => key switch
            {
                DPadKey.Up => new[] { VK_NUMPAD8 },
                DPadKey.Down => new[] { VK_NUMPAD2 },
                DPadKey.Left => new[] { VK_NUMPAD4 },
                DPadKey.Right => new[] { VK_NUMPAD6 },
                DPadKey.UpLeft => new[] { VK_NUMPAD7 },
                DPadKey.UpRight => new[] { VK_NUMPAD9 },
                DPadKey.DownLeft => new[] { VK_NUMPAD1 },
                DPadKey.DownRight => new[] { VK_NUMPAD3 },
                _ => Array.Empty<ushort>()
            },
            DPadMode.PadDiagonal => key switch
            {
                DPadKey.Up => new[] { VK_UP },
                DPadKey.Down => new[] { VK_DOWN },
                DPadKey.Left => new[] { VK_LEFT },
                DPadKey.Right => new[] { VK_RIGHT },
                DPadKey.UpLeft => new[] { VK_NUMPAD7 },
                DPadKey.UpRight => new[] { VK_NUMPAD9 },
                DPadKey.DownLeft => new[] { VK_NUMPAD1 },
                DPadKey.DownRight => new[] { VK_NUMPAD3 },
                _ => Array.Empty<ushort>()
            },
            _ => Array.Empty<ushort>()
        };
    }

    private void ProcessActionButtons(GamepadState current)
    {
        ProcessHoldButton(current.A, ref _aWasPressed, ref _currentAHeldKey, () =>
            _profiles.IsDefaultProfile
                ? _currentDPadMode == DPadMode.Pad ? VK_RETURN : VK_NUMPAD5
                : _profiles.CurrentProfile.Mappings.GetValueOrDefault("A", VK_RETURN));

        ProcessHoldButton(current.B, ref _bWasPressed, ref _currentBHeldKey, () =>
            _profiles.IsDefaultProfile ? VK_F2 : _profiles.CurrentProfile.Mappings.GetValueOrDefault("B", VK_F2));

        ProcessHoldButton(current.X, ref _xWasPressed, ref _currentXHeldKey, () =>
            _profiles.IsDefaultProfile ? VK_F1 : _profiles.CurrentProfile.Mappings.GetValueOrDefault("X", VK_F1));

        ProcessHoldButton(current.Y, ref _yWasPressed, ref _currentYHeldKey, () =>
            _profiles.IsDefaultProfile ? VK_MULTIPLY : _profiles.CurrentProfile.Mappings.GetValueOrDefault("Y", VK_MULTIPLY));
    }

    private void ProcessShoulderButtons(GamepadState current)
    {
        ProcessHoldButton(current.LB, ref _lbWasPressed, ref _currentLBHeldKey, () =>
            _profiles.IsDefaultProfile ? VK_MULTIPLY : _profiles.CurrentProfile.Mappings.GetValueOrDefault("LB", VK_MULTIPLY));

        ProcessHoldButton(current.RB, ref _rbWasPressed, ref _currentRBHeldKey, () =>
            _profiles.IsDefaultProfile ? VK_DIVIDE : _profiles.CurrentProfile.Mappings.GetValueOrDefault("RB", VK_DIVIDE));

        ProcessHoldButton(current.LT, ref _ltWasPressed, ref _currentLTHeldKey, () =>
            _profiles.IsDefaultProfile ? VK_F1 : _profiles.CurrentProfile.Mappings.GetValueOrDefault("LT", VK_F1));

        ProcessHoldButton(current.RT, ref _rtWasPressed, ref _currentRTHeldKey, () =>
            _profiles.IsDefaultProfile ? VK_F2 : _profiles.CurrentProfile.Mappings.GetValueOrDefault("RT", VK_F2));

        ProcessHoldButton(current.RightThumb, ref _rightThumbWasPressed, ref _currentRightThumbHeldKey, () =>
            _profiles.IsDefaultProfile ? VK_RETURN : _profiles.CurrentProfile.Mappings.GetValueOrDefault("RightThumb", VK_RETURN));
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
        Stop();
        _timer.Dispose();
    }
}
