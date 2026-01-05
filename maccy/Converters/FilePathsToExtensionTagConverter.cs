using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia.Data.Converters;

namespace maccy.Converters;

public sealed class FilePathsToExtensionTagConverter : IValueConverter
{
    public static readonly FilePathsToExtensionTagConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IEnumerable<string> paths)
            return null;

        var first = paths.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        if (string.IsNullOrWhiteSpace(first))
            return null;

        string ext;
        try
        {
            ext = Path.GetExtension(first) ?? string.Empty;
        }
        catch
        {
            ext = string.Empty;
        }

        if (string.IsNullOrWhiteSpace(ext))
            return "FILE";

        ext = ext.Trim();
        if (ext.StartsWith('.'))
            ext = ext[1..];

        if (string.IsNullOrWhiteSpace(ext))
            return "FILE";

        return ext.ToUpperInvariant();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
