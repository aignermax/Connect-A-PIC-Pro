using System;
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
/// offset editor).
/// </summary>
public sealed class ComponentCheckStatusToTextConverter : IValueConverter
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

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
