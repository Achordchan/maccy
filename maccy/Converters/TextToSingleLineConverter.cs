using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Data.Converters;

namespace maccy.Converters;

public sealed class TextToSingleLineConverter : IValueConverter
{
    public static readonly TextToSingleLineConverter Instance = new();

    private static readonly Regex Newlines = new(@"[\r\n]+", RegexOptions.Compiled);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null)
            return null;

        var text = value.ToString();
        if (string.IsNullOrWhiteSpace(text))
            return text;

        // Replace newlines with a single space to enforce single-line display.
        return Newlines.Replace(text, " ").Trim();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
