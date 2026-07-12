using System;
using System.IO;
using System.Runtime.InteropServices;

namespace J2MEGamepad.Services;

internal static class DllSafety
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    public static void HardenNativeLoading()
    {
        SetDllDirectory(string.Empty);

        foreach (var name in new[] { "dinput8.dll", "xinput1_3.dll", "winmm.dll" })
        {
            var fullPath = Path.Combine(Environment.SystemDirectory, name);
            if (File.Exists(fullPath))
                LoadLibrary(fullPath);
        }
    }
}
