using System;
using System.Timers;
using J2MEGamepad.NativeMethods;

namespace J2MEGamepad.Services;

public class ControllerWatchdog : IDisposable
{
    private readonly Timer _timer;
    private bool _wasConnected;

    public event Action? Connected;
    public event Action? Disconnected;

    public bool IsConnected { get; private set; }

    public ControllerWatchdog()
    {
        _timer = new Timer(1000);
        _timer.Elapsed += OnTick;
    }

    public void Start()
    {
        CheckConnection();
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
    }

    private void OnTick(object? sender, ElapsedEventArgs e)
    {
        CheckConnection();
    }

    private void CheckConnection()
    {
        var result = XInput.GetState(0, out _);
        IsConnected = result == XInput.ERROR_SUCCESS;

        if (IsConnected && !_wasConnected)
        {
            _wasConnected = true;
            Connected?.Invoke();
        }
        else if (!IsConnected && _wasConnected)
        {
            _wasConnected = false;
            Disconnected?.Invoke();
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
    }
}
