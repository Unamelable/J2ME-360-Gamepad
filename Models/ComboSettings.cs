using System;
using System.Collections.Generic;
using System.IO;
using J2MEGamepad.Services;
using Newtonsoft.Json;

namespace J2MEGamepad.Models;

public class ComboSettings
{
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        MaxDepth = 32
    };

    public Dictionary<string, List<ushort>> Actions { get; set; } = new();
    public Dictionary<string, string> OSDNames { get; set; } = new();
    public Dictionary<string, string> ExecPaths { get; set; } = new();

    public string ToJson()
    {
        return JsonConvert.SerializeObject(this, Formatting.Indented);
    }

    public static ComboSettings? FromJson(string json)
    {
        return JsonConvert.DeserializeObject<ComboSettings>(json, JsonSettings);
    }

    public static string GetFilePath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "J2MEGamepad", "combo_settings.json");
    }

    public void Save()
    {
        var path = GetFilePath();
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, ToJson());
    }

    public static ComboSettings Load()
    {
        var path = GetFilePath();
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                return FromJson(json) ?? new ComboSettings();
            }
            catch (Exception ex) { LogHelper.Error("ComboSettings", "Load", ex); }
        }
        return new ComboSettings();
    }
}
