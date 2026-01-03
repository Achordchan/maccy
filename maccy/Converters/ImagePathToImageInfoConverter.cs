using System;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace maccy.Converters;

public sealed class ImagePathToImageInfoConverter : IValueConverter
{
    public static readonly ImagePathToImageInfoConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path)
            return null;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            var bytes = new FileInfo(path).Length;
            using var fs = File.OpenRead(path);
            using var bmp = new Bitmap(fs);
            var kb = Math.Max(1, bytes / 1024);
            return $"Image · {bmp.PixelSize.Width}×{bmp.PixelSize.Height} · {kb} KB";
        }
        catch
        {
            return "Image";
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
