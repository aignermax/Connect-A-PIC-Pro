using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using CAP.Avalonia.Services.Localization;
using CAP_DataAccess.Components.ComponentDraftMapper;

namespace CAP.Avalonia.Converters;

/// <summary>
/// Maps a <see cref="ComponentCheckStatus"/> to its localized display text via
/// the <c>PdkOffset.CheckStatus.*</c> resource keys. The enum lives in the
/// data-access layer which cannot reference the localization service, so the
/// UI translates at the binding boundary (batch report table in the PDK
/// offset editor). Used as a MULTI-value converter there: the second binding
/// input targets the <see cref="LocalizationService"/> indexer, whose "Item"
/// notification re-fires the binding on a live language switch — a plain
/// one-way binding to the row's Status would keep the old language's text
/// until the next Check-All run (round-5 review [6]).
/// </summary>
public sealed class ComponentCheckStatusToTextConverter : IValueConverter, IMultiValueConverter
{
    /// <summary>Shared stateless instance for AXAML <c>x:Static</c> bindings.</summary>
    public static readonly ComponentCheckStatusToTextConverter Instance = new();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ComponentCheckStatus status)
            return value?.ToString() ?? "";
        return LocalizationService.Instance.Translate($"PdkOffset.CheckStatus.{status}");
    }

    /// <summary>
    /// Multi-binding entry point: <c>values[0]</c> carries the status; any further
    /// inputs are ignored — they exist only to re-trigger the binding (language switch).
    /// </summary>
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        => Convert(values.Count > 0 ? values[0] : null, targetType, parameter, culture);

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
