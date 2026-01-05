using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Input.Platform;
using maccy.Models;

namespace maccy.Services;

public sealed class ClipboardApplyService
{
    private readonly IClipboard _clipboard;
    private readonly ClipboardCaptureService _capture;

    public ClipboardApplyService(IClipboard clipboard, ClipboardCaptureService capture)
    {
        _clipboard = clipboard;
        _capture = capture;
    }

    public async Task ApplyAsync(ClipboardItem item)
    {
        _capture.MarkWriteByUs();

        switch (item.Kind)
        {
            case ClipboardContentKind.Text:
                await _clipboard.SetTextAsync(item.Text);
                return;

            case ClipboardContentKind.FileList:
            {
                var paths = item.FilePaths?.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
                if (paths is null || paths.Length == 0)
                    return;

                var resolved = paths
                    .Select((p, i) =>
                    {
                        try
                        {
                            var name = Path.GetFileName(p);
                            if (string.IsNullOrWhiteSpace(name))
                                return null;

                            var cached = Path.Combine(AppPaths.FilesRoot, item.Id.ToString("N"), i.ToString("D4") + "_" + name);
                            if (File.Exists(cached))
                                return cached;

                            if (!string.IsNullOrWhiteSpace(p) && File.Exists(p))
                                return p;

                            return null;
                        }
                        catch
                        {
                            return null;
                        }
                    })
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Cast<string>()
                    .ToArray();

                if (resolved.Length == 0)
                    return;

                var data = new DataObject();
                data.Set("FileNames", resolved);
                await _clipboard.SetDataObjectAsync(data);
                return;
            }

            case ClipboardContentKind.Image:
            {
                if (string.IsNullOrWhiteSpace(item.ImageFilePath) || !File.Exists(item.ImageFilePath))
                    return;

                var data = new DataObject();
                var pngBytes = await File.ReadAllBytesAsync(item.ImageFilePath);
                data.Set("image/png", pngBytes);
                data.Set("PNG", pngBytes);
                data.Set("FileNames", new[] { item.ImageFilePath });
                await _clipboard.SetDataObjectAsync(data);
                return;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(item.Kind), item.Kind, null);
        }
    }
}
