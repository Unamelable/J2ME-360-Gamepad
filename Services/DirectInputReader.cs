using System;
using System.IO;
using System.Runtime.InteropServices;
using J2MEGamepad.NativeMethods;

namespace J2MEGamepad.Services;

public class DirectInputReader : IDisposable
{
    private const uint DIRECTINPUT_VERSION = 0x0800;
    private static readonly Guid IID_IDirectInput8W = new Guid("BF798031-483A-11D2-8D99-0000F875AC12");
    private static readonly Guid IID_IDirectInput8A = new Guid("BF798030-483A-11D2-8D99-0000F875AC12");
    private static readonly Guid IID_IDirectInputDevice8W = new Guid("25BC619B-3814-11D2-8D99-0000F875AC12");

    private const int DI_OK = 0;
    private const int DI_NOEFFECT = 1;
    private const int DI_POLLEDDEVICE = 2;
    private const int E_PENDING = unchecked((int)0x8000000A);
    private const int DIERR_INPUTLOST = unchecked((int)0x8007001E);
    private const int DIERR_NOTACQUIRED = unchecked((int)0x80040101);
    private const int DIERR_ACQUIRED = unchecked((int)0x800700AA);

    private const int DI8DEVCLASS_GAMECTRL = 4;
    private const int DIEDFL_ATTACHEDONLY = 1;
    private const int DISCL_BACKGROUND = 2;
    private const int DISCL_NONEXCLUSIVE = 4;

    private const int DI8DEVTYPE_GAMEPAD = 0x12;

    // DIPROP_AXISMODE
    private static readonly Guid DIPROP_AXISMODE = new Guid("A36D02E4-C9F3-11CF-BFC7-444553540000");
    private const int DIPROPAXISMODE_ABS = 0;

    private IntPtr _diPtr;
    private IntPtr _devicePtr;
    private IntPtr _diVtable;
    private IntPtr _devVtable;

    private fun_CreateDevice _createDevice;
    private fun_EnumDevices _enumDevices;
    private fun_Release _diRelease;
    private fun_Release _devRelease;

    private fun_Acquire _acquire;
    private fun_Unacquire _unacquire;
    private fun_Poll _poll;
    private fun_GetDeviceState _getDeviceState;
    private fun_SetDataFormat _setDataFormat;
    private fun_SetCooperativeLevel _setCooperativeLevel;
    private fun_SetProperty _setProperty;

    private int _lastButtons;
    private int _lastPov;
    private int _lastX, _lastY, _lastZ, _lastRx, _lastRy, _lastRz;
    private bool _available;
    private bool _disposed;
    private string? _debugInfo;
    private readonly object _stateLock = new();
    private readonly bool[] _buttons = new bool[128];
    private int _initPollsToSkip;
    private string _deviceName = "";
    private bool _lastPollOk;

    private IntPtr _dfPtr;
    private bool _dfLoaded;
    private IntPtr _hwnd;

