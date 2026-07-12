using System;
using System.Collections.Generic;
using System.IO;
using J2MEGamepad.Services;
using Newtonsoft.Json;

namespace J2MEGamepad.Models;

public class DInputMapping
{
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        MaxDepth = 32
    };

    public Dictionary<string, int> ActionToButton { get; set; } = new()
    {
        ["X"] = 0,
        ["A"] = 1,
        ["B"] = 2,
        ["Y"] = 3,
        ["LB"] = 4,
        ["RB"] = 5,
        ["LT"] = 6,
        ["RT"] = 7,
        ["RightThumb"] = 11,
        ["Start"] = 9,
        ["Back"] = 8,
        ["LeftThumb"] = 10,
        ["DPadUp"] = 12,
        ["DPadDown"] = 13,
        ["DPadLeft"] = 14,
        ["DPadRight"] = 15,
    };

    public Dictionary<int, string> BuildReverseMap()
    {
        var rev = new Dictionary<int, string>();
        foreach (var kvp in ActionToButton)
        {
            rev[kvp.Value] = kvp.Key;
        }
        return rev;
    }

    public string ToJson()
    {
        return JsonConvert.SerializeObject(this, Formatting.Indented);
    }

    public static DInputMapping? FromJson(string json)
    {
        return JsonConvert.DeserializeObject<DInputMapping>(json, JsonSettings);
    }

    public static string GetFilePath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "J2MEGamepad", "dinput_mapping.json");
    }

    public void Save()
    {
        var path = GetFilePath();
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, ToJson());
    }

    public static DInputMapping Load()
    {
        var path = GetFilePath();
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                return FromJson(json) ?? new DInputMapping();
            }
            catch (Exception ex) { LogHelper.Error("DInputMapping", "Load", ex); }
        }
        return new DInputMapping();
    }
}
