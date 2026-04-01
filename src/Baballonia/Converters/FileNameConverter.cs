using Avalonia.Data.Converters;
using System;
using System.Globalization;
using System.IO;

namespace Baballonia.Converters;

public class FileNameConverter: IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Path.GetFileName((string?) value);
    }

    object? IValueConverter.ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
