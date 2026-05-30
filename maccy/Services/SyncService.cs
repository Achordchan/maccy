using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace maccy.Services;

public sealed class SyncService
{
    private const string SubscriptionExpiredError = "subscription expired";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly object TimingLogGate = new();

    private readonly ClipboardHistoryService _history;
    private readonly AppSettingsService _settings;
    private readonly ClipboardPersistenceService _persistence;
    private readonly SnapshotSqliteService _snapshot;
    private readonly AuthService _authing;

    public readonly record struct SyncProgress(string Code, string Message, int Percent);

    public sealed record SyncManifestInfo(
        string? Version,
        DateTimeOffset? UpdatedAt,
        string? SnapshotSha256,
        long? SnapshotSize,
        string? RawJson);

    public sealed record SyncDecision(
        SyncDirection Direction,
        string LocalFingerprint,
        SyncManifestInfo Manifest,
        string Reason);

    public SyncService(ClipboardHistoryService history, AppSettingsService settings, ClipboardPersistenceService persistence)
    {
        _history = history;
        _settings = settings;
        _persistence = persistence;
        _snapshot = new SnapshotSqliteService();
        _authing = new AuthService();
    }

    public async Task<UploadSnapshotResult?> UploadAsync(
        CancellationToken ct,
        Action<SyncProgress>? reportProgress = null,
        string reason = "auto")
    {
        await Gate.WaitAsync(ct);
        var startedAt = Stopwatch.StartNew();
        var snapshotPath = string.Empty;
        try
        {
            reportProgress?.Invoke(new SyncProgress("prepare_local", "准备本地数据", 30));
            var (baseUrl, token) = await GetConnectionAsync(ct);
            var client = new NasAgentClient(baseUrl);

            var dir = Path.Combine(AppPaths.AppDataRoot, "sync", "tmp");
            Directory.CreateDirectory(dir);
            snapshotPath = Path.Combine(dir, "snapshot-" + Guid.NewGuid().ToString("N") + ".sqlite");
            var exportItems = _history.Items.ToList();
            var exportSettings = CloneSettings(_settings.Current);
            WriteTimingLog("upload", "snapshot_source_ready", startedAt.ElapsedMilliseconds, $"{reason};items={exportItems.Count}");

            reportProgress?.Invoke(new SyncProgress("export_snapshot", "导出快照", 52));
            var exportResult = await _snapshot.ExportAsync(snapshotPath, exportItems, exportSettings, ct);
            var size = exportResult.SnapshotSize;
            var snapshotSha256 = exportResult.SnapshotSha256;
            WriteTimingLog("upload", "export_snapshot", startedAt.ElapsedMilliseconds, $"{reason};size={size};sha256={snapshotSha256};blobs={exportResult.Blobs.Count}");

            var lastRemoteSha = (_settings.Current.SyncLastRemoteSha256 ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(lastRemoteSha)
                && string.Equals(lastRemoteSha, snapshotSha256, StringComparison.OrdinalIgnoreCase))
            {
                WriteTimingLog("upload", "skip_upload_same_sha", startedAt.ElapsedMilliseconds, reason);
                return new UploadSnapshotResult(_settings.Current.SyncLastRemoteVersion, snapshotSha256, size);
            }

            if (exportResult.Blobs.Count > 0)
            {
                reportProgress?.Invoke(new SyncProgress("upload_blobs", "上传二进制对象", 68));
                token = await UploadMissingBlobsWithRefreshAsync(client, token, exportResult, startedAt, reason, ct);
            }

            reportProgress?.Invoke(new SyncProgress("upload_snapshot", "上传快照", 84));
            try
            {
                var result = await client.UploadSnapshotAsync(token, snapshotPath, ct);
                WriteTimingLog("upload", "upload_snapshot", startedAt.ElapsedMilliseconds, reason);
                return result;
            }
            catch (NasAgentApiException ex)
            {
                if (IsSubscriptionExpired(ex))
                    throw new InvalidOperationException(SubscriptionExpiredError);
                if (!IsAuthError(ex))
                    throw;
                if (!await TryRefreshAndPersistAsync(ct))
                    throw;

                var token2 = SelectBearerToken(_settings.Current);
                if (exportResult.Blobs.Count > 0)
                    token2 = await UploadMissingBlobsWithRefreshAsync(client, token2, exportResult, startedAt, reason + ";after_refresh", ct);
                var result = await client.UploadSnapshotAsync(token2, snapshotPath, ct);
                WriteTimingLog("upload", "upload_snapshot_after_refresh", startedAt.ElapsedMilliseconds, reason);
                return result;
            }
        }
        finally
        {
            TryDeleteWithRetry(snapshotPath);
            WriteTimingLog("upload", "finished", startedAt.ElapsedMilliseconds, reason);
            Gate.Release();
        }
    }

