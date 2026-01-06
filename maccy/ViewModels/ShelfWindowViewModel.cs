using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Media.Imaging;
using maccy.Models;
using maccy.Services;

namespace maccy.ViewModels;

public partial class ShelfWindowViewModel : ViewModelBase
{
    private readonly FileIconService _iconService;

    public ObservableCollection<ShelfFileItemViewModel> Items { get; } = new();

    public ObservableCollection<ShelfStackCardViewModel> StackItems { get; } = new();

    public bool HasSingleItem => Items.Count == 1;

    public bool HasMultipleItems => Items.Count > 1;

    public bool ShowExpandedControls => Items.Count <= 1;

    public ShelfFileItemViewModel? PrimaryItem => Items.FirstOrDefault();

    public event Action? RequestClose;

    [ObservableProperty]
    private bool _pinned;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isGridView = true;

    public bool IsCompact => !IsExpanded;

    public bool ShowCompact => !IsExpanded;

    public bool ShowExpandedGrid => IsExpanded && IsGridView;

    public bool ShowExpandedList => IsExpanded && !IsGridView;

    public string TitleText
    {
        get
        {
            if (Items.Count == 0)
                return "放置你的内容项";

            if (Items.Count == 1)
                return Items[0].FileName;

            return $"{Items.Count} 个文件";
        }
    }

    public string TotalSizeText
    {
        get
        {
            var total = Items.Sum(x => Math.Max(0, x.SizeBytes));
            if (total <= 0)
                return "";
            return FormatBytes(total);
        }
    }

    public string CountBadgeText
    {
        get
        {
            if (Items.Count <= 1)
                return "";
            return $"{Items.Count} 档 >";
        }
    }

    public IRelayCommand CloseCommand { get; }

    public IRelayCommand ToggleExpandedCommand { get; }

    public IRelayCommand ToggleViewCommand { get; }

    public ShelfWindowViewModel(FileIconService iconService)
    {
        _iconService = iconService;
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke());
        ToggleExpandedCommand = new RelayCommand(() =>
        {
            if (Items.Count > 1)
                return;
            IsExpanded = !IsExpanded;
        });
        ToggleViewCommand = new RelayCommand(() =>
        {
            if (Items.Count > 1)
                return;
            IsGridView = !IsGridView;
        });

        Items.CollectionChanged += (_, _) =>
        {
            RefreshStack();
            OnPropertyChanged(nameof(TitleText));
            OnPropertyChanged(nameof(TotalSizeText));
            OnPropertyChanged(nameof(CountBadgeText));
            OnPropertyChanged(nameof(HasSingleItem));
            OnPropertyChanged(nameof(HasMultipleItems));
            OnPropertyChanged(nameof(ShowExpandedControls));
            OnPropertyChanged(nameof(PrimaryItem));

            if (Items.Count > 1)
            {
                IsGridView = false;
                IsExpanded = true;
            }
        };
    }

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCompact));
        OnPropertyChanged(nameof(ShowCompact));
        OnPropertyChanged(nameof(ShowExpandedGrid));
        OnPropertyChanged(nameof(ShowExpandedList));
    }

    partial void OnIsGridViewChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowExpandedGrid));
        OnPropertyChanged(nameof(ShowExpandedList));
    }

    public void ClearAndClose()
    {
        Items.Clear();
        StackItems.Clear();
        Pinned = false;
        RequestClose?.Invoke();
    }

    public void AddFiles(System.Collections.Generic.IReadOnlyList<string> paths)
    {
        var normalized = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalized.Count == 0)
            return;

        foreach (var p in normalized)
        {
            var name = System.IO.Path.GetFileName(p);
            if (string.IsNullOrWhiteSpace(name))
                name = p;

            var icon = _iconService.GetLargeIcon(p);
            var sizeBytes = TryGetSizeBytes(p);
            var (w, h) = TryGetImageDimensions(p);
            Items.Add(new ShelfFileItemViewModel(Guid.NewGuid(), p, name, icon, sizeBytes, w, h));
        }

        Pinned = true;
        RefreshStack();
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(TotalSizeText));
        OnPropertyChanged(nameof(CountBadgeText));
    }

    private void RefreshStack()
    {
        StackItems.Clear();

        const double containerW = 220;
        const double containerH = 170;
        const double cardW = 120;
        const double cardH = 90;

        var baseLeft = (containerW - cardW) / 2.0;
        var baseTop = (containerH - cardH) / 2.0;

        const double dx = 24.0;
        const double dy = 18.0;

        var i = 0;
        var preview = Items.Reverse().Take(3);
        foreach (var it in preview)
        {
            StackItems.Add(new ShelfStackCardViewModel(
                it.Id,
                it.FilePath,
                it.FileName,
                it.Icon,
                Left: baseLeft + i * dx,
                Top: baseTop + i * dy,
                ZIndex: 100 - i,
                Layer: i));
            i++;
        }
    }

    private static long TryGetSizeBytes(string path)
    {
        try
        {
            if (File.Exists(path))
                return new FileInfo(path).Length;
        }
        catch
        {
        }

        return 0;
    }

    private static (int? w, int? h) TryGetImageDimensions(string path)
    {
        try
        {
            if (!File.Exists(path))
                return (null, null);

            var ext = Path.GetExtension(path);
            if (string.IsNullOrWhiteSpace(ext))
                return (null, null);

            ext = ext.ToLowerInvariant();
            if (ext is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp"))
                return (null, null);

            using var fs = File.OpenRead(path);
            using var bmp = new Bitmap(fs);
            var px = bmp.PixelSize;
            return (px.Width, px.Height);
        }
        catch
        {
            return (null, null);
        }
    }

    internal static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024L * 1024L)
            return $"{bytes / 1024.0 / 1024.0:0.#} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:0.#} GB";
    }
}

