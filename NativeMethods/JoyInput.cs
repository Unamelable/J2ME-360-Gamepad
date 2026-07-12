using System;
using System.Runtime.InteropServices;

namespace J2MEGamepad.NativeMethods;

[StructLayout(LayoutKind.Sequential)]
public struct JOYINFOEX
{
    public int dwSize;
    public int dwFlags;
    public int dwXpos;
    public int dwYpos;
    public int dwZpos;
    public int dwRpos;
    public int dwUpos;
    public int dwVpos;
    public int dwButtons;
    public int dwButtonNumber;
    public int dwPOV;
    public int dwReserved1;
    public int dwReserved2;
}

public static class JoyInput
{
    private const string DllName = "winmm.dll";

    [DllImport(DllName)]
    private static extern int joyGetNumDevs();

    [DllImport(DllName)]
    private static extern int joyGetPosEx(int uJoyID, ref JOYINFOEX pji);

    public const int JOYERR_NOERROR = 0;
    public const int JOYERR_UNPLUGGED = 165;
    // POV flag for proper centered reporting (0xFFFF = centered)
    public const int JOY_RETURNPOVCTS = 0x200;
    public const int JOY_RETURNALL = 0xFF;

    public const int POV_CENTERED = -1;
    public const int POV_CENTERED_U = 0xFFFF;

    public static int GetNumDevices()
    {
        return joyGetNumDevs();
    }

    public static int GetPosEx(int uJoyID, ref JOYINFOEX pji)
    {
        return joyGetPosEx(uJoyID, ref pji);
    }
}
