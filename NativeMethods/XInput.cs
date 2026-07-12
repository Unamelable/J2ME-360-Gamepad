using System.Runtime.InteropServices;

namespace J2MEGamepad.NativeMethods;

[StructLayout(LayoutKind.Sequential)]
public struct XInputState
{
    public uint dwPacketNumber;
    public XInputGamepad Gamepad;
}

[StructLayout(LayoutKind.Sequential)]
public struct XInputGamepad
{
    public ushort wButtons;
    public byte bLeftTrigger;
    public byte bRightTrigger;
    public short sThumbLX;
    public short sThumbLY;
    public short sThumbRX;
    public short sThumbRY;
}

[StructLayout(LayoutKind.Sequential)]
public struct XInputVibration
{
    public ushort wLeftMotorSpeed;
    public ushort wRightMotorSpeed;
}

public static class XInputButtons
{
    public const ushort XINPUT_GAMEPAD_DPAD_UP = 0x0001;
    public const ushort XINPUT_GAMEPAD_DPAD_DOWN = 0x0002;
    public const ushort XINPUT_GAMEPAD_DPAD_LEFT = 0x0004;
    public const ushort XINPUT_GAMEPAD_DPAD_RIGHT = 0x0008;
    public const ushort XINPUT_GAMEPAD_START = 0x0010;
    public const ushort XINPUT_GAMEPAD_BACK = 0x0020;
    public const ushort XINPUT_GAMEPAD_LEFT_THUMB = 0x0040;
    public const ushort XINPUT_GAMEPAD_RIGHT_THUMB = 0x0080;
    public const ushort XINPUT_GAMEPAD_LEFT_SHOULDER = 0x0100;
    public const ushort XINPUT_GAMEPAD_RIGHT_SHOULDER = 0x0200;
    public const ushort XINPUT_GAMEPAD_A = 0x1000;
    public const ushort XINPUT_GAMEPAD_B = 0x2000;
    public const ushort XINPUT_GAMEPAD_X = 0x4000;
    public const ushort XINPUT_GAMEPAD_Y = 0x8000;
}

public static class XInput
{
    private const string DllName = "xinput1_3.dll";

    [DllImport(DllName, EntryPoint = "XInputGetState")]
    private static extern uint XInputGetStateNative(int dwUserIndex, out XInputState pState);

    [DllImport(DllName, EntryPoint = "XInputSetState")]
    private static extern uint XInputSetStateNative(int dwUserIndex, ref XInputVibration pVibration);

    public const uint ERROR_SUCCESS = 0;
    public const uint ERROR_DEVICE_NOT_CONNECTED = 1167;

    public static uint GetState(int userIndex, out XInputState state)
    {
        return XInputGetStateNative(userIndex, out state);
    }

    public static uint SetState(int userIndex, ref XInputVibration vibration)
    {
        return XInputSetStateNative(userIndex, ref vibration);
    }
}