    public IntPtr WindowHandle { set => _hwnd = value; }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int fun_Release(IntPtr p);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int fun_CreateDevice(IntPtr p, [In] ref Guid rguid, out IntPtr dev, IntPtr unk);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int fun_EnumDevices(IntPtr p, uint dwDevType, IntPtr cb, IntPtr pvRef, uint dwFlags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int fun_Acquire(IntPtr p);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int fun_Unacquire(IntPtr p);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int fun_Poll(IntPtr p);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int fun_GetDeviceState(IntPtr p, int cb, IntPtr pData);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int fun_SetDataFormat(IntPtr p, IntPtr df);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int fun_SetCooperativeLevel(IntPtr p, IntPtr hwnd, int flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int fun_SetProperty(IntPtr p, [In] ref Guid guid, IntPtr pdiph);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int fun_DIEnumDevicesCallback(ref DIDEVICEINSTANCEW info, IntPtr pvRef);

    public bool Available => _available;
    public int X { get { lock (_stateLock) return _lastX; } }
    public int Y { get { lock (_stateLock) return _lastY; } }
    public int Z { get { lock (_stateLock) return _lastZ; } }
    public int Rx { get { lock (_stateLock) return _lastRx; } }
    public int Ry { get { lock (_stateLock) return _lastRy; } }
    public int Rz { get { lock (_stateLock) return _lastRz; } }
    public int Pov { get { lock (_stateLock) return _lastPov; } }
    public bool[] Buttons { get { lock (_stateLock) return _buttons; } }
    public string? DebugInfo { get; private set; }
    public string DeviceName => _deviceName;

    private string? _lastPollMsg;
    private void LogPollOnce(string msg)
    {
        if (msg == _lastPollMsg) return;
        _lastPollMsg = msg;
        LogHelper.Info("DIReader", msg);
    }

    [DllImport("dinput8.dll")]
    private static extern int DirectInput8Create(IntPtr hinst, uint dwVersion, [In] ref Guid riidltf, out IntPtr ppvOut, IntPtr punkOuter);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, int dwCoInit);
    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance([In] ref Guid rclsid, IntPtr pUnkOuter, int dwClsContext, [In] ref Guid riid, out IntPtr ppv);
    private const int CLSCTX_INPROC_SERVER = 1;
    private const int COINIT_APARTMENTTHREADED = 2;

    private static readonly Guid CLSID_DirectInput8 = new Guid("25E609E0-B259-11CF-BFC7-444553540000");

    // ── DirectInput8 initialization ─────────────────────────────────
    // Tries three strategies in order:
    //   1. DirectInput8Create(DIRECTINPUT_VERSION, IID_IDirectInput8W)
    //   2. DirectInput8Create(DIRECTINPUT_VERSION, IID_IDirectInput8A)
    //   3. CoCreateInstance(CLSID_DirectInput8, IID_IDirectInput8W)
    //
    // On some Windows 11 systems the COM class is not registered and
    // ALL three fail with 0x80004002 (E_NOINTERFACE).  When that
    // happens the caller falls back to WinMM (winmm.dll/joyGetPosEx).
    // Do NOT remove that fallback without replacing it first.
    //
    // If a future HID API path is added, it should be checked before
    // the WinMM fallback (not before this DirectInput path).
    // ─────────────────────────────────────────────────────────────────
    public bool Initialize()
    {
        Cleanup();

        try
        {
            CoInitializeEx(IntPtr.Zero, 0); // COINIT_MULTITHREADED (matches thread-pool MTA)

            var iid = IID_IDirectInput8W;
            int hr = DirectInput8Create(GetModuleHandle(null), DIRECTINPUT_VERSION,
                ref iid, out _diPtr, IntPtr.Zero);

            if (hr != DI_OK || _diPtr == IntPtr.Zero)
            {
                iid = IID_IDirectInput8A;
                hr = DirectInput8Create(GetModuleHandle(null), DIRECTINPUT_VERSION,
                    ref iid, out _diPtr, IntPtr.Zero);
            }

            if (hr != DI_OK || _diPtr == IntPtr.Zero)
            {
                iid = IID_IDirectInput8W;
                Guid clsid = CLSID_DirectInput8;
                hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out _diPtr);
            }

            if (hr != DI_OK || _diPtr == IntPtr.Zero)
            {
                DebugInfo = $"DirectInput failed: 0x{hr:X8}";
                return false;
            }

            _diVtable = Marshal.ReadIntPtr(_diPtr);
            int sz = IntPtr.Size;
            _diRelease = (fun_Release)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(_diVtable, 2 * sz), typeof(fun_Release));
            _createDevice = (fun_CreateDevice)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(_diVtable, 3 * sz), typeof(fun_CreateDevice));
            _enumDevices = (fun_EnumDevices)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(_diVtable, 4 * sz), typeof(fun_EnumDevices));

            // Prefer gamepad subtype, fallback to first device
            Guid deviceGuid = Guid.Empty;
            string fallbackName = "";
            _deviceName = "";
            bool hasGamepad = false;
            string enumLog = "";
            var enumCb = new fun_DIEnumDevicesCallback((ref DIDEVICEINSTANCEW info, IntPtr pvRef) =>
            {
                string name = info.tszProductName ?? info.tszInstanceName ?? "(unnamed)";
                int devType = info.dwDevType & 0xFF;
                enumLog += $"  [{info.guidInstance}] \"{name}\" type=0x{devType:X2}\n";
                if (devType == DI8DEVTYPE_GAMEPAD)
                {
                    deviceGuid = info.guidInstance;
                    _deviceName = name;
                    hasGamepad = true;
                    return 0;
                }
                if (deviceGuid == Guid.Empty)
                {
                    deviceGuid = info.guidInstance;
                    fallbackName = name;
                }
                return 1;
            });
            IntPtr cbPtr = Marshal.GetFunctionPointerForDelegate(enumCb);

            hr = _enumDevices(_diPtr, (uint)DI8DEVCLASS_GAMECTRL, cbPtr, IntPtr.Zero, (uint)DIEDFL_ATTACHEDONLY);
            if (enumLog.Length > 0)
            {
                LogHelper.Info("DIReader", "EnumDevices:");
                foreach (string line in enumLog.TrimEnd().Split('\n'))
                    if (!string.IsNullOrWhiteSpace(line))
                        LogHelper.Info("DIReader", "  " + line);
            }
            if (deviceGuid == Guid.Empty)
            {
                DebugInfo = "No DInput devices found";
                Cleanup();
                return false;
            }

            if (!hasGamepad && _deviceName == "")
                _deviceName = fallbackName;

            hr = _createDevice(_diPtr, ref deviceGuid, out _devicePtr, IntPtr.Zero);
            if (hr != DI_OK || _devicePtr == IntPtr.Zero)
            {
                DebugInfo = $"CreateDevice failed: 0x{hr:X8}";
                Cleanup();
                return false;
            }

            _devVtable = Marshal.ReadIntPtr(_devicePtr);
            _devRelease = (fun_Release)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(_devVtable, 2 * sz), typeof(fun_Release));
            _acquire = (fun_Acquire)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(_devVtable, 7 * sz), typeof(fun_Acquire));
            _unacquire = (fun_Unacquire)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(_devVtable, 8 * sz), typeof(fun_Unacquire));
            _getDeviceState = (fun_GetDeviceState)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(_devVtable, 9 * sz), typeof(fun_GetDeviceState));
            _setDataFormat = (fun_SetDataFormat)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(_devVtable, 11 * sz), typeof(fun_SetDataFormat));
            _setCooperativeLevel = (fun_SetCooperativeLevel)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(_devVtable, 13 * sz), typeof(fun_SetCooperativeLevel));
            _setProperty = (fun_SetProperty)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(_devVtable, 6 * sz), typeof(fun_SetProperty));
            _poll = (fun_Poll)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(_devVtable, 25 * sz), typeof(fun_Poll));

            _dfPtr = LoadAndGetDataFormat();
            if (_dfPtr == IntPtr.Zero)
            {
                DebugInfo = "Failed to load c_dfDIJoystick2";
                Cleanup();
                return false;
            }

            hr = _setDataFormat(_devicePtr, _dfPtr);
            if (hr != DI_OK)
            {
                DebugInfo = $"SetDataFormat failed: 0x{hr:X8}";
                Cleanup();
                return false;
            }

            // SetCooperativeLevel — must have valid HWND on Windows XP
            hr = _setCooperativeLevel(_devicePtr, _hwnd, DISCL_BACKGROUND | DISCL_NONEXCLUSIVE);
            if (hr != DI_OK)
            {
                DebugInfo = $"SetCooperativeLevel failed: 0x{hr:X8}";
                // Non-fatal — may succeed later when HWND is set via WindowHandle
            }

            hr = _acquire(_devicePtr);
            if (hr != DI_OK)
            {
                DebugInfo = $"Acquire failed: 0x{hr:X8}";
                Cleanup();
                return false;
            }

            // Set absolute axis mode for consistent behavior
            SetAbsoluteAxisMode();

            DebugInfo = $"OK: {_deviceName}";
            _available = true;
            _initPollsToSkip = 3;
            return true;
        }
        catch (Exception ex)
        {
            DebugInfo = $"FAIL: {ex.Message}";
            Cleanup();
            return false;
        }
    }

    private void SetAbsoluteAxisMode()
    {
        if (_devicePtr == IntPtr.Zero || _unacquire == null || _setProperty == null || _acquire == null)
            return;

        var mode = new DIPROPDWORD();
        mode.diph.dwSize = Marshal.SizeOf(typeof(DIPROPDWORD));
        mode.diph.dwHeaderSize = Marshal.SizeOf(typeof(DIPROPHEADER));
        mode.diph.dwHow = 1; // DIPH_DEVICE
        mode.diph.dwObj = 0;
        mode.dwData = DIPROPAXISMODE_ABS;

        GCHandle pin = GCHandle.Alloc(mode, GCHandleType.Pinned);
        try
        {
            int hr = _unacquire(_devicePtr);
            if (hr != DI_OK && hr != DI_NOEFFECT)
                return;

            Guid guid = DIPROP_AXISMODE;
            _setProperty(_devicePtr, ref guid, pin.AddrOfPinnedObject());

            _acquire(_devicePtr);
        }
        finally
        {
            pin.Free();
        }
    }

    private IntPtr LoadAndGetDataFormat()
    {
        IntPtr hMod = LoadLibrary("dinput8.dll");
        if (hMod == IntPtr.Zero) return IntPtr.Zero;
        return GetProcAddress(hMod, "c_dfDIJoystick2");
    }

    public bool Poll()
    {
        if (!_available || _devicePtr == IntPtr.Zero)
        {
            LogPollOnce("Poll: not available");
            return false;
        }

        try
        {
            int hr = _poll(_devicePtr);
            if (hr != DI_OK)
            {
                if (hr == DIERR_INPUTLOST || hr == DIERR_NOTACQUIRED)
                {
                    LogPollOnce($"Poll: reacquire after 0x{hr:X8}");
                    hr = _acquire(_devicePtr);
                    if (hr != DI_OK) { LogPollOnce("Poll: reacquire failed"); return false; }
                    hr = _poll(_devicePtr);
                    if (hr != DI_OK) { LogPollOnce("Poll: reacquire poll failed"); return false; }
                }
                else
                {
                    LogPollOnce("Poll: device error");
                    return false;
                }
            }

            DIJOYSTATE2 state = new DIJOYSTATE2();
            GCHandle handle = GCHandle.Alloc(state, GCHandleType.Pinned);
            try
            {
                hr = _getDeviceState(_devicePtr, Marshal.SizeOf(typeof(DIJOYSTATE2)), handle.AddrOfPinnedObject());
                if (hr == E_PENDING) { LogPollOnce("Poll: E_PENDING"); return false; }
                if (hr != DI_OK) { LogPollOnce("Poll: GetDeviceState failed"); return false; }
                state = (DIJOYSTATE2)Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(DIJOYSTATE2));
            }
            finally
            {
                handle.Free();
            }

            if (_initPollsToSkip > 0)
            {
                _initPollsToSkip--;
                LogPollOnce("Poll: init skip");
                return false;
            }

            lock (_stateLock)
            {
                _lastX = state.lX;
                _lastY = state.lY;
                _lastZ = state.lZ;
                _lastRx = state.lRx;
                _lastRy = state.lRy;
                _lastRz = state.lRz;
                _lastPov = state.rgdwPOV.Length > 0 ? (int)state.rgdwPOV[0] : -1;
                _lastButtons = 0;
                for (int i = 0; i < 128 && i < state.rgbButtons.Length; i++)
                {
                    bool pressed = (state.rgbButtons[i] & 0x80) != 0;
                    _buttons[i] = pressed;
                    if (pressed) _lastButtons |= (1 << i);
                }
            }
            LogPollOnce("Poll: OK");
            return true;
        }
        catch (Exception ex)
        {
            LogPollOnce("Poll: exception");
            return false;
        }
    }

    public int GetButtonMask()
    {
        lock (_stateLock) return _lastButtons;
    }

    private void Cleanup()
    {
        _available = false;
        if (_devicePtr != IntPtr.Zero && _unacquire != null)
        {
            try { _unacquire(_devicePtr); } catch { }
        }
        if (_devicePtr != IntPtr.Zero && _devRelease != null)
        {
            try { _devRelease(_devicePtr); } catch { }
        }
        _devicePtr = IntPtr.Zero;
        _devVtable = IntPtr.Zero;
        if (_diPtr != IntPtr.Zero && _diRelease != null)
        {
            try { _diRelease(_diPtr); } catch { }
        }
        _diPtr = IntPtr.Zero;
        _diVtable = IntPtr.Zero;
        _acquire = null;
        _unacquire = null;
        _poll = null;
        _getDeviceState = null;
        _setDataFormat = null;
        _setCooperativeLevel = null;
        _setProperty = null;
        _createDevice = null;
        _enumDevices = null;
        _diRelease = null;
        _devRelease = null;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            Cleanup();
        }
    }
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DIDEVICEINSTANCEW
{
    public int dwSize;
    public Guid guidInstance;
    public Guid guidProduct;
    public int dwDevType;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string tszInstanceName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string tszProductName;
    public Guid guidFFDriver;
    public ushort wUsagePage;
    public ushort wUsage;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DIJOYSTATE2
{
    public int lX;
    public int lY;
    public int lZ;
    public int lRx;
    public int lRy;
    public int lRz;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
    public int[] rglSlider;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public int[] rgdwPOV;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
    public byte[] rgbButtons;
    public int lVX;
    public int lVY;
    public int lVZ;
    public int lVRx;
    public int lVRy;
    public int lVRz;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
    public int[] rglVSlider;
    public int lAX;
    public int lAY;
    public int lAZ;
    public int lARx;
    public int lARy;
    public int lARz;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
    public int[] rglASlider;
    public int lFX;
    public int lFY;
    public int lFZ;
    public int lFRx;
    public int lFRy;
    public int lFRz;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
    public int[] rglFSlider;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DIPROPHEADER
{
    public int dwSize;
    public int dwHeaderSize;
    public int dwObj;
    public int dwHow;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DIPROPDWORD
{
    public DIPROPHEADER diph;
    public int dwData;
}