public sealed record ShelfFileItemViewModel(
    Guid Id,
    string FilePath,
    string FileName,
    Bitmap? Icon,
    long SizeBytes,
    int? ImageWidth,
    int? ImageHeight
)
{
    public string TypeColor
    {
        get
        {
            var ext = Path.GetExtension(FilePath)?.ToLowerInvariant() ?? string.Empty;
            if (ext is ".xlsx" or ".xls" or ".csv")
                return "#2E7D32";
            if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp")
                return "#1565C0";
            if (ext is ".pdf")
                return "#C62828";
            if (ext is ".mp3" or ".wav" or ".m4a" or ".aac" or ".flac" or ".ogg")
                return "#616161";
            return "#757575";
        }
    }

    public string TypeGlyph
    {
        get
        {
            var ext = Path.GetExtension(FilePath)?.ToLowerInvariant() ?? string.Empty;
            if (ext is ".mp3" or ".wav" or ".m4a" or ".aac" or ".flac" or ".ogg")
                return "";
            if (ext is ".xlsx" or ".xls" or ".csv")
                return "";
            if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp")
                return "";
            if (ext is ".pdf")
                return "";
            return "";
        }
    }

    public string MetaText
    {
        get
        {
            var parts = new System.Collections.Generic.List<string>();
            if (SizeBytes > 0)
                parts.Add(ShelfWindowViewModel.FormatBytes(SizeBytes));
            if (ImageWidth is not null && ImageHeight is not null)
                parts.Add($"{ImageWidth}x{ImageHeight}");
            return string.Join(" · ", parts);
        }
    }
}

public sealed record ShelfStackCardViewModel(
    Guid Id,
    string FilePath,
    string FileName,
    Bitmap? Icon,
    double Left,
    double Top,
    int ZIndex,
    int Layer
)
{
    public string Shadow
    {
        get
        {
            return Layer switch
            {
                0 => "0 10 22 0 #2A000000",
                1 => "0 8 18 0 #1D000000",
                _ => "0 6 14 0 #14000000",
            };
        }
    }

    public string TypeColor
    {
        get
        {
            var ext = Path.GetExtension(FilePath)?.ToLowerInvariant() ?? string.Empty;
            if (ext is ".xlsx" or ".xls" or ".csv")
                return "#2E7D32";
            if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp")
                return "#1565C0";
            if (ext is ".pdf")
                return "#C62828";
            if (ext is ".mp3" or ".wav" or ".m4a" or ".aac" or ".flac" or ".ogg")
                return "#616161";
            return "#757575";
        }
    }

    public string TypeGlyph
    {
        get
        {
            var ext = Path.GetExtension(FilePath)?.ToLowerInvariant() ?? string.Empty;
            if (ext is ".mp3" or ".wav" or ".m4a" or ".aac" or ".flac" or ".ogg")
                return "";
            if (ext is ".xlsx" or ".xls" or ".csv")
                return "";
            if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp")
                return "";
            if (ext is ".pdf")
                return "";
            return "";
        }
    }
};
