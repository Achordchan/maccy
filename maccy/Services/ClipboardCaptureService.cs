using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using maccy.Models;

namespace maccy.Services;

public sealed class ClipboardCaptureService
{
    private DateTimeOffset _lastWriteByUsAt = DateTimeOffset.MinValue;

    private readonly IClipboard _clipboard;
    private readonly Func<AppIdentity?>? _foregroundAppResolver;

    public bool CaptureText { get; set; } = true;

    public bool CaptureImages { get; set; } = true;

    public bool CaptureFiles { get; set; } = true;

    public ClipboardHistoryService History { get; }

    public ClipboardCaptureService(ClipboardHistoryService history, IClipboard clipboard, Func<AppIdentity?>? foregroundAppResolver = null)
    {
        History = history;
        _clipboard = clipboard;
        _foregroundAppResolver = foregroundAppResolver;
    }

    public void MarkWriteByUs()
    {
        _lastWriteByUsAt = DateTimeOffset.UtcNow;
    }

    public async Task CaptureAsync()
    {
        if ((DateTimeOffset.UtcNow - _lastWriteByUsAt) < TimeSpan.FromMilliseconds(250))
            return;

        var formats = (await _clipboard.GetFormatsAsync()).ToList();
        var formatSet = new HashSet<string>(formats, StringComparer.OrdinalIgnoreCase);

        var captured = false;

        if (CaptureText && (formatSet.Contains("Text") || formatSet.Contains("UnicodeText") || formatSet.Contains("text/plain")))
        {
            var text = await _clipboard.GetTextAsync();
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (History.MergeDuplicates && History.Items.Count > 0 && History.Items[0].Kind == ClipboardContentKind.Text && History.Items[0].Text == text)
                    return;

                History.Add(new ClipboardItem(
                    Guid.NewGuid(),
                    ClipboardContentKind.Text,
                    DateTimeOffset.Now,
                    text.Length * 2,
                    text,
                    null,
                    null,
                    false,
                    FirstCapturedAt: DateTimeOffset.Now,
                    ContentHash: ComputeHash("text", text),
                    SourceAppName: _foregroundAppResolver?.Invoke()?.Name,
                    SourceAppPath: _foregroundAppResolver?.Invoke()?.Path));

                captured = true;
                return;
            }
        }

        if (CaptureFiles)
        {
            var fileFormats = new[] { "FileNames", "Files" };
            var fileFormat = fileFormats.FirstOrDefault(f => formatSet.Contains(f));
            if (fileFormat is not null)
            {
                var data = await _clipboard.GetDataAsync(fileFormat);
                var paths = TryCoerceStringList(data);
                if (paths is { Count: > 0 })
                {
                    if (CaptureImages && paths.Count == 1 && IsImagePath(paths[0]) && File.Exists(paths[0]))
                    {
                        try
                        {
                            await using var fs = File.OpenRead(paths[0]);
                            var bmp = new Bitmap(fs);
                            var path = SaveBitmapPng(bmp);
                            History.Add(new ClipboardItem(
                                Guid.NewGuid(),
                                ClipboardContentKind.Image,
                                DateTimeOffset.Now,
                                new FileInfo(path).Length,
                                null,
                                path,
                                null,
                                false,
                                FirstCapturedAt: DateTimeOffset.Now,
                                ContentHash: ComputeFileHash("img", path),
                                SourceAppName: _foregroundAppResolver?.Invoke()?.Name,
                                SourceAppPath: _foregroundAppResolver?.Invoke()?.Path));
                            captured = true;
                            return;
                        }
                        catch
                        {
                        }
                    }

                    var bytes = paths.Sum(p => (long)p.Length * 2);
                    History.Add(new ClipboardItem(
                        Guid.NewGuid(),
                        ClipboardContentKind.FileList,
                        DateTimeOffset.Now,
                        bytes,
                        null,
                        null,
                        paths,
                        false,
                        FirstCapturedAt: DateTimeOffset.Now,
                        ContentHash: ComputeHash("files", string.Join("\n", paths)),
                        SourceAppName: _foregroundAppResolver?.Invoke()?.Name,
                        SourceAppPath: _foregroundAppResolver?.Invoke()?.Path));

                    captured = true;
                    return;
                }
            }
        }