    public async Task DownloadAndApplyAsync(
        CancellationToken ct,
        Action<SyncProgress>? reportProgress = null,
        string reason = "auto")
    {
        await Gate.WaitAsync(ct);
        var startedAt = Stopwatch.StartNew();
        var snapshotPath = string.Empty;
        try
        {
            reportProgress?.Invoke(new SyncProgress("prepare_local", "准备本地数据", 30));
            var (baseUrl, token) = await GetConnectionAsync(ct);
            var client = new NasAgentClient(baseUrl);

            var dir = Path.Combine(AppPaths.AppDataRoot, "sync", "tmp");
            Directory.CreateDirectory(dir);
            snapshotPath = Path.Combine(dir, "snapshot-" + Guid.NewGuid().ToString("N") + ".sqlite");

            reportProgress?.Invoke(new SyncProgress("download_snapshot", "下载快照", 52));
            try
            {
                await client.DownloadSnapshotAsync(token, snapshotPath, ct);
            }
            catch (NasAgentApiException ex)
            {
                if (IsSubscriptionExpired(ex))
                    throw new InvalidOperationException(SubscriptionExpiredError);
                if (!IsAuthError(ex))
                    throw;
                if (!await TryRefreshAndPersistAsync(ct))
                    throw;
                var token2 = SelectBearerToken(_settings.Current);
                await client.DownloadSnapshotAsync(token2, snapshotPath, ct);
            }
            WriteTimingLog("download", "download_snapshot", startedAt.ElapsedMilliseconds, $"{reason};size={TryGetFileSize(snapshotPath)}");

            reportProgress?.Invoke(new SyncProgress("import_snapshot", "导入快照", 76));
            await _snapshot.ImportAsync(
                snapshotPath,
                _history,
                _settings,
                (blobSha256, blobCt) => EnsureBlobDownloadedAsync(client, token, blobSha256, blobCt),
                ct);
            WriteTimingLog("download", "import_snapshot", startedAt.ElapsedMilliseconds, reason);

            reportProgress?.Invoke(new SyncProgress("persist_local", "保存本地状态", 90));
            await _persistence.FlushAsync(ct);
            WriteTimingLog("download", "persist_local", startedAt.ElapsedMilliseconds, reason);
        }
        finally
        {
            TryDeleteWithRetry(snapshotPath);
            WriteTimingLog("download", "finished", startedAt.ElapsedMilliseconds, reason);
            Gate.Release();
        }
    }

