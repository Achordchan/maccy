using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace maccy.Services;

public static class DragFileHelper
{
    private const string NewFileFormatName = "File";

    public static bool HasFileData(IDataObject data)
    {
        if (data.Contains(DataFormats.FileNames))
            return true;

        if (data.Contains(DataFormats.Files))
            return true;

        // Avalonia 11+ recommends DataFormat.File; keep a string fallback to avoid hard dependency.
        if (data.Contains(NewFileFormatName))
            return true;

        return false;
    }

    public static IReadOnlyList<string> GetFilePaths(IDataObject data)
    {
        if (data.Contains(DataFormats.FileNames))
        {
            var raw = data.Get(DataFormats.FileNames);
            if (raw is IEnumerable<string> enumerable)
                return enumerable.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            if (raw is string[] arr)
                return arr.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        }

        var storage = data.Get(DataFormats.Files) ?? data.Get(NewFileFormatName);
        if (storage is IEnumerable<IStorageItem> storageItems)
        {
            var paths = storageItems
                .Select(i => i.TryGetLocalPath())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Cast<string>()
                .ToList();
            return paths;
        }

        if (storage is IEnumerable<string> rawPaths)
            return rawPaths.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();

        return new List<string>();
    }
}
