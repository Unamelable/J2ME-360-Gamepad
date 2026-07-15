using System;
using System.Collections.Generic;
using J2MEGamepad.NativeMethods;

namespace J2MEGamepad.Services;

public class KeyboardInjector : IDisposable
{
    private const ushort VK_MBUTTON = 0x04;

    private readonly HashSet<ushort> _keysDown = new();
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Sends key-up for common modifier keys to unstick any left over from a
    /// previous crash/force-kill. Safe to call even if keys aren't held.
    /// </summary>
    public static void UnstickModifierKeys()
    {
        // Left/Right Ctrl, Shift, Alt + generic VK codes
        KeyboardInput.SendKeyUp(0xA2); // LCtrl
        KeyboardInput.SendKeyUp(0xA3); // RCtrl
        KeyboardInput.SendKeyUp(0xA0); // LShift
        KeyboardInput.SendKeyUp(0xA1); // RShift
        KeyboardInput.SendKeyUp(0xA4); // LAlt
        KeyboardInput.SendKeyUp(0xA5); // RAlt
        KeyboardInput.SendKeyUp(0x11); // Ctrl (generic)
        KeyboardInput.SendKeyUp(0x10); // Shift (generic)
        KeyboardInput.SendKeyUp(0x12); // Alt (generic)
    }

    public void PressKey(ushort vkCode)
    {
        if (_disposed) return;
        lock (_lock)
        {
            if (_disposed) return;
            if (!_keysDown.Contains(vkCode))
            {
                if (vkCode == VK_MBUTTON)
                    KeyboardInput.SendMouseDown();
                else
                    KeyboardInput.SendKeyDown(vkCode);
                _keysDown.Add(vkCode);
            }
        }
    }

    public void ReleaseKey(ushort vkCode)
    {
        if (_disposed) return;
        lock (_lock)
        {
            if (_disposed) return;
            if (_keysDown.Contains(vkCode))
            {
                if (vkCode == VK_MBUTTON)
                    KeyboardInput.SendMouseUp();
                else
                    KeyboardInput.SendKeyUp(vkCode);
                _keysDown.Remove(vkCode);
            }
        }
    }

    public void ReleaseAll()
    {
        lock (_lock)
        {
            foreach (var key in _keysDown)
                KeyboardInput.SendKeyUp(key);
            _keysDown.Clear();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            ReleaseAll();
        }
    }
}
