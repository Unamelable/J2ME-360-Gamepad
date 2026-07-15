using System.Collections.Generic;
using Newtonsoft.Json;

namespace J2MEGamepad.Models;

public class KeyMapProfile
{
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        MaxDepth = 32
    };

    public string Name { get; set; } = "Default";

    public Dictionary<string, ushort> Mappings { get; set; } = new()
    {
        ["Y"] = 0x60,     // Numpad 0
        ["X"] = 0x70,     // F1
        ["A"] = 0x72,     // F3
        ["B"] = 0x71,     // F2
        ["LB"] = 0x6A,    // *
        ["RB"] = 0x6F,    // /
        ["LT"] = 0x70,    // F1
        ["RT"] = 0x71,    // F2
        ["RightThumb"] = 0x72, // F3
    };

    public int DiagonalDelayMs { get; set; } = 0;

    public int DirectionalDelayMs { get; set; } = 0;

    public bool DiagonalDelayHoldCardinals { get; set; } = false;

    public bool LeftThumbIsComboModifier { get; set; }
    public bool RightThumbIsComboModifier { get; set; }
    public Dictionary<string, List<ushort>> ComboActions { get; set; } = new();
    public Dictionary<string, string> ComboOSDNames { get; set; } = new();
    public Dictionary<string, string> ComboExecPaths { get; set; } = new();

    [JsonIgnore]
    public static ushort KeyEnter { get; } = 0x0D;
    [JsonIgnore]
    public static ushort KeyF3 { get; } = 0x72;
    [JsonIgnore]
    public static ushort KeyNumpad0 { get; } = 0x60;
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

    public ushort GetValueOrDefault(string key, ushort defaultValue)
    {
        ushort value;
        return Mappings.TryGetValue(key, out value) ? value : defaultValue;
    }

    public string ToJson()
    {
        return JsonConvert.SerializeObject(this, Formatting.Indented);
    }

    public static KeyMapProfile? FromJson(string json)
    {
        return JsonConvert.DeserializeObject<KeyMapProfile>(json, JsonSettings);
    }
}
