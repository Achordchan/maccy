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
            {
                var migrated = MigrateLegacySyncBaseUrl(s);
                Current = s;
                if (migrated)
                    Save();
            }
        }
        catch
        {
        }
    }

    public void Save()
    {
        try
        {
            MigrateLegacySyncBaseUrl(Current);
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

    public void ClearAuthSession(bool clearUserEmail = false)
    {
        Update(s =>
        {
            s.AuthAccessToken = null;
            s.AuthRefreshToken = null;
            s.AuthIdToken = null;
            s.AuthExpiresAtUnixMs = 0;
            if (clearUserEmail)
                s.AuthUserEmail = null;
        });
    }

    private static bool MigrateLegacySyncBaseUrl(AppSettings settings)
    {
        var current = NormalizeBaseUrl(settings.NasAgentBaseUrl);
        var official = NormalizeBaseUrl(ServerDefaults.OfficialSyncBaseUrl);

        if (string.Equals(current, official, StringComparison.OrdinalIgnoreCase)
            && string.Equals(settings.NasAgentBaseUrl, ServerDefaults.OfficialSyncBaseUrl, StringComparison.Ordinal))
            return false;

        settings.NasAgentBaseUrl = ServerDefaults.OfficialSyncBaseUrl;
        return true;
    }

    private static string NormalizeBaseUrl(string? value)
    {
        return (value ?? string.Empty).Trim().TrimEnd('/');
    }

}
