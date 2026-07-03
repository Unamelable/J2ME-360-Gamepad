using System;
using System.IO;
using System.Text.Json;

namespace J2MEGamepad.Models;

public class DInputCalibration
{
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

    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, s_jsonOptions);
    }

    public static DInputCalibration? FromJson(string json)
    {
        return JsonSerializer.Deserialize<DInputCalibration>(json);
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
            catch { }
        }
        return new DInputCalibration();
    }
}
