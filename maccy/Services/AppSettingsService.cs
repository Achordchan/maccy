using System;
using System.Net;
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
        if (string.IsNullOrWhiteSpace(current))
            return false;

        var legacy = NormalizeBaseUrl(ServerDefaults.LegacyOfficialSyncBaseUrl);
        if (string.Equals(current, legacy, StringComparison.OrdinalIgnoreCase))
        {
            settings.NasAgentBaseUrl = ServerDefaults.OfficialSyncBaseUrl;
            return true;
        }

        if (TryUpgradeOfficialDomainToHttps(current, out var upgraded))
        {
            settings.NasAgentBaseUrl = upgraded;
            return true;
        }

        if (TryMigratePrivateNasEndpoint(settings, current, out var migrated))
        {
            settings.NasAgentBaseUrl = migrated;
            return true;
        }

        return false;
    }

    private static string NormalizeBaseUrl(string? value)
    {
        return (value ?? string.Empty).Trim().TrimEnd('/');
    }

    private static bool TryUpgradeOfficialDomainToHttps(string current, out string upgraded)
    {
        upgraded = string.Empty;

        if (!Uri.TryCreate(current, UriKind.Absolute, out var currentUri))
            return false;
        if (!Uri.TryCreate(ServerDefaults.OfficialSyncBaseUrl, UriKind.Absolute, out var officialUri))
            return false;

        var sameHost = string.Equals(currentUri.Host, officialUri.Host, StringComparison.OrdinalIgnoreCase);
        var isHttp = string.Equals(currentUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        var defaultPort = currentUri.IsDefaultPort || currentUri.Port == 80;
        var noPath = string.IsNullOrWhiteSpace(currentUri.AbsolutePath) || currentUri.AbsolutePath == "/";

        if (!sameHost || !isHttp || !defaultPort || !noPath)
            return false;

        upgraded = ServerDefaults.OfficialSyncBaseUrl;
        return true;
    }

    private static bool TryMigratePrivateNasEndpoint(AppSettings settings, string current, out string migrated)
    {
        migrated = string.Empty;

        if (!HasAuthSession(settings))
            return false;
        if (!Uri.TryCreate(current, UriKind.Absolute, out var currentUri))
            return false;

        var noPath = string.IsNullOrWhiteSpace(currentUri.AbsolutePath) || currentUri.AbsolutePath == "/";
        if (!noPath)
            return false;
        if (!IsPrivateOrLoopbackHost(currentUri.Host))
            return false;

        migrated = ServerDefaults.OfficialSyncBaseUrl;
        return true;
    }

    private static bool HasAuthSession(AppSettings settings)
    {
        return !string.IsNullOrWhiteSpace(settings.AuthAccessToken)
            || !string.IsNullOrWhiteSpace(settings.AuthRefreshToken);
    }

    private static bool IsPrivateOrLoopbackHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!IPAddress.TryParse(host, out var ip))
            return false;
        if (IPAddress.IsLoopback(ip))
            return true;

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 10)
                return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                return true;
            if (b[0] == 192 && b[1] == 168)
                return true;
            if (b[0] == 169 && b[1] == 254)
                return true;
            return false;
        }

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;

        return false;
    }
}
