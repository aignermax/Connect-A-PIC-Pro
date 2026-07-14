using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using CAP.Avalonia.ViewModels.Components.AddCustomComponent;

namespace CAP.Avalonia.Converters;

/// <summary>
/// Binds a <see cref="PdkProcessSource"/> value to a single RadioButton's (or panel's)
/// <c>IsChecked</c>/<c>IsVisible</c>, via a <c>ConverterParameter</c> naming the enum member
/// it represents (e.g. "UseExisting" or "DefineNew"). Scoped narrowly to this one enum rather
/// than reintroducing a generic <c>EnumToBooleanConverter</c> (removed in #723) — this is its
/// only consumer, in <see cref="CAP.Avalonia.Views.CreateCustomPdkWindow"/>.
/// </summary>
public sealed class PdkProcessSourceToBoolConverter : IValueConverter
{
    /// <summary>Shared singleton instance, referenced from AXAML via <c>x:Static</c>.</summary>
    public static readonly PdkProcessSourceToBoolConverter Instance = new();

    /// <summary>True when <paramref name="value"/> equals the enum member named by <paramref name="parameter"/>.</summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not PdkProcessSource source || parameter is not string target)
            return false;
        return source.ToString() == target;
    }

    /// <summary>
    /// Only a RadioButton becoming checked (true) carries information — the previously
    /// checked RadioButton's own "false" callback is ignored so it doesn't clobber the
    /// value the "true" callback just set.
    /// </summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter is string target)
            return Enum.Parse<PdkProcessSource>(target);
        return BindingOperations.DoNothing;
    }
}
