using System;
using System.Linq;
using SharpDX.DirectInput;

namespace J2MEGamepad.Services;

public class DirectInputReader : IDisposable
{
    private DirectInput? _directInput;
    private Joystick? _joystick;
    private bool _available;
    private JoystickState _lastState = new();
    private int _pollsToIgnoreAfterAcquire;
    private readonly object _stateLock = new();

    public bool Available => _available;
    public int X { get { lock (_stateLock) return _lastState.X; } }
    public int Y { get { lock (_stateLock) return _lastState.Y; } }
    public int Z { get { lock (_stateLock) return _lastState.Z; } }
    public int Rx { get { lock (_stateLock) return _lastState.RotationX; } }
    public int Ry { get { lock (_stateLock) return _lastState.RotationY; } }
    public int Rz { get { lock (_stateLock) return _lastState.RotationZ; } }
    public int Pov
    {
        get
        {
            lock (_stateLock)
                return _lastState.PointOfViewControllers.Length > 0
                    ? _lastState.PointOfViewControllers[0] : -1;
        }
    }
    public bool[] Buttons { get { lock (_stateLock) return _lastState.Buttons; } }

    public string? DebugInfo { get; private set; }

    public bool Initialize()
    {
        Cleanup();

        try
        {
            _directInput = new DirectInput();
            var devices = _directInput.GetDevices(DeviceType.Joystick, DeviceEnumerationFlags.AttachedOnly)
                .Concat(_directInput.GetDevices(DeviceType.Gamepad, DeviceEnumerationFlags.AttachedOnly))
                .ToList();

            if (devices.Count == 0)
            {
                DebugInfo = "No DInput devices found";
                return false;
            }

            var devInfo = devices[0];
            _joystick = new Joystick(_directInput, devInfo.InstanceGuid);
            _joystick.SetCooperativeLevel(IntPtr.Zero,
                CooperativeLevel.NonExclusive | CooperativeLevel.Background);

            _joystick.Acquire();

            DebugInfo = $"OK: {devInfo.InstanceName}";
            _available = true;
            _pollsToIgnoreAfterAcquire = 3; // First 3 polls often return garbage (driver quirk)
            return true;
        }
        catch (Exception ex)
        {
            DebugInfo = $"FAIL: {ex.Message}";
            Cleanup();
            return false;
        }
    }

    public bool Poll()
    {
        if (!_available || _joystick == null) return false;

        try
        {
            _joystick.Poll();
            var state = _joystick.GetCurrentState();

            lock (_stateLock)
                _lastState = state;

            if (_pollsToIgnoreAfterAcquire > 0)
            {
                _pollsToIgnoreAfterAcquire--;
                return false;
            }
            return true;
        }
        catch
        {
            try
            {
                _joystick.Acquire();
                _pollsToIgnoreAfterAcquire = 3;
            }
            catch { }
            return false;
        }
    }

    public int GetButtonMask()
    {
        int mask = 0;
        var btns = Buttons;
        if (btns == null) return mask;
        for (int i = 0; i < btns.Length; i++)
        {
            if (btns[i])
                mask |= (1 << i);
        }
        return mask;
    }

    private void Cleanup()
    {
        _available = false;
        _joystick?.Unacquire();
        _joystick?.Dispose();
        _joystick = null;
        _directInput?.Dispose();
        _directInput = null;
    }

    public void Dispose()
    {
        Cleanup();
    }
}