        if (CaptureImages)
        {
            var imageFormats = new[] { "Bitmap", "image/png", "PNG", "DeviceIndependentBitmap", "DIB" };
            var imageFormat = imageFormats.FirstOrDefault(f => formatSet.Contains(f));
            if (imageFormat is not null)
            {
                var data = await _clipboard.GetDataAsync(imageFormat);

                var bmp = TryCoerceBitmap(data);
                if (bmp is null)
                {
                    // if a format matches but we can't coerce, continue trying other formats
                }
                else
                {
                    var path = SaveBitmapPng(bmp);
                    History.Add(new ClipboardItem(
                        Guid.NewGuid(),
                        ClipboardContentKind.Image,
                        DateTimeOffset.Now,
                        new FileInfo(path).Length,
                        null,
                        path,
                        null,
                        false,
                        FirstCapturedAt: DateTimeOffset.Now,
                        ContentHash: ComputeFileHash("img", path)));

                    captured = true;
                    return;
                }
            }

            foreach (var fmt in imageFormats)
            {
                if (!formatSet.Contains(fmt))
                    continue;

                var data = await _clipboard.GetDataAsync(fmt);
                var bmp = TryCoerceBitmap(data);
                if (bmp is null)
                    continue;

                var path = SaveBitmapPng(bmp);
                History.Add(new ClipboardItem(
                    Guid.NewGuid(),
                    ClipboardContentKind.Image,
                    DateTimeOffset.Now,
                    new FileInfo(path).Length,
                    null,
                    path,
                    null,
                    false,
                    FirstCapturedAt: DateTimeOffset.Now,
                    ContentHash: ComputeFileHash("img", path)));

                captured = true;
                return;
            }

            // Fallback: try any remaining clipboard format. Many screenshot tools (e.g. WeChat) expose images
            // under non-standard names like CF_DIBV5 / Format17 / image/bmp.
            foreach (var fmt in formats)
            {
                if (string.IsNullOrWhiteSpace(fmt))
                    continue;

                if (fmt.Equals("Text", StringComparison.OrdinalIgnoreCase) ||
                    fmt.Equals("UnicodeText", StringComparison.OrdinalIgnoreCase) ||
                    fmt.Equals("text/plain", StringComparison.OrdinalIgnoreCase) ||
                    fmt.Equals("FileNames", StringComparison.OrdinalIgnoreCase) ||
                    fmt.Equals("Files", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                object? data;
                try
                {
                    data = await _clipboard.GetDataAsync(fmt);
                }
                catch
                {
                    continue;
                }

                Bitmap? bmp;
                try
                {
                    bmp = TryCoerceBitmap(data);
                }
                catch
                {
                    bmp = null;
                }

                if (bmp is null)
                    continue;

                var path = SaveBitmapPng(bmp);
                History.Add(new ClipboardItem(
                    Guid.NewGuid(),
                    ClipboardContentKind.Image,
                    DateTimeOffset.Now,
                    new FileInfo(path).Length,
                    null,
                    path,
                    null,
                    false,
                    ContentHash: ComputeFileHash("img", path)));

                captured = true;
                return;
            }
        }

        if (!captured)
            DebugLogFormats(formats);
    }

    private static void DebugLogFormats(IReadOnlyList<string> formats)
    {
#if DEBUG
        try
        {
            var path = Path.Combine(AppPaths.AppDataRoot, "capture_debug.log");
            var line = DateTimeOffset.Now.ToString("O") + " | formats: " + string.Join(", ", formats) + Environment.NewLine;
            File.AppendAllText(path, line);
        }
        catch
        {
        }
#endif
    }

    private static List<string>? TryCoerceStringList(object? data)
    {
        if (data is null)
            return null;

        if (data is string[] arr)
            return arr.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();

        if (data is IEnumerable<string> enumerable)
            return enumerable.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();

        if (data is IEnumerable<IStorageItem> storageItems)
        {
            var paths = storageItems
                .Select(i => i.TryGetLocalPath())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Cast<string>()
                .ToList();
            return paths.Count > 0 ? paths : null;
        }

        return null;
    }

    private static bool IsImagePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var ext = Path.GetExtension(path);
        if (string.IsNullOrWhiteSpace(ext))
            return false;

        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private static byte[]? TryWrapDibAsBmp(byte[] dib)
    {
        if (dib.Length < 40)
            return null;

        uint headerSize = ReadUInt32LE(dib, 0);
        if (headerSize < 40 || headerSize > dib.Length)
            return null;

        ushort bpp = ReadUInt16LE(dib, 14);
        uint clrUsed = ReadUInt32LE(dib, 32);

        uint colors = 0;
        if (bpp is 1 or 4 or 8)
            colors = clrUsed != 0 ? clrUsed : (uint)(1 << bpp);

        uint paletteSize = colors * 4;
        uint pixelOffset = 14 + headerSize + paletteSize;
        if (pixelOffset >= (uint)dib.Length + 14)
            return null;

        uint fileSize = 14 + (uint)dib.Length;

        var bmp = new byte[fileSize];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        WriteUInt32LE(bmp, 2, fileSize);
        WriteUInt32LE(bmp, 10, pixelOffset);
        Buffer.BlockCopy(dib, 0, bmp, 14, dib.Length);
        return bmp;
    }

    private static ushort ReadUInt16LE(byte[] data, int offset)
    {
        return (ushort)(data[offset] | (data[offset + 1] << 8));
    }

    private static uint ReadUInt32LE(byte[] data, int offset)
    {
        return (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
    }

    private static void WriteUInt32LE(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value & 0xFF);
        data[offset + 1] = (byte)((value >> 8) & 0xFF);
        data[offset + 2] = (byte)((value >> 16) & 0xFF);
        data[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static Bitmap? TryCoerceBitmap(object? data)
    {
        if (data is null)
            return null;

        if (data is Bitmap bmp)
            return bmp;

        if (data is byte[] bytes)
        {
            try
            {
                using var ms = new MemoryStream(bytes);
                return new Bitmap(ms);
            }
            catch
            {
                var bmpBytes = TryWrapDibAsBmp(bytes);
                if (bmpBytes is null)
                    return null;

                using var ms = new MemoryStream(bmpBytes);
                return new Bitmap(ms);
            }
        }

        if (data is Stream stream)
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            ms.Position = 0;
            return new Bitmap(ms);
        }

        return null;
    }

    private static string SaveBitmapPng(Bitmap bmp)
    {
        var fileName = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "_" + Guid.NewGuid().ToString("N") + ".png";
        var path = Path.Combine(AppPaths.ImagesRoot, fileName);

        using var fs = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        bmp.Save(fs);
        return path;
    }

    private static string ComputeHash(string prefix, string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload ?? string.Empty);
        var hash = SHA256.HashData(bytes);
        return prefix + ":" + Convert.ToHexString(hash);
    }

    private static string? ComputeFileHash(string prefix, string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var hash = SHA256.HashData(fs);
            return prefix + ":" + Convert.ToHexString(hash);
        }
        catch
        {
            return null;
        }
    }
}
