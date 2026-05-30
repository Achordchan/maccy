using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    public string PackageKind { get; init; } = string.Empty;
    public string PackageRuntime { get; init; } = string.Empty;
    public string PackageUrl { get; init; } = string.Empty;
    public string PackageSha256 { get; init; } = string.Empty;
    public long PackageSize { get; init; }

    public bool HasSupportedPackage =>
        string.Equals(PackageKind, "zip", StringComparison.OrdinalIgnoreCase)
        && string.Equals(PackageRuntime, UpdateService.CurrentRuntime, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(PackageUrl)
        && !string.IsNullOrWhiteSpace(PackageSha256);
}

public sealed class PreparedPackageUpdate
{
    public string Version { get; init; } = string.Empty;
    public string PackagePath { get; init; } = string.Empty;
    public string StagingPath { get; init; } = string.Empty;
    public string PackageManifestPath { get; init; } = string.Empty;
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
    public static string CurrentRuntime { get; } = DetectRuntime();

    private static readonly TimeSpan ManifestAttemptTimeout = TimeSpan.FromSeconds(8);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly HttpStatusCode[] TransientStatusCodes =
    [
        HttpStatusCode.RequestTimeout,
        HttpStatusCode.TooManyRequests,
        HttpStatusCode.InternalServerError,
        HttpStatusCode.BadGateway,
        HttpStatusCode.ServiceUnavailable,
        HttpStatusCode.GatewayTimeout,
    ];

    private static readonly string[] ProtectedDataRoots =
    [
        "images",
        "files",
        "blobs",
        "sync",
        "updates",
    ];

