using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace CAP.Avalonia.ViewModels.Converters;

/// <summary>
/// Binds a group of <c>RadioButton</c>s directly to a single enum-valued property: each
/// button's <c>IsChecked</c> compares the bound enum against the button's own
/// <c>ConverterParameter</c> (the enum member name, e.g. "OwnCode"). Avoids adding one bool
/// flag property per option to the view model just to drive radio-button selection.
/// </summary>
public sealed class EnumToBooleanConverter : IValueConverter
{
    /// <summary>True when <paramref name="value"/>'s enum member name equals <paramref name="parameter"/>.</summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return false;
        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// When a RadioButton becomes checked, parses <paramref name="parameter"/> back into the
    /// source enum type. A RadioButton becoming unchecked (as its group's partner gets checked)
    /// yields <see cref="BindingOperations.DoNothing"/> so it never overwrites the value the
    /// partner is about to set.
    /// </summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter is null) return BindingOperations.DoNothing;
        return Enum.Parse(targetType, parameter.ToString()!);
    }
}
