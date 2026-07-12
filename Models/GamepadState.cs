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

        double cx = cal?.CenterX ?? 32767;
        double cy = cal?.CenterY ?? 32767;
        double deadzone = (cal?.DeadzonePercent ?? 40) / 100.0;

        // ── Priority 1: POV hat ──────────────────────────────────────────
        // Centered when low word is 0xFFFF (handles both -1 and 65535)
        if ((pov & 0xFFFF) != 0xFFFF)
        {
            // POV=0 is ambiguous: could be "No POV hat" (struct stays 0) or "North".
            // WinMM often returns 0 even without a POV hat. Always fall through to analog axes.
            if (pov == 0)
                goto fallthrough;

            // 8-direction sector detection (±2250° tolerance per 45° sector)
            if (pov < 2250 || pov >= 33750)                DPadUp = true;
            else if (pov < 6750)  { DPadUp = true; DPadRight = true; }
            else if (pov < 11250)                          DPadRight = true;
            else if (pov < 15750) { DPadDown = true; DPadRight = true; }
            else if (pov < 20250)                          DPadDown = true;
            else if (pov < 24750) { DPadDown = true; DPadLeft = true; }
            else if (pov < 29250)                          DPadLeft = true;
            else if (pov < 33750) { DPadUp = true; DPadLeft = true; }

            return;
        }
    fallthrough:

        // ── Priority 2: X/Y analog stick ─────────────────────────────────
        // Some cheap gamepads have no POV hat and use the analog stick as
        // a DPad. Only use this when the stick is actually deflected beyond
        // the deadzone threshold.
        if (ReadAxisDirection(xPos, yPos, cx, cy, deadzone, cal))
            return;

        // ── Priority 3: Z/R axis ─────────────────────────────────────────
        // Skip Z/R when both axes are far from center (>85% of range).
        // On cheap DInput gamepads, Z/R are triggers at rest (0,0) which
        // would produce a persistent false UpLeft reading without this.
        double zDistNorm = Math.Abs(zPos - cx) / 32767.0;
        double rDistNorm = Math.Abs(rPos - cy) / 32767.0;
        if (zDistNorm <= 0.85 || rDistNorm <= 0.85)
        {
            if (ReadAxisDirection(zPos, rPos, cx, cy, deadzone, cal))
                return;
        }
    }

    private bool ReadAxisDirection(double rawX, double rawY, double cx, double cy, double deadzone, DInputCalibration? cal = null)
    {
        double nx = (rawX - cx) / 32767.0;
        double ny = (rawY - cy) / 32767.0;
        double mag = Math.Sqrt(nx * nx + ny * ny);

        if (mag <= deadzone) return false;

        if (cal != null && cal.HasUserCalibration)
            return ReadAxisDirectionCalibrated(rawX, rawY, cx, cy, cal);

        double angle = Math.Atan2(-ny, nx) * 180.0 / Math.PI;

        // 8-direction sector mapping
        if (angle >= -22.5 && angle < 22.5)
            DPadRight = true;
        else if (angle >= 22.5 && angle < 67.5)
            { DPadUp = true; DPadRight = true; }
        else if (angle >= 67.5 && angle < 112.5)
            DPadUp = true;
        else if (angle >= 112.5 && angle < 157.5)
            { DPadUp = true; DPadLeft = true; }
        else if (angle >= 157.5 || angle < -157.5)
            DPadLeft = true;
        else if (angle >= -157.5 && angle < -112.5)
            { DPadDown = true; DPadLeft = true; }
        else if (angle >= -112.5 && angle < -67.5)
            DPadDown = true;
        else if (angle >= -67.5 && angle < -22.5)
            { DPadDown = true; DPadRight = true; }

        return true;
    }

    private bool ReadAxisDirectionCalibrated(double rawX, double rawY, double cx, double cy, DInputCalibration cal)
    {
        double currentAngle = NormalizedAngle(rawX, rawY, cx, cy);

        var dirNames = new[] { "Right", "UpRight", "Up", "UpLeft", "Left", "DownLeft", "Down", "DownRight" };
        var dirAngles = new[]
        {
            NormalizedAngle(cal.RightX, cal.RightY, cx, cy),
            NormalizedAngle(cal.UpRightX, cal.UpRightY, cx, cy),
            NormalizedAngle(cal.UpX, cal.UpY, cx, cy),
            NormalizedAngle(cal.UpLeftX, cal.UpLeftY, cx, cy),
            NormalizedAngle(cal.LeftX, cal.LeftY, cx, cy),
            NormalizedAngle(cal.DownLeftX, cal.DownLeftY, cx, cy),
            NormalizedAngle(cal.DownX, cal.DownY, cx, cy),
            NormalizedAngle(cal.DownRightX, cal.DownRightY, cx, cy),
        };

        double minDiff = double.MaxValue;
        int bestIdx = 0;
        for (int i = 0; i < dirAngles.Length; i++)
        {
            double diff = Math.Abs(currentAngle - dirAngles[i]);
            if (diff > 180) diff = 360 - diff;
            if (diff < minDiff) { minDiff = diff; bestIdx = i; }
        }

        switch (dirNames[bestIdx])
        {
            case "Right":     DPadRight = true; break;
            case "UpRight":   DPadUp = true; DPadRight = true; break;
            case "Up":        DPadUp = true; break;
            case "UpLeft":    DPadUp = true; DPadLeft = true; break;
            case "Left":      DPadLeft = true; break;
            case "DownLeft":  DPadDown = true; DPadLeft = true; break;
            case "Down":      DPadDown = true; break;
            case "DownRight": DPadDown = true; DPadRight = true; break;
        }

        return true;
    }

    private static double NormalizedAngle(double rawX, double rawY, double cx, double cy)
    {
        double nx = (rawX - cx) / 32767.0;
        double ny = (rawY - cy) / 32767.0;
        double angle = Math.Atan2(-ny, nx) * 180.0 / Math.PI;
        if (angle < 0) angle += 360;
        return angle;
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
