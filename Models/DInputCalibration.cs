using System;
using System.IO;
using J2MEGamepad.Services;
using Newtonsoft.Json;

namespace J2MEGamepad.Models;

public class DInputCalibration
{
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        MaxDepth = 32
    };

    public int DeadzonePercent { get; set; } = 40;
    public int CenterX { get; set; } = 32767;
    public int CenterY { get; set; } = 32767;
    public int UpX { get; set; } = 32767;
    public int UpY { get; set; }
    public int DownX { get; set; } = 32767;
    public int DownY { get; set; } = 65535;
    public int LeftX { get; set; }
    public int LeftY { get; set; } = 32767;
    public int RightX { get; set; } = 65535;
    public int RightY { get; set; } = 32767;
    public int UpLeftX { get; set; }
    public int UpLeftY { get; set; }
    public int UpRightX { get; set; } = 65535;
    public int UpRightY { get; set; }
    public int DownLeftX { get; set; }
    public int DownLeftY { get; set; } = 65535;
    public int DownRightX { get; set; } = 65535;
    public int DownRightY { get; set; } = 65535;

    public bool HasUserCalibration =>
        UpX != 32767 || UpY != 0 ||
        DownX != 32767 || DownY != 65535 ||
        LeftX != 0 || LeftY != 32767 ||
        RightX != 65535 || RightY != 32767 ||
        UpLeftX != 0 || UpLeftY != 0 ||
        UpRightX != 65535 || UpRightY != 0 ||
        DownLeftX != 0 || DownLeftY != 65535 ||
        DownRightX != 65535 || DownRightY != 65535;

    public string ToJson()
    {
        return JsonConvert.SerializeObject(this, Formatting.Indented);
    }

    public static DInputCalibration? FromJson(string json)
    {
        return JsonConvert.DeserializeObject<DInputCalibration>(json, JsonSettings);
    }

    public static string GetFilePath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "J2MEGamepad");
        return Path.Combine(dir, "dpad_calibration.json");
    }

    public void Save()
    {
        var path = GetFilePath();
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, ToJson());
    }

    public static DInputCalibration Load()
    {
        var path = GetFilePath();
        if (File.Exists(path))
        {
            try
            {
                return FromJson(File.ReadAllText(path)) ?? new DInputCalibration();
            }
            catch (Exception ex) { LogHelper.Error("DInputCalibration", "Load", ex); }
        }
        return new DInputCalibration();
    }
}