    private static readonly string[] ProtectedDataFiles =
    [
        "settings.json",
        "history.json",
        "preferences_last_error.txt",
        "sync_last_error.txt",
        "auth_last_error.txt",
        "capture_debug.log",
    ];

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

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct, Action<string>? status = null)
    {
        if (string.IsNullOrWhiteSpace(_manifestUrl))
            return UpdateCheckResult.Error("manifest url is empty");

        UpdateManifest? manifest;
        try
        {
            status?.Invoke("正在检查是否有新版本...");
            manifest = await ExecuteWithRetryAsync(
                maxAttempts: 3,
                delayFactory: attempt => TimeSpan.FromMilliseconds(700 * attempt),
                operation: async (attempt, retryCt) =>
                {
                    if (attempt > 1)
                        status?.Invoke($"更新服务器响应较慢，正在第 {attempt} 次重试...");

                    using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(retryCt);
                    attemptCts.CancelAfter(ManifestAttemptTimeout);
                    using var req = new HttpRequestMessage(HttpMethod.Get, _manifestUrl);
                    try
                    {
                        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token);
                        resp.EnsureSuccessStatusCode();
                        await using var stream = await resp.Content.ReadAsStreamAsync(attemptCts.Token);
                        return await JsonSerializer.DeserializeAsync<UpdateManifest>(stream, JsonOptions, attemptCts.Token);
                    }
                    catch (OperationCanceledException) when (!retryCt.IsCancellationRequested)
                    {
                        throw new TimeoutException("检查更新超时");
                    }
                },
                shouldRetry: ShouldRetryManifest,
                ct: ct);
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
        var package = manifest.Latest.Package;

        if (string.IsNullOrWhiteSpace(installerUrl) && string.IsNullOrWhiteSpace(package?.Url))
            return UpdateCheckResult.Error("manifest missing update url");

        var info = new UpdateInfo
        {
            Version = latestVersion,
            Mandatory = manifest.Latest.Mandatory,
            Notes = manifest.Latest.Notes ?? string.Empty,
            InstallerUrl = installerUrl,
            InstallerSha256 = installerSha256,
            InstallerSize = manifest.Latest.Installer?.Size ?? 0,
            PackageKind = (package?.Kind ?? string.Empty).Trim(),
            PackageRuntime = (package?.Runtime ?? string.Empty).Trim(),
            PackageUrl = (package?.Url ?? string.Empty).Trim(),
            PackageSha256 = (package?.Sha256 ?? string.Empty).Trim(),
            PackageSize = package?.Size ?? 0,
        };

        return UpdateCheckResult.Available(info);
    }

    public async Task<string> DownloadInstallerAsync(UpdateInfo update, string downloadFolder, Action<long, long?>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(downloadFolder);

        if (string.IsNullOrWhiteSpace(update.InstallerUrl))
            throw new InvalidOperationException("manifest missing installer url");

        var fileName = "maccy-" + update.Version + "-setup.exe";
        var target = Path.Combine(downloadFolder, fileName);

        await DownloadAndVerifyFileAsync(
            update.InstallerUrl,
            target,
            update.InstallerSha256,
            update.InstallerSize,
            progress,
            ct);

        return target;
    }

    public async Task<PreparedPackageUpdate> DownloadPackageAsync(UpdateInfo update, string updateRoot, Action<long, long?>? progress, CancellationToken ct)
    {
        if (!update.HasSupportedPackage)
            throw new InvalidOperationException("manifest package is not supported by this client");

        var packageDir = Path.Combine(updateRoot, "packages");
        var stagingRoot = Path.Combine(updateRoot, "staging");
        Directory.CreateDirectory(packageDir);
        Directory.CreateDirectory(stagingRoot);

        var packagePath = Path.Combine(packageDir, "maccy-" + update.Version + "-" + update.PackageRuntime + ".zip");
        await DownloadAndVerifyFileAsync(
            update.PackageUrl,
            packagePath,
            update.PackageSha256,
            update.PackageSize,
            progress,
            ct);

        return await PreparePackageAsync(update, packagePath, stagingRoot, ct);
    }

    private async Task<PreparedPackageUpdate> PreparePackageAsync(UpdateInfo update, string packagePath, string stagingRoot, CancellationToken ct)
    {
        var stagingPath = Path.Combine(stagingRoot, update.Version + "-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(stagingPath))
            Directory.Delete(stagingPath, recursive: true);

        Directory.CreateDirectory(stagingPath);
        ExtractPackageSafely(packagePath, stagingPath);

        ct.ThrowIfCancellationRequested();

        var manifestPath = Path.Combine(stagingPath, "maccy-package.json");
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException("package manifest missing");

        var packageManifest = await ReadPackageManifestAsync(manifestPath, ct);
        ValidatePackageManifest(update, stagingPath, packageManifest);

        return new PreparedPackageUpdate
        {
            Version = update.Version,
            PackagePath = packagePath,
            StagingPath = stagingPath,
            PackageManifestPath = manifestPath,
        };
    }

    private static void ExtractPackageSafely(string packagePath, string stagingPath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        foreach (var entry in archive.Entries)
        {
            var relativePath = NormalizePackagePath(entry.FullName);
            var destination = GetPathInsideRoot(stagingPath, relativePath);

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: false);
        }
    }

    private static async Task<UpdatePackageManifest> ReadPackageManifestAsync(string manifestPath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<UpdatePackageManifest>(stream, JsonOptions, ct);
        return manifest ?? throw new InvalidOperationException("package manifest invalid");
    }

    private static void ValidatePackageManifest(UpdateInfo update, string stagingPath, UpdatePackageManifest manifest)
    {
        if (!string.Equals(manifest.AppId, "maccy", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("package app id mismatch");
        if (!string.Equals(NormalizeVersion(manifest.Version), update.Version, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("package version mismatch");
        if (!string.Equals(manifest.Runtime, update.PackageRuntime, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("package runtime mismatch");
        if (manifest.Files.Count == 0)
            throw new InvalidOperationException("package file list is empty");

        var hasExe = false;
        foreach (var file in manifest.Files)
        {
            var relativePath = NormalizePackagePath(file.Path);
            if (IsProtectedRelativePath(relativePath))
                throw new InvalidOperationException("package contains protected data path: " + file.Path);

            if (string.Equals(ToManifestPath(relativePath), "maccy.exe", StringComparison.OrdinalIgnoreCase))
                hasExe = true;

            var absolutePath = GetPathInsideRoot(stagingPath, relativePath);
            if (!File.Exists(absolutePath))
                throw new InvalidOperationException("package file missing: " + file.Path);

            var info = new FileInfo(absolutePath);
            if (file.Size >= 0 && file.Size != info.Length)
                throw new InvalidOperationException("package file size mismatch: " + file.Path);

            if (string.IsNullOrWhiteSpace(file.Sha256))
                throw new InvalidOperationException("package file sha256 missing: " + file.Path);

            var actualSha256 = ComputeSha256Hex(absolutePath);
            if (!string.Equals(actualSha256, file.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("package file sha256 mismatch: " + file.Path);
        }

        if (!hasExe)
            throw new InvalidOperationException("package missing maccy.exe");
    }

    private async Task DownloadAndVerifyFileAsync(
        string url,
        string target,
        string expectedSha256,
        long expectedSize,
        Action<long, long?>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".download";

        try
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
        catch
        {
        }

        await ExecuteWithRetryAsync(
            maxAttempts: 3,
            delayFactory: attempt => TimeSpan.FromSeconds(attempt),
            operation: async (_, retryCt) =>
            {
                try
                {
                    if (File.Exists(temp))
                        File.Delete(temp);
                }
                catch
                {
                }

                long totalRead = 0;
                long? total = expectedSize > 0 ? expectedSize : null;

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, retryCt);
                resp.EnsureSuccessStatusCode();

                total = resp.Content.Headers.ContentLength ?? total;

                await using (var input = await resp.Content.ReadAsStreamAsync(retryCt))
                await using (var output = File.Create(temp))
                {
                    var buffer = new byte[81920];
                    while (true)
                    {
                        var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), retryCt);
                        if (read <= 0)
                            break;

                        await output.WriteAsync(buffer.AsMemory(0, read), retryCt);
                        totalRead += read;
                        progress?.Invoke(totalRead, total);
                    }
                }

                return true;
            },
            shouldRetry: ShouldRetryDownload,
            ct: ct);

        if (expectedSize > 0)
        {
            var actualSize = new FileInfo(temp).Length;
            if (actualSize != expectedSize)
            {
                TryDeleteFile(temp);
                throw new InvalidOperationException("file size mismatch");
            }
        }

        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            var actual = ComputeSha256Hex(temp);
            if (!string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteFile(temp);
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
            v = v[1..];

        v = v.Trim();

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

    private static string NormalizePackagePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("package path is empty");

        var normalized = path.Trim().Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized))
            throw new InvalidOperationException("package path must be relative: " + path);

        normalized = normalized.TrimStart(Path.DirectorySeparatorChar);
        var parts = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(x => x == "." || x == ".."))
            throw new InvalidOperationException("package path is unsafe: " + path);

        return Path.Combine(parts);
    }

    private static string GetPathInsideRoot(string root, string relativePath)
    {
        var rootFull = Path.GetFullPath(root);
        var combined = Path.GetFullPath(Path.Combine(rootFull, relativePath));
        var prefix = rootFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("package path escapes root: " + relativePath);
        return combined;
    }

    private static bool IsProtectedRelativePath(string relativePath)
    {
        var path = ToManifestPath(relativePath).TrimStart('/').ToLowerInvariant();
        if (ProtectedDataFiles.Contains(path, StringComparer.OrdinalIgnoreCase))
            return true;

        return ProtectedDataRoots.Any(root =>
            string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase));
    }

    private static string ToManifestPath(string relativePath)
    {
        return relativePath.Replace('\\', '/');
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    private static async Task<T> ExecuteWithRetryAsync<T>(
        int maxAttempts,
        Func<int, TimeSpan> delayFactory,
        Func<int, CancellationToken, Task<T>> operation,
        Func<Exception, bool> shouldRetry,
        CancellationToken ct)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                return await operation(attempt, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < maxAttempts && shouldRetry(ex))
            {
                lastError = ex;
                await Task.Delay(delayFactory(attempt), ct);
            }
            catch (Exception ex)
            {
                lastError = ex;
                break;
            }
        }

        throw lastError ?? new InvalidOperationException("retry operation failed");
    }

    private static bool ShouldRetryManifest(Exception ex)
    {
        return IsTransientHttpFailure(ex) || ex is TimeoutException;
    }

    private static bool ShouldRetryDownload(Exception ex)
    {
        return IsTransientHttpFailure(ex) || ex is IOException;
    }

    private static bool IsTransientHttpFailure(Exception ex)
    {
        if (ex is HttpRequestException hre)
        {
            if (hre.StatusCode is null)
                return true;

            return Array.IndexOf(TransientStatusCodes, hre.StatusCode.Value) >= 0;
        }

        return false;
    }

    private static string DetectRuntime()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "win-x64",
                Architecture.Arm64 => "win-arm64",
                Architecture.X86 => "win-x86",
                _ => "win",
            };
        }

        return RuntimeInformation.RuntimeIdentifier;
    }
}
