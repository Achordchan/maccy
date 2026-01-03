using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace maccy.Converters;

public sealed class ImagePathToBitmapConverter : IValueConverter
{
    public static readonly ImagePathToBitmapConverter Instance = new();

    private readonly ConcurrentDictionary<string, Bitmap> _cache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path)
            return null;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        return _cache.GetOrAdd(path, p =>
        {
            using var fs = File.OpenRead(p);
            return new Bitmap(fs);
        });
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