    public async Task<string?> GetManifestJsonAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        var startedAt = Stopwatch.StartNew();
        try
        {
            var (baseUrl, token) = await GetConnectionAsync(ct);
            var client = new NasAgentClient(baseUrl);
            try
            {
                var json = await client.GetManifestJsonAsync(token, ct);
                WriteTimingLog("manifest", "fetch", startedAt.ElapsedMilliseconds, "ok");
                return json;
            }
            catch (NasAgentApiException ex)
            {
                if (IsSubscriptionExpired(ex))
                    throw new InvalidOperationException(SubscriptionExpiredError);
                if (!IsAuthError(ex))
                    throw;
                if (!await TryRefreshAndPersistAsync(ct))
                    throw;
                var token2 = SelectBearerToken(_settings.Current);
                var json = await client.GetManifestJsonAsync(token2, ct);
                WriteTimingLog("manifest", "fetch_after_refresh", startedAt.ElapsedMilliseconds, "ok");
                return json;
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<SyncManifestInfo> GetManifestInfoAsync(CancellationToken ct)
    {
        var json = await GetManifestJsonAsync(ct);
        return ParseManifestInfo(json);
    }

    public async Task<SyncDirection> DecideDirectionAsync(CancellationToken ct)
    {
        var manifest = await GetManifestInfoAsync(ct);
        return DecideDirectionWithState(manifest).Direction;
    }

    public SyncDecision DecideDirectionWithState(SyncManifestInfo manifest)
    {
        var localFingerprint = ComputeLocalFingerprint();
        var localLatest = GetLocalLatestCapturedAt();
        var current = _settings.Current;

        var localHasItems = _history.Items.Count > 0;
        if (manifest.UpdatedAt is null)
        {
            var direction = localHasItems ? SyncDirection.Upload : SyncDirection.None;
            return new SyncDecision(direction, localFingerprint, manifest, "remote_missing");
        }

        var lastFp = (current.SyncLastLocalFingerprint ?? string.Empty).Trim();
        var lastVersion = (current.SyncLastRemoteVersion ?? string.Empty).Trim();
        var remoteVersion = (manifest.Version ?? string.Empty).Trim();

        if (!string.IsNullOrWhiteSpace(lastFp) && !string.IsNullOrWhiteSpace(lastVersion))
        {
            if (string.Equals(lastFp, localFingerprint, StringComparison.Ordinal)
                && string.Equals(lastVersion, remoteVersion, StringComparison.Ordinal))
            {
                return new SyncDecision(SyncDirection.None, localFingerprint, manifest, "state_matched");
            }

            if (!string.Equals(lastFp, localFingerprint, StringComparison.Ordinal)
                && string.Equals(lastVersion, remoteVersion, StringComparison.Ordinal))
            {
                return new SyncDecision(SyncDirection.Upload, localFingerprint, manifest, "local_changed_only");
            }

            if (string.Equals(lastFp, localFingerprint, StringComparison.Ordinal)
                && !string.Equals(lastVersion, remoteVersion, StringComparison.Ordinal))
            {
                return new SyncDecision(SyncDirection.Download, localFingerprint, manifest, "remote_changed_only");
            }
        }

        var fallback = DecideByTimestamp(localLatest, manifest.UpdatedAt);
        return new SyncDecision(fallback, localFingerprint, manifest, "timestamp_fallback");
    }

    public void PersistSyncState(string localFingerprint, SyncManifestInfo manifest)
    {
        _settings.Update(s =>
        {
            s.SyncLastLocalFingerprint = string.IsNullOrWhiteSpace(localFingerprint) ? null : localFingerprint;
            s.SyncLastRemoteVersion = string.IsNullOrWhiteSpace(manifest.Version) ? null : manifest.Version;
            s.SyncLastRemoteUpdatedAt = manifest.UpdatedAt?.ToString("O");
            s.SyncLastRemoteSha256 = string.IsNullOrWhiteSpace(manifest.SnapshotSha256) ? null : manifest.SnapshotSha256;
        });
    }

    public string ComputeLocalFingerprint()
    {
        try
        {
            using var sha = SHA256.Create();
            using var ms = new MemoryStream();
            using var writer = new StreamWriter(ms, new UTF8Encoding(false), 1024, leaveOpen: true);

            writer.WriteLine("schema=1");
            foreach (var item in _history.Items)
            {
                writer.Write(item.Id);
                writer.Write('|');
                writer.Write((int)item.Kind);
                writer.Write('|');
                writer.Write(item.CapturedAt.UtcDateTime.Ticks);
                writer.Write('|');
                writer.Write(item.ApproxBytes);
                writer.Write('|');
                writer.Write(item.Pinned ? '1' : '0');
                writer.Write('|');
                writer.Write(item.CopyCount);
                writer.Write('|');
                writer.Write(item.FirstCapturedAt?.UtcDateTime.Ticks ?? 0);
                writer.Write('|');
                writer.Write(item.Note ?? string.Empty);
                writer.Write('|');
                writer.Write(item.ContentHash ?? string.Empty);
                writer.Write('|');
                writer.Write(item.Text ?? string.Empty);
                writer.Write('|');
                if (item.FilePaths is not null && item.FilePaths.Count > 0)
                    writer.Write(string.Join(",", item.FilePaths.Select(Path.GetFileName)));
                writer.WriteLine();
            }

            writer.Flush();
            ms.Position = 0;
            return Convert.ToHexString(sha.ComputeHash(ms)).ToLowerInvariant();
        }
        catch
        {
            return "fingerprint-error";
        }
    }

    public static SyncManifestInfo ParseManifestInfo(string? manifestJson)
    {
        if (string.IsNullOrWhiteSpace(manifestJson))
            return new SyncManifestInfo(null, null, null, null, manifestJson);

        try
        {
            using var doc = JsonDocument.Parse(manifestJson);
            if (!doc.RootElement.TryGetProperty("latest", out var latest))
                return new SyncManifestInfo(null, null, null, null, manifestJson);

            string? version = TryReadString(latest, "version");
            DateTimeOffset? updatedAt = null;
            var updatedAtText = TryReadString(latest, "updatedAt");
            if (!string.IsNullOrWhiteSpace(updatedAtText) && DateTimeOffset.TryParse(updatedAtText, out var parsed))
                updatedAt = parsed;

            string? sha256 = null;
            long? size = null;
            if (latest.TryGetProperty("snapshot", out var snapshot))
            {
                sha256 = TryReadString(snapshot, "sha256");
                size = TryReadInt64(snapshot, "size");
            }

            return new SyncManifestInfo(version, updatedAt, sha256, size, manifestJson);
        }
        catch
        {
            return new SyncManifestInfo(null, null, null, null, manifestJson);
        }
    }

    private async Task<string> UploadMissingBlobsWithRefreshAsync(
        NasAgentClient client,
        string accessToken,
        SnapshotSqliteService.SnapshotExportResult exportResult,
        Stopwatch startedAt,
        string reason,
        CancellationToken ct)
    {
        try
        {
            await UploadMissingBlobsAsync(client, accessToken, exportResult, startedAt, reason, ct);
            return accessToken;
        }
        catch (NasAgentApiException ex) when (IsAuthError(ex))
        {
            if (!await TryRefreshAndPersistAsync(ct))
                throw;

            var refreshedToken = SelectBearerToken(_settings.Current);
            await UploadMissingBlobsAsync(client, refreshedToken, exportResult, startedAt, reason + ";blob_refresh", ct);
            return refreshedToken;
        }
    }

    private static async Task UploadMissingBlobsAsync(
        NasAgentClient client,
        string accessToken,
        SnapshotSqliteService.SnapshotExportResult exportResult,
        Stopwatch startedAt,
        string reason,
        CancellationToken ct)
    {
        var uniqueBlobs = exportResult.Blobs
            .GroupBy(x => x.Sha256, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();

        if (uniqueBlobs.Count == 0)
            return;

        var check = await client.CheckMissingBlobsAsync(accessToken, uniqueBlobs.Select(x => x.Sha256).ToArray(), ct);
        var missing = check.Missing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        WriteTimingLog("upload", "check_missing_blobs", startedAt.ElapsedMilliseconds, $"{reason};total={uniqueBlobs.Count};missing={missing.Count}");

        foreach (var blob in uniqueBlobs)
        {
            ct.ThrowIfCancellationRequested();
            if (!missing.Contains(blob.Sha256))
                continue;
            await client.UploadBlobAsync(accessToken, blob.Sha256, blob.SourcePath, ct);
        }

        WriteTimingLog("upload", "upload_blobs", startedAt.ElapsedMilliseconds, $"{reason};uploaded={missing.Count}");
    }

    private async Task<string?> EnsureBlobDownloadedAsync(
        NasAgentClient client,
        string accessToken,
        string blobSha256,
        CancellationToken ct)
    {
        var localPath = SnapshotSqliteService.GetLocalBlobPath(blobSha256);
        if (File.Exists(localPath))
            return localPath;

        try
        {
            await client.DownloadBlobAsync(accessToken, blobSha256, localPath, ct);
            return File.Exists(localPath) ? localPath : null;
        }
        catch (NasAgentApiException ex) when (IsAuthError(ex))
        {
            if (!await TryRefreshAndPersistAsync(ct))
                return null;

            var refreshedToken = SelectBearerToken(_settings.Current);
            await client.DownloadBlobAsync(refreshedToken, blobSha256, localPath, ct);
            return File.Exists(localPath) ? localPath : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<(string BaseUrl, string Token)> GetConnectionAsync(CancellationToken ct)
    {
        var s = _settings.Current;

        var baseUrl = NormalizeOfficialSyncBaseUrl();
        if (!string.Equals((s.NasAgentBaseUrl ?? string.Empty).Trim().TrimEnd('/'), baseUrl, StringComparison.OrdinalIgnoreCase))
        {
            _settings.Update(x => x.NasAgentBaseUrl = ServerDefaults.OfficialSyncBaseUrl);
            s = _settings.Current;
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (s.AuthExpiresAtUnixMs <= nowMs + 60_000)
            await TryRefreshAndPersistAsync(ct);

        s = _settings.Current;
        var token = SelectBearerToken(s);
        if (string.IsNullOrWhiteSpace(token) || s.AuthExpiresAtUnixMs <= nowMs + 30_000)
            throw new InvalidOperationException("not logged in");

        return (baseUrl, token);
    }

    private static string NormalizeOfficialSyncBaseUrl()
    {
        return ServerDefaults.OfficialSyncBaseUrl.TrimEnd('/');
    }

    private static bool IsAuthError(NasAgentApiException ex)
    {
        return ex.StatusCode == HttpStatusCode.Unauthorized;
    }

    private static bool IsSubscriptionExpired(NasAgentApiException ex)
    {
        if (ex.StatusCode != HttpStatusCode.Forbidden)
            return false;

        var body = ex.ResponseBody ?? string.Empty;
        if (string.IsNullOrWhiteSpace(body))
            return false;

        return body.Contains("\u8ba2\u9605\u5df2\u8fc7\u671f", StringComparison.OrdinalIgnoreCase)
            || (body.Contains("subscription", StringComparison.OrdinalIgnoreCase)
                && body.Contains("expired", StringComparison.OrdinalIgnoreCase));
    }

    private static string SelectBearerToken(AppSettings s)
    {
        var access = (s.AuthAccessToken ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(access) ? string.Empty : access;
    }

    private DateTimeOffset? GetLocalLatestCapturedAt()
    {
        try
        {
            if (_history.Items.Count == 0)
                return null;
            return _history.Items.Max(x => x.CapturedAt);
        }
        catch
        {
            return null;
        }
    }

    private static SyncDirection DecideByTimestamp(DateTimeOffset? localLatest, DateTimeOffset? remoteUpdatedAt)
    {
        if (remoteUpdatedAt is null)
            return localLatest is null ? SyncDirection.None : SyncDirection.Upload;
        if (localLatest is null)
            return SyncDirection.Download;

        var localUtc = localLatest.Value.UtcDateTime;
        var remoteUtc = remoteUpdatedAt.Value.UtcDateTime;
        var diff = localUtc - remoteUtc;
        if (Math.Abs(diff.TotalSeconds) <= 2)
            return SyncDirection.None;
        return diff.TotalSeconds > 0 ? SyncDirection.Upload : SyncDirection.Download;
    }

    private async Task<bool> TryRefreshAndPersistAsync(CancellationToken ct)
    {
        var current = _settings.Current;
        var refresh = (current.AuthRefreshToken ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(refresh))
            return false;

        try
        {
            var token = await _authing.RefreshAsync(ServerDefaults.OfficialSyncBaseUrl, refresh, ct);
            _settings.Update(s =>
            {
                s.AuthAccessToken = token.AccessToken;
                s.AuthRefreshToken = string.IsNullOrWhiteSpace(token.RefreshToken) ? current.AuthRefreshToken : token.RefreshToken;
                s.AuthIdToken = string.IsNullOrWhiteSpace(token.IdToken) ? current.AuthIdToken : token.IdToken;
                s.AuthExpiresAtUnixMs = token.ExpiresAtUtc.ToUnixTimeMilliseconds();
                s.AuthUserEmail = string.IsNullOrWhiteSpace(token.Email) ? current.AuthUserEmail : token.Email;
                s.NasAgentBaseUrl = ServerDefaults.OfficialSyncBaseUrl;
            });
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            _settings.ClearAuthSession();
            return false;
        }
    }

    private static void TryDeleteWithRetry(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        for (var i = 0; i < 6; i++)
        {
            try
            {
                if (!File.Exists(path))
                    return;
                File.Delete(path);
                return;
            }
            catch
            {
                try
                {
                    Thread.Sleep(120);
                }
                catch
                {
                }
            }
        }
    }

    private static long TryGetFileSize(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return 0;
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static string ComputeFileSha256(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return string.Empty;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static AppSettings CloneSettings(AppSettings src)
    {
        return new AppSettings
        {
            Theme = src.Theme,
            StartWithWindows = src.StartWithWindows,
            MaxItems = src.MaxItems,
            MaxMegabytes = src.MaxMegabytes,
            CaptureText = src.CaptureText,
            CaptureImages = src.CaptureImages,
            CaptureFiles = src.CaptureFiles,
            CaptureFileExtensions = src.CaptureFileExtensions,
            CaptureFileMaxMegabytes = src.CaptureFileMaxMegabytes,
            MergeDuplicates = src.MergeDuplicates,
            ExcludePinnedFromLimits = src.ExcludePinnedFromLimits,
            ShelfEnabled = src.ShelfEnabled,
            ShelfTriggerModifier = src.ShelfTriggerModifier,
        };
    }

    private static string? TryReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
            return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
    }

    private static long? TryReadInt64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
            return null;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var value))
            return value;
        if (property.ValueKind == JsonValueKind.String && long.TryParse(property.GetString(), out value))
            return value;
        return null;
    }

    private static void WriteTimingLog(string operation, string stage, long elapsedMs, string? details)
    {
        try
        {
            var root = AppPaths.AppDataRoot;
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "sync_timing.log");
            var line =
                DateTimeOffset.Now.ToString("O") +
                " | " + operation +
                " | " + stage +
                " | elapsedMs=" + elapsedMs +
                (string.IsNullOrWhiteSpace(details) ? string.Empty : " | " + details.Trim()) +
                Environment.NewLine;

            lock (TimingLogGate)
            {
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch
        {
        }
    }

    public enum SyncDirection
    {
        None,
        Upload,
        Download,
    }
}
