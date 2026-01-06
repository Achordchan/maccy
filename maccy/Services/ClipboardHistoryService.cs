using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using maccy.Models;

namespace maccy.Services;

public sealed class ClipboardHistoryService
{
    public ObservableCollection<ClipboardItem> Items { get; } = new();

    public event Action? Changed;

    public int? MaxItems { get; set; } = 200;

    public long? MaxBytes { get; set; } = 300L * 1024 * 1024;

    public bool MergeDuplicates { get; set; } = true;

    public bool ExcludePinnedFromLimits { get; set; } = true;

    public void Add(ClipboardItem item)
    {
        if (MergeDuplicates && !string.IsNullOrWhiteSpace(item.ContentHash))
        {
            var idxExisting = Items.ToList().FindIndex(x =>
                x.Kind == item.Kind &&
                !string.IsNullOrWhiteSpace(x.ContentHash) &&
                string.Equals(x.ContentHash, item.ContentHash, StringComparison.OrdinalIgnoreCase));

            if (idxExisting >= 0)
            {
                var existing = Items[idxExisting];

                if (existing.Kind == ClipboardContentKind.Image &&
                    !string.IsNullOrWhiteSpace(item.ImageFilePath) &&
                    !string.IsNullOrWhiteSpace(existing.ImageFilePath) &&
                    !string.Equals(item.ImageFilePath, existing.ImageFilePath, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(item.ImageFilePath))
                {
                    try
                    {
                        File.Delete(item.ImageFilePath);
                    }
                    catch
                    {
                    }
                }

                if (existing.Kind == ClipboardContentKind.FileList)
                {
                    try
                    {
                        var dir = Path.Combine(AppPaths.FilesRoot, item.Id.ToString("N"));
                        if (Directory.Exists(dir))
                            Directory.Delete(dir, recursive: true);
                    }
                    catch
                    {
                    }
                }

                var first = existing.FirstCapturedAt ?? existing.CapturedAt;
                var nextCount = Math.Max(1, existing.CopyCount) + 1;

                var updated = existing with
                {
                    CapturedAt = item.CapturedAt,
                    ApproxBytes = item.ApproxBytes,
                    Text = item.Text ?? existing.Text,
                    ImageFilePath = existing.ImageFilePath ?? item.ImageFilePath,
                    FilePaths = item.FilePaths ?? existing.FilePaths,
                    Pinned = existing.Pinned,
                    CopyCount = nextCount,
                    FirstCapturedAt = first,
                    Note = existing.Note,
                    ContentHash = existing.ContentHash ?? item.ContentHash,
                };

                Items.RemoveAt(idxExisting);
                Items.Insert(0, updated);
                EnforceLimits();
                Changed?.Invoke();
                return;
            }
        }

        Items.Insert(0, item);
        EnforceLimits();
        Changed?.Invoke();
    }

    public void UpdateNote(Guid id, string? note)
    {
        var idx = Items.ToList().FindIndex(x => x.Id == id);
        if (idx < 0)
            return;

        var item = Items[idx];
        var normalized = string.IsNullOrWhiteSpace(note) ? null : note;
        Items[idx] = item with { Note = normalized };
        Changed?.Invoke();
    }

    public void Touch(Guid id)
    {
        var idx = Items.ToList().FindIndex(x => x.Id == id);
        if (idx < 0)
            return;

        var existing = Items[idx];
        var first = existing.FirstCapturedAt ?? existing.CapturedAt;
        var nextCount = Math.Max(1, existing.CopyCount) + 1;
        var updated = existing with
        {
            CapturedAt = DateTimeOffset.Now,
            CopyCount = nextCount,
            FirstCapturedAt = first,
        };

        Items.RemoveAt(idx);
        Items.Insert(0, updated);
        EnforceLimits();
        Changed?.Invoke();
    }

    public void Remove(Guid id)
    {
        var idx = Items.ToList().FindIndex(x => x.Id == id);
        if (idx < 0)
            return;

        var item = Items[idx];
        Items.RemoveAt(idx);

        if (!string.IsNullOrWhiteSpace(item.ImageFilePath) && File.Exists(item.ImageFilePath))
        {
            try
            {
                File.Delete(item.ImageFilePath);
            }
            catch
            {
            }
        }

        if (item.Kind == ClipboardContentKind.FileList)
        {
            try
            {
                var dir = Path.Combine(AppPaths.FilesRoot, item.Id.ToString("N"));
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch
            {
            }
        }

        Changed?.Invoke();
    }

    public void ReplaceAll(IReadOnlyList<ClipboardItem> items)
    {
        var newImages = items
            .Select(x => x.ImageFilePath)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var newFileDirs = items
            .Where(x => x.Kind == ClipboardContentKind.FileList)
            .Select(x => x.Id)
            .Distinct()
            .ToHashSet();

        var toDeleteImages = Items
            .Select(x => x.ImageFilePath)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(x => x is not null && !newImages.Contains(x))
            .ToList();

        var toDeleteFileDirs = Items
            .Where(x => x.Kind == ClipboardContentKind.FileList)
            .Select(x => x.Id)
            .Distinct()
            .Where(x => !newFileDirs.Contains(x))
            .ToList();

        Items.Clear();
        foreach (var item in items)
            Items.Add(item);
        EnforceLimits();
        Changed?.Invoke();

        foreach (var path in toDeleteImages)
        {
            try
            {
                if (path is not null && File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        foreach (var id in toDeleteFileDirs)
        {
            try
            {
                var dir = Path.Combine(AppPaths.FilesRoot, id.ToString("N"));
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch
            {
            }
        }
    }

    public void ClearAll()
    {
        if (Items.Count == 0)
            return;

        var toDelete = Items
            .Select(x => x.ImageFilePath)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var toDeleteFileDirs = Items
            .Where(x => x.Kind == ClipboardContentKind.FileList)
            .Select(x => x.Id)
            .Distinct()
            .ToList();

        Items.Clear();

        foreach (var path in toDelete)
        {
            try
            {
                if (path is not null && File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        foreach (var id in toDeleteFileDirs)
        {
            try
            {
                var dir = Path.Combine(AppPaths.FilesRoot, id.ToString("N"));
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch
            {
            }
        }

        Changed?.Invoke();
    }

    public void TogglePinned(Guid id)
    {
        var idx = Items.ToList().FindIndex(x => x.Id == id);
        if (idx < 0)
            return;

        var item = Items[idx];
        Items[idx] = item with { Pinned = !item.Pinned };
        Changed?.Invoke();
    }

    public void MoveBefore(Guid movedId, Guid? beforeId)
    {
        var list = Items.ToList();
        var fromIdx = list.FindIndex(x => x.Id == movedId);
        if (fromIdx < 0)
            return;

        var item = list[fromIdx];
        list.RemoveAt(fromIdx);

        int toIdx;
        if (beforeId is null)
        {
            toIdx = list.Count;
        }
        else
        {
            var targetIdx = list.FindIndex(x => x.Id == beforeId.Value);
            toIdx = targetIdx >= 0 ? targetIdx : list.Count;
        }

        list.Insert(toIdx, item);

        Items.Clear();
        foreach (var it in list)
            Items.Add(it);

        Changed?.Invoke();
    }

    private void EnforceLimits()
    {
        if (Items.Count == 0)
            return;

        var maxItems = MaxItems;
        var maxBytes = MaxBytes;

        while (true)
        {
            var nonPinnedCount = ExcludePinnedFromLimits ? Items.Count(x => !x.Pinned) : Items.Count;
            var nonPinnedBytes = ExcludePinnedFromLimits ? Items.Where(x => !x.Pinned).Sum(x => x.ApproxBytes) : Items.Sum(x => x.ApproxBytes);

            var tooMany = maxItems is not null && nonPinnedCount > maxItems.Value;
            var tooBig = maxBytes is not null && nonPinnedBytes > maxBytes.Value;

            if (!tooMany && !tooBig)
                break;

            var last = Items.LastOrDefault(x => !x.Pinned);
            if (last is null)
                break;

            Remove(last.Id);
        }
    }
}
