using System;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;

namespace maccy.Converters;

public sealed class ImagePathToFormatTagConverter : IValueConverter
{
    public static readonly ImagePathToFormatTagConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path)
            return null;

        string ext;
        try
        {
            ext = Path.GetExtension(path) ?? string.Empty;
        }
        catch
        {
            ext = string.Empty;
        }

        ext = (ext ?? string.Empty).Trim();
        if (ext.StartsWith('.'))
            ext = ext[1..];

        if (string.IsNullOrWhiteSpace(ext))
            return "IMG";

        return ext.ToUpperInvariant();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
