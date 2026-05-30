using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using maccy.Models;

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
            token = await DownloadSnapshotWithRefreshAsync(client, token, snapshotPath, ct);
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

    public async Task MergeAndApplyAsync(
        SyncManifestInfo basisManifest,
        string preMergeLocalFingerprint,
        CancellationToken ct,
        Action<SyncProgress>? reportProgress = null,
        string reason = "auto")
    {
        await Gate.WaitAsync(ct);
        var startedAt = Stopwatch.StartNew();
        var snapshotPath = string.Empty;
        try
        {
            reportProgress?.Invoke(new SyncProgress("prepare_local", "prepare local", 18));
            var (baseUrl, token) = await GetConnectionAsync(ct);
            var client = new NasAgentClient(baseUrl);

            var localItems = _history.Items.ToList();
            var localSettings = CloneSettings(_settings.Current);

            reportProgress?.Invoke(new SyncProgress("backup_snapshot", "backup local snapshot", 28));
            var backupPath = CreatePreMergeBackupPath();
            var backupResult = await _snapshot.ExportAsync(backupPath, localItems, localSettings, ct);
            await EnsureLocalBackupBlobsAsync(backupResult, ct);
            WriteTimingLog("merge", "backup_snapshot", startedAt.ElapsedMilliseconds, $"{reason};path={backupPath};items={localItems.Count};size={backupResult.SnapshotSize}");

            var dir = Path.Combine(AppPaths.AppDataRoot, "sync", "tmp");
            Directory.CreateDirectory(dir);
            snapshotPath = Path.Combine(dir, "snapshot-" + Guid.NewGuid().ToString("N") + ".sqlite");

            reportProgress?.Invoke(new SyncProgress("download_snapshot", "download remote snapshot", 44));
            token = await DownloadSnapshotWithRefreshAsync(client, token, snapshotPath, ct);
            WriteTimingLog("merge", "download_snapshot", startedAt.ElapsedMilliseconds, $"{reason};size={TryGetFileSize(snapshotPath)}");

            reportProgress?.Invoke(new SyncProgress("read_snapshot", "read remote snapshot", 62));
            var remote = await _snapshot.ReadAsync(
                snapshotPath,
                (blobSha256, blobCt) => EnsureBlobDownloadedAsync(client, token, blobSha256, blobCt),
                ct);
            WriteTimingLog("merge", "read_snapshot", startedAt.ElapsedMilliseconds, $"{reason};remoteItems={remote.Items.Count};images={remote.ImageCount};files={remote.FileCount}");

            reportProgress?.Invoke(new SyncProgress("merge_snapshot", "merge snapshots", 78));
            var mergedItems = MergeItems(remote.Items, localItems);
            var mergedSettings = CloneSettings(remote.Settings ?? localSettings);
            EnsureSettingsCanHoldItems(mergedSettings, mergedItems);

            _settings.Update(s => CopySettings(s, mergedSettings));
            ApplyHistoryLimits(_history, _settings.Current);
            await _history.ReplaceAllAsync(mergedItems, ct);
            WriteTimingLog("merge", "merge_snapshot", startedAt.ElapsedMilliseconds, $"{reason};localItems={localItems.Count};remoteItems={remote.Items.Count};mergedItems={mergedItems.Count}");

            reportProgress?.Invoke(new SyncProgress("persist_local", "persist local", 90));
            await _persistence.FlushAsync(ct);
            PersistPendingMergeUploadState(preMergeLocalFingerprint, basisManifest);
            WriteTimingLog("merge", "persist_local", startedAt.ElapsedMilliseconds, reason);
        }
        finally
        {
            TryDeleteWithRetry(snapshotPath);
            WriteTimingLog("merge", "finished", startedAt.ElapsedMilliseconds, reason);
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
        var lastRemoteSha = (current.SyncLastRemoteSha256 ?? string.Empty).Trim();
        var remoteSha = (manifest.SnapshotSha256 ?? string.Empty).Trim();
        var remoteMatched = IsSameRemoteSnapshot(lastVersion, remoteVersion, lastRemoteSha, remoteSha);

        if (!string.IsNullOrWhiteSpace(lastFp) && !string.IsNullOrWhiteSpace(lastVersion))
        {
            if (IsRemoteRollback(current.SyncLastRemoteUpdatedAt, manifest.UpdatedAt))
            {
                return new SyncDecision(SyncDirection.Download, localFingerprint, manifest, "remote_rolled_back");
            }

            if (string.Equals(lastFp, localFingerprint, StringComparison.Ordinal)
                && remoteMatched)
            {
                return new SyncDecision(SyncDirection.None, localFingerprint, manifest, "state_matched");
            }

            if (!string.Equals(lastFp, localFingerprint, StringComparison.Ordinal)
                && remoteMatched)
            {
                return new SyncDecision(SyncDirection.Upload, localFingerprint, manifest, "local_changed_only");
            }

            if (string.Equals(lastFp, localFingerprint, StringComparison.Ordinal)
                && !remoteMatched)
            {
                return new SyncDecision(SyncDirection.Download, localFingerprint, manifest, "remote_changed_only");
            }
        }
        else
        {
            var direction = localHasItems ? SyncDirection.Merge : SyncDirection.Download;
            var reason = localHasItems ? "first_sync_merge" : "first_sync_remote_exists";
            return new SyncDecision(direction, localFingerprint, manifest, reason);
        }

        var fallback = DecideByTimestamp(localLatest, manifest.UpdatedAt);
        return new SyncDecision(fallback, localFingerprint, manifest, "timestamp_fallback");
    }

    private static bool IsSameRemoteSnapshot(string lastVersion, string remoteVersion, string lastSha, string remoteSha)
    {
        if (!string.Equals(lastVersion, remoteVersion, StringComparison.Ordinal))
            return false;

        if (string.IsNullOrWhiteSpace(lastSha) || string.IsNullOrWhiteSpace(remoteSha))
            return true;

        return string.Equals(lastSha, remoteSha, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRemoteRollback(string? lastUpdatedAtText, DateTimeOffset? remoteUpdatedAt)
    {
        if (remoteUpdatedAt is null || string.IsNullOrWhiteSpace(lastUpdatedAtText))
            return false;

        if (!DateTimeOffset.TryParse(lastUpdatedAtText, out var lastUpdatedAt))
            return false;

        return remoteUpdatedAt.Value.UtcDateTime < lastUpdatedAt.UtcDateTime.AddSeconds(-2);
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

    private async Task<string> DownloadSnapshotWithRefreshAsync(
        NasAgentClient client,
        string accessToken,
        string snapshotPath,
        CancellationToken ct)
    {
        try
        {
            await client.DownloadSnapshotAsync(accessToken, snapshotPath, ct);
            return accessToken;
        }
        catch (NasAgentApiException ex)
        {
            if (IsSubscriptionExpired(ex))
                throw new InvalidOperationException(SubscriptionExpiredError);
            if (!IsAuthError(ex))
                throw;
            if (!await TryRefreshAndPersistAsync(ct))
                throw;

            var refreshedToken = SelectBearerToken(_settings.Current);
            await client.DownloadSnapshotAsync(refreshedToken, snapshotPath, ct);
            return refreshedToken;
        }
    }

    private async Task EnsureLocalBackupBlobsAsync(SnapshotSqliteService.SnapshotExportResult exportResult, CancellationToken ct)
    {
        foreach (var blob in exportResult.Blobs)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(blob.SourcePath) || !File.Exists(blob.SourcePath))
                continue;

            var targetPath = SnapshotSqliteService.GetLocalBlobPath(blob.Sha256);
            if (File.Exists(targetPath))
                continue;

            try
            {
                if (string.Equals(Path.GetFullPath(blob.SourcePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                await using var input = new FileStream(blob.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                await using var output = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await input.CopyToAsync(output, ct);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string CreatePreMergeBackupPath()
    {
        var dir = Path.Combine(AppPaths.AppDataRoot, "sync", "backups");
        Directory.CreateDirectory(dir);
        var fileName = "pre-merge-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".sqlite";
        return Path.Combine(dir, fileName);
    }

    private static List<ClipboardItem> MergeItems(IReadOnlyList<ClipboardItem> remoteItems, IReadOnlyList<ClipboardItem> localItems)
    {
        var merged = new Dictionary<string, ClipboardItem>(StringComparer.OrdinalIgnoreCase);
        var passthrough = new List<ClipboardItem>();

        foreach (var item in remoteItems)
            AddMergeItem(item, isLocal: false, merged, passthrough);

        foreach (var item in localItems)
            AddMergeItem(item, isLocal: true, merged, passthrough);

        return merged.Values
            .Concat(passthrough)
            .OrderByDescending(x => x.CapturedAt)
            .ThenBy(x => x.Id)
            .ToList();
    }

    private static void AddMergeItem(
        ClipboardItem item,
        bool isLocal,
        Dictionary<string, ClipboardItem> merged,
        List<ClipboardItem> passthrough)
    {
        var key = GetMergeKey(item);
        if (string.IsNullOrWhiteSpace(key))
        {
            passthrough.Add(item);
            return;
        }

        if (merged.TryGetValue(key, out var existing))
        {
            merged[key] = MergeDuplicate(existing, item, isLocal);
            return;
        }

        merged[key] = item;
    }

    private static string? GetMergeKey(ClipboardItem item)
    {
        var hash = (item.ContentHash ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(hash))
            return "hash:" + (int)item.Kind + ":" + hash;

        return item.Id == Guid.Empty ? null : "id:" + item.Id.ToString("D");
    }

    private static ClipboardItem MergeDuplicate(ClipboardItem existing, ClipboardItem incoming, bool incomingIsLocal)
    {
        var latest = incoming.CapturedAt >= existing.CapturedAt ? incoming : existing;
        var first = MinDate(
            existing.FirstCapturedAt ?? existing.CapturedAt,
            incoming.FirstCapturedAt ?? incoming.CapturedAt);
        var note = SelectMergedNote(existing.Note, incoming.Note, incomingIsLocal);
        var contentHash = string.IsNullOrWhiteSpace(latest.ContentHash)
            ? (string.IsNullOrWhiteSpace(existing.ContentHash) ? incoming.ContentHash : existing.ContentHash)
            : latest.ContentHash;

        return latest with
        {
            ApproxBytes = Math.Max(existing.ApproxBytes, incoming.ApproxBytes),
            Pinned = existing.Pinned || incoming.Pinned,
            CopyCount = Math.Max(0, existing.CopyCount) + Math.Max(0, incoming.CopyCount),
            FirstCapturedAt = first,
            Note = note,
            ContentHash = contentHash,
        };
    }

    private static DateTimeOffset MinDate(DateTimeOffset left, DateTimeOffset right)
    {
        return left <= right ? left : right;
    }

    private static string? SelectMergedNote(string? existingNote, string? incomingNote, bool incomingIsLocal)
    {
        if (incomingIsLocal && !string.IsNullOrWhiteSpace(incomingNote))
            return incomingNote;
        if (!string.IsNullOrWhiteSpace(existingNote))
            return existingNote;
        return string.IsNullOrWhiteSpace(incomingNote) ? null : incomingNote;
    }

    private static void EnsureSettingsCanHoldItems(AppSettings settings, IReadOnlyList<ClipboardItem> items)
    {
        if (settings.MaxItems < items.Count)
            settings.MaxItems = items.Count;

        long totalBytes = 0;
        foreach (var item in items)
        {
            if (item.ApproxBytes <= 0)
                continue;

            if (long.MaxValue - totalBytes < item.ApproxBytes)
            {
                totalBytes = long.MaxValue;
                break;
            }

            totalBytes += item.ApproxBytes;
        }

        var neededMegabytes = totalBytes <= 0
            ? 1
            : (int)Math.Min(int.MaxValue, Math.Max(1, (totalBytes + 1024L * 1024L - 1) / (1024L * 1024L)));
        if (settings.MaxMegabytes < neededMegabytes)
            settings.MaxMegabytes = neededMegabytes;
    }

    private void PersistPendingMergeUploadState(string preMergeLocalFingerprint, SyncManifestInfo basisManifest)
    {
        _settings.Update(s =>
        {
            s.SyncLastLocalFingerprint = string.IsNullOrWhiteSpace(preMergeLocalFingerprint) ? "pre-merge" : preMergeLocalFingerprint;
            s.SyncLastRemoteVersion = string.IsNullOrWhiteSpace(basisManifest.Version) ? null : basisManifest.Version;
            s.SyncLastRemoteUpdatedAt = basisManifest.UpdatedAt?.ToString("O");
            s.SyncLastRemoteSha256 = string.IsNullOrWhiteSpace(basisManifest.SnapshotSha256) ? null : basisManifest.SnapshotSha256;
        });
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
        var clone = new AppSettings();
        CopySettings(clone, src);
        return clone;
    }

    private static void CopySettings(AppSettings dest, AppSettings src)
    {
        dest.Theme = src.Theme;
        dest.StartWithWindows = src.StartWithWindows;
        dest.MaxItems = src.MaxItems;
        dest.MaxMegabytes = src.MaxMegabytes;
        dest.CaptureText = src.CaptureText;
        dest.CaptureImages = src.CaptureImages;
        dest.CaptureFiles = src.CaptureFiles;
        dest.CaptureFileExtensions = src.CaptureFileExtensions;
        dest.CaptureFileMaxMegabytes = src.CaptureFileMaxMegabytes;
        dest.MergeDuplicates = src.MergeDuplicates;
        dest.ExcludePinnedFromLimits = src.ExcludePinnedFromLimits;
        dest.ShelfEnabled = src.ShelfEnabled;
        dest.ShelfTriggerModifier = src.ShelfTriggerModifier;
    }

    private static void ApplyHistoryLimits(ClipboardHistoryService history, AppSettings settings)
    {
        history.MaxItems = settings.MaxItems;
        history.MaxBytes = (long)settings.MaxMegabytes * 1024 * 1024;
        history.MergeDuplicates = settings.MergeDuplicates;
        history.ExcludePinnedFromLimits = settings.ExcludePinnedFromLimits;
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
        Merge,
    }
}
