using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace J2MEGamepad.Models;

public class KeyMapProfile
{
    public string Name { get; set; } = "Default";

    public Dictionary<string, ushort> Mappings { get; set; } = new()
    {
        ["Y"] = 0x6A,     // Numpad *
        ["X"] = 0x70,     // F1
        ["A"] = 0x0D,     // Enter
        ["B"] = 0x71,     // F2
        ["LB"] = 0x6A,    // *
        ["RB"] = 0x6F,    // /
        ["LT"] = 0x70,    // F1
        ["RT"] = 0x71,    // F2
        ["RightThumb"] = 0x0D, // Enter
    };

    public int DiagonalDelayMs { get; set; } = 0;

    public int DirectionalDelayMs { get; set; } = 0;

    public bool DiagonalDelayHoldCardinals { get; set; } = false;

    [JsonIgnore]
    public static ushort KeyEnter { get; } = 0x0D;
    [JsonIgnore]
    public static ushort KeyNumpad5 { get; } = 0x65;
    [JsonIgnore]
    public static ushort KeyMultiply { get; } = 0x6A;
    [JsonIgnore]
    public static ushort KeyDivide { get; } = 0x6F;
    [JsonIgnore]
    public static ushort KeyF1 { get; } = 0x70;
    [JsonIgnore]
    public static ushort KeyF2 { get; } = 0x71;
    [JsonIgnore]
    public static ushort KeyNumpad2 { get; } = 0x62;
    [JsonIgnore]
    public static ushort KeyNumpad4 { get; } = 0x64;
    [JsonIgnore]
    public static ushort KeyNumpad6 { get; } = 0x66;
    [JsonIgnore]
    public static ushort KeyNumpad8 { get; } = 0x68;
    [JsonIgnore]
    public static ushort KeyNumpad7 { get; } = 0x67;
    [JsonIgnore]
    public static ushort KeyNumpad9 { get; } = 0x69;
    [JsonIgnore]
    public static ushort KeyNumpad1 { get; } = 0x61;
    [JsonIgnore]
    public static ushort KeyNumpad3 { get; } = 0x63;
    [JsonIgnore]
    public static ushort KeyUp { get; } = 0x26;
    [JsonIgnore]
    public static ushort KeyDown { get; } = 0x28;
    [JsonIgnore]
    public static ushort KeyLeft { get; } = 0x25;
    [JsonIgnore]
    public static ushort KeyRight { get; } = 0x27;

    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, s_jsonOptions);
    }

    public static KeyMapProfile? FromJson(string json)
    {
        return JsonSerializer.Deserialize<KeyMapProfile>(json);
    }
}
