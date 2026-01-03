using System;
using System.IO;
using System.Text.Json;

namespace maccy.Services;

public sealed class AppSettingsService
{
    private readonly string _filePath;

    public event Action? Changed;

    public AppSettings Current { get; private set; } = new();

    public AppSettingsService()
    {
        _filePath = Path.Combine(AppPaths.AppDataRoot, "settings.json");
    }

    public void Load()
    {
        if (!File.Exists(_filePath))
            return;

        try
        {
            var json = File.ReadAllText(_filePath);
            var s = JsonSerializer.Deserialize<AppSettings>(json);
            if (s is not null)
                Current = s;
        }
        catch
        {
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var json = JsonSerializer.Serialize(Current);
            File.WriteAllText(_filePath, json);
            Changed?.Invoke();
        }
        catch
        {
        }
    }

    public void Update(Action<AppSettings> mutator)
    {
        mutator(Current);
        Save();
    }
}
