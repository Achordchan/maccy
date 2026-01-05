using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using maccy.Models;

namespace maccy.Services;

public sealed class ClipboardPersistenceService : IDisposable
{
    private readonly ClipboardHistoryService _history;
    private readonly string _filePath;

    private readonly object _gate = new();
    private Timer? _timer;
    private int _saveScheduled;

    public ClipboardPersistenceService(ClipboardHistoryService history)
    {
        _history = history;
        _filePath = Path.Combine(AppPaths.AppDataRoot, "history.json");

        _history.Changed += OnHistoryChanged;
    }

    public async Task LoadAsync()
    {
        if (!File.Exists(_filePath))
            return;

        try
        {
            await using var fs = File.OpenRead(_filePath);
            var items = await JsonSerializer.DeserializeAsync<List<ClipboardItem>>(fs);
            if (items is null)
                return;

            var normalized = items
                .Select(Normalize)
                .Where(x => x is not null)
                .Cast<ClipboardItem>()
                .ToList();

            _history.ReplaceAll(normalized);
        }
        catch
        {
        }
    }

    public void FlushSync()
    {
        try
        {
            SaveNow();
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        _history.Changed -= OnHistoryChanged;

        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }

        FlushSync();
    }

    private void OnHistoryChanged()
    {
        if (Interlocked.Exchange(ref _saveScheduled, 1) == 1)
            return;

        lock (_gate)
        {
            _timer ??= new Timer(_ =>
            {
                try
                {
                    SaveNow();
                }
                catch
                {
                }
                finally
                {
                    Interlocked.Exchange(ref _saveScheduled, 0);
                }
            });

            _timer.Change(350, Timeout.Infinite);
        }
    }

    private void SaveNow()
    {
        var items = _history.Items.ToList();

        var tmp = _filePath + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

        var json = JsonSerializer.Serialize(items);
        File.WriteAllText(tmp, json);

        if (File.Exists(_filePath))
            File.Replace(tmp, _filePath, null);
        else
            File.Move(tmp, _filePath);
    }

    private static ClipboardItem? Normalize(ClipboardItem item)
    {
        if (item.Kind == ClipboardContentKind.Image)
        {
            if (string.IsNullOrWhiteSpace(item.ImageFilePath) || !File.Exists(item.ImageFilePath))
                return null;

            var bytes = new FileInfo(item.ImageFilePath).Length;
            return item with { ApproxBytes = bytes };
        }

        if (item.Kind == ClipboardContentKind.Text)
        {
            if (string.IsNullOrWhiteSpace(item.Text))
                return null;

            return item with { ApproxBytes = item.Text.Length * 2 };
        }

        if (item.Kind == ClipboardContentKind.FileList)
        {
            var paths = item.FilePaths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
            if (paths is null || paths.Length == 0)
                return null;

            var hasAnySource = false;
            try
            {
                hasAnySource = paths.Any(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
            }
            catch
            {
                hasAnySource = false;
            }

            var cacheDir = Path.Combine(AppPaths.FilesRoot, item.Id.ToString("N"));
            var hasCache = false;
            try
            {
                hasCache = Directory.Exists(cacheDir) && Directory.GetFiles(cacheDir).Length > 0;
            }
            catch
            {
                hasCache = false;
            }

            if (!hasAnySource && !hasCache)
                return null;

            long bytes;
            try
            {
                bytes = hasCache
                    ? Directory.GetFiles(cacheDir).Sum(p => File.Exists(p) ? new FileInfo(p).Length : 0)
                    : 0;

                if (bytes <= 0)
                    bytes = paths.Sum(p => File.Exists(p) ? new FileInfo(p).Length : 0);
            }
            catch
            {
                bytes = 0;
            }

            if (bytes <= 0)
                return null;
            return item with { ApproxBytes = bytes, FilePaths = paths };
        }

        return item;
    }
}
