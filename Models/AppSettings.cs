using System;
using System.IO;
using J2MEGamepad.Services;
using Newtonsoft.Json;

namespace J2MEGamepad.Models;

public class AppSettings
{
    public int Version { get; set; }

    public bool StartMinimized { get; set; }
    public bool TerminateIfKemulatorClosed { get; set; }
    public bool TerminateWarningHidden { get; set; }
    public int DiagonalDelayMs { get; set; }
    public int DirectionalDelayMs { get; set; }
    public bool DiagonalDelayHold { get; set; } = true;
    public bool DiagonalDelayPerProfile { get; set; }
    public bool BackCycles { get; set; }
    public bool SkipDefault { get; set; }
    public bool ComboPerProfile { get; set; }
    public bool DisableComboModifierOSD { get; set; }
    public bool ComboConfirmationHold { get; set; }
    public bool LeftThumbIsComboModifier { get; set; }
    public bool RightThumbIsComboModifier { get; set; }
    public bool FirstRunCompleted { get; set; }
    public bool CustomWarningHidden { get; set; }
    public int KeysFontSize { get; set; } = 18;
    public double KeysWindowWidth { get; set; } = 593;
    public double KeysWindowHeight { get; set; } = 682;

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        MaxDepth = 32,
        Formatting = Formatting.Indented
    };

    public string ToJson() => JsonConvert.SerializeObject(this, JsonSettings);

    public static AppSettings? FromJson(string json) => JsonConvert.DeserializeObject<AppSettings>(json, JsonSettings);

    public static string GetFilePath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "J2MEGamepad", "app_settings.json");
    }

    public void Save()
    {
        var path = GetFilePath();
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, ToJson());
    }

    public static AppSettings Load()
    {
        var path = GetFilePath();
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                var loaded = FromJson(json) ?? new AppSettings();
                if (loaded.Version == 0)
                {
                    MigrateFromOldFiles(loaded);
                    loaded.Version = 1;
                    loaded.Save();
                }
                return loaded;
            }
            catch (Exception ex) { LogHelper.Error("AppSettings", "Load", ex); }
        }
        var settings = new AppSettings();
        MigrateFromOldFiles(settings);
        settings.Version = 1;
        settings.Save();
        return settings;
    }

    private static string SettingsDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "J2MEGamepad");

    private static bool ReadBool(string file)
    {
        var path = Path.Combine(SettingsDir(), file);
        return File.Exists(path) && bool.TryParse(File.ReadAllText(path).Trim(), out bool val) && val;
    }

    private static int ReadInt(string file, int defaultValue = 0)
    {
        var path = Path.Combine(SettingsDir(), file);
        if (File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out int val))
            return val;
        return defaultValue;
    }

    private static void MigrateFromOldFiles(AppSettings s)
    {
        try
        {
            s.StartMinimized = ReadBool("start_minimized.txt");
            s.TerminateIfKemulatorClosed = ReadBool("terminate.txt");
            s.TerminateWarningHidden = ReadBool("terminate_warning_hidden.txt");
            s.DiagonalDelayMs = ReadInt("diagdelay.txt");
            s.DirectionalDelayMs = ReadInt("directional_delay.txt");
            s.DiagonalDelayHold = ReadBool("diaghold.txt");
            s.DiagonalDelayPerProfile = ReadBool("diagperprofile.txt");
            s.ComboPerProfile = ReadBool("comboperprofile.txt");
            s.DisableComboModifierOSD = ReadBool("disable_combo_modifier_osd.txt");
            s.FirstRunCompleted = File.Exists(Path.Combine(SettingsDir(), "firstrun.txt"));
            s.CustomWarningHidden = ReadBool("custom_warning_hidden.txt");

            var fontFile = Path.Combine(SettingsDir(), "keysfont.txt");
            if (File.Exists(fontFile) && int.TryParse(File.ReadAllText(fontFile).Trim(), out int fontSize))
                s.KeysFontSize = Math.Max(8, Math.Min(36, fontSize));

            var sizeFile = Path.Combine(SettingsDir(), "keyssize.txt");
            if (File.Exists(sizeFile))
            {
                var parts = File.ReadAllText(sizeFile).Trim().Split('x');
                if (parts.Length == 2 && double.TryParse(parts[0], out double sw) && double.TryParse(parts[1], out double sh))
                {
                    s.KeysWindowWidth = Math.Max(sw, 300);
                    s.KeysWindowHeight = Math.Max(sh, 200);
                }
            }
        }
        catch (Exception ex) { LogHelper.Error("AppSettings", "MigrateFromOldFiles", ex); }
    }
}
