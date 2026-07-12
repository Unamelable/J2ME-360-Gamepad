using System;
using System.Runtime.InteropServices;

namespace J2MEGamepad.NativeMethods;

public static class KeyboardInput
{
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint INPUT_MOUSE = 0;
    private const uint KEYEVENTF_KEYDOWN = 0x0000;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;

    private static readonly int InputSize = Marshal.SizeOf(typeof(INPUT));

    [ThreadStatic]
    private static INPUT[]? _inputBuffer;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    public static void SendKeyDown(ushort virtualKeyCode)
    {
        SendKey(virtualKeyCode, KEYEVENTF_KEYDOWN);
    }

    public static void SendKeyUp(ushort virtualKeyCode)
    {
        SendKey(virtualKeyCode, KEYEVENTF_KEYUP);
    }

    public static void SendMouseDown()
    {
        SendMouse(MOUSEEVENTF_MIDDLEDOWN);
    }

    public static void SendMouseUp()
    {
        SendMouse(MOUSEEVENTF_MIDDLEUP);
    }

    private static void SendKey(ushort virtualKeyCode, uint flags)
    {
        _inputBuffer ??= new INPUT[1];
        _inputBuffer[0] = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = virtualKeyCode,
                    wScan = (ushort)((uint)virtualKeyCode & 0xFF),
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        SendInput(1, _inputBuffer, InputSize);
    }

    private static void SendMouse(uint flags)
    {
        _inputBuffer ??= new INPUT[1];
        _inputBuffer[0] = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = 0,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        SendInput(1, _inputBuffer, InputSize);
    }
}
