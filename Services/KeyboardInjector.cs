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
