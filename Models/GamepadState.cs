using System;
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

public struct GamepadState
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

    public void UpdateFromDInput(uint buttons, int pov, int xPos, int yPos, int zPos = 0, int rPos = 0, DInputCalibration? cal = null)
    {
        var mask = buttons;
        A = (mask & (1 << 1)) != 0;
        B = (mask & (1 << 2)) != 0;
        X = (mask & (1 << 0)) != 0;
        Y = (mask & (1 << 3)) != 0;
        LB = (mask & (1 << 4)) != 0;
        RB = (mask & (1 << 5)) != 0;
        LT = (mask & (1 << 6)) != 0;
        RT = (mask & (1 << 7)) != 0;
        Start = (mask & (1 << 9)) != 0;
        Back = (mask & (1 << 8)) != 0;
        LeftThumb = (mask & (1 << 10)) != 0;
        RightThumb = (mask & (1 << 11)) != 0;
        DecodeDInputDpad(pov, xPos, yPos, zPos, rPos, cal);
    }

    private void DecodeDInputDpad(int pov, int xPos, int yPos, int zPos, int rPos, DInputCalibration? cal = null)
    {
        DPadUp = false;
        DPadDown = false;
        DPadLeft = false;
        DPadRight = false;

        float cx = cal?.CenterX ?? 32767;
        float cy = cal?.CenterY ?? 32767;
        float deadzone = (cal?.DeadzonePercent ?? 40) / 100.0f;

        // Normalize axes to -1..1 float and get angle + magnitude
        if (ReadAxisDirection(xPos, yPos, cx, cy, deadzone)) return;

        // ── Z/R axis guard ──────────────────────────────────────────────
        // Skip Z/R when both axes are far from center (>85% of range).
        // On cheap DInput gamepads, Z/R are triggers at rest (0,0) which
        // would produce a persistent false UpLeft reading without this.
        // ────────────────────────────────────────────────────────────────
        float zDistNorm = MathF.Abs(zPos - cx) / 32767.0f;
        float rDistNorm = MathF.Abs(rPos - cy) / 32767.0f;
        if (zDistNorm <= 0.85f || rDistNorm <= 0.85f)
        {
            if (ReadAxisDirection(zPos, rPos, cx, cy, deadzone)) return;
        }

        // Fallback: POV hat
        if (pov >= 0 && pov != 0xFFFF)
        {
            if (pov < 4500 || pov >= 31500) DPadUp = true;
            if (pov >= 4500 && pov < 13500) DPadRight = true;
            if (pov >= 13500 && pov < 22500) DPadDown = true;
            if (pov >= 22500 && pov < 31500) DPadLeft = true;
        }
    }

    private bool ReadAxisDirection(float rawX, float rawY, float cx, float cy, float deadzone)
    {
        float nx = (rawX - cx) / 32767.0f;
        float ny = (rawY - cy) / 32767.0f;
        float mag = MathF.Sqrt(nx * nx + ny * ny);

        if (mag <= deadzone) return false;

        float angle = MathF.Atan2(-ny, nx) * 180.0f / MathF.PI;

        // 8-direction sector mapping
        if (angle >= -22.5f && angle < 22.5f)
            DPadRight = true;
        else if (angle >= 22.5f && angle < 67.5f)
            { DPadUp = true; DPadRight = true; }
        else if (angle >= 67.5f && angle < 112.5f)
            DPadUp = true;
        else if (angle >= 112.5f && angle < 157.5f)
            { DPadUp = true; DPadLeft = true; }
        else if (angle >= 157.5f || angle < -157.5f)
            DPadLeft = true;
        else if (angle >= -157.5f && angle < -112.5f)
            { DPadDown = true; DPadLeft = true; }
        else if (angle >= -112.5f && angle < -67.5f)
            DPadDown = true;
        else if (angle >= -67.5f && angle < -22.5f)
            { DPadDown = true; DPadRight = true; }

        return true;
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
