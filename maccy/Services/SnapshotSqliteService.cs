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
    private const int SchemaVersion = 1;

    public async Task ExportAsync(string snapshotPath, CancellationToken ct = default)
    {
        await Task.Run(async () =>
        {
            if (string.IsNullOrWhiteSpace(snapshotPath))
                throw new ArgumentException("snapshotPath is empty", nameof(snapshotPath));

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
PRAGMA journal_mode=DELETE;
PRAGMA synchronous=FULL;

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
  bytes BLOB NOT NULL
);

CREATE TABLE item_files (
  itemId TEXT NOT NULL,
  idx INTEGER NOT NULL,
  fileName TEXT NOT NULL,
  bytes BLOB NOT NULL,
  PRIMARY KEY(itemId, idx)
);
";
                await cmd.ExecuteNonQueryAsync(ct);
            }

            using (var tx = conn.BeginTransaction())
            {
                await InsertMetaAsync(conn, tx, ct);
                await InsertSettingsAsync(conn, tx, ct);
                await InsertHistoryAsync(conn, tx, ct);

                tx.Commit();
            }

            await conn.CloseAsync();
        }, ct);
    }

    public async Task<SnapshotImportResult> ImportAsync(string snapshotPath, ClipboardHistoryService history, AppSettingsService settings, CancellationToken ct = default)
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
            if (!int.TryParse(schema, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed != SchemaVersion)
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
                    var img = await ReadImageAsync(conn, item.Id, ct);
                    if (img is not null)
                    {
                        var path = WriteImageFile(img.Value.FileName, img.Value.Bytes);
                        writtenImageCount++;
                        items[i] = item with { ImageFilePath = path };
                    }
                }

                if (item.Kind == ClipboardContentKind.FileList)
                {
                    var fileEntries = await ReadFilesAsync(conn, item.Id, ct);
                    if (fileEntries.Count > 0)
                    {
                        var dir = Path.Combine(AppPaths.FilesRoot, item.Id.ToString("N"));
                        Directory.CreateDirectory(dir);

                        foreach (var f in fileEntries)
                        {
                            var safeName = SanitizeFileName(f.FileName);
                            var dest = Path.Combine(dir, f.Idx.ToString("D4") + "_" + safeName);
                            try
                            {
                                File.WriteAllBytes(dest, f.Bytes);
                                writtenFileCount++;
                            }
                            catch
                            {
                            }
                        }
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

        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("$k", "createdAt");
        cmd.Parameters.AddWithValue("$v", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertSettingsAsync(SqliteConnection conn, SqliteTransaction tx, CancellationToken ct)
    {
        var settingsPath = Path.Combine(AppPaths.AppDataRoot, "settings.json");
        if (!File.Exists(settingsPath))
            return;

        string json;
        try
        {
            json = await File.ReadAllTextAsync(settingsPath, ct);
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

    private static async Task InsertHistoryAsync(SqliteConnection conn, SqliteTransaction tx, CancellationToken ct)
    {
        var historyPath = Path.Combine(AppPaths.AppDataRoot, "history.json");
        if (!File.Exists(historyPath))
            return;

        List<ClipboardItem>? items;
        try
        {
            await using var fs = File.OpenRead(historyPath);
            items = await JsonSerializer.DeserializeAsync<List<ClipboardItem>>(fs, cancellationToken: ct);
        }
        catch
        {
            items = null;
        }

        if (items is null || items.Count == 0)
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
                TryInsertImage(conn, tx, item, ct);
            }

            if (item.Kind == ClipboardContentKind.FileList)
            {
                TryInsertFiles(conn, tx, item, ct);
            }

            position++;
        }
    }

    private static void TryInsertImage(SqliteConnection conn, SqliteTransaction tx, ClipboardItem item, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(item.ImageFilePath) || !File.Exists(item.ImageFilePath))
            return;

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(item.ImageFilePath);
        }
        catch
        {
            return;
        }

        if (bytes.Length == 0)
            return;

        var fileName = Path.GetFileName(item.ImageFilePath);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "image.png";

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO item_images(itemId, fileName, bytes) VALUES ($id, $name, $bytes);";
        cmd.Parameters.AddWithValue("$id", item.Id.ToString("D"));
        cmd.Parameters.AddWithValue("$name", fileName);
        cmd.Parameters.Add("$bytes", SqliteType.Blob).Value = bytes;
        try
        {
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
        }
    }

    private static void TryInsertFiles(SqliteConnection conn, SqliteTransaction tx, ClipboardItem item, CancellationToken ct)
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

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(f.path);
            }
            catch
            {
                continue;
            }

            if (bytes.Length == 0)
                continue;

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO item_files(itemId, idx, fileName, bytes) VALUES ($id, $idx, $name, $bytes);";
            cmd.Parameters.AddWithValue("$id", item.Id.ToString("D"));
            cmd.Parameters.AddWithValue("$idx", f.idx);
            cmd.Parameters.AddWithValue("$name", f.name);
            cmd.Parameters.Add("$bytes", SqliteType.Blob).Value = bytes;
            try
            {
                cmd.ExecuteNonQuery();
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

    private static async Task<(string FileName, byte[] Bytes)?> ReadImageAsync(SqliteConnection conn, Guid itemId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT fileName, bytes FROM item_images WHERE itemId = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", itemId.ToString("D"));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var fileName = reader.GetString(0);
        var bytes = (byte[])reader[1];
        return (fileName, bytes);
    }

    private static async Task<List<(int Idx, string FileName, byte[] Bytes)>> ReadFilesAsync(SqliteConnection conn, Guid itemId, CancellationToken ct)
    {
        var list = new List<(int Idx, string FileName, byte[] Bytes)>();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT idx, fileName, bytes FROM item_files WHERE itemId = $id ORDER BY idx ASC;";
        cmd.Parameters.AddWithValue("$id", itemId.ToString("D"));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var idx = reader.GetInt32(0);
            var fileName = reader.GetString(1);
            var bytes = (byte[])reader[2];
            list.Add((Idx: idx, FileName: fileName, Bytes: bytes));
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
