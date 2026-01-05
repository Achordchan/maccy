using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace maccy.Services;

public enum UpdateStatus
{
    UpToDate,
    UpdateAvailable,
    Error,
}

public sealed class UpdateInfo
{
    public string Version { get; init; } = string.Empty;
    public bool Mandatory { get; init; }
    public string Notes { get; init; } = string.Empty;
    public string InstallerUrl { get; init; } = string.Empty;
    public string InstallerSha256 { get; init; } = string.Empty;
    public long InstallerSize { get; init; }
}

public sealed class UpdateCheckResult
{
    public UpdateStatus Status { get; init; }
    public UpdateInfo? Update { get; init; }
    public string? ErrorMessage { get; init; }

    public static UpdateCheckResult UpToDate() => new() { Status = UpdateStatus.UpToDate };
    public static UpdateCheckResult Available(UpdateInfo info) => new() { Status = UpdateStatus.UpdateAvailable, Update = info };
    public static UpdateCheckResult Error(string message) => new() { Status = UpdateStatus.Error, ErrorMessage = message };
}

public sealed class UpdateService
{
    private readonly HttpClient _http;
    private readonly string _manifestUrl;

    public UpdateService(string manifestUrl, HttpClient? httpClient = null)
    {
        _manifestUrl = manifestUrl;
        _http = httpClient ?? new HttpClient();
    }

    public string GetCurrentVersionString()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrWhiteSpace(info.ProductVersion))
                    return info.ProductVersion;
                if (!string.IsNullOrWhiteSpace(info.FileVersion))
                    return info.FileVersion;
            }
        }
        catch
        {
        }

        try
        {
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            return v is null ? "0.0.0" : v.ToString();
        }
        catch
        {
            return "0.0.0";
        }
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_manifestUrl))
            return UpdateCheckResult.Error("manifest url is empty");

        UpdateManifest? manifest;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _manifestUrl);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            manifest = await JsonSerializer.DeserializeAsync<UpdateManifest>(stream, cancellationToken: ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return UpdateCheckResult.Error(ex.Message);
        }

        if (manifest is null || manifest.Latest is null)
            return UpdateCheckResult.Error("invalid manifest");

        var latestVersion = NormalizeVersion(manifest.Latest.Version);
        if (string.IsNullOrWhiteSpace(latestVersion))
            return UpdateCheckResult.Error("manifest missing version");

        var current = NormalizeVersion(GetCurrentVersionString());

        if (!TryParseVersion(latestVersion, out var latestV) || !TryParseVersion(current, out var currentV))
            return UpdateCheckResult.Error("version parse failed (latest=" + latestVersion + ", current=" + current + ")");

        if (latestV <= currentV)
            return UpdateCheckResult.UpToDate();

        var installerUrl = manifest.Latest.Installer?.Url ?? string.Empty;
        var installerSha256 = (manifest.Latest.Installer?.Sha256 ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(installerUrl))
            return UpdateCheckResult.Error("manifest missing installer url");

        var info = new UpdateInfo
        {
            Version = latestVersion,
            Mandatory = manifest.Latest.Mandatory,
            Notes = manifest.Latest.Notes ?? string.Empty,
            InstallerUrl = installerUrl,
            InstallerSha256 = installerSha256,
            InstallerSize = manifest.Latest.Installer?.Size ?? 0,
        };

        return UpdateCheckResult.Available(info);
    }

    public async Task<string> DownloadInstallerAsync(UpdateInfo update, string downloadFolder, Action<long, long?>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(downloadFolder);

        var fileName = "maccy-" + update.Version + "-setup.exe";
        var target = Path.Combine(downloadFolder, fileName);
        var temp = target + ".download";

        try
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
        catch
        {
        }

        long totalRead = 0;
        long? total = null;

        using var req = new HttpRequestMessage(HttpMethod.Get, update.InstallerUrl);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        total = resp.Content.Headers.ContentLength;

        await using (var input = await resp.Content.ReadAsStreamAsync(ct))
        await using (var output = File.Create(temp))
        {
            var buffer = new byte[81920];
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read <= 0)
                    break;

                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                totalRead += read;
                progress?.Invoke(totalRead, total);
            }
        }

        if (!string.IsNullOrWhiteSpace(update.InstallerSha256))
        {
            var actual = ComputeSha256Hex(temp);
            if (!string.Equals(actual, update.InstallerSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Delete(temp);
                }
                catch
                {
                }
                throw new InvalidOperationException("sha256 mismatch");
            }
        }

        try
        {
            if (File.Exists(target))
                File.Delete(target);
        }
        catch
        {
        }

        File.Move(temp, target);
        return target;
    }

    private static string ComputeSha256Hex(string filePath)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(filePath);
        var hash = sha.ComputeHash(fs);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static string NormalizeVersion(string? v)
    {
        if (string.IsNullOrWhiteSpace(v))
            return string.Empty;

        v = v.Trim();
        if (v.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            v = v.Substring(1);

        v = v.Trim();

        // Extract a numeric version token so ProductVersion like "1.0.0+abc" or "1.0.0.0 (dev)" won't break parsing.
        var m = Regex.Match(v, @"\d+(?:\.\d+){0,3}");
        return m.Success ? m.Value : string.Empty;
    }

    private static bool TryParseVersion(string v, out Version version)
    {
        if (Version.TryParse(v, out var parsed) && parsed is not null)
        {
            version = parsed;
            return true;
        }

        var parts = v.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 1)
        {
            if (!int.TryParse(parts[0], out var major))
                goto Failed;

            var minor = 0;
            var build = 0;
            var revision = 0;

            if (parts.Length >= 2 && !int.TryParse(parts[1], out minor))
                goto Failed;
            if (parts.Length >= 3 && !int.TryParse(parts[2], out build))
                goto Failed;
            if (parts.Length >= 4 && !int.TryParse(parts[3], out revision))
                goto Failed;

            version = parts.Length switch
            {
                1 => new Version(major, 0),
                2 => new Version(major, minor),
                3 => new Version(major, minor, build),
                _ => new Version(major, minor, build, revision),
            };

            return true;
        }

        Failed:

        version = new Version(0, 0, 0);
        return false;
    }
}
