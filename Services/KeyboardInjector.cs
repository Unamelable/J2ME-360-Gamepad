using System;
using System.Collections.Generic;
using J2MEGamepad.NativeMethods;

namespace J2MEGamepad.Services;

public class KeyboardInjector : IDisposable
{
    private readonly HashSet<ushort> _keysDown = new();

    public void PressKey(ushort vkCode)
    {
        if (!_keysDown.Contains(vkCode))
        {
            KeyboardInput.SendKeyDown(vkCode);
            _keysDown.Add(vkCode);
        }
    }

    public void ReleaseKey(ushort vkCode)
    {
        if (_keysDown.Contains(vkCode))
        {
            KeyboardInput.SendKeyUp(vkCode);
            _keysDown.Remove(vkCode);
        }
    }

    public void ReleaseAll()
    {
        foreach (var key in _keysDown)
        {
            KeyboardInput.SendKeyUp(key);
        }
        _keysDown.Clear();
    }

    public void Dispose()
    {
        ReleaseAll();
    }
}
