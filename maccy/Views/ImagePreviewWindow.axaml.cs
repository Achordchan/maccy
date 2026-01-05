using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using maccy.Models;
using DrawingIcon = System.Drawing.Icon;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingImageFormat = System.Drawing.Imaging.ImageFormat;

namespace maccy.Views;

public partial class ImagePreviewWindow : Window
{
    private static readonly object SourceIconCacheLock = new();
    private static readonly System.Collections.Generic.Dictionary<string, Bitmap?> SourceIconCache = new(StringComparer.OrdinalIgnoreCase);

    private Avalonia.Controls.Image? _image;
    private Button? _openOriginalButton;
    private Avalonia.Controls.Image? _sourceIcon;
    private TextBlock? _sourceText;
    private StackPanel? _sourcePanel;
    private ScrollViewer? _textScroll;
    private TextBlock? _previewText;
    private TextBlock? _metaLine;
    private TextBlock? _timeLine;
    private TextBlock? _noteLine;

    private Grid? _rootGrid;
    private Border? _previewDivider;

    private string? _currentImagePath;

    public event Action<bool>? HoverChanged;

    public ImagePreviewWindow()
    {
        InitializeComponent();
        _rootGrid = this.FindControl<Grid>("RootGrid");
        _previewDivider = this.FindControl<Border>("PreviewDivider");
        _image = this.FindControl<Image>("PreviewImage");
        _openOriginalButton = this.FindControl<Button>("OpenOriginalButton");
        _sourceIcon = this.FindControl<Image>("SourceIcon");
        _sourceText = this.FindControl<TextBlock>("SourceText");
        _sourcePanel = this.FindControl<StackPanel>("SourcePanel");
        _textScroll = this.FindControl<ScrollViewer>("TextScroll");
        _previewText = this.FindControl<TextBlock>("PreviewText");
        _metaLine = this.FindControl<TextBlock>("MetaLine");
        _timeLine = this.FindControl<TextBlock>("TimeLine");
        _noteLine = this.FindControl<TextBlock>("NoteLine");

        PointerEntered += (_, _) => HoverChanged?.Invoke(true);
        PointerExited += (_, _) => HoverChanged?.Invoke(false);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public void SetSource(IImage? source)
    {
        if (_image is null)
            return;

        _image.Source = source;
    }

    public void SetItem(ClipboardItem item, IImage? source, string? text = null)
    {
        var isImage = item.Kind == ClipboardContentKind.Image;
        var isText = item.Kind == ClipboardContentKind.Text;
        var isFile = item.Kind == ClipboardContentKind.FileList;

        _currentImagePath = isImage ? item.ImageFilePath : null;

        if (_image is not null)
            _image.IsVisible = isImage;

        if (_openOriginalButton is not null)
            _openOriginalButton.IsVisible = isImage && !string.IsNullOrWhiteSpace(_currentImagePath) && File.Exists(_currentImagePath);

        if (_textScroll is not null)
            _textScroll.IsVisible = isText;

        if (_previewText is not null)
        {
            _previewText.Text = isText ? (text ?? string.Empty) : string.Empty;
            _previewText.IsVisible = isText;
        }

        if (isImage)
            SetSource(source);
        else
            SetSource(null);

        try
        {
            if (_rootGrid is not null && _rootGrid.RowDefinitions.Count >= 3)
            {
                if (isFile)
                {
                    _rootGrid.RowDefinitions[0].Height = new Avalonia.Controls.GridLength(0);
                    if (_previewDivider is not null)
                        _previewDivider.IsVisible = false;
                }
                else
                {
                    _rootGrid.RowDefinitions[0].Height = new Avalonia.Controls.GridLength(1, Avalonia.Controls.GridUnitType.Star);
                    if (_previewDivider is not null)
                        _previewDivider.IsVisible = true;
                }
            }
        }
        catch
        {
        }

        var copyCount = Math.Max(1, item.CopyCount);
        var first = item.FirstCapturedAt ?? item.CapturedAt;
        var last = item.CapturedAt;

        var note = string.IsNullOrWhiteSpace(item.Note) ? "（无备注）" : item.Note;

        var meta = $"复制次数：{copyCount}";
        if (isImage && !string.IsNullOrWhiteSpace(item.ImageFilePath) && File.Exists(item.ImageFilePath))
        {
            try
            {
                var bytes = new FileInfo(item.ImageFilePath).Length;
                var kb = Math.Max(1, bytes / 1024);

                if (source is Bitmap bmp)
                {
                    meta += $" · {bmp.PixelSize.Width}×{bmp.PixelSize.Height} · {kb} KB";
                }
                else
                {
                    meta += $" · {kb} KB";
                }
            }
            catch
            {
            }
        }

        if (isFile)
        {
            try
            {
                var count = item.FilePaths?.Count(x => !string.IsNullOrWhiteSpace(x)) ?? 0;
                var bytes = 0L;
                if (item.FilePaths is not null)
                {
                    foreach (var p in item.FilePaths)
                    {
                        if (string.IsNullOrWhiteSpace(p) || !File.Exists(p))
                            continue;
                        bytes += new FileInfo(p).Length;
                    }
                }

                if (bytes <= 0)
                    bytes = Math.Max(0, item.ApproxBytes);

                meta += $" · {Math.Max(1, count)} 个文件 · {FormatBytes(bytes)}";
            }
            catch
            {
            }
        }

        _metaLine!.Text = meta;
        _timeLine!.Text = $"首次：{first:yyyy-MM-dd HH:mm:ss}  最后：{last:yyyy-MM-dd HH:mm:ss}";
        _noteLine!.Text = "备注：" + note;

        SetSourceInfo(item);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
            return "0 KB";

        var kb = bytes / 1024.0;
        if (kb < 1024)
            return $"{Math.Max(1, (long)Math.Round(kb))} KB";

        var mb = kb / 1024.0;
        if (mb < 1024)
            return $"{mb:0.#} MB";

        var gb = mb / 1024.0;
        return $"{gb:0.#} GB";
    }

    private void OnOpenOriginalClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentImagePath))
            return;

        if (!File.Exists(_currentImagePath))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(_currentImagePath)
            {
                UseShellExecute = true,
            });
        }
        catch
        {
        }
    }

    private void SetSourceInfo(ClipboardItem item)
    {
        if (_sourcePanel is null || _sourceText is null || _sourceIcon is null)
            return;

        if (string.IsNullOrWhiteSpace(item.SourceAppName) && string.IsNullOrWhiteSpace(item.SourceAppPath))
        {
            _sourcePanel.IsVisible = false;
            _sourceIcon.Source = null;
            _sourceText.Text = string.Empty;
            return;
        }

        _sourcePanel.IsVisible = true;
        _sourceText.Text = string.IsNullOrWhiteSpace(item.SourceAppName)
            ? (string.IsNullOrWhiteSpace(item.SourceAppPath) ? "(未知来源)" : Path.GetFileNameWithoutExtension(item.SourceAppPath))
            : item.SourceAppName;

        _sourceIcon.Source = null;

        if (string.IsNullOrWhiteSpace(item.SourceAppPath) || !File.Exists(item.SourceAppPath))
            return;

        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            Bitmap? cached;
            lock (SourceIconCacheLock)
            {
                SourceIconCache.TryGetValue(item.SourceAppPath, out cached);
            }

            if (cached is not null)
            {
                _sourceIcon.Source = cached;
                return;
            }

            using var icon = DrawingIcon.ExtractAssociatedIcon(item.SourceAppPath);
            if (icon is null)
            {
                lock (SourceIconCacheLock)
                {
                    SourceIconCache[item.SourceAppPath] = null;
                }
                return;
            }

            using var bmp = icon.ToBitmap();
            using var ms = new MemoryStream();
            bmp.Save(ms, DrawingImageFormat.Png);
            ms.Position = 0;

            var avaloniaBitmap = new Bitmap(ms);
            lock (SourceIconCacheLock)
            {
                SourceIconCache[item.SourceAppPath] = avaloniaBitmap;
            }

            _sourceIcon.Source = avaloniaBitmap;
        }
        catch
        {
            // ignore icon failures
        }
    }
}
