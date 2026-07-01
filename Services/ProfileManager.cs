using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using J2MEGamepad.Models;

namespace J2MEGamepad.Services;

public class ProfileManager : IDisposable
{
    private readonly string _profilesDir;
    private FileSystemWatcher? _watcher;
    private readonly object _lock = new();
    private bool _isReloading;
    private List<KeyMapProfile> _profiles = new();

    public List<KeyMapProfile> Profiles
    {
        get
        {
            lock (_lock)
                return new List<KeyMapProfile>(_profiles);
        }
    }
    public int CurrentProfileIndex { get; set; } = 0;

    public KeyMapProfile CurrentProfile
    {
        get
        {
            lock (_lock)
                return _profiles.Count > 0 ? _profiles[CurrentProfileIndex] : new KeyMapProfile();
        }
    }

    public bool IsDefaultProfile
    {
        get
        {
            lock (_lock)
                return CurrentProfile.Name == "Default";
        }
    }

    public int UserProfileCount
    {
        get
        {
            lock (_lock)
                return _profiles.Count(p => p.Name != "Default");
        }
    }

    public event Action? ProfilesChanged;

    public ProfileManager()
    {
        _profilesDir = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
            "J2MEGamepad", "profiles");
        Directory.CreateDirectory(_profilesDir);
        LoadProfiles();
        StartWatching();
    }

    private void StartWatching()
    {
        _watcher = new FileSystemWatcher(_profilesDir, "*.json")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };
        _watcher.Created += (_, _) => ReloadAndNotify();
        _watcher.Deleted += (_, _) => ReloadAndNotify();
        _watcher.Changed += (_, _) => ReloadAndNotify();
        _watcher.Renamed += (_, _) => ReloadAndNotify();
    }

    private void ReloadAndNotify()
    {
        if (_isReloading) return;
        _isReloading = true;
        try
        {
            LoadProfiles();
            ProfilesChanged?.Invoke();
        }
        finally
        {
            _isReloading = false;
        }
    }

    public void LoadProfiles()
    {
        lock (_lock)
        {
            _profiles.Clear();
            _profiles.Add(new KeyMapProfile());

            if (Directory.Exists(_profilesDir))
            {
                foreach (var file in Directory.GetFiles(_profilesDir, "*.json"))
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var profile = KeyMapProfile.FromJson(json);
                        if (profile != null && profile.Name != "Default")
                            _profiles.Add(profile);
                    }
                    catch { }
                }
            }

            if (_profiles.Count == 0)
                _profiles.Add(new KeyMapProfile());
        }
    }

    public void SaveProfile(KeyMapProfile profile)
    {
        lock (_lock)
        {
            var existing = _profiles.FirstOrDefault(p => p.Name == profile.Name);
            if (existing != null)
                _profiles.Remove(existing);
            _profiles.Add(profile);
            SaveToFile(profile);
        }
    }

    public void DeleteProfile(string name)
    {
        if (name == "Default") return;
        lock (_lock)
        {
            var profile = _profiles.FirstOrDefault(p => p.Name == name);
            if (profile != null)
            {
                _profiles.Remove(profile);
                var path = GetProfilePath(name);
                if (File.Exists(path))
                    File.Delete(path);
            }
        }
    }

    public int CycleForward(bool skipDefault)
    {
        lock (_lock)
        {
            if (_profiles.Count <= 1) return CurrentProfileIndex;
            int attempts = 0;
            do
            {
                CurrentProfileIndex = (CurrentProfileIndex + 1) % _profiles.Count;
                attempts++;
            } while (skipDefault
                     && _profiles[CurrentProfileIndex].Name == "Default"
                     && attempts < _profiles.Count
                     && UserProfileCount > 0);
            return CurrentProfileIndex;
        }
    }

    public int CycleBackward(bool skipDefault)
    {
        lock (_lock)
        {
            if (_profiles.Count <= 1) return CurrentProfileIndex;
            int attempts = 0;
            do
            {
                CurrentProfileIndex = (CurrentProfileIndex - 1 + _profiles.Count) % _profiles.Count;
                attempts++;
            } while (skipDefault
                     && _profiles[CurrentProfileIndex].Name == "Default"
                     && attempts < _profiles.Count
                     && UserProfileCount > 0);
            return CurrentProfileIndex;
        }
    }

    public void RenameProfile(string oldName, string newName)
    {
        if (oldName == "Default" || newName == "Default") return;
        lock (_lock)
        {
            var profile = _profiles.FirstOrDefault(p => p.Name == oldName);
            if (profile == null) return;

            var oldPath = GetProfilePath(oldName);
            if (File.Exists(oldPath))
                File.Delete(oldPath);

            profile.Name = newName;
            SaveToFile(profile);
        }
    }

    public string ExportProfile(string name)
    {
        lock (_lock)
        {
            var profile = _profiles.FirstOrDefault(p => p.Name == name);
            if (profile == null) return "";
            return profile.ToJson();
        }
    }

    public void ImportProfile(string json, string? renameTo = null)
    {
        var profile = KeyMapProfile.FromJson(json);
        if (profile == null) return;
        profile.Name = renameTo ?? profile.Name;
        SaveProfile(profile);
    }

    public string GetNextAvailableName(string baseName)
    {
        lock (_lock)
        {
            int counter = 1;
            string name;
            do
            {
                name = $"{baseName} {counter}";
                counter++;
            } while (_profiles.Any(p => p.Name == name));
            return name;
        }
    }

    private void SaveToFile(KeyMapProfile profile)
    {
        var path = GetProfilePath(profile.Name);
        File.WriteAllText(path, profile.ToJson());
    }

    private string GetProfilePath(string name)
    {
        return Path.Combine(_profilesDir, $"{SanitizeFileName(name)}.json");
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
    }
}
