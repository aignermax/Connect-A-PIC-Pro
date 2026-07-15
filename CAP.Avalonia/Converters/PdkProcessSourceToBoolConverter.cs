using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using CAP.Avalonia.ViewModels.Components.AddCustomComponent;

namespace CAP.Avalonia.Converters;

public sealed class PdkProcessSourceToBoolConverter : IValueConverter
{
    public static readonly PdkProcessSourceToBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not PdkProcessSource source || parameter is not string target)
            return false;
        return source.ToString() == target;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter is string target)
            return Enum.Parse<PdkProcessSource>(target);
        return BindingOperations.DoNothing;
    }
}
