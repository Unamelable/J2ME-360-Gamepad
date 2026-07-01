using J2MEGamepad.NativeMethods;

namespace J2MEGamepad.Models;

public enum DPadMode
{
    Pad,
    Keypad,
    PadDiagonal
}

public enum DPadKey
{
    None,
    Up,
    Down,
    Left,
    Right,
    UpLeft,
    UpRight,
    DownLeft,
    DownRight
}

public class GamepadState
{
    public bool A { get; set; }
    public bool B { get; set; }
    public bool X { get; set; }
    public bool Y { get; set; }
    public bool LB { get; set; }
    public bool RB { get; set; }
    public bool LT { get; set; }
    public bool RT { get; set; }
    public bool Start { get; set; }
    public bool Back { get; set; }
    public bool LeftThumb { get; set; }
    public bool RightThumb { get; set; }
    public bool DPadUp { get; set; }
    public bool DPadDown { get; set; }
    public bool DPadLeft { get; set; }
    public bool DPadRight { get; set; }

    public void UpdateFromButtons(ushort buttons)
    {
        A = (buttons & XInputButtons.XINPUT_GAMEPAD_A) != 0;
        B = (buttons & XInputButtons.XINPUT_GAMEPAD_B) != 0;
        X = (buttons & XInputButtons.XINPUT_GAMEPAD_X) != 0;
        Y = (buttons & XInputButtons.XINPUT_GAMEPAD_Y) != 0;
        LB = (buttons & XInputButtons.XINPUT_GAMEPAD_LEFT_SHOULDER) != 0;
        RB = (buttons & XInputButtons.XINPUT_GAMEPAD_RIGHT_SHOULDER) != 0;
        LT = false;
        RT = false;
        Start = (buttons & XInputButtons.XINPUT_GAMEPAD_START) != 0;
        Back = (buttons & XInputButtons.XINPUT_GAMEPAD_BACK) != 0;
        LeftThumb = (buttons & XInputButtons.XINPUT_GAMEPAD_LEFT_THUMB) != 0;
        RightThumb = (buttons & XInputButtons.XINPUT_GAMEPAD_RIGHT_THUMB) != 0;
        DPadUp = (buttons & XInputButtons.XINPUT_GAMEPAD_DPAD_UP) != 0;
        DPadDown = (buttons & XInputButtons.XINPUT_GAMEPAD_DPAD_DOWN) != 0;
        DPadLeft = (buttons & XInputButtons.XINPUT_GAMEPAD_DPAD_LEFT) != 0;
        DPadRight = (buttons & XInputButtons.XINPUT_GAMEPAD_DPAD_RIGHT) != 0;
    }

    public void UpdateTriggers(byte leftTrigger, byte rightTrigger, byte threshold = 50)
    {
        LT = leftTrigger >= threshold;
        RT = rightTrigger >= threshold;
    }

    public DPadKey GetDPadKey()
    {
        if (!DPadUp && !DPadDown && !DPadLeft && !DPadRight)
            return DPadKey.None;

        bool up = DPadUp;
        bool down = DPadDown;
        bool left = DPadLeft;
        bool right = DPadRight;

        if (up && left) return DPadKey.UpLeft;
        if (up && right) return DPadKey.UpRight;
        if (down && left) return DPadKey.DownLeft;
        if (down && right) return DPadKey.DownRight;
        if (up) return DPadKey.Up;
        if (down) return DPadKey.Down;
        if (left) return DPadKey.Left;
        if (right) return DPadKey.Right;

        return DPadKey.None;
    }
}
