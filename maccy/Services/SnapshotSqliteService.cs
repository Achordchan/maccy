using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using maccy.Models;

namespace maccy.Services;

public sealed class SnapshotSqliteService
{
    private const int SchemaVersion = 2;

    public sealed record SnapshotBlob(string Sha256, string SourcePath, long SizeBytes);

    public sealed record SnapshotExportResult(long SnapshotSize, string SnapshotSha256, IReadOnlyList<SnapshotBlob> Blobs);

    public async Task<SnapshotExportResult> ExportAsync(
        string snapshotPath,
        IReadOnlyList<ClipboardItem> items,
        AppSettings settings,
        CancellationToken ct = default)
    {
        return await Task.Run(async () =>
        {
            if (string.IsNullOrWhiteSpace(snapshotPath))
                throw new ArgumentException("snapshotPath is empty", nameof(snapshotPath));
            if (items is null)
                throw new ArgumentNullException(nameof(items));
            if (settings is null)
                throw new ArgumentNullException(nameof(settings));

            Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);

            TryDeleteWithRetry(snapshotPath, ct);
            TryDeleteWithRetry(snapshotPath + "-journal", ct);
            TryDeleteWithRetry(snapshotPath + "-wal", ct);
            TryDeleteWithRetry(snapshotPath + "-shm", ct);

            await using var conn = new SqliteConnection("Data Source=" + snapshotPath + ";Pooling=False");
            await conn.OpenAsync(ct);

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
PRAGMA journal_mode=MEMORY;
PRAGMA synchronous=OFF;
PRAGMA temp_store=MEMORY;
PRAGMA locking_mode=EXCLUSIVE;

CREATE TABLE meta (
  key TEXT PRIMARY KEY,
  value TEXT NOT NULL
);

CREATE TABLE settings (
  json TEXT NOT NULL
);

CREATE TABLE items (
  position INTEGER NOT NULL,
  id TEXT PRIMARY KEY,
  kind INTEGER NOT NULL,
  capturedAt TEXT NOT NULL,
  approxBytes INTEGER NOT NULL,
  text TEXT,
  filePathsJson TEXT,
  pinned INTEGER NOT NULL,
  copyCount INTEGER NOT NULL,
  firstCapturedAt TEXT,
  note TEXT,
  contentHash TEXT,
  sourceAppName TEXT,
  sourceAppPath TEXT
);

CREATE TABLE item_images (
  itemId TEXT PRIMARY KEY,
  fileName TEXT NOT NULL,
  blobSha256 TEXT,
  sizeBytes INTEGER,
  bytes BLOB
);

CREATE TABLE item_files (
  itemId TEXT NOT NULL,
  idx INTEGER NOT NULL,
  fileName TEXT NOT NULL,
  blobSha256 TEXT,
  sizeBytes INTEGER,
  bytes BLOB,
  PRIMARY KEY(itemId, idx)
);
";
                await cmd.ExecuteNonQueryAsync(ct);
            }

            var blobs = new Dictionary<string, SnapshotBlob>(StringComparer.OrdinalIgnoreCase);

            using (var tx = conn.BeginTransaction())
            {
                await InsertMetaAsync(conn, tx, ct);
                await InsertSettingsAsync(conn, tx, settings, ct);
                await InsertHistoryAsync(conn, tx, items, blobs, ct);

                tx.Commit();
            }

            await conn.CloseAsync();
            var snapshotSize = new FileInfo(snapshotPath).Length;
            var snapshotSha256 = ComputeFileSha256(snapshotPath);
            return new SnapshotExportResult(snapshotSize, snapshotSha256, blobs.Values.ToList());
        }, ct);
    }

    public async Task<SnapshotImportResult> ImportAsync(
        string snapshotPath,
        ClipboardHistoryService history,
        AppSettingsService settings,
        Func<string, CancellationToken, Task<string?>>? ensureBlobPathAsync = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(snapshotPath))
            throw new ArgumentException("snapshotPath is empty", nameof(snapshotPath));
        if (!File.Exists(snapshotPath))
            throw new FileNotFoundException("snapshot not found", snapshotPath);

        var data = await Task.Run(async () =>
        {
            await using var conn = new SqliteConnection("Data Source=" + snapshotPath + ";Mode=ReadOnly");
            await conn.OpenAsync(ct);

            var schema = await ReadMetaAsync(conn, "schema", ct);
            if (!int.TryParse(schema, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || (parsed != 1 && parsed != SchemaVersion))
                throw new InvalidOperationException("unsupported snapshot schema");

            var importedSettings = await ReadSettingsJsonAsync(conn, ct);
            var items = await ReadItemsAsync(conn, ct);

            var writtenImageCount = 0;
            var writtenFileCount = 0;

            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.Kind == ClipboardContentKind.Image)
                {
                    var img = await ReadImageAsync(conn, item.Id, parsed, ct);
                    if (img is not null)
                    {
                        var path = await WriteImportedImageAsync(img, ensureBlobPathAsync, ct);
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            writtenImageCount++;
                            items[i] = item with { ImageFilePath = path };
                        }
                    }
                }

                if (item.Kind == ClipboardContentKind.FileList)
                {
                    var fileEntries = await ReadFilesAsync(conn, item.Id, parsed, ct);
                    if (fileEntries.Count > 0)
                    {
                        var dir = Path.Combine(AppPaths.FilesRoot, item.Id.ToString("N"));
                        Directory.CreateDirectory(dir);
                        var localPaths = new List<string>();

                        foreach (var f in fileEntries)
                        {
                            var safeName = SanitizeFileName(f.FileName);
                            var dest = Path.Combine(dir, f.Idx.ToString("D4") + "_" + safeName);
                            var wrote = await WriteImportedFileAsync(dest, f, ensureBlobPathAsync, ct);
                            if (wrote)
                            {
                                writtenFileCount++;
                                localPaths.Add(dest);
                            }
                        }

                        if (localPaths.Count > 0)
                            items[i] = item with { FilePaths = localPaths };
                    }
                }
            }

            return (ImportedSettings: importedSettings, Items: items, WrittenImageCount: writtenImageCount, WrittenFileCount: writtenFileCount);
        }, ct);

        if (!string.IsNullOrWhiteSpace(data.ImportedSettings))
        {
            try
            {
                var s = JsonSerializer.Deserialize<AppSettings>(data.ImportedSettings);
                if (s is not null)
                    settings.Update(cur => CopySettings(cur, s));
            }
            catch
            {
            }
        }

        await history.ReplaceAllAsync(data.Items, ct);

        return new SnapshotImportResult(data.Items.Count, data.WrittenImageCount, data.WrittenFileCount);
    }

    private static async Task InsertMetaAsync(SqliteConnection conn, SqliteTransaction tx, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO meta(key, value) VALUES ($k, $v);";
        cmd.Parameters.AddWithValue("$k", "schema");
        cmd.Parameters.AddWithValue("$v", SchemaVersion.ToString(CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertSettingsAsync(SqliteConnection conn, SqliteTransaction tx, AppSettings settings, CancellationToken ct)
    {
        string json;
        try
        {
            json = JsonSerializer.Serialize(CloneSettings(settings));
        }
        catch
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(json))
            return;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO settings(json) VALUES ($json);";
        cmd.Parameters.AddWithValue("$json", json);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertHistoryAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        IReadOnlyList<ClipboardItem> items,
        Dictionary<string, SnapshotBlob> blobs,
        CancellationToken ct)
    {
        if (items.Count == 0)
            return;

        var seenIds = new HashSet<Guid>();
        var position = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            if (!seenIds.Add(item.Id))
                continue;

            var filePathsJson = item.FilePaths is null ? null : JsonSerializer.Serialize(item.FilePaths);

            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT OR REPLACE INTO items(
  position, id, kind, capturedAt, approxBytes, text, filePathsJson, pinned, copyCount,
  firstCapturedAt, note, contentHash, sourceAppName, sourceAppPath
) VALUES (
  $pos, $id, $kind, $capturedAt, $approxBytes, $text, $filePathsJson, $pinned, $copyCount,
  $firstCapturedAt, $note, $contentHash, $sourceAppName, $sourceAppPath
);
";
                cmd.Parameters.AddWithValue("$pos", position);
                cmd.Parameters.AddWithValue("$id", item.Id.ToString("D"));
                cmd.Parameters.AddWithValue("$kind", (int)item.Kind);
                cmd.Parameters.AddWithValue("$capturedAt", item.CapturedAt.ToString("O", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("$approxBytes", item.ApproxBytes);
                cmd.Parameters.AddWithValue("$text", (object?)item.Text ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$filePathsJson", (object?)filePathsJson ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$pinned", item.Pinned ? 1 : 0);
                cmd.Parameters.AddWithValue("$copyCount", item.CopyCount);
                cmd.Parameters.AddWithValue("$firstCapturedAt", (object?)item.FirstCapturedAt?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$note", (object?)item.Note ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$contentHash", (object?)item.ContentHash ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$sourceAppName", (object?)item.SourceAppName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$sourceAppPath", (object?)item.SourceAppPath ?? DBNull.Value);
                try
                {
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
                {
                    continue;
                }
            }

            if (item.Kind == ClipboardContentKind.Image)
            {
                TryInsertImage(conn, tx, item, blobs, ct);
            }

            if (item.Kind == ClipboardContentKind.FileList)
            {
                TryInsertFiles(conn, tx, item, blobs, ct);
            }

            position++;
        }
    }

    private static void TryInsertImage(
        SqliteConnection conn,
        SqliteTransaction tx,
        ClipboardItem item,
        Dictionary<string, SnapshotBlob> blobs,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(item.ImageFilePath) || !File.Exists(item.ImageFilePath))
            return;

        string sha256;
        long sizeBytes;
        try
        {
            var imageInfo = new FileInfo(item.ImageFilePath);
            sizeBytes = imageInfo.Length;
            if (sizeBytes <= 0)
                return;
            sha256 = ComputeFileSha256(item.ImageFilePath);
        }
        catch
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(sha256))
            return;

        var fileName = Path.GetFileName(item.ImageFilePath);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "image.png";

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO item_images(itemId, fileName, blobSha256, sizeBytes, bytes) VALUES ($id, $name, $sha256, $sizeBytes, NULL);";
        cmd.Parameters.AddWithValue("$id", item.Id.ToString("D"));
        cmd.Parameters.AddWithValue("$name", fileName);
        cmd.Parameters.AddWithValue("$sha256", sha256);
        cmd.Parameters.AddWithValue("$sizeBytes", sizeBytes);
        try
        {
            cmd.ExecuteNonQuery();
            blobs[sha256] = new SnapshotBlob(sha256, item.ImageFilePath!, sizeBytes);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
        }
    }

    private static void TryInsertFiles(
        SqliteConnection conn,
        SqliteTransaction tx,
        ClipboardItem item,
        Dictionary<string, SnapshotBlob> blobs,
        CancellationToken ct)
    {
        var cachedDir = Path.Combine(AppPaths.FilesRoot, item.Id.ToString("N"));
        var cachedFiles = new List<(int idx, string name, string path)>();

        try
        {
            if (Directory.Exists(cachedDir))
            {
                foreach (var p in Directory.GetFiles(cachedDir))
                {
                    var name = Path.GetFileName(p);
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    var idx = TryParseIndexPrefix(name);
                    if (idx < 0)
                        continue;

                    var shortName = StripIndexPrefix(name);
                    if (string.IsNullOrWhiteSpace(shortName))
                        shortName = name;

                    cachedFiles.Add((idx, shortName, p));
                }
            }
        }
        catch
        {
        }

        if (cachedFiles.Count == 0)
        {
            var paths = item.FilePaths?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            if (paths is null || paths.Count == 0)
                return;

            for (var i = 0; i < paths.Count; i++)
            {
                var src = paths[i];
                if (string.IsNullOrWhiteSpace(src) || !File.Exists(src))
                    continue;

                var name = Path.GetFileName(src);
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                cachedFiles.Add((i, name, src));
            }
        }

        foreach (var f in cachedFiles.OrderBy(x => x.idx))
        {
            ct.ThrowIfCancellationRequested();

            string sha256;
            long sizeBytes;
            try
            {
                var fileInfo = new FileInfo(f.path);
                sizeBytes = fileInfo.Length;
                if (sizeBytes <= 0)
                    continue;
                sha256 = ComputeFileSha256(f.path);
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(sha256))
                continue;

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO item_files(itemId, idx, fileName, blobSha256, sizeBytes, bytes) VALUES ($id, $idx, $name, $sha256, $sizeBytes, NULL);";
            cmd.Parameters.AddWithValue("$id", item.Id.ToString("D"));
            cmd.Parameters.AddWithValue("$idx", f.idx);
            cmd.Parameters.AddWithValue("$name", f.name);
            cmd.Parameters.AddWithValue("$sha256", sha256);
            cmd.Parameters.AddWithValue("$sizeBytes", sizeBytes);
            try
            {
                cmd.ExecuteNonQuery();
                blobs[sha256] = new SnapshotBlob(sha256, f.path, sizeBytes);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
            }
        }
    }

    private static async Task<string?> ReadMetaAsync(SqliteConnection conn, string key, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $k LIMIT 1;";
        cmd.Parameters.AddWithValue("$k", key);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result?.ToString();
    }

    private static async Task<string?> ReadSettingsJsonAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT json FROM settings LIMIT 1;";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result?.ToString();
    }

    private static async Task<List<ClipboardItem>> ReadItemsAsync(SqliteConnection conn, CancellationToken ct)
    {
        var items = new List<ClipboardItem>();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT position, id, kind, capturedAt, approxBytes, text, filePathsJson, pinned, copyCount,
       firstCapturedAt, note, contentHash, sourceAppName, sourceAppPath
FROM items
ORDER BY position ASC;
";

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = Guid.Parse(reader.GetString(1));
            var kind = (ClipboardContentKind)reader.GetInt32(2);
            var capturedAt = DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var approxBytes = reader.GetInt64(4);
            var text = reader.IsDBNull(5) ? null : reader.GetString(5);
            var filePathsJson = reader.IsDBNull(6) ? null : reader.GetString(6);
            var pinned = reader.GetInt64(7) != 0;
            var copyCount = reader.GetInt32(8);
            DateTimeOffset? firstCapturedAt = reader.IsDBNull(9)
                ? (DateTimeOffset?)null
                : DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var note = reader.IsDBNull(10) ? null : reader.GetString(10);
            var contentHash = reader.IsDBNull(11) ? null : reader.GetString(11);
            var sourceAppName = reader.IsDBNull(12) ? null : reader.GetString(12);
            var sourceAppPath = reader.IsDBNull(13) ? null : reader.GetString(13);

            IReadOnlyList<string>? filePaths = null;
            if (!string.IsNullOrWhiteSpace(filePathsJson))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<List<string>>(filePathsJson);
                    if (parsed is { Count: > 0 })
                        filePaths = parsed;
                }
                catch
                {
                }
            }

            items.Add(new ClipboardItem(
                id,
                kind,
                capturedAt,
                approxBytes,
                text,
                null,
                filePaths,
                pinned,
                copyCount,
                firstCapturedAt,
                note,
                contentHash,
                sourceAppName,
                sourceAppPath));
        }

        return items;
    }

    private sealed record SnapshotImageRecord(string FileName, string? BlobSha256, long? SizeBytes, byte[]? Bytes);

    private sealed record SnapshotFileRecord(int Idx, string FileName, string? BlobSha256, long? SizeBytes, byte[]? Bytes);

    private static async Task<SnapshotImageRecord?> ReadImageAsync(SqliteConnection conn, Guid itemId, int schemaVersion, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = schemaVersion >= 2
            ? "SELECT fileName, blobSha256, sizeBytes, bytes FROM item_images WHERE itemId = $id LIMIT 1;"
            : "SELECT fileName, bytes FROM item_images WHERE itemId = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", itemId.ToString("D"));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        if (schemaVersion >= 2)
        {
            var fileName = reader.GetString(0);
            var blobSha256 = reader.IsDBNull(1) ? null : reader.GetString(1);
            long? sizeBytes = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);
            var bytes = reader.IsDBNull(3) ? null : (byte[])reader[3];
            return new SnapshotImageRecord(fileName, blobSha256, sizeBytes, bytes);
        }

        return new SnapshotImageRecord(reader.GetString(0), null, null, (byte[])reader[1]);
    }

    private static async Task<List<SnapshotFileRecord>> ReadFilesAsync(SqliteConnection conn, Guid itemId, int schemaVersion, CancellationToken ct)
    {
        var list = new List<SnapshotFileRecord>();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = schemaVersion >= 2
            ? "SELECT idx, fileName, blobSha256, sizeBytes, bytes FROM item_files WHERE itemId = $id ORDER BY idx ASC;"
            : "SELECT idx, fileName, bytes FROM item_files WHERE itemId = $id ORDER BY idx ASC;";
        cmd.Parameters.AddWithValue("$id", itemId.ToString("D"));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (schemaVersion >= 2)
            {
                list.Add(new SnapshotFileRecord(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : (byte[])reader[4]));
            }
            else
            {
                list.Add(new SnapshotFileRecord(reader.GetInt32(0), reader.GetString(1), null, null, (byte[])reader[2]));
            }
        }

        return list;
    }

    private static string WriteImageFile(string preferredFileName, byte[] bytes)
    {
        Directory.CreateDirectory(AppPaths.ImagesRoot);

        var ext = string.Empty;
        try
        {
            ext = Path.GetExtension(preferredFileName) ?? string.Empty;
        }
        catch
        {
            ext = string.Empty;
        }

        if (string.IsNullOrWhiteSpace(ext))
            ext = ".png";

        var fileName = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "_" + Guid.NewGuid().ToString("N") + ext;
        var path = Path.Combine(AppPaths.ImagesRoot, fileName);

        try
        {
            File.WriteAllBytes(path, bytes);
            return path;
        }
        catch
        {
            return path;
        }
    }

    private static async Task<string?> WriteImportedImageAsync(
        SnapshotImageRecord image,
        Func<string, CancellationToken, Task<string?>>? ensureBlobPathAsync,
        CancellationToken ct)
    {
        if (image.Bytes is { Length: > 0 })
            return WriteImageFile(image.FileName, image.Bytes);

        if (string.IsNullOrWhiteSpace(image.BlobSha256))
            return null;

        var blobPath = await EnsureLocalBlobPathAsync(image.BlobSha256, ensureBlobPathAsync, ct);
        if (string.IsNullOrWhiteSpace(blobPath) || !File.Exists(blobPath))
            return null;

        try
        {
            var bytes = await File.ReadAllBytesAsync(blobPath, ct);
            return WriteImageFile(image.FileName, bytes);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> WriteImportedFileAsync(
        string destinationPath,
        SnapshotFileRecord file,
        Func<string, CancellationToken, Task<string?>>? ensureBlobPathAsync,
        CancellationToken ct)
    {
        try
        {
            if (file.Bytes is { Length: > 0 })
            {
                await File.WriteAllBytesAsync(destinationPath, file.Bytes, ct);
                return true;
            }

            if (string.IsNullOrWhiteSpace(file.BlobSha256))
                return false;

            var blobPath = await EnsureLocalBlobPathAsync(file.BlobSha256, ensureBlobPathAsync, ct);
            if (string.IsNullOrWhiteSpace(blobPath) || !File.Exists(blobPath))
                return false;

            await using var input = new FileStream(blobPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            await using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string?> EnsureLocalBlobPathAsync(
        string blobSha256,
        Func<string, CancellationToken, Task<string?>>? ensureBlobPathAsync,
        CancellationToken ct)
    {
        var localPath = GetLocalBlobPath(blobSha256);
        if (File.Exists(localPath))
            return localPath;

        if (ensureBlobPathAsync is null)
            return null;

        var fetchedPath = await ensureBlobPathAsync(blobSha256, ct);
        if (!string.IsNullOrWhiteSpace(fetchedPath) && File.Exists(fetchedPath))
            return fetchedPath;

        return File.Exists(localPath) ? localPath : null;
    }

    public static string GetLocalBlobPath(string blobSha256)
    {
        var normalized = NormalizeSha256(blobSha256);
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("blob hash is empty", nameof(blobSha256));

        var bucket1 = normalized.Substring(0, 2);
        var bucket2 = normalized.Substring(2, 2);
        var dir = Path.Combine(AppPaths.BlobsRoot, bucket1, bucket2);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, normalized + ".blob");
    }

    private static string NormalizeSha256(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static string ComputeFileSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static void ReplaceItem(List<ClipboardItem> items, Guid id, Func<ClipboardItem, ClipboardItem> map)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Id == id)
            {
                items[i] = map(items[i]);
                return;
            }
        }
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

    private static AppSettings CloneSettings(AppSettings src)
    {
        var clone = new AppSettings();
        CopySettings(clone, src);
        return clone;
    }

    private static int TryParseIndexPrefix(string name)
    {
        if (name.Length < 5)
            return -1;

        var prefix = name.Substring(0, 4);
        if (!int.TryParse(prefix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
            return -1;

        if (name.Length >= 5 && name[4] != '_')
            return -1;

        return idx;
    }

    private static string StripIndexPrefix(string name)
    {
        if (name.Length <= 5)
            return string.Empty;
        return name.Substring(5);
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "file";

        var invalid = Path.GetInvalidFileNameChars();
        var arr = name.ToCharArray();
        for (var i = 0; i < arr.Length; i++)
        {
            if (invalid.Contains(arr[i]))
                arr[i] = '_';
        }

        return new string(arr);
    }

    private static void TryDeleteWithRetry(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        for (var i = 0; i < 10; i++)
        {
            ct.ThrowIfCancellationRequested();

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

    private static void MoveWithRetry(string src, string dst, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(dst))
            throw new ArgumentException("invalid path");

        for (var i = 0; i < 12; i++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                File.Move(src, dst, overwrite: true);
                return;
            }
            catch (IOException) when (i < 11)
            {
                Thread.Sleep(150);
            }
            catch (UnauthorizedAccessException) when (i < 11)
            {
                Thread.Sleep(150);
            }
        }

        File.Move(src, dst, overwrite: true);
    }
}

public readonly record struct SnapshotImportResult(int ItemCount, int ImageCount, int FileCount);
